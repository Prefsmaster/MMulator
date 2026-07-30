using P2000.Machine.Memory;

namespace P2000.Machine.Debug;

/// <summary>
/// Machine-owned collection of debug breakpoints (project CLAUDE.md §3b.2).
///
/// Breakpoints are evaluated inside <see cref="Machine.Tick"/> behind a cheap
/// <see cref="AnyArmed"/> fast path — an empty store costs nothing per tick.
/// A hit raises <see cref="Machine.BreakHit"/> at the next instruction boundary
/// and sets <see cref="Machine.IsPaused"/>.
///
/// IDs are stable for the lifetime of the breakpoint; pass them to
/// <see cref="Remove"/> to remove individual entries.
///
/// <b>Bank-qualified breakpoints (project CLAUDE.md §13 milestone 24):</b> an Exec/MemRead/
/// MemWrite/MemAccess breakpoint whose address falls in the banked window
/// (<see cref="PageTable.BankedWindowStart"/>-<see cref="PageTable.BankedWindowEnd"/>) can
/// optionally carry a specific bank index. A bank-qualified breakpoint fires ONLY when the
/// live active-bank value passed into the internal Check* methods matches its qualifier; an
/// unqualified breakpoint (<c>bank: null</c>, the only shape that existed before this milestone)
/// fires regardless of which bank is active, exactly as before — no behavior change for any
/// existing breakpoint, banked-region or not. I/O breakpoints never carry a bank qualifier —
/// ports have no relationship to the banked memory window.
/// </summary>
public sealed class BreakpointStore
{
    private readonly record struct Entry(int Id, BreakpointKind Kind, ushort Address, int? Bank);

    private readonly List<Entry> _list = new();
    private int _nextId = 1;

    // ---- Query ------------------------------------------------------------------

    /// <summary>True when at least one breakpoint is armed. The tick loop checks this
    /// first; when false the remaining breakpoint logic is completely skipped.</summary>
    public bool AnyArmed => _list.Count > 0;

    // ---- Registration -----------------------------------------------------------

    /// <summary>Adds an execute breakpoint at <paramref name="address"/>. Fires before
    /// the instruction at that address executes. <paramref name="bank"/> (project CLAUDE.md §13
    /// milestone 24) optionally restricts the hit to one specific bank when
    /// <paramref name="address"/> falls in the banked window — see the class doc.</summary>
    public int AddExec(ushort address, int? bank = null) => Add(BreakpointKind.Exec, address, bank);

    /// <summary>Adds a memory-read watchpoint at <paramref name="address"/>. Fires on any
    /// MREQ+RD to that address, including instruction fetches. <paramref name="bank"/> — see
    /// <see cref="AddExec"/>.</summary>
    public int AddMemRead(ushort address, int? bank = null) => Add(BreakpointKind.MemRead, address, bank);

    /// <summary>Adds a memory-write watchpoint at <paramref name="address"/>. <paramref name="bank"/>
    /// — see <see cref="AddExec"/>.</summary>
    public int AddMemWrite(ushort address, int? bank = null) => Add(BreakpointKind.MemWrite, address, bank);

    /// <summary>Adds a memory-access watchpoint at <paramref name="address"/>. Fires on
    /// either a read or a write. <paramref name="bank"/> — see <see cref="AddExec"/>.</summary>
    public int AddMemAccess(ushort address, int? bank = null) => Add(BreakpointKind.MemAccess, address, bank);

    /// <summary>Adds an I/O-read watchpoint on <paramref name="port"/>. Fires on IORQ+RD
    /// to that port (excludes interrupt-acknowledge cycles). No bank qualifier — ports have
    /// no relationship to the banked memory window.</summary>
    public int AddIoRead(byte port) => Add(BreakpointKind.IoRead, port, bank: null);

    /// <summary>Adds an I/O-write watchpoint on <paramref name="port"/>.</summary>
    public int AddIoWrite(byte port) => Add(BreakpointKind.IoWrite, port, bank: null);

    // ---- Removal ----------------------------------------------------------------

    /// <summary>Removes the breakpoint with the given <paramref name="id"/>. Returns
    /// <c>true</c> if found and removed, <c>false</c> if the ID was not present.</summary>
    public bool Remove(int id)
    {
        for (var i = 0; i < _list.Count; i++)
        {
            if (_list[i].Id != id) continue;
            _list.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>Removes all breakpoints.</summary>
    public void Clear() => _list.Clear();

    // ---- Internal check API (called from Machine.Tick) --------------------------

    /// <param name="activeBank">The page table's live-active bank index at the moment of this
    /// check (project CLAUDE.md §13 milestone 24) — ignored by every unqualified breakpoint;
    /// only consulted against a bank-qualified one. Pass the raw value even on a machine with
    /// no banking at all — no bank-qualified breakpoint can exist there in practice (nothing
    /// offers the qualifier), so the value is inert.</param>
    internal BreakEvent? CheckExec(ushort pc, int activeBank)
        => Check(BreakpointKind.Exec, pc, activeBank);

    internal BreakEvent? CheckMemRead(ushort address, int activeBank)
        => Check(BreakpointKind.MemRead, address, activeBank)
        ?? Check(BreakpointKind.MemAccess, address, activeBank);

    internal BreakEvent? CheckMemWrite(ushort address, int activeBank)
        => Check(BreakpointKind.MemWrite, address, activeBank)
        ?? Check(BreakpointKind.MemAccess, address, activeBank);

    internal BreakEvent? CheckIoRead(byte port)
        => Check(BreakpointKind.IoRead, port, activeBank: 0);

    internal BreakEvent? CheckIoWrite(byte port)
        => Check(BreakpointKind.IoWrite, port, activeBank: 0);

    // ---- Helpers ----------------------------------------------------------------

    private int Add(BreakpointKind kind, ushort address, int? bank)
    {
        if (bank is not null && (address < PageTable.BankedWindowStart || address > PageTable.BankedWindowEnd))
        {
            throw new ArgumentException(
                $"A bank qualifier only applies to addresses in the banked window " +
                $"(0x{PageTable.BankedWindowStart:X4}-0x{PageTable.BankedWindowEnd:X4}); " +
                $"address 0x{address:X4} is outside it.", nameof(bank));
        }

        var id = _nextId++;
        _list.Add(new Entry(id, kind, address, bank));
        return id;
    }

    private BreakEvent? Check(BreakpointKind kind, ushort address, int activeBank)
    {
        foreach (var e in _list)
        {
            if (e.Kind != kind || e.Address != address) continue;
            if (e.Bank is int wantBank && wantBank != activeBank) continue; // bank-qualified, not the active one
            return new BreakEvent(kind, address, e.Id);
        }
        return null;
    }
}

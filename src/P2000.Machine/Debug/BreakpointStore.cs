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
/// </summary>
public sealed class BreakpointStore
{
    private readonly record struct Entry(int Id, BreakpointKind Kind, ushort Address);

    private readonly List<Entry> _list = new();
    private int _nextId = 1;

    // ---- Query ------------------------------------------------------------------

    /// <summary>True when at least one breakpoint is armed. The tick loop checks this
    /// first; when false the remaining breakpoint logic is completely skipped.</summary>
    public bool AnyArmed => _list.Count > 0;

    // ---- Registration -----------------------------------------------------------

    /// <summary>Adds an execute breakpoint at <paramref name="address"/>. Fires before
    /// the instruction at that address executes.</summary>
    public int AddExec(ushort address) => Add(BreakpointKind.Exec, address);

    /// <summary>Adds a memory-read watchpoint at <paramref name="address"/>. Fires on any
    /// MREQ+RD to that address, including instruction fetches.</summary>
    public int AddMemRead(ushort address) => Add(BreakpointKind.MemRead, address);

    /// <summary>Adds a memory-write watchpoint at <paramref name="address"/>.</summary>
    public int AddMemWrite(ushort address) => Add(BreakpointKind.MemWrite, address);

    /// <summary>Adds a memory-access watchpoint at <paramref name="address"/>. Fires on
    /// either a read or a write.</summary>
    public int AddMemAccess(ushort address) => Add(BreakpointKind.MemAccess, address);

    /// <summary>Adds an I/O-read watchpoint on <paramref name="port"/>. Fires on IORQ+RD
    /// to that port (excludes interrupt-acknowledge cycles).</summary>
    public int AddIoRead(byte port) => Add(BreakpointKind.IoRead, port);

    /// <summary>Adds an I/O-write watchpoint on <paramref name="port"/>.</summary>
    public int AddIoWrite(byte port) => Add(BreakpointKind.IoWrite, port);

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

    internal BreakEvent? CheckExec(ushort pc)
        => Check(BreakpointKind.Exec, pc);

    internal BreakEvent? CheckMemRead(ushort address)
        => Check(BreakpointKind.MemRead, address)
        ?? Check(BreakpointKind.MemAccess, address);

    internal BreakEvent? CheckMemWrite(ushort address)
        => Check(BreakpointKind.MemWrite, address)
        ?? Check(BreakpointKind.MemAccess, address);

    internal BreakEvent? CheckIoRead(byte port)
        => Check(BreakpointKind.IoRead, port);

    internal BreakEvent? CheckIoWrite(byte port)
        => Check(BreakpointKind.IoWrite, port);

    // ---- Helpers ----------------------------------------------------------------

    private int Add(BreakpointKind kind, ushort address)
    {
        var id = _nextId++;
        _list.Add(new Entry(id, kind, address));
        return id;
    }

    private BreakEvent? Check(BreakpointKind kind, ushort address)
    {
        foreach (var e in _list)
        {
            if (e.Kind == kind && e.Address == address)
                return new BreakEvent(kind, address, e.Id);
        }
        return null;
    }
}

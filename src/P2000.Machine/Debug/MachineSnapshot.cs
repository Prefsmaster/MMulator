using Z80.Core;

namespace P2000.Machine.Debug;

/// <summary>
/// A read-only, allocation-light snapshot of machine state taken at an instruction boundary
/// (project CLAUDE.md §3b.1, milestone 13). Safe to read from any thread once obtained —
/// the register copy is immutable; memory is read live through <see cref="ReadMemory"/>
/// against the machine's page table (which is deterministic and single-threaded).
///
/// <b>Only obtain via <see cref="Machine.TakeSnapshot"/>.</b> The method guards that the
/// machine is at an instruction boundary (<see cref="Z80.Core.Z80.AtInstructionBoundary"/>),
/// the same safe point <c>SaveState</c> relies on (§11). Taking a snapshot mid-instruction
/// is not supported and will throw.
/// </summary>
public readonly struct MachineSnapshot
{
    // ---- Constructor (package-private — only Machine creates these) -----------------

    internal MachineSnapshot(Registers reg, int fieldTState, Func<ushort, byte> readMemory)
    {
        Registers = reg;
        FieldTState = fieldTState;
        ReadMemory = readMemory;
    }

    // ---- Full register file ---------------------------------------------------------

    /// <summary>A copy of the Z80 register file at the snapshot boundary. All fields
    /// are consistent: the CPU was at an instruction boundary when the copy was taken.
    /// </summary>
    public Registers Registers { get; }

    // Convenience aliases for the most-commonly debugged registers.
    public byte A => Registers.A;
    public byte F => Registers.F;
    public byte B => Registers.B;
    public byte C => Registers.C;
    public byte D => Registers.D;
    public byte E => Registers.E;
    public byte H => Registers.H;
    public byte L => Registers.L;

    public ushort AF => Registers.AF;
    public ushort BC => Registers.BC;
    public ushort DE => Registers.DE;
    public ushort HL => Registers.HL;

    public ushort AF_ => Registers.AF_;
    public ushort BC_ => Registers.BC_;
    public ushort DE_ => Registers.DE_;
    public ushort HL_ => Registers.HL_;

    public ushort IX => Registers.IX;
    public ushort IY => Registers.IY;
    public ushort SP => Registers.SP;
    public ushort PC => Registers.PC;

    public byte I => Registers.I;
    public byte R => Registers.R;

    /// <summary>Internal WZ / MEMPTR register (affects undocumented flags and is useful
    /// for tracing some addressing modes).</summary>
    public ushort WZ => Registers.WZ;

    public bool IFF1 => Registers.IFF1;
    public bool IFF2 => Registers.IFF2;

    /// <summary>Interrupt mode: 0, 1, or 2.</summary>
    public byte IM => Registers.IM;

    // ---- Flags broken out (from F) --------------------------------------------------

    /// <summary>Sign flag (F bit 7).</summary>
    public bool SF => (F & 0x80) != 0;

    /// <summary>Zero flag (F bit 6).</summary>
    public bool ZF => (F & 0x40) != 0;

    /// <summary>Undocumented Y flag / bit 5 of result (F bit 5).</summary>
    public bool YF => (F & 0x20) != 0;

    /// <summary>Half-carry flag (F bit 4).</summary>
    public bool HF => (F & 0x10) != 0;

    /// <summary>Undocumented X flag / bit 3 of result (F bit 3).</summary>
    public bool XF => (F & 0x08) != 0;

    /// <summary>Parity / overflow flag (F bit 2).</summary>
    public bool PF => (F & 0x04) != 0;

    /// <summary>Add/subtract flag (F bit 1).</summary>
    public bool NF => (F & 0x02) != 0;

    /// <summary>Carry flag (F bit 0).</summary>
    public bool CF => (F & 0x01) != 0;

    // ---- In-frame cycle position ----------------------------------------------------

    /// <summary>T-state offset within the current 50 Hz field at the moment the snapshot
    /// was taken (0 – <see cref="Contention.VideoFetchUnit.TStatesPerField"/>-1). Zero at
    /// field start; wraps to zero when a new field begins. Together with <see cref="PC"/>
    /// this gives the exact point in the field where the machine paused.</summary>
    public int FieldTState { get; }

    // ---- Memory read view -----------------------------------------------------------

    /// <summary>Live read from the machine's page table. Returns the byte at
    /// <paramref name="address"/> as it would be seen by a CPU MREQ+RD at that address
    /// (open-bus 0xFF for unpopulated regions; no side-effects).</summary>
    public Func<ushort, byte> ReadMemory { get; }
}

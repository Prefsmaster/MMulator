namespace P2000.Machine;

/// <summary>Top-level model axis (reference doc §3a); gates everything else. Only
/// <see cref="P2000T"/> is built out so far — <see cref="P2000M"/> is a later phase
/// (project CLAUDE.md §14).</summary>
public enum MachineModel
{
    P2000T,
    P2000M,
}

/// <summary>Which internal-slot extension board is fitted, if any (reference doc §5 /
/// project CLAUDE.md §5). Bare (<see cref="None"/>) is the default: fixed base RAM, no
/// disk. RAM-only and floppy+RAM boards are the two ways to grow memory; only the
/// floppy+RAM board adds disk/CTC.</summary>
public enum InternalBoard
{
    None,
    RamOnly,
    FloppyRam,
}

/// <summary>
/// Machine TOPOLOGY — what the machine IS, independent of what it's doing right now
/// (project CLAUDE.md §11 / reference doc §3a). Serializable, small, human-editable.
/// Loading a <see cref="MachineConfig"/> rebuilds the machine (reset-to-apply, locked
/// decision §2.3); it never mutates a running machine's topology in place.
///
/// <b>Bare by default</b> (locked decision §2.1): a new <see cref="MachineConfig"/> has
/// no SLOT1/SLOT2 cartridge, an empty cassette, and no extension board — the honest
/// baseline that exercises the ROM's presence-probe fallbacks. Field growth (RAM socket
/// population, slot contents, mounts, display/audio prefs) lands milestone by milestone
/// as the devices that consume them are built (project CLAUDE.md §13).
/// </summary>
public sealed class MachineConfig
{
    public MachineModel Model { get; init; } = MachineModel.P2000T;

    public InternalBoard Board { get; init; } = InternalBoard.None;
}

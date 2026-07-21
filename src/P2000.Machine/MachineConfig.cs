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

/// <summary>The commercial RAM presets (reference doc §5), each a contiguous-population
/// preset the monitor ROM's boot-time RAM test sizes correctly via open-bus. PTC-96K is
/// NOT modelled yet — it's a floppyboard-only variant (reference doc open item #4: how its
/// 16 KB + 64 KB combine is unconfirmed) and floppy support is deferred (project CLAUDE.md
/// §14), so there is no confirmed hardware to build against yet.</summary>
public enum RamVariant
{
    /// <summary>16 KB — base RAM only (0x6000-0x9FFF). The bare-motherboard default.</summary>
    T38,

    /// <summary>32 KB — base + 16 KB expansion (0xA000-0xDFFF).</summary>
    T54,

    /// <summary>80 KB — base + 16 KB expansion + 48 KB banked (6 x 8 KB) at 0xE000-0xFFFF
    /// via I/O port 0x94.</summary>
    T102,
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

    public RamVariant RamVariant { get; init; } = RamVariant.T38;

    /// <summary>Bank count for the 0xE000-0xFFFF window. <c>null</c> derives the faithful
    /// count from <see cref="RamVariant"/> (6 for T102, 0 = unbanked/open-bus otherwise).
    /// The hardware places no restriction on this register (reference doc §5), so a
    /// homebrew module can set any count up to 256; an index at or beyond the populated
    /// count reads open bus, same as any other unpopulated region.</summary>
    public int? BankCount { get; init; }

    public int EffectiveBankCount => BankCount ?? (RamVariant == RamVariant.T102 ? 6 : 0);

    /// <summary>Optional path to a custom monitor ROM file (.bin / .rom). <c>null</c> (the
    /// default) loads the embedded P2000ROM.rom so the machine boots out of the box with
    /// zero setup (project CLAUDE.md §5). Set this only to run a patched or alternate
    /// monitor revision — the override reads from disk at machine-assembly time.</summary>
    public string? MonitorRomPath { get; init; }

    /// <summary>Optional path to a SLOT1 cartridge image (.bin / .rom). <c>null</c> (the
    /// default) leaves SLOT1 empty (open-bus), which causes the monitor ROM's boot sequence
    /// to skip to the cassette-wait loop. Set to a BASIC cartridge path to boot into BASIC
    /// (project CLAUDE.md §5, §7). Reset-to-apply — topology is fixed once the machine
    /// is running (locked decision §2.3).</summary>
    public string? Slot1CartridgePath { get; init; }

    /// <summary>Optional path to a raw <c>.dsk</c> floppy image to mount in FDC drive 0 at
    /// machine-assembly time (project CLAUDE.md §13 milestone 19). <c>null</c> (the default)
    /// leaves the drive empty. Only meaningful when <see cref="Board"/> is
    /// <see cref="InternalBoard.FloppyRam"/>; ignored otherwise. Reset-to-apply, same as
    /// <see cref="Slot1CartridgePath"/>.</summary>
    public string? FloppyDiskImagePath { get; init; }

    /// <summary>Optional seed for the RAM power-on garbage fill (project CLAUDE.md §17,
    /// 2026-07-21/22 finding — real volatile RAM doesn't power up all-zero). <c>null</c> (the
    /// default) uses <see cref="Memory.PageTable.DefaultRamSeed"/> — a fixed, deterministic
    /// value, so tests/CI and any caller that doesn't care stay fully reproducible (locked
    /// decision §2.2: no randomness in emulation code). Set this to reproduce a specific bug
    /// report that names its seed, or leave it null and let <see cref="P2000.UI"/> supply a
    /// genuinely random value at each real cold boot / app launch. Same null-means-default
    /// convention as <see cref="MonitorRomPath"/>.</summary>
    public ulong? RamSeed { get; init; }
}

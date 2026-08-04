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

/// <summary>Sidedness of a floppy drive/image (project CLAUDE.md §13 milestone 20).</summary>
public enum DiskSides
{
    Single,
    Double,
}

/// <summary>
/// Topology for one floppy drive on the floppy+RAM internal-extension board (project CLAUDE.md
/// §13 milestone 20; reference doc §5d — the confirmed 4-position <c>DRISEL0</c>-<c>3</c>
/// connector). <see cref="MachineConfig.FloppyDrives"/> holds up to 4 of these. Drive
/// presence/<see cref="Capacity"/>/<see cref="Sides"/> is TOPOLOGY (reset-to-apply, same rule as
/// every other axis in <see cref="MachineConfig"/>); <see cref="ImagePath"/> is only the initial
/// image mounted at machine-assembly time — mounting/ejecting/swapping an image at runtime is a
/// host-side action on <see cref="Devices.Fdc.Upd765"/>, not a config change.
/// </summary>
public sealed class FloppyDriveConfig
{
    /// <summary>0-3 — the physical drive-select position on the board's connector (reference doc
    /// §5d <c>DRISEL0</c>-<c>3</c>), NOT a list index. Must be unique within
    /// <see cref="MachineConfig.FloppyDrives"/>.</summary>
    public int DriveIndex { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Track count seed for blank/unlabeled media (35/40/80 — reference doc §5d). Only
    /// consulted when creating a fresh unformatted image for this drive; a mounted image with a
    /// valid on-disk label always wins over this (M19's auto-detect rule, unchanged by M20).</summary>
    public int Capacity { get; init; } = 40;

    /// <summary>Sidedness seed for blank/unlabeled media — same "label wins, this is only the
    /// seed" rule as <see cref="Capacity"/>.</summary>
    public DiskSides Sides { get; init; } = DiskSides.Single;

    /// <summary>Optional path to a raw <c>.dsk</c> image to mount in this drive at
    /// machine-assembly time. <c>null</c> leaves the drive present but empty (the "configured/
    /// enabled drive with no image mounted" no-op case, project CLAUDE.md §13.20).</summary>
    public string? ImagePath { get; init; }
}

/// <summary>
/// Socket/piggyback hardware MODIFICATIONS fitted to the machine (reference doc §3a's
/// "modifications" axis, §5 "80-column mode"). Orthogonal to model / RAM / internal-slot board
/// / slot population — a modified T can also carry any of those. Reset-to-apply, like every
/// other topology axis. Defaults are "stock machine", so a config that says nothing about
/// modifications is byte-for-byte the machine this project has always built.
/// </summary>
public sealed class ModificationsConfig
{
    /// <summary>The 1986 80-character daughterboard (P2000 Nieuwsbrief §13.25 — see
    /// <c>docs/P2000T-80column-board-1986-newsletter.md</c>). <b>T-only:</b> a config that
    /// fits it on a <see cref="MachineModel.P2000M"/> is INVALID and rejected at machine
    /// assembly, not silently ignored — the M has no SAA5050 at all, and its native 80-column
    /// business display is entirely different circuitry (reference doc §5).</summary>
    public bool EightyColumnBoard { get; init; }

    /// <summary>Reproduce the out-of-spec SAA5050 rendering artifact the article documents for
    /// 80-column mode (§13.25.2: at 12 MHz the character generator runs "far outside its
    /// specifications" and "sometimes one sees, at the position of a switch-over character, a
    /// small block or a few dashes instead of a space"). Sibling in spirit to the existing
    /// "show contention glitches" toggle: an authentic-but-ugly hardware behaviour the user can
    /// switch off. Defaults ON — the article's own commissioning procedure calls the artifact
    /// normal. Meaningless (and inert) with no board fitted or in 40-column mode.</summary>
    public bool ShowEightyColumnArtifacts { get; init; } = true;
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

    /// <summary>Socket/piggyback hardware modifications (T-only). Never null — an absent
    /// <c>modifications</c> key in an older <c>.cfg</c>/<c>.state</c> deserializes to this
    /// all-defaults instance, i.e. "no board fitted".</summary>
    public ModificationsConfig Modifications { get; init; } = new();

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

    /// <summary>Optional path to a <c>.cas</c> image to mount in the cassette deck at
    /// machine-assembly time (project CLAUDE.md milestone 20b; reference doc §3a "RESOLVED —
    /// cassette gets the same treatment, not left asymmetric"). <c>null</c> (the default)
    /// leaves the cassette deck empty, same as <see cref="Slot1CartridgePath"/>'s and
    /// <see cref="FloppyDriveConfig.ImagePath"/>'s null-means-absent convention. Mounted via
    /// the same <see cref="Devices.Cassette.MdcrDevice.InsertTape"/> path the runtime host-API
    /// already uses — purely additive, doesn't touch the "bare by default" locked decision
    /// (§2.1). The runtime mount/eject/swap capability (already locked) is unaffected on top
    /// of this, exactly as already true for disk.</summary>
    public string? CassettePath { get; init; }

    /// <summary>Per-drive floppy topology — up to 4 drives (project CLAUDE.md §13 milestone 20;
    /// reference doc §5d, the confirmed 4-position connector), replacing the earlier
    /// milestone-19 singular <c>FloppyDiskImagePath</c> (implicitly drive 1 only). Only
    /// meaningful when <see cref="Board"/> is <see cref="InternalBoard.FloppyRam"/>; ignored
    /// otherwise. Reset-to-apply, same as <see cref="Slot1CartridgePath"/>. Empty by default —
    /// a fitted board with no drives configured is a legitimate (if unusual) topology, same as
    /// a bare board carrying no cartridge.</summary>
    public IReadOnlyList<FloppyDriveConfig> FloppyDrives { get; init; } = Array.Empty<FloppyDriveConfig>();

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

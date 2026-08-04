using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.State;
using P2000.UI.Runner;
using P2000.UI.State;
using System.Collections.ObjectModel;

namespace P2000.UI.ViewModels;

/// <summary>One row of the "Floppy drives" config axis (project CLAUDE.md §14 milestone 14) —
/// Capacity/Sides are reset-to-apply topology, and only the SEED for blank/unlabeled media
/// (machine-layer M19/M20: a mounted image's own on-disk label always wins). <see cref="DriveIndex"/>
/// is fixed at construction — rows are always in <see cref="ConfigWindowVm.DriveIndexSequence"/>
/// order (1, 2, 3, 0 — the real machine's own on-screen drive numbering, milestone 14d), not
/// 0-based sequential, matching the config window's drive-COUNT selector (no per-row enable/gaps,
/// unlike the machine's own more general <see cref="FloppyDriveConfig"/> shape).</summary>
public sealed partial class FloppyDriveRowVm : ObservableObject
{
    private readonly ConfigWindowVm _owner;

    public int DriveIndex { get; }

    [ObservableProperty] private int _capacity = 40;
    [ObservableProperty] private DiskSides _sides = DiskSides.Single;

    /// <summary>Capacity/Sides as of the last time they were synced FROM somewhere authoritative
    /// (a loaded `.cfg`, the live machine's own config, or a live drive's actual geometry) —
    /// project CLAUDE.md §14 item 18, 2026-07-31 bugfix. Distinguishes "the operator never
    /// touched this since the last sync" (safe to silently refresh from live reality, e.g. after
    /// a live "Adjust and remount" elsewhere) from "the operator deliberately edited this" (must
    /// never be silently overwritten) — see <see cref="ConfigWindowVm.RefreshLiveGeometryFromDrives"/>.
    /// Initialized to match <see cref="Capacity"/>/<see cref="Sides"/>'s own defaults so a freshly
    /// constructed, never-loaded row isn't mistaken for "deliberately edited."</summary>
    internal int CapacityBaseline { get; set; } = 40;
    internal DiskSides SidesBaseline { get; set; } = DiskSides.Single;

    /// <summary>Manually-authored initial image path (project CLAUDE.md milestone 14c) — for
    /// hand-authoring a <c>.cfg</c> (e.g. a "starter kit" for someone else) without a machine
    /// running to capture from. Complementary to, not a substitute for,
    /// <see cref="ConfigWindowVm.SaveCfgAsync"/> now capturing whatever's actually live-mounted.
    /// Picking a new image goes through <see cref="ConfigWindowVm.PickImageForRowAsync"/> (project
    /// CLAUDE.md milestone 14g) — a LIVE delegation or an OFFLINE preview, depending on whether
    /// this row's drive currently exists in the machine's live topology.</summary>
    [ObservableProperty] private string _imagePath = "";

    public static IReadOnlyList<int> Capacities { get; } = [35, 40, 80];
    public static IReadOnlyList<DiskSides> SidesOptions { get; } = [DiskSides.Single, DiskSides.Double];

    public FloppyDriveRowVm(int driveIndex, ConfigWindowVm owner)
    {
        DriveIndex = driveIndex;
        _owner = owner;
    }

    public FloppyDriveConfig ToConfig() => new()
    {
        DriveIndex = DriveIndex,
        Enabled = true,
        Capacity = Capacity,
        Sides = Sides,
        ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath.Trim(),
    };

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var file = await ConfigWindowVm.PickStorageFileAsync($"Drive {DriveIndex} initial image (.dsk / .img)",
            [new FilePickerFileType("P2000T Disk") { Patterns = ["*.dsk", "*.img"] }]);
        if (file is not null) await _owner.PickImageForRowAsync(this, file);
    }

    [RelayCommand]
    private void ClearImage() => ImagePath = "";

    /// <summary>"Update this row's Capacity/Sides to match" — the offline preview dialog's
    /// candidate-resolution action (project CLAUDE.md milestone 14g), the offline analogue of
    /// <see cref="Views.DiskDriveWindow"/>'s live <c>ReconfigureAndRemount</c> button. Setting
    /// either property re-triggers the SAME preview check against the (now-matching) file, which
    /// resolves to <see cref="DiskGeometryMismatchKind.None"/> — closing the loop.</summary>
    public void UpdateGeometryTo(int tracks, DiskSides sides)
    {
        Capacity = tracks;
        Sides = sides;
    }

    // Bidirectional recheck (project CLAUDE.md milestone 14g): editing Capacity/Sides AFTER a
    // path is already set re-runs the offline preview against the new values. No-op for a row
    // backed by a live drive (RecheckOfflineMismatchAsync itself gates on that) and no-op while
    // ImagePath is still empty (nothing to preview yet).
    partial void OnCapacityChanged(int value) => _ = _owner.RecheckOfflineMismatchAsync(this);
    partial void OnSidesChanged(DiskSides value) => _ = _owner.RecheckOfflineMismatchAsync(this);
}

/// <summary>ViewModel for the config window (milestone 5, extended by milestone 14 for the
/// floppy-drive axis; milestone 14c for cassette/per-drive path authoring + startup pinning).
/// Exposes the topology axes of <see cref="MachineConfig"/> as observable properties; Apply
/// rebuilds and cold-resets the machine (reset-to-apply, locked decision §2.3). Cassette
/// live-mount is not a topology axis — it lives in the deck window (runtime exception §2.7);
/// disk IMAGES are the same runtime exception (drive COUNT/geometry is topology, an image
/// mounted in an already-present drive is a live swap — the Disk Drives window's job).
/// <see cref="CassettePath"/>/<see cref="FloppyDriveRowVm.ImagePath"/> here are for HAND-AUTHORING
/// a <c>.cfg</c>'s initial mount, not for driving a running machine's live deck/drives.</summary>
public sealed partial class ConfigWindowVm : ObservableObject
{
    private readonly EmulationRunner _runner;

    /// <summary>The SAME <see cref="DiskDriveWindowVm"/> the Disk Drives satellite window uses
    /// (shared via <c>DisplayWindowVm.DiskVm</c>, not a second instance) — project CLAUDE.md
    /// milestone 14g. Reused for two things: (1) <see cref="FindLiveDrive"/> locates the live
    /// <see cref="DiskDriveVm"/> for a row's drive index so picking a new image can delegate
    /// straight into its own <c>MountBytes</c>, the exact same mount path the Disk Drives window
    /// itself uses; (2) <see cref="Apply"/> forces its rebuild synchronously right after a
    /// reconfigure so a `.cfg`-authored mismatch surfaces immediately, regardless of whether the
    /// Disk Drives window has ever been opened.</summary>
    private readonly DiskDriveWindowVm _diskDrives;

    /// <summary>Raised when browsing a new image for an OFFLINE row (no live drive backing it —
    /// project CLAUDE.md milestone 14g) previews a real geometry mismatch via
    /// <see cref="DskImage.DetectMismatch"/>. The live case never raises this — it goes through
    /// <see cref="_diskDrives"/>'s own <c>GeometryMismatchDetected</c> instead, via the SAME
    /// <see cref="DiskDriveVm.MountBytes"/> path the Disk Drives window uses.</summary>
    public event Action<FloppyDriveRowVm, DiskGeometryMismatch>? OfflineMismatchDetected;

    /// <summary>Carried through from whatever was last loaded (a `.cfg` file or the running
    /// machine's own config) so <see cref="Apply"/> doesn't silently discard them — neither has a
    /// bound UI field of its own. Without this, EVERY Apply passed <c>RamSeed = null</c> to
    /// <see cref="EmulationRunner.Reconfigure"/>, which then generates a FRESH random seed even
    /// when nothing was actually changed (a "real cold start" is only meant to happen when
    /// authoring a genuinely new config from scratch, not on every Load→Apply round-trip) —
    /// found via owner report, 2026-07-27: "load a config, apply, unpin, pin again asks for
    /// save" even with no edits, because the live machine's re-rolled `RamSeed` could never match
    /// whatever concrete seed the saved file actually had.</summary>
    private ulong? _ramSeed;

    /// <summary>Same reasoning as <see cref="_ramSeed"/> — <see cref="MachineConfig.BankCount"/>
    /// has no bound UI field either.</summary>
    private int? _bankCount;

    [ObservableProperty] private RamVariant _ramVariant;

    /// <summary>Internal-slot board (project CLAUDE.md §7): none / RAM-only / floppy+RAM.
    /// Gates whether the FDC/CTC + disk drives exist at all. <see cref="FloppyRam"/> requires
    /// <see cref="RamVariant.T102"/> (machine-layer gate, <c>Machine.cs</c>) — selecting it
    /// here auto-forces the RAM selector to T102 and disables it (<see cref="CanEditRamVariant"/>)
    /// so this invalid combination can't be built from the UI at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditRamVariant), nameof(ShowFloppyDrives))]
    private InternalBoard _board;

    /// <summary>The 1986 80-column modification daughterboard (machine milestone 25,
    /// <c>MachineConfig.Modifications.EightyColumnBoard</c>) — T-only, reset-to-apply. The
    /// machine layer REJECTS this on a P2000M rather than ignoring it, so
    /// <see cref="CanEditModifications"/> gates the control rather than letting Apply throw.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditEightyColumnArtifacts))]
    private bool _eightyColumnBoard;

    /// <summary>Reproduce the article's documented out-of-spec 80-column rendering artifact
    /// (<c>MachineConfig.Modifications.ShowEightyColumnArtifacts</c>). Defaults ON, matching the
    /// machine-layer default; only meaningful with the board fitted.</summary>
    [ObservableProperty] private bool _showEightyColumnArtifacts = true;

    [ObservableProperty] private string _slot1CartridgePath = "";
    [ObservableProperty] private string _monitorRomPath = "";

    /// <summary>Manually-authored initial cassette path (project CLAUDE.md milestone 14c) —
    /// mirrors <see cref="Slot1CartridgePath"/>'s existing browse/clear pattern. Complementary to
    /// <see cref="SaveCfgAsync"/> now capturing whatever's actually live-mounted in the deck.</summary>
    [ObservableProperty] private string _cassettePath = "";

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>The last <c>.cfg</c> path this window loaded from or saved to — what
    /// <see cref="PinAsStartupConfigAsync"/> pins directly, without re-prompting, since pinning
    /// designates an already-named, already-saved file, not whatever happens to be in the fields
    /// right now (project CLAUDE.md milestone 14c). <c>null</c> no longer disables the Pin
    /// button (owner report, 2026-07-27: gating it was counter-intuitive right after Apply,
    /// before any Save/Load) — <see cref="PinAsStartupConfigAsync"/> prompts for a save first
    /// when this is still null.</summary>
    [ObservableProperty] private string? _lastCfgPath;

    /// <summary>True when <see cref="AppPreferences.StartupCfgPath"/> is pinned — auto-remember
    /// (writing <c>last-session.cfg</c> on quit) stops overwriting it until unpinned (project
    /// CLAUDE.md milestone 14c).</summary>
    [ObservableProperty] private bool _isStartupPinned;

    public IReadOnlyList<RamVariant> RamVariants { get; } =
        [RamVariant.T38, RamVariant.T54, RamVariant.T102];

    public IReadOnlyList<InternalBoard> Boards { get; } =
        [InternalBoard.None, InternalBoard.RamOnly, InternalBoard.FloppyRam];

    /// <summary>False (RAM selector disabled) while <see cref="Board"/> is
    /// <see cref="InternalBoard.FloppyRam"/> — the combination with anything but T102 is
    /// rejected at machine assembly (reference doc §5b), so the UI never offers it.</summary>
    public bool CanEditRamVariant => Board != InternalBoard.FloppyRam;

    public bool ShowFloppyDrives => Board == InternalBoard.FloppyRam;

    /// <summary>False when the modifications axis is unavailable for the selected model. Only
    /// the P2000T is offered by this window today (<see cref="BuildConfig"/> hardcodes it), so
    /// this is constant true for now — kept as a real property rather than inlined <c>true</c>
    /// so adding the M selector later greys the axis out instead of building an invalid
    /// config.</summary>
    public bool CanEditModifications => true;

    /// <summary>The artifact toggle is meaningless without the board — grey it out rather than
    /// leaving a live-looking control that does nothing.</summary>
    public bool CanEditEightyColumnArtifacts => CanEditModifications && EightyColumnBoard;

    // ── Floppy drives axis (project CLAUDE.md §14 milestone 14) ──────────────

    public IReadOnlyList<int> FloppyDriveCounts { get; } = [0, 1, 2, 3, 4];

    [ObservableProperty] private int _floppyDriveCount;

    public ObservableCollection<FloppyDriveRowVm> FloppyDriveRows { get; } = new();

    public ConfigWindowVm(EmulationRunner runner, DiskDriveWindowVm diskDrives)
    {
        _runner = runner;
        _diskDrives = diskDrives;
        LoadFromCurrentConfig();
        IsStartupPinned = AppPreferencesFile.Load().StartupCfgIsPinned;
    }

    // ── Sync UI from the live machine config ─────────────────────────────────

    public void LoadFromCurrentConfig()
    {
        // CaptureCurrentConfig() (machine ms.20c), not the stale construction-time Machine.Config
        // — project CLAUDE.md §14 item 18, 2026-07-31 bugfix: a plain Machine.Config read never
        // reflected a live "Adjust and remount" elsewhere (Disk Drives window ms.14e, or the SAME
        // dialog reachable via this window's own live image-picking flow, ms.14g), so re-opening
        // the Config window after one showed the drive's PRE-reconfigure Capacity/Sides, which
        // Apply then silently pushed back — reverting the fix the operator just made.
        var cfg = _runner.Machine.CaptureCurrentConfig();
        RamVariant = cfg.RamVariant;
        Board = cfg.Board;
        Slot1CartridgePath = cfg.Slot1CartridgePath ?? "";
        MonitorRomPath = cfg.MonitorRomPath ?? "";
        CassettePath = cfg.CassettePath ?? "";
        EightyColumnBoard = cfg.Modifications.EightyColumnBoard;
        ShowEightyColumnArtifacts = cfg.Modifications.ShowEightyColumnArtifacts;
        LoadFloppyDrivesFrom(cfg.FloppyDrives);
        _ramSeed = cfg.RamSeed;
        _bankCount = cfg.BankCount;
        StatusMessage = "";
    }

    /// <summary>The real machine's own on-screen drive numbering convention (project CLAUDE.md
    /// milestone 14d; reference doc §5d "RESOLVED — the Config window's drive-count axis now
    /// follows this exact real convention"): the ROM's `get_drive_choice` maps user-facing drive
    /// 1/2/3/4 to internal unit-select 1/2/3/0 — the ROM's disk driver hardcodes unit-select to
    /// drive 1 and never addresses unit 0, so a drive authored at internal index 0 (this window's
    /// OLD 0-based sequential default for "1 drive") is silently invisible to the ROM. Row
    /// position <c>i</c> (0-based, in display/left-to-right order — "Drive 1", "Drive 2", …)
    /// always targets <c>MachineConfig.FloppyDrives</c> index <c>DriveIndexSequence[i]</c>.</summary>
    private static readonly int[] DriveIndexSequence = [1, 2, 3, 0];

    /// <summary>Rebuilds <see cref="FloppyDriveRows"/> (and <see cref="FloppyDriveCount"/>) from
    /// a loaded config's drive list. Missing/disabled/gapped entries collapse to the config
    /// window's simpler sequential-count model (§14 milestone 14, restated against
    /// <see cref="DriveIndexSequence"/> by milestone 14d) — count = one past the highest POSITION
    /// in that sequence with an enabled drive; this only round-trips what THIS window itself could
    /// have produced, not every shape <see cref="MachineConfig.FloppyDrives"/> can technically hold
    /// (e.g. a hand-edited .cfg with gaps, or index 0 set alone with nothing else — collapses to
    /// count 4 with drives 2/3 shown empty, an accepted lossy-collapse limitation, not a crash).</summary>
    private void LoadFloppyDrivesFrom(IReadOnlyList<FloppyDriveConfig> drives)
    {
        var byIndex = drives.Where(d => d.Enabled).ToDictionary(d => d.DriveIndex);
        var count = 0;
        for (var i = 0; i < DriveIndexSequence.Length; i++)
        {
            if (byIndex.ContainsKey(DriveIndexSequence[i])) count = i + 1;
        }
        FloppyDriveCount = count; // triggers OnFloppyDriveCountChanged → ResizeFloppyDriveRows
        foreach (var row in FloppyDriveRows)
        {
            if (byIndex.TryGetValue(row.DriveIndex, out var d))
            {
                // ImagePath set FIRST: Capacity/Sides' own OnChanged hooks re-run the offline
                // preview check (project CLAUDE.md milestone 14g) against whatever ImagePath is
                // CURRENT at that moment — setting it last would recheck against the row's STALE
                // previous path instead of the one this load just brought in.
                row.ImagePath = d.ImagePath ?? "";
                row.Capacity = d.Capacity;
                row.Sides = d.Sides;
                // This IS the fresh baseline — whatever was just loaded, live or from a file
                // (project CLAUDE.md §14 item 18).
                row.CapacityBaseline = d.Capacity;
                row.SidesBaseline = d.Sides;
            }
        }
    }

    partial void OnFloppyDriveCountChanged(int value) => ResizeFloppyDriveRows(value);

    private void ResizeFloppyDriveRows(int count)
    {
        while (FloppyDriveRows.Count > count)
            FloppyDriveRows.RemoveAt(FloppyDriveRows.Count - 1);
        while (FloppyDriveRows.Count < count)
            FloppyDriveRows.Add(new FloppyDriveRowVm(DriveIndexSequence[FloppyDriveRows.Count], this));
    }

    partial void OnBoardChanged(InternalBoard value)
    {
        if (value == InternalBoard.FloppyRam) RamVariant = RamVariant.T102;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Refreshes every row backed by a LIVE drive to that drive's CURRENT actual
    /// Capacity/Sides — but only when the row's displayed value still equals its own baseline
    /// (the value as of the last load/sync), i.e. the operator hasn't deliberately edited it
    /// since. A genuine edit is never silently overwritten (project CLAUDE.md §14 item 18,
    /// 2026-07-31 bugfix).
    ///
    /// Guards <see cref="Apply"/> against reverting a live "Adjust and remount" REGARDLESS of
    /// window-focus history — re-showing the Config window already refreshes via
    /// <see cref="LoadFromCurrentConfig"/>, but a window that stayed visible/frontmost the whole
    /// time (never re-triggering that path) would otherwise still push a stale pre-reconfigure
    /// value on Apply.</summary>
    private void RefreshLiveGeometryFromDrives()
    {
        foreach (var row in FloppyDriveRows)
        {
            var live = FindLiveDrive(row.DriveIndex);
            if (live is null) continue;

            if (row.Capacity == row.CapacityBaseline) row.Capacity = live.Capacity;
            if (row.Sides == row.SidesBaseline) row.Sides = live.Sides;

            row.CapacityBaseline = live.Capacity;
            row.SidesBaseline = live.Sides;
        }
    }

    [RelayCommand]
    private void Apply()
    {
        try
        {
            RefreshLiveGeometryFromDrives();
            var config = BuildConfig();
            _runner.Reconfigure(config);
            // Proactive mismatch surfacing (project CLAUDE.md milestone 14g): force the Disk
            // Drives VM to rebuild against the JUST-reconfigured machine right now, rather than
            // waiting for its next async FrameReady tick. Each freshly-built DiskDriveVm's own
            // construction-time RaisePendingMismatchIfAny (already-existing ms.14e behavior) is
            // what surfaces a `.cfg`-authored mismatch as a dialog here — regardless of whether
            // the Disk Drives window has ever been opened this session, since DisplayWindow
            // subscribes to DiskDrives.GeometryMismatchDetected unconditionally at startup.
            _diskDrives.RebuildIfMachineChanged();
            StatusMessage = "Applied — machine cold-reset.";
        }
        catch (ArgumentException ex)
        {
            // The machine's own assembly-time validation (e.g. an unsupported Board/RamVariant
            // combination, or a floppy-drive config the connector can't carry — Machine.cs)
            // throws rather than silently misbuilding. Surface it here instead of crashing the
            // UI thread with an unhandled exception.
            StatusMessage = $"Could not apply: {ex.Message}";
        }
    }

    // ── Disk-image picking: live delegation vs. offline preview (project CLAUDE.md milestone
    // 14g) ───────────────────────────────────────────────────────────────────────────────────
    // "Mounting media has always been a runtime swap, not topology" (reference doc §3a) — a row
    // backed by an already-existing live drive delegates straight into that drive's OWN mount
    // path; a row with no live drive to mount into (composing a brand-new .cfg, or a board not
    // yet Applied) stays a lightweight, non-blocking preview. Capacity/Sides remain genuine
    // topology either way — only ImagePath picking gets this treatment.

    private DiskDriveVm? FindLiveDrive(int driveIndex) =>
        _diskDrives.Drives.FirstOrDefault(d => d.DriveIndex == driveIndex);

    /// <summary>Entry point for <see cref="FloppyDriveRowVm.BrowseImageAsync"/> after a file is
    /// picked. Live case: reads the bytes and calls straight into the matching
    /// <see cref="DiskDriveVm.MountBytes"/> — the SAME mount path (<c>DskImage.Mount</c>, the same
    /// <c>GeometryMismatchDetected</c> event) the Disk Drives window itself uses; the row's
    /// <see cref="FloppyDriveRowVm.ImagePath"/> is then read back from what's actually mounted,
    /// same as <c>Machine.CaptureCurrentConfig()</c> does. Offline case: just records the path and
    /// runs the preview check.</summary>
    internal async Task PickImageForRowAsync(FloppyDriveRowVm row, IStorageFile file)
    {
        var liveDrive = FindLiveDrive(row.DriveIndex);

        byte[] bytes;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read {file.Name}: {ex.Message}";
            return;
        }

        if (liveDrive is not null)
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            liveDrive.MountBytes(bytes, name, file);
            row.ImagePath = _runner.Machine.Fdc?.GetDisk(row.DriveIndex)?.MountedPath ?? file.Path.LocalPath;
        }
        else
        {
            row.ImagePath = file.Path.LocalPath;
            await PreviewOfflineMismatchAsync(row, bytes);
        }
    }

    /// <summary>Bidirectional recheck (project CLAUDE.md milestone 14g): re-runs the offline
    /// preview against a row's CURRENT <see cref="FloppyDriveRowVm.ImagePath"/> whenever its
    /// Capacity/Sides change. No-op for a row backed by a live drive (nothing to preview — the
    /// image is already mounted for real) or with no path set yet.</summary>
    internal async Task RecheckOfflineMismatchAsync(FloppyDriveRowVm row)
    {
        if (string.IsNullOrWhiteSpace(row.ImagePath)) return;
        if (FindLiveDrive(row.DriveIndex) is not null) return;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(row.ImagePath);
        }
        catch
        {
            return; // unreadable path -> nothing to preview; Apply surfaces real problems later
        }

        await PreviewOfflineMismatchAsync(row, bytes);
    }

    /// <summary>The actual preview check: <see cref="DskImage.DetectMismatch"/> (machine ms.20e)
    /// against the row's currently-set Capacity/Sides, skipping IMD files (self-describing, never
    /// mismatches — <see cref="DskImage.DetectMismatch"/> itself doesn't sniff IMD, so a caller
    /// must). Raises <see cref="OfflineMismatchDetected"/> for a real mismatch, never for
    /// <see cref="DiskGeometryMismatchKind.None"/>.</summary>
    private Task PreviewOfflineMismatchAsync(FloppyDriveRowVm row, byte[] bytes)
    {
        if (DskImage.IsImdFile(bytes)) return Task.CompletedTask;

        var sides = row.Sides == DiskSides.Double ? 2 : 1;
        var mismatch = DskImage.DetectMismatch(bytes, row.Capacity, sides);
        if (mismatch.Kind != DiskGeometryMismatchKind.None)
            OfflineMismatchDetected?.Invoke(row, mismatch);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task BrowseSlot1Async()
    {
        var path = await PickFileAsync("SLOT1 cartridge (.bin / .rom)",
            [new FilePickerFileType("ROM image") { Patterns = ["*.bin", "*.rom"] }]);
        if (path is not null) Slot1CartridgePath = path;
    }

    [RelayCommand]
    private void ClearSlot1() => Slot1CartridgePath = "";

    [RelayCommand]
    private async Task BrowseMonitorRomAsync()
    {
        var path = await PickFileAsync("Monitor ROM override (.bin / .rom)",
            [new FilePickerFileType("ROM image") { Patterns = ["*.bin", "*.rom"] }]);
        if (path is not null) MonitorRomPath = path;
    }

    [RelayCommand]
    private void ClearMonitorRom() => MonitorRomPath = "";

    [RelayCommand]
    private async Task BrowseCassetteAsync()
    {
        var path = await PickFileAsync("Initial cassette (.cas / .p2000t)",
            [new FilePickerFileType("P2000T Cassette") { Patterns = ["*.cas", "*.p2000t"] }]);
        if (path is not null) CassettePath = path;
    }

    [RelayCommand]
    private void ClearCassette() => CassettePath = "";

    [RelayCommand]
    private async Task LoadCfgAsync()
    {
        var path = await PickFileAsync("Load .cfg",
            [new FilePickerFileType("Machine config") { Patterns = ["*.cfg"] }]);
        if (path is null) return;
        try
        {
            var cfg = MachineConfigFile.LoadFromFile(path);
            RamVariant = cfg.RamVariant;
            Board = cfg.Board;
            Slot1CartridgePath = cfg.Slot1CartridgePath ?? "";
            MonitorRomPath = cfg.MonitorRomPath ?? "";
            CassettePath = cfg.CassettePath ?? "";
            EightyColumnBoard = cfg.Modifications.EightyColumnBoard;
            ShowEightyColumnArtifacts = cfg.Modifications.ShowEightyColumnArtifacts;
            LoadFloppyDrivesFrom(cfg.FloppyDrives);
            _ramSeed = cfg.RamSeed;
            _bankCount = cfg.BankCount;
            LastCfgPath = path;
            StatusMessage = $"Loaded {Path.GetFileName(path)} — press Apply to use it.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
    }

    /// <summary>Saves a <c>.cfg</c> capturing what the machine is ACTUALLY running right now —
    /// including whatever disk/cassette is currently live-mounted, via
    /// <see cref="Machine.CaptureCurrentConfig"/> (machine ms.20c) — not just this window's own
    /// bound fields. Closes the 2026-07-26 investigation's confirmed gap (§18): previously this
    /// only ever serialized <see cref="BuildConfig"/>, which always saved a null/empty
    /// <c>ImagePath</c>/<c>CassettePath</c> regardless of what was actually mounted.</summary>
    [RelayCommand]
    private async Task SaveCfgAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save .cfg",
            SuggestedFileName = "machine.cfg",
            DefaultExtension = "cfg",
            FileTypeChoices = [new FilePickerFileType("Machine config") { Patterns = ["*.cfg"] }],
        });
        if (file is null) return;

        try
        {
            var path = file.Path.LocalPath;
            MachineConfigFile.SaveToFile(_runner.Machine.CaptureCurrentConfig(), path);
            LastCfgPath = path;
            StatusMessage = $"Saved to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    // ── Startup pinning (project CLAUDE.md milestone 14c) ───────────────────────

    /// <summary>"Always start with this configuration": pins <see cref="LastCfgPath"/> — the
    /// last file THIS window explicitly loaded or saved — as the startup default, so auto-remember
    /// stops overwriting it until <see cref="UnpinStartupConfig"/>. Always enabled, not gated on
    /// having saved/loaded a `.cfg` first (owner report, 2026-07-27: a machine fresh off Apply has
    /// no `LastCfgPath` yet, and a permanently-ghosted button with no explanation was
    /// counter-intuitive) — when nothing's been saved yet, OR when the live machine no longer
    /// matches what's actually stored at <see cref="LastCfgPath"/> (owner follow-up, 2026-07-27:
    /// load a `.cfg`, tweak a field, Apply — `LastCfgPath` is still set, but the file on disk is
    /// now stale), this prompts the SAME Save `.cfg` dialog <see cref="SaveCfgAsync"/> uses, then
    /// pins whatever the user just saved. Cancelling that dialog leaves nothing pinned, same as
    /// cancelling any other save.</summary>
    [RelayCommand]
    private async Task PinAsStartupConfigAsync()
    {
        if (LastCfgPath is null || !SavedCfgMatchesLiveConfig(LastCfgPath))
        {
            await SaveCfgAsync();
            // Re-check rather than just "is LastCfgPath null" — LastCfgPath may already have
            // been non-null (a STALE path) going in, so a cancelled/failed save would otherwise
            // fall through and pin the very stale file this check exists to catch. Only a save
            // that actually landed leaves the file matching live content.
            if (LastCfgPath is null || !SavedCfgMatchesLiveConfig(LastCfgPath)) return;
        }

        var prefs = AppPreferencesFile.Load();
        prefs.StartupCfgPath = LastCfgPath;
        prefs.StartupCfgIsPinned = true;
        AppPreferencesFile.Save(prefs);
        IsStartupPinned = true;
        StatusMessage = $"Pinned {Path.GetFileName(LastCfgPath)} as the startup configuration.";
    }

    /// <summary>True when the file at <paramref name="path"/> already holds byte-for-byte what
    /// <see cref="Machine.CaptureCurrentConfig"/> would produce right now — i.e. pinning it needs
    /// no re-save. False (the safe default, triggering a re-save prompt) on any read/parse
    /// problem, or when they genuinely differ — the case this exists for (project CLAUDE.md
    /// milestone 14c follow-up, 2026-07-27): fields tweaked and Applied since the last Load/Save
    /// mean the running machine has moved on from whatever's still sitting in that file.</summary>
    private bool SavedCfgMatchesLiveConfig(string path)
    {
        try
        {
            var onDisk = File.ReadAllText(path);
            var live = MachineConfigFile.Serialize(_runner.Machine.CaptureCurrentConfig());
            return onDisk == live;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void UnpinStartupConfig()
    {
        var prefs = AppPreferencesFile.Load();
        prefs.StartupCfgIsPinned = false;
        AppPreferencesFile.Save(prefs);
        IsStartupPinned = false;
        StatusMessage = "Unpinned — the app will remember your last session again on quit.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MachineConfig BuildConfig() => new()
    {
        Model = MachineModel.P2000T,
        RamVariant = RamVariant,
        Board = Board,
        BankCount = _bankCount,
        Slot1CartridgePath = NullIfEmpty(Slot1CartridgePath),
        MonitorRomPath = NullIfEmpty(MonitorRomPath),
        CassettePath = NullIfEmpty(CassettePath),
        FloppyDrives = FloppyDriveRows.Select(r => r.ToConfig()).ToList(),
        RamSeed = _ramSeed,
        Modifications = new ModificationsConfig
        {
            EightyColumnBoard = EightyColumnBoard,
            ShowEightyColumnArtifacts = ShowEightyColumnArtifacts,
        },
    };

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Internal (not private) so <see cref="FloppyDriveRowVm"/>'s own browse command can
    /// reuse the same file-dialog plumbing rather than duplicating it.</summary>
    internal static async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType> types)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    /// <summary>Same file-dialog plumbing as <see cref="PickFileAsync"/>, but returns the
    /// <see cref="IStorageFile"/> itself rather than just its path — needed for a disk-image
    /// pick (project CLAUDE.md milestone 14g) so the LIVE case can pass it straight into
    /// <see cref="DiskDriveVm.MountBytes"/> (which stamps <c>DskImage.MountedPath</c> from it),
    /// exactly like a live mount via the Disk Drives window already does.</summary>
    internal static async Task<IStorageFile?> PickStorageFileAsync(string title, IReadOnlyList<FilePickerFileType> types)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0] : null;
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        is { } w ? Avalonia.Controls.TopLevel.GetTopLevel(w) : null;
}

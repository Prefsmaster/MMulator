using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
using P2000.UI.Runner;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for one row of the Disk Drives window (project CLAUDE.md §14 milestone
/// 14) — the disk analogue of <see cref="CassetteDeckVm"/>, scoped to a single, fixed
/// <see cref="DriveIndex"/>. Mount/Eject/New/Save are host-side container operations on
/// <c>Upd765</c>/<c>DskImage</c> (machine milestone 20), same category as the cassette's own
/// mount/eject/new/save (§3.1) — always fast, independent of <c>TimingPolicy</c>. Drive
/// presence and geometry (<see cref="Capacity"/>/<see cref="Sides"/>) are TOPOLOGY, fixed at
/// construction from the machine's config — only the mounted IMAGE is a live/runtime concern
/// this VM mutates.</summary>
public sealed partial class DiskDriveVm : ObservableObject
{
    private readonly EmulationRunner _runner;

    public int DriveIndex { get; }

    /// <summary>Geometry seed for "New (blank) disk" — normally topology, fixed at construction
    /// from the Config window (machine M19/M20); the ONE exception is
    /// <see cref="ReconfigureAndRemount"/> (project CLAUDE.md milestone 14e), a deliberate,
    /// user-initiated override in response to a real geometry mismatch — "let the user decide,
    /// don't guess for them" (reference doc §5d). A mounted image's own on-disk label still wins
    /// over this when it validates (machine ms.20d).</summary>
    public int Capacity { get; private set; }
    public DiskSides Sides { get; private set; }

    /// <summary>The file this disk was loaded from or last saved to; null when the mounted
    /// image is unbacked (fresh off "New (blank) disk"). Drives "Save" vs "Save as…", same
    /// pattern as <see cref="CassetteDeckVm"/>.</summary>
    private IStorageFile? _backingFile;

    [ObservableProperty] private bool _hasImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeader))]
    private string _imageLabel = "No disk";

    [ObservableProperty] private bool _isWriteProtected;

    /// <summary>True when the mounted image has unsaved changes (machine milestone 20a
    /// <c>DskImage.IsDirty</c>) — surfaced in the tab header (project CLAUDE.md §14 "DRIVE
    /// TABS" decision, 2026-07-23) so the user can tell which drives have pending changes
    /// without opening each tab. Also gates the eject/replace unsaved-changes warning
    /// (milestone 14a, via <see cref="ConfirmDiscardRequested"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabHeader))]
    private bool _isDirty;

    /// <summary>Tab header text: drive index + image label + a dirty asterisk (project
    /// CLAUDE.md §14, 2026-07-23 "DRIVE TABS" decision — "enough per-drive summary that the
    /// user can tell tabs apart without opening each one").</summary>
    public string TabHeader => $"{DriveIndex}: {ImageLabel}{(IsDirty ? " *" : "")}";

    /// <summary>The board's single shared MOTORON line (project CLAUDE.md §13.20 — NOT
    /// per-drive real hardware) — every configured drive's row reads the SAME value; this is
    /// not an independent per-drive signal, it just happens to be exposed per-row for layout
    /// consistency (see the milestone's own explicit warning against implying otherwise).</summary>
    [ObservableProperty] private bool _isMotorOn;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _directionText = "—";
    [ObservableProperty] private string _cylinderText = "—";

    /// <summary>Head/sector while THIS drive has an active READ/WRITE DATA transfer; "–"
    /// otherwise (project CLAUDE.md §17, 2026-07-23 owner decision: neither is a real
    /// persistent register on idle hardware — show the real value only while something is
    /// actually happening, not a stale/guessed one).</summary>
    [ObservableProperty] private string _headText = "—";
    [ObservableProperty] private string _sectorText = "—";

    /// <summary>The mounted disk's ACTUAL current geometry (project CLAUDE.md §14 milestone 19)
    /// — "40 Tracks, DS" — recomputed by <see cref="RecomputeTopologyText"/> at every point this
    /// VM itself changes what's mounted, straight from the live <see cref="DskImage"/>'s own
    /// <see cref="DskImage.Tracks"/>/<see cref="DskImage.Sides"/>, NOT from this VM's own
    /// <see cref="Capacity"/>/<see cref="Sides"/> fields. Deliberately not the same source:
    /// <see cref="Capacity"/>/<see cref="Sides"/> is the drive's CONFIGURED geometry, passed to
    /// <see cref="DskImage.Mount"/> only as the fallback a mount uses when the file has no valid
    /// on-disk label — a validated JWSDOS label can legitimately win and mount at a DIFFERENT
    /// geometry with no mismatch ever raised (machine ms.20d), which would make a display sourced
    /// from <see cref="Capacity"/>/<see cref="Sides"/> silently wrong in exactly that case.
    /// Reading the live <c>DskImage</c> itself (the same source <c>Machine.CaptureCurrentConfig()</c>
    /// and the Config window's item-18 fix both already use) is the only way this can never drift
    /// from what's actually mounted. Empty string when no disk is mounted — "no topology shown for
    /// an empty drive" (the milestone's own explicit requirement).</summary>
    [ObservableProperty] private string _topologyText = "";

    [ObservableProperty] private IReadOnlyList<string> _programs = [];

    /// <summary>Short explanatory message shown INSTEAD of the directory table (project CLAUDE.md
    /// §14 milestone 15b; machine ms.22b) for a <see cref="DiskDirectoryFormat.PdosSystem"/> or
    /// <see cref="DiskDirectoryFormat.Unknown"/> mount — empty for every other format, which is
    /// what <see cref="HasDirectoryMessage"/> gates the view's fallback-vs-table visibility on.
    /// Never shown alongside an (empty) table — <see cref="RefreshDirectoryTable"/> always clears
    /// <see cref="Programs"/> to <c>[]</c> in the same branch that sets this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirectoryMessage))]
    private string _directoryMessage = "";

    public bool HasDirectoryMessage => DirectoryMessage.Length > 0;

    /// <summary>Hex+ASCII dump of sector 1 (16 rows × 16 bytes, machine ms.22b's
    /// <c>DskImage.ReadSector</c>) — populated only alongside <see cref="DirectoryMessage"/>, for
    /// the same two fallback formats. Row format matches this codebase's existing hex-dump
    /// convention (the debugger's memory watch window, `P2000.UI` CLAUDE.md §10): offset, 16
    /// space-separated 2-digit uppercase hex bytes, then the ASCII rendering (printable bytes
    /// verbatim, <c>.</c> for anything else).</summary>
    [ObservableProperty] private IReadOnlyList<string> _sectorDump = [];

    /// <summary>Column header for <see cref="Programs"/> — varies with the mounted disk's
    /// detected directory format (project CLAUDE.md §14 milestone 15; machine ms.22's
    /// <c>DetectDirectoryFormat</c>): a JWSDOS mount gets two extra columns (Side, Track/Sector),
    /// every other format (including no disk mounted) keeps milestone 14's original three-column
    /// header unchanged. Instance state, not the milestone-14 static constant it replaces — the
    /// header must be able to change per-mount.</summary>
    [ObservableProperty] private string _directoryHeader = LegacyDirectoryHeader;

    private static readonly string LegacyDirectoryHeader = $"{"Filename",-16} {"Ty",-2} {"Size",8}";
    private static readonly string JwsdosDirectoryHeader =
        $"{"Filename",-16} {"Ty",-2} {"Size",8}  {"Side",-6}  {"Track/Sector"}";

    /// <summary>PDOS has no file-type byte (unlike JWSDOS's <c>DE_filetype</c>) and no double-
    /// sided concept at all (project CLAUDE.md §14 milestone 15a; docs/P2000T-disk-formats.md
    /// §6a's hard geometry ceiling rules out anything wider than single-sided) — no "Ty" or
    /// "Side" column here, matching the JWSDOS header's overall shell otherwise.</summary>
    private static readonly string PdosDirectoryHeader =
        $"{"Filename",-16} {"Size",8}  {"Track/Sector"}";

    /// <summary>Raised when a save error should be surfaced as a dialog.</summary>
    public event Action<string>? ShowMessageRequested;

    /// <summary>Raised before an eject/replace that would discard unsaved changes (project
    /// CLAUDE.md §14 milestone 14a — wired to <c>DskImage.IsDirty</c>, machine ms.20a). The
    /// view must show a Discard/Cancel dialog and resolve true to proceed, false to cancel. No
    /// subscriber (e.g. headless tests) means "proceed" — the machine-layer flag, not this
    /// event, is what actually gates the warning.</summary>
    public event Func<string, Task<bool>>? ConfirmDiscardRequested;

    /// <summary>Raised whenever a mount (live, via <see cref="MountBytes"/>, or a `.cfg`-authored
    /// construction-time one surfaced via <see cref="RaisePendingMismatchIfAny"/>) produces a
    /// real geometry mismatch (project CLAUDE.md milestone 14e; machine ms.20d). Never raised for
    /// <see cref="DiskGeometryMismatchKind.None"/> — the common case stays silent. The image is
    /// ALREADY mounted by the time this fires (mounting never blocks); the view shows the
    /// appropriate dialog shape and calls back into <see cref="ReconfigureAndRemount"/>,
    /// <see cref="ContinueWithCurrentMount"/>, <see cref="ExtendMountedDiskToFullSize"/>, or
    /// <see cref="CancelMount"/> based on the user's choice.</summary>
    public event Action<DiskGeometryMismatch>? GeometryMismatchDetected;

    /// <summary>A `.cfg`-authored mismatch captured at construction time (machine ms.20d's
    /// per-drive <c>Upd765.GetMismatch</c>) — nothing could show a dialog at machine-assembly
    /// time, so this is surfaced later via <see cref="RaisePendingMismatchIfAny"/>, once the
    /// window (and therefore a dialog) actually exists. <c>null</c> when there's nothing pending
    /// (the common case, or once already raised/handled).</summary>
    public DiskGeometryMismatch? PendingMismatch { get; private set; }

    /// <summary>Raised by <see cref="SaveAsAsync"/> to ask which container format to save as
    /// (project CLAUDE.md milestone 14f) — the offered choices are always IMD and `.dsk`, but
    /// their wording/lossy-export framing differs by <paramref name="currentFormat"/>'s value
    /// (a `.dsk`-backed drive offers "Save as IMD"/"Save as `.dsk`"; an IMD-backed drive offers
    /// "Save as IMD"/"Save as plain `.dsk`" with an explicit lossy-order warning, since any
    /// recorded sector order collapses to plain logical order in the exported file). The view
    /// resolves the returned task with the chosen format, or <c>null</c> if the user cancels the
    /// format choice itself (distinct from cancelling the subsequent file-save dialog, which
    /// <see cref="SaveAsAsync"/> handles separately). No subscriber (headless/tests) keeps the
    /// CURRENT format — same "no subscriber, proceed" shape as
    /// <see cref="ConfirmDiscardRequested"/>.</summary>
    public event Func<DiskImageFormat, Task<DiskImageFormat?>>? SaveAsFormatRequested;

    public DiskDriveVm(EmulationRunner runner, int driveIndex, int capacity, DiskSides sides)
    {
        _runner = runner;
        DriveIndex = driveIndex;
        Capacity = capacity;
        Sides = sides;
        runner.FrameReady += OnFrameReady;
        RefreshFromMachine();

        // Sync image state from an already-mounted disk (owner-reported bug, 2026-07-28):
        // MachineConfig.FloppyDrives[i].ImagePath is mounted directly onto Upd765 at Machine
        // construction (Machine.cs), bypassing MountBytes entirely — without this, a drive that
        // booted with an image already in it (the "plug everything in and flip the switch"
        // startup case) showed as "No disk" in this window even though RefreshFromMachine's
        // IsDirty read (a couple of lines up) already reflects the real mounted disk correctly,
        // producing the reported symptom: an asterisk with no filename behind it.
        var disk = runner.Machine.Fdc?.GetDisk(driveIndex);
        if (disk is not null)
        {
            HasImage = true;
            ImageLabel = disk.MountedPath is { } path ? Path.GetFileNameWithoutExtension(path) : "(mounted)";
            IsWriteProtected = disk.WriteProtected;
            RefreshDirectoryTable(disk);
        }
        RecomputeTopologyText();

        var mismatch = runner.Machine.Fdc?.GetMismatch(driveIndex);
        PendingMismatch = mismatch is { Kind: not DiskGeometryMismatchKind.None } ? mismatch : null;
    }

    /// <summary>Raises <see cref="GeometryMismatchDetected"/> for whatever
    /// <see cref="PendingMismatch"/> was captured at construction. Call this AFTER subscribing to
    /// the event — mirroring how <c>DiskDriveWindowVm</c> already subscribes to this VM's other
    /// events right after constructing it — otherwise a mismatch raised from inside the
    /// constructor itself would fire before anyone could possibly be listening (project CLAUDE.md
    /// milestone 14e). No-op (and clears <see cref="PendingMismatch"/>) once already called.</summary>
    public void RaisePendingMismatchIfAny()
    {
        if (PendingMismatch is not { } mismatch) return;
        PendingMismatch = null;
        GeometryMismatchDetected?.Invoke(mismatch);
    }

    private int SidesCount => Sides == DiskSides.Double ? 2 : 1;

    /// <summary>Recomputes <see cref="TopologyText"/> from the live mounted <see cref="DskImage"/>
    /// (or blanks it when nothing is mounted). Deliberately called explicitly, at every point
    /// this VM itself changes what's mounted (construction, <see cref="MountBytes"/>,
    /// <see cref="ReconfigureAndRemount"/>, <see cref="ReturnToEmptyState"/>,
    /// <see cref="NewBlankDiskAsync"/>) — NOT from the 50 Hz <see cref="RefreshFromMachine"/> frame
    /// poll. A mounted disk's geometry only ever changes at one of those explicit mutation points
    /// in this build (nothing external re-geometries a drive mid-frame), so polling it every field
    /// would just be wasted work for a value that's usually unchanged frame-to-frame.</summary>
    private void RecomputeTopologyText()
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        TopologyText = disk is not null ? $"{disk.Tracks} Tracks, {(disk.Sides == 2 ? "DS" : "SS")}" : "";
    }

    // ── Frame callback ────────────────────────────────────────────────────────────────────────

    private void OnFrameReady(uint[] _, bool __, bool[] ___) => RefreshFromMachine();

    private void RefreshFromMachine()
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null)
        {
            IsMotorOn = false;
            IsActive = false;
            DirectionText = "—";
            CylinderText = "—";
            HeadText = "—";
            SectorText = "—";
            IsDirty = false;
            return;
        }

        IsMotorOn = fdc.MotorOn;
        CylinderText = fdc.GetCylinder(DriveIndex).ToString();
        IsDirty = fdc.GetDisk(DriveIndex)?.IsDirty ?? false;

        var transfer = fdc.CurrentTransfer;
        IsActive = transfer is { } t && t.Drive == DriveIndex;
        DirectionText = IsActive ? (transfer!.Value.IsWrite ? "Write" : "Read") : "—";
        HeadText = IsActive ? transfer!.Value.Head.ToString() : "—";
        SectorText = IsActive ? transfer!.Value.Sector.ToString() : "—";
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task MountAsync()
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Mount disk in drive {DriveIndex}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Disk image") { Patterns = ["*.dsk", "*.img", "*.imd"] },
                new FilePickerFileType("IMD (ImageDisk)") { Patterns = ["*.imd"] },
                new FilePickerFileType("P2000T Disk (.dsk)") { Patterns = ["*.dsk", "*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var name = Path.GetFileNameWithoutExtension(files[0].Name);

        await TryMountBytesAsync(ms.ToArray(), name, files[0]);
    }

    /// <summary>Gate for eject/replace (§14 milestone 14a): true immediately when the mounted
    /// image isn't dirty (the common case — no added friction) or when no view has subscribed
    /// (headless/tests); otherwise defers to <see cref="ConfirmDiscardRequested"/> and returns
    /// its Discard(true)/Cancel(false) answer.</summary>
    private Task<bool> ConfirmDiscardAsync(string action)
    {
        var dirty = _runner.Machine.Fdc?.GetDisk(DriveIndex)?.IsDirty ?? false;
        if (!dirty || ConfirmDiscardRequested is null)
            return Task.FromResult(true);
        return ConfirmDiscardRequested($"Drive {DriveIndex}'s disk has unsaved changes — {action} anyway?");
    }

    /// <summary>Gated entry point for a user-initiated mount (file dialog or drag-drop) that
    /// may replace an already-mounted, dirty image (§14 milestone 14a). Runs the same
    /// discard-confirmation as <see cref="EjectAsync"/>/<see cref="NewBlankDiskAsync"/> before
    /// calling <see cref="MountBytes"/>; returns false without mounting if the user cancels.
    /// Called by the drag-drop handler in <see cref="Views.DiskDriveWindow"/>.</summary>
    public async Task<bool> TryMountBytesAsync(byte[] diskImage, string filename, IStorageFile? backingFile = null)
    {
        if (!await ConfirmDiscardAsync("replace")) return false;
        MountBytes(diskImage, filename, backingFile);
        return true;
    }

    /// <summary>Mounts a <c>.dsk</c> image at runtime (host-side, always fast — machine M19/
    /// M20), unconditionally — no discard-confirmation. <paramref name="backingFile"/> becomes
    /// this drive's Save target; pass null for an unbacked mount. User-facing mount paths
    /// should go through <see cref="TryMountBytesAsync"/> instead so a dirty image isn't
    /// silently discarded.
    ///
    /// <b>Goes through <see cref="DskImage.Mount"/>, not the raw constructor</b> (project
    /// CLAUDE.md milestone 14e; machine ms.20d) — validates the on-disk JWSDOS label against the
    /// file's actual length rather than trusting it blind, falling back to this drive's
    /// configured <see cref="Capacity"/>/<see cref="Sides"/>. Never fails to mount (no more
    /// "not a valid disk image" rejection for a too-short file — it mounts anyway, using the
    /// configured geometry, and reports a mismatch instead via
    /// <see cref="GeometryMismatchDetected"/> if one exists).</summary>
    public void MountBytes(byte[] diskImage, string filename, IStorageFile? backingFile = null)
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        var (disk, mismatch) = DskImage.Mount(diskImage, Capacity, SidesCount);

        // MountedPath (project CLAUDE.md milestone 14c) must be stamped here — Mount's
        // object-initializer construction path doesn't go through DskImage(string), so nothing
        // sets it automatically; a live UI mount reads bytes itself (file dialog / drag-drop)
        // and passes them in, so the real path is only known here. Without this,
        // Machine.CaptureCurrentConfig() (and therefore auto-remember/Save .cfg) would silently
        // see this drive as unmounted.
        disk.MountedPath = backingFile?.Path.LocalPath;

        fdc.MountDisk(DriveIndex, disk, mismatch);
        _backingFile = backingFile;
        HasImage = true;
        IsWriteProtected = disk.WriteProtected;
        ImageLabel = filename;
        RefreshDirectoryTable(disk);
        RecomputeTopologyText();
        NotifyCommands();

        if (mismatch.Kind != DiskGeometryMismatchKind.None)
            GeometryMismatchDetected?.Invoke(mismatch);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task EjectAsync()
    {
        if (!await ConfirmDiscardAsync("eject")) return;
        ReturnToEmptyState();
    }

    /// <summary>Shared by <see cref="EjectAsync"/> and <see cref="CancelMount"/> — both leave the
    /// drive genuinely empty; they differ only in WHETHER the unsaved-changes gate runs first
    /// (Cancel is undoing a mount the user just made, not discarding unrelated prior work, so it
    /// skips <see cref="ConfirmDiscardAsync"/>).</summary>
    private void ReturnToEmptyState()
    {
        _runner.Machine.Fdc?.EjectDisk(DriveIndex);
        _backingFile = null;
        HasImage = false;
        ImageLabel = "No disk";
        IsWriteProtected = false;
        ClearDirectoryTable();
        RecomputeTopologyText();
        NotifyCommands();
    }

    // ── Geometry-mismatch recovery (project CLAUDE.md milestone 14e) ────────────────────────

    /// <summary>"Reconfigure the drive to the matching geometry and remount" — one of the
    /// candidate-mismatch dialog's options (owner's own requested resolution: let the user
    /// decide, don't guess). Re-mounts the CURRENTLY-mounted image's bytes under the new
    /// geometry (which the caller already confirmed matches the file's actual length via
    /// <see cref="DiskGeometryMismatch.Candidates"/>), so this should always resolve to
    /// <see cref="DiskGeometryMismatchKind.None"/> — but stays honest and re-raises
    /// <see cref="GeometryMismatchDetected"/> if it somehow doesn't. Updates
    /// <see cref="Capacity"/>/<see cref="Sides"/> to the new geometry going forward (e.g. for a
    /// later "New (blank) disk").</summary>
    public void ReconfigureAndRemount(int tracks, DiskSides sides)
    {
        var fdc = _runner.Machine.Fdc;
        var current = fdc?.GetDisk(DriveIndex);
        if (fdc is null || current is null) return;

        var bytes = current.GetBytes();
        var wasProtected = current.WriteProtected;
        var mountedPath = current.MountedPath;

        Capacity = tracks;
        Sides = sides;

        var (disk, mismatch) = DskImage.Mount(bytes, tracks, sides == DiskSides.Double ? 2 : 1);
        disk.MountedPath = mountedPath;
        disk.WriteProtected = wasProtected;
        fdc.MountDisk(DriveIndex, disk, mismatch);

        IsWriteProtected = disk.WriteProtected;
        RefreshDirectoryTable(disk);
        RecomputeTopologyText();

        if (mismatch.Kind != DiskGeometryMismatchKind.None)
            GeometryMismatchDetected?.Invoke(mismatch);
    }

    /// <summary>"Continue mounting with the current configuration anyway" / "continue as-is" —
    /// the safe no-op choice on either mismatch dialog shape (project CLAUDE.md milestone 14e
    /// test (f)). The image is ALREADY mounted (mounting never blocks), so this changes nothing;
    /// the mismatch stays on record for the session (<c>Upd765.GetMismatch</c> keeps reporting
    /// it) rather than being silently cleared, so a persistent status indicator could still
    /// reflect it if the view chooses to show one.</summary>
    public void ContinueWithCurrentMount() { /* intentionally a no-op — see doc comment */ }

    /// <summary>"Extend to full size" — the no-candidate-mismatch dialog's recovery option.
    /// Pads the in-memory image up to <see cref="DiskGeometryMismatch.ExpectedLength"/> (machine
    /// ms.20d's <c>DskImage.ExtendTo</c>, <c>0x00</c> fill — honestly, this fills blank space, it
    /// does not recover missing data) and clears the mismatch by re-recording it as
    /// <see cref="DiskGeometryMismatchKind.None"/> at its new, now-matching length. No-op if
    /// nothing is mounted.</summary>
    public void ExtendMountedDiskToFullSize(int expectedLength)
    {
        var fdc = _runner.Machine.Fdc;
        var disk = fdc?.GetDisk(DriveIndex);
        if (fdc is null || disk is null) return;

        disk.ExtendTo(expectedLength);
        fdc.MountDisk(DriveIndex, disk, DiskGeometryMismatch.None(expectedLength));
    }

    /// <summary>"Cancel" on either mismatch dialog shape — the one path that does NOT end in
    /// "mounted" (project CLAUDE.md milestone 14e: "every path... ends in a mounted drive (or a
    /// cancelled mount if the user explicitly chooses Cancel)"). The mount always happens
    /// immediately and optimistically; Cancel is a deliberate, explicit undo of THAT mount, not a
    /// block — so it skips the unsaved-changes gate <see cref="EjectAsync"/> uses (there's
    /// nothing of the user's to lose; they just mounted this moments ago).</summary>
    public void CancelMount() => ReturnToEmptyState();

    /// <summary>"New (blank) disk": creates a genuinely unformatted in-memory image sized to
    /// this drive's own configured <see cref="Capacity"/>/<see cref="Sides"/> (no label, no
    /// directory — machine M20) and mounts it live. No format step affordance — a guest DOS
    /// still has to format it via its own routine before it's usable, same as a real blank
    /// floppy.</summary>
    [RelayCommand]
    private async Task NewBlankDiskAsync()
    {
        if (!await ConfirmDiscardAsync("replace")) return;

        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        var disk = DskImage.CreateBlank(Capacity, SidesCount);
        fdc.MountDisk(DriveIndex, disk);
        _backingFile = null;
        HasImage = true;
        IsWriteProtected = false;
        ImageLabel = "(blank disk)";
        RefreshDirectoryTable(disk); // no directory yet — an unformatted blank disk
        RecomputeTopologyText();
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleWriteProtect() => IsWriteProtected = !IsWriteProtected;

    /// <summary>Plain "Save" — NEVER changes format (project CLAUDE.md milestone 14f): writes
    /// back in place in whatever format currently backs the drive (<c>DskImage.Format</c>), no
    /// prompt, same as this command already behaved before IMD existed. Only
    /// <see cref="SaveAsAsync"/> can change format.</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsync()
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        if (disk is null) return;

        if (_backingFile is not null)
        {
            await WriteDiskToFileAsync(_backingFile, disk.Format);
            return;
        }

        // A disk mounted via MachineConfig.FloppyDrives[i].ImagePath at machine construction (or
        // reconfigured/remounted since) never went through an IStorageFile-based mount, so
        // _backingFile stays null even though it genuinely has a path on disk — write straight to
        // that path instead of falling through to a needless Save-As prompt (owner-reported bug,
        // 2026-07-28, same root cause as the constructor sync above).
        if (disk.MountedPath is { } path)
        {
            await WriteDiskToPathAsync(path, disk.Format);
            return;
        }

        await SaveAsAsync();
    }

    /// <summary>"Save As" — the ONLY path that can change format, and it always asks for a name
    /// and destination, never a silent conversion (project CLAUDE.md milestone 14f). First asks
    /// which format to save as via <see cref="SaveAsFormatRequested"/> (the view offers "Save as
    /// IMD" plus either "Save as `.dsk`" or "Save as plain `.dsk`" depending on the drive's
    /// CURRENT format), then the usual native save-file dialog, then writes and updates the
    /// drive's tracked format AND path going forward.</summary>
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsAsync()
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        if (disk is null) return;

        var chosenFormat = SaveAsFormatRequested is null ? disk.Format : await SaveAsFormatRequested(disk.Format);
        if (chosenFormat is null) return; // user cancelled the format choice

        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var ext = chosenFormat == DiskImageFormat.Imd ? "imd" : "dsk";
        var typeName = chosenFormat == DiskImageFormat.Imd ? "IMD (ImageDisk)" : "P2000T Disk (.dsk)";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save Disk (drive {DriveIndex}) As",
            SuggestedFileName = $"{SuggestedFileNameStem()}.{ext}",
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{ext}"] }],
            DefaultExtension = ext,
        });
        if (file is null) return;

        if (!await WriteDiskToFileAsync(file, chosenFormat.Value)) return;

        _backingFile = file;
        ImageLabel = Path.GetFileNameWithoutExtension(file.Name);
        // The image is now backed by this new file (and possibly a new format) — update
        // MountedPath the same reason MountBytes does (project CLAUDE.md milestone 14c), and
        // Format so a later plain Save keeps writing whatever format was just chosen here.
        disk.MountedPath = file.Path.LocalPath;
        disk.Format = chosenFormat.Value;
    }

    private string SuggestedFileNameStem()
    {
        if (_backingFile is not null) return Path.GetFileNameWithoutExtension(_backingFile.Name);
        var mountedPath = _runner.Machine.Fdc?.GetDisk(DriveIndex)?.MountedPath;
        return mountedPath is { } path ? Path.GetFileNameWithoutExtension(path) : "disk";
    }

    /// <summary>Writes the mounted image to <paramref name="file"/> in the given
    /// <paramref name="format"/> (<c>DskImage.GetBytes</c> for a raw `.dsk`, <c>GetImdBytes</c>
    /// for IMD — project CLAUDE.md milestone 21/14f), then marks the image clean (project
    /// CLAUDE.md §13.20a dirty-tracking signal). Returns false (and surfaces a message) on
    /// failure or when no image is mounted.</summary>
    private async Task<bool> WriteDiskToFileAsync(IStorageFile file, DiskImageFormat format)
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        if (disk is null)
        {
            ShowMessageRequested?.Invoke("No disk mounted — nothing to save.");
            return false;
        }

        try
        {
            var bytes = format == DiskImageFormat.Imd ? disk.GetImdBytes() : disk.GetBytes();
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            disk.MarkClean();
            return true;
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Save failed:\n{ex.Message}");
            return false;
        }
    }

    /// <summary>Same as <see cref="WriteDiskToFileAsync"/> but for a disk that has a
    /// <see cref="DskImage.MountedPath"/> without ever having gone through an
    /// <see cref="IStorageFile"/>-based mount (a `.cfg`-authored <c>FloppyDrives[i].ImagePath</c>
    /// mounted directly at machine construction — <see cref="_backingFile"/> stays null in that
    /// case). Writes straight to the raw path via <see cref="File"/> instead.</summary>
    private async Task<bool> WriteDiskToPathAsync(string path, DiskImageFormat format)
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        if (disk is null)
        {
            ShowMessageRequested?.Invoke("No disk mounted — nothing to save.");
            return false;
        }

        try
        {
            var bytes = format == DiskImageFormat.Imd ? disk.GetImdBytes() : disk.GetBytes();
            await File.WriteAllBytesAsync(path, bytes);
            disk.MarkClean();
            return true;
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Save failed:\n{ex.Message}");
            return false;
        }
    }

    // ── CommunityToolkit hooks ───────────────────────────────────────────────────────────────

    partial void OnHasImageChanged(bool value) => NotifyCommands();

    /// <summary>Pushes a write-protect change to the live mounted image. Guarded by
    /// <see cref="HasImage"/> so eject/init transitions never touch a null disk (harmless
    /// either way, matches <see cref="CassetteDeckVm"/>'s identical guard).</summary>
    partial void OnIsWriteProtectedChanged(bool value)
    {
        if (HasImage)
        {
            var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
            if (disk is not null) disk.WriteProtected = value;
        }
    }

    private void NotifyCommands()
    {
        EjectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        ToggleWriteProtectCommand.NotifyCanExecuteChanged();
    }

    // ── Directory formatting (project CLAUDE.md §14 milestone 15) ──────────────────────────────

    /// <summary>Resets <see cref="Programs"/>/<see cref="DirectoryHeader"/>/<see cref="DirectoryMessage"/>/
    /// <see cref="SectorDump"/> to the empty, no-disk-mounted state — shared by eject/cancel-mount,
    /// the one place that used to just set <c>Programs = []</c> directly.</summary>
    private void ClearDirectoryTable()
    {
        Programs = [];
        DirectoryHeader = LegacyDirectoryHeader;
        DirectoryMessage = "";
        SectorDump = [];
    }

    /// <summary>Dispatches on <see cref="DskImage.DetectDirectoryFormat"/> (machine milestones
    /// 22/22a/22b) and (re)builds whichever of <see cref="Programs"/>/<see cref="DirectoryHeader"/>
    /// or <see cref="DirectoryMessage"/>/<see cref="SectorDump"/> applies for the mounted
    /// <paramref name="disk"/> — exhaustive over all four <see cref="DiskDirectoryFormat"/> values,
    /// exactly one branch ever wins:
    /// <list type="bullet">
    /// <item><see cref="DiskDirectoryFormat.PdosSystem"/>/<see cref="DiskDirectoryFormat.Unknown"/>
    /// (milestone 15b) — replace the table entirely with a short message plus a hex/ascii dump of
    /// sector 1; <see cref="Programs"/> stays empty so the table never shows alongside it.</item>
    /// <item><see cref="DiskDirectoryFormat.PdosWorking"/> (milestone 15a) — PDOS's own shell:
    /// filename, size, track/sector, no Side column (PDOS has no double-sided concept).</item>
    /// <item><see cref="DiskDirectoryFormat.Jwsdos"/> (milestone 15) — milestone 14's original
    /// three columns plus Side/Track-Sector.</item>
    /// </list>
    /// The format-detection/parse logic itself lives entirely in <c>DskImage</c> — nothing here
    /// re-derives it.</summary>
    private void RefreshDirectoryTable(DskImage disk)
    {
        var format = disk.DetectDirectoryFormat();

        if (format is DiskDirectoryFormat.PdosSystem or DiskDirectoryFormat.Unknown)
        {
            // Unknown splits into two sub-cases (project CLAUDE.md §14 milestone 16; machine
            // ms.23's DskImage.IsDirectoryRegionBlank): a genuinely blank/freshly-formatted disk
            // (all-zero at both formats' directory regions) gets a distinct, friendlier message
            // than real unrecognized garbage — the sector-1 dump alongside it is itself
            // informative either way (confirms "blank," not "corrupt").
            DirectoryMessage = format == DiskDirectoryFormat.PdosSystem
                ? "PDOS system disk — no file directory"
                : disk.IsDirectoryRegionBlank()
                    ? "Clean disk — no data written yet"
                    : "Unknown disk contents/structure";
            SectorDump = FormatSectorDump(disk.ReadSector(0, 0, 1));
            Programs = [];
            DirectoryHeader = LegacyDirectoryHeader;
            return;
        }

        DirectoryMessage = "";
        SectorDump = [];

        if (format == DiskDirectoryFormat.PdosWorking)
        {
            var pdosEntries = disk.ReadPdosDirectory();
            var pdosRows = new string[pdosEntries.Count];
            for (var i = 0; i < pdosEntries.Count; i++)
            {
                var e = pdosEntries[i];
                var trackRange = FormatPdosTrackRange(e.StartTrack, e.EndTrack);
                pdosRows[i] = $"{e.FullName,-16} {e.FileLength,8}  {trackRange}";
            }
            Programs = pdosRows;
            DirectoryHeader = PdosDirectoryHeader;
            return;
        }

        // format == DiskDirectoryFormat.Jwsdos — the only value left. ReadDirectory() now reads
        // BOTH sides (machine ms.24 findings-log, 2026-07-31 fix) — side 1 entries first, then
        // side 2, skipping side 2 entirely when the image is single-sided. Nothing extra needed
        // here: the loop below already renders whichever entries come back, side reflected via
        // e.Head.
        var entries = disk.ReadDirectory();
        var rows = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var type = char.IsControl((char)e.FileType) ? ' ' : (char)e.FileType;
            // Side reflects the file's CURRENT physical side (reassignable by disk_defragment
            // during ordinary DOS use, docs/P2000T-disk-formats.md §7 items 2-3) — NOT "which
            // directory held the entry." Side 1 = DE_head 0, Side 2 = DE_head 1 (dir_side1_prep/
            // dir_side2_prep, same doc §2).
            var side = e.Head == 0 ? "Side 1" : "Side 2";
            var trackSector = FormatTrackSectorRange(e.StartSector, e.EndSector);
            rows[i] = $"{e.FullName,-16} {type,-2} {e.FileLength,8}  {side,-6}  {trackSector}";
        }
        Programs = rows;
        DirectoryHeader = JwsdosDirectoryHeader;
    }

    /// <summary>Formats a PDOS entry's pre-derived start/end track (already <c>record ÷ 4 + 1</c>
    /// from the machine layer, milestone 22a) as a compact range — e.g. "T4-T6" for a file
    /// spanning multiple tracks, or just "T3" when it stays within one (docs/P2000T-disk-formats.md
    /// §6a/§7 item 8's resolved display formula: no physical-interleave conversion, no sector-
    /// within-track figure — PDOS's own allocation granularity is the whole record/track, unlike
    /// JWSDOS's per-sector precision).</summary>
    private static string FormatPdosTrackRange(int startTrack, int endTrack) =>
        startTrack == endTrack ? $"T{startTrack}" : $"T{startTrack}-T{endTrack}";

    /// <summary>Formats a JWSDOS entry's <c>DE_start_sector</c>/<c>DE_end_sector</c> (logical
    /// sector numbers spanning the whole side) as a compact track/sector range, via the confirmed
    /// 16-sectors/track linear formula (docs/P2000T-disk-formats.md §1/§4): e.g. "T39 S14-T40 S8"
    /// when a file spans a track boundary, or "T2 S9-16" when it stays within one track.</summary>
    private static string FormatTrackSectorRange(ushort startSector, ushort endSector)
    {
        var (startTrack, startInTrack) = ToTrackSector(startSector);
        var (endTrack, endInTrack) = ToTrackSector(endSector);
        return startTrack == endTrack
            ? $"T{startTrack} S{startInTrack}-{endInTrack}"
            : $"T{startTrack} S{startInTrack}-T{endTrack} S{endInTrack}";
    }

    /// <summary>Converts a 1-based logical sector number (spanning the whole side) into a
    /// (1-based track, 1-based sector-within-track) pair — 16 sectors/track, confirmed
    /// (docs/P2000T-disk-formats.md §1; e.g. logical sector 25 = track 2, sector 9).</summary>
    private static (int Track, int SectorInTrack) ToTrackSector(int logicalSector)
    {
        var zeroBased = logicalSector - 1;
        return (zeroBased / DskImage.SectorsPerTrack + 1, zeroBased % DskImage.SectorsPerTrack + 1);
    }

    /// <summary>Formats a 256-byte sector as 16 rows of 16 bytes — offset, space-separated
    /// 2-digit uppercase hex, then an ASCII rendering — matching this codebase's existing hex-dump
    /// convention (the debugger's memory watch window, `P2000.UI` CLAUDE.md §10:
    /// <c>MemoryWatchRow.Refresh</c>'s own printable-byte/<c>'.'</c> rule), reused here rather than
    /// inventing a second one (project CLAUDE.md §14 milestone 15b). A static one-shot dump of a
    /// mounted disk image, not live executing memory — no per-byte change-highlighting is needed
    /// or built, unlike the fully interactive memory watch window this borrows its layout from.</summary>
    private static IReadOnlyList<string> FormatSectorDump(ReadOnlySpan<byte> sector)
    {
        const int bytesPerRow = 16;
        var rows = new string[sector.Length / bytesPerRow];
        for (var row = 0; row < rows.Length; row++)
        {
            var offset = row * bytesPerRow;
            var hex = new System.Text.StringBuilder();
            var ascii = new char[bytesPerRow];
            for (var i = 0; i < bytesPerRow; i++)
            {
                var b = sector[offset + i];
                hex.Append(b.ToString("X2")).Append(' ');
                ascii[i] = b is >= 0x20 and <= 0x7E ? (char)b : '.';
            }
            rows[row] = $"{offset:X2}  {hex.ToString().TrimEnd()}  {new string(ascii)}";
        }
        return rows;
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel()
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return mainWindow as Avalonia.Controls.TopLevel ?? Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────────────────────

    public void Detach() => _runner.FrameReady -= OnFrameReady;
}

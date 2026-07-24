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

    /// <summary>Geometry seed for "New (blank) disk" — topology, not editable here (the Config
    /// window's job); a mounted image's own on-disk label always wins (machine M19/M20).</summary>
    public int Capacity { get; }
    public DiskSides Sides { get; }

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
    /// without opening each tab. Not yet wired to an eject/replace warning (milestone 14a,
    /// still unbuilt) — this is only the display half.</summary>
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

    [ObservableProperty] private IReadOnlyList<string> _programs = [];

    public static string DirectoryHeader { get; } =
        $"{"Filename",-16} {"Ty",-2} {"Size",8}";

    /// <summary>Raised when a save error should be surfaced as a dialog.</summary>
    public event Action<string>? ShowMessageRequested;

    public DiskDriveVm(EmulationRunner runner, int driveIndex, int capacity, DiskSides sides)
    {
        _runner = runner;
        DriveIndex = driveIndex;
        Capacity = capacity;
        Sides = sides;
        runner.FrameReady += OnFrameReady;
        RefreshFromMachine();
    }

    private int SidesCount => Sides == DiskSides.Double ? 2 : 1;

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
                new FilePickerFileType("P2000T Disk") { Patterns = ["*.dsk", "*.img"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var name = Path.GetFileNameWithoutExtension(files[0].Name);

        MountBytes(ms.ToArray(), name, files[0]);
    }

    /// <summary>Mounts a <c>.dsk</c> image at runtime (host-side, always fast — machine M19/
    /// M20). Also called by a future drag-drop handler. <paramref name="backingFile"/> becomes
    /// this drive's Save target; pass null for an unbacked mount.</summary>
    public void MountBytes(byte[] diskImage, string filename, IStorageFile? backingFile = null)
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        DskImage disk;
        try
        {
            disk = new DskImage(diskImage);
        }
        catch (ArgumentException ex)
        {
            ShowMessageRequested?.Invoke($"Cannot mount — not a valid disk image:\n{ex.Message}");
            return;
        }

        fdc.MountDisk(DriveIndex, disk);
        _backingFile = backingFile;
        HasImage = true;
        IsWriteProtected = disk.WriteProtected;
        ImageLabel = filename;
        Programs = FormatDirectory(disk);
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void Eject()
    {
        _runner.Machine.Fdc?.EjectDisk(DriveIndex);
        _backingFile = null;
        HasImage = false;
        ImageLabel = "No disk";
        IsWriteProtected = false;
        Programs = [];
        NotifyCommands();
    }

    /// <summary>"New (blank) disk": creates a genuinely unformatted in-memory image sized to
    /// this drive's own configured <see cref="Capacity"/>/<see cref="Sides"/> (no label, no
    /// directory — machine M20) and mounts it live. No format step affordance — a guest DOS
    /// still has to format it via its own routine before it's usable, same as a real blank
    /// floppy.</summary>
    [RelayCommand]
    private void NewBlankDisk()
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        var disk = DskImage.CreateBlank(Capacity, SidesCount);
        fdc.MountDisk(DriveIndex, disk);
        _backingFile = null;
        HasImage = true;
        IsWriteProtected = false;
        ImageLabel = "(blank disk)";
        Programs = [];
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ToggleWriteProtect() => IsWriteProtected = !IsWriteProtected;

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsync()
    {
        if (_backingFile is null)
        {
            await SaveAsAsync();
            return;
        }
        await WriteDiskToFileAsync(_backingFile);
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task SaveAsAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save Disk (drive {DriveIndex}) As",
            SuggestedFileName = $"{SuggestedFileNameStem()}.dsk",
            FileTypeChoices = [new FilePickerFileType("P2000T Disk") { Patterns = ["*.dsk"] }],
            DefaultExtension = "dsk",
        });
        if (file is null) return;

        if (!await WriteDiskToFileAsync(file)) return;

        _backingFile = file;
        ImageLabel = Path.GetFileNameWithoutExtension(file.Name);
    }

    private string SuggestedFileNameStem() =>
        _backingFile is not null ? Path.GetFileNameWithoutExtension(_backingFile.Name) : "disk";

    /// <summary>Writes the mounted image's raw bytes (<c>DskImage.GetBytes</c> — a plain
    /// byte-for-byte copy, no bitstream encode needed) to <paramref name="file"/>, then marks
    /// the image clean (project CLAUDE.md §13.20a dirty-tracking signal). Returns false (and
    /// surfaces a message) on failure or when no image is mounted.</summary>
    private async Task<bool> WriteDiskToFileAsync(IStorageFile file)
    {
        var disk = _runner.Machine.Fdc?.GetDisk(DriveIndex);
        if (disk is null)
        {
            ShowMessageRequested?.Invoke("No disk mounted — nothing to save.");
            return false;
        }

        try
        {
            var bytes = disk.GetBytes();
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

    // ── Directory formatting ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> FormatDirectory(DskImage disk)
    {
        // Side 2 directory location is unconfirmed (docs/JWSDOS-format.md §7 item 2) —
        // ReadDirectory() itself only ever reads side 1's confirmed active region regardless
        // of the mounted image's Sides; nothing extra needed here to enforce that.
        var entries = disk.ReadDirectory();
        var rows = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var type = char.IsControl((char)e.FileType) ? ' ' : (char)e.FileType;
            rows[i] = $"{e.FullName,-16} {type,-2} {e.FileLength,8}";
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

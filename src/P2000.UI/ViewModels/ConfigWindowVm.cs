using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine;
using P2000.Machine.State;
using P2000.UI.Runner;
using System.Collections.ObjectModel;

namespace P2000.UI.ViewModels;

/// <summary>One row of the "Floppy drives" config axis (project CLAUDE.md §14 milestone 14) —
/// Capacity/Sides are reset-to-apply topology, and only the SEED for blank/unlabeled media
/// (machine-layer M19/M20: a mounted image's own on-disk label always wins). <see cref="DriveIndex"/>
/// is fixed at construction — rows are always sequential 0..N-1, matching the config window's
/// drive-COUNT selector (no per-row enable/gaps, unlike the machine's own more general
/// <see cref="FloppyDriveConfig"/> shape).</summary>
public sealed partial class FloppyDriveRowVm : ObservableObject
{
    public int DriveIndex { get; }

    [ObservableProperty] private int _capacity = 40;
    [ObservableProperty] private DiskSides _sides = DiskSides.Single;

    public static IReadOnlyList<int> Capacities { get; } = [35, 40, 80];
    public static IReadOnlyList<DiskSides> SidesOptions { get; } = [DiskSides.Single, DiskSides.Double];

    public FloppyDriveRowVm(int driveIndex) => DriveIndex = driveIndex;

    public FloppyDriveConfig ToConfig() => new()
    {
        DriveIndex = DriveIndex,
        Enabled = true,
        Capacity = Capacity,
        Sides = Sides,
        ImagePath = null, // initial media is mounted live from the Disk Drives window, not here
    };
}

/// <summary>ViewModel for the config window (milestone 5, extended by milestone 14 for the
/// floppy-drive axis). Exposes the topology axes of <see cref="MachineConfig"/> as observable
/// properties; Apply rebuilds and cold-resets the machine (reset-to-apply, locked decision
/// §2.3). Cassette is not a topology axis — it lives in the deck window (runtime exception
/// §2.7); disk IMAGES are the same runtime exception (drive COUNT/geometry is topology, an
/// image mounted in an already-present drive is a live swap — the Disk Drives window's job).</summary>
public sealed partial class ConfigWindowVm : ObservableObject
{
    private readonly EmulationRunner _runner;

    [ObservableProperty] private RamVariant _ramVariant;

    /// <summary>Internal-slot board (project CLAUDE.md §7): none / RAM-only / floppy+RAM.
    /// Gates whether the FDC/CTC + disk drives exist at all. <see cref="FloppyRam"/> requires
    /// <see cref="RamVariant.T102"/> (machine-layer gate, <c>Machine.cs</c>) — selecting it
    /// here auto-forces the RAM selector to T102 and disables it (<see cref="CanEditRamVariant"/>)
    /// so this invalid combination can't be built from the UI at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditRamVariant), nameof(ShowFloppyDrives))]
    private InternalBoard _board;

    [ObservableProperty] private string _slot1CartridgePath = "";
    [ObservableProperty] private string _monitorRomPath = "";
    [ObservableProperty] private string _statusMessage = "";

    public IReadOnlyList<RamVariant> RamVariants { get; } =
        [RamVariant.T38, RamVariant.T54, RamVariant.T102];

    public IReadOnlyList<InternalBoard> Boards { get; } =
        [InternalBoard.None, InternalBoard.RamOnly, InternalBoard.FloppyRam];

    /// <summary>False (RAM selector disabled) while <see cref="Board"/> is
    /// <see cref="InternalBoard.FloppyRam"/> — the combination with anything but T102 is
    /// rejected at machine assembly (reference doc §5b), so the UI never offers it.</summary>
    public bool CanEditRamVariant => Board != InternalBoard.FloppyRam;

    public bool ShowFloppyDrives => Board == InternalBoard.FloppyRam;

    // ── Floppy drives axis (project CLAUDE.md §14 milestone 14) ──────────────

    public IReadOnlyList<int> FloppyDriveCounts { get; } = [0, 1, 2, 3, 4];

    [ObservableProperty] private int _floppyDriveCount;

    public ObservableCollection<FloppyDriveRowVm> FloppyDriveRows { get; } = new();

    public ConfigWindowVm(EmulationRunner runner)
    {
        _runner = runner;
        LoadFromCurrentConfig();
    }

    // ── Sync UI from the live machine config ─────────────────────────────────

    public void LoadFromCurrentConfig()
    {
        var cfg = _runner.Machine.Config;
        RamVariant = cfg.RamVariant;
        Board = cfg.Board;
        Slot1CartridgePath = cfg.Slot1CartridgePath ?? "";
        MonitorRomPath = cfg.MonitorRomPath ?? "";
        LoadFloppyDrivesFrom(cfg.FloppyDrives);
        StatusMessage = "";
    }

    /// <summary>Rebuilds <see cref="FloppyDriveRows"/> (and <see cref="FloppyDriveCount"/>) from
    /// a loaded config's drive list. Missing/disabled/gapped entries collapse to the config
    /// window's simpler sequential-count model (§14 milestone 14) — any drive at index ≥ the
    /// highest populated index is simply not represented; this only round-trips what THIS
    /// window itself could have produced, not every shape <see cref="MachineConfig.FloppyDrives"/>
    /// can technically hold (e.g. a hand-edited .cfg with gaps or a disabled middle drive).</summary>
    private void LoadFloppyDrivesFrom(IReadOnlyList<FloppyDriveConfig> drives)
    {
        var byIndex = drives.Where(d => d.Enabled).ToDictionary(d => d.DriveIndex);
        var count = byIndex.Count == 0 ? 0 : byIndex.Keys.Max() + 1;
        FloppyDriveCount = count; // triggers OnFloppyDriveCountChanged → ResizeFloppyDriveRows
        foreach (var row in FloppyDriveRows)
        {
            if (byIndex.TryGetValue(row.DriveIndex, out var d))
            {
                row.Capacity = d.Capacity;
                row.Sides = d.Sides;
            }
        }
    }

    partial void OnFloppyDriveCountChanged(int value) => ResizeFloppyDriveRows(value);

    private void ResizeFloppyDriveRows(int count)
    {
        while (FloppyDriveRows.Count > count)
            FloppyDriveRows.RemoveAt(FloppyDriveRows.Count - 1);
        while (FloppyDriveRows.Count < count)
            FloppyDriveRows.Add(new FloppyDriveRowVm(FloppyDriveRows.Count));
    }

    partial void OnBoardChanged(InternalBoard value)
    {
        if (value == InternalBoard.FloppyRam) RamVariant = RamVariant.T102;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        try
        {
            var config = BuildConfig();
            _runner.Reconfigure(config);
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
            LoadFloppyDrivesFrom(cfg.FloppyDrives);
            StatusMessage = $"Loaded {Path.GetFileName(path)} — press Apply to use it.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
    }

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
            MachineConfigFile.SaveToFile(BuildConfig(), path);
            StatusMessage = $"Saved to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MachineConfig BuildConfig() => new()
    {
        Model = MachineModel.P2000T,
        RamVariant = RamVariant,
        Board = Board,
        Slot1CartridgePath = NullIfEmpty(Slot1CartridgePath),
        MonitorRomPath = NullIfEmpty(MonitorRomPath),
        FloppyDrives = FloppyDriveRows.Select(r => r.ToConfig()).ToList(),
    };

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType> types)
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

    private static Avalonia.Controls.TopLevel? GetTopLevel() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        is { } w ? Avalonia.Controls.TopLevel.GetTopLevel(w) : null;
}

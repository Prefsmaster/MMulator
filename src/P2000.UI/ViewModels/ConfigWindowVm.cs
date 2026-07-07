using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine;
using P2000.Machine.State;
using P2000.UI.Runner;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the config window (milestone 5). Exposes the topology axes of
/// <see cref="MachineConfig"/> as observable properties; Apply rebuilds and cold-resets the
/// machine (reset-to-apply, locked decision §2.3). Cassette is not a topology axis — it lives
/// in the deck window (runtime exception §2.7).</summary>
public sealed partial class ConfigWindowVm : ObservableObject
{
    private readonly EmulationRunner _runner;

    [ObservableProperty] private RamVariant _ramVariant;
    [ObservableProperty] private string _slot1CartridgePath = "";
    [ObservableProperty] private string _monitorRomPath = "";
    [ObservableProperty] private string _statusMessage = "";

    public IReadOnlyList<RamVariant> RamVariants { get; } =
        [RamVariant.T38, RamVariant.T54, RamVariant.T102];

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
        Slot1CartridgePath = cfg.Slot1CartridgePath ?? "";
        MonitorRomPath = cfg.MonitorRomPath ?? "";
        StatusMessage = "";
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Apply()
    {
        var config = BuildConfig();
        _runner.Reconfigure(config);
        StatusMessage = "Applied — machine cold-reset.";
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
            Slot1CartridgePath = cfg.Slot1CartridgePath ?? "";
            MonitorRomPath = cfg.MonitorRomPath ?? "";
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
        Slot1CartridgePath = NullIfEmpty(Slot1CartridgePath),
        MonitorRomPath = NullIfEmpty(MonitorRomPath),
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

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.UI.Runner;
using System.Text;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the cassette deck window. Subscribes to FrameReady to reflect
/// live motor direction and R/W activity. Mount/Eject are the only mutations: both call
/// <c>MdcrDevice</c> directly (CIP is the one runtime exception per machine CLAUDE.md §7).</summary>
public sealed partial class CassetteDeckVm : ObservableObject
{
    private readonly EmulationRunner _runner;
    private byte[]? _casBytes;
    private int _frameTick;

    [ObservableProperty] private bool _hasTape;
    [ObservableProperty] private string _tapeLabel = "No cassette";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _directionText = "— Stopped";
    [ObservableProperty] private bool _isWriteProtected;
    [ObservableProperty] private IReadOnlyList<string> _programs = [];

    public CassetteDeckVm(EmulationRunner runner)
    {
        _runner = runner;
        runner.FrameReady += OnFrameReady;
    }

    // ── Frame callback (runs on UI thread via Dispatcher.UIThread.Post in EmulationRunner) ──

    private void OnFrameReady(uint[] _)
    {
        var fwd = _runner.Machine.CpOut.Forward;
        var rev = _runner.Machine.CpOut.Reverse;
        IsActive = fwd || rev;
        DirectionText = fwd ? "▶ Forward" : rev ? "◀ Reverse" : "— Stopped";

        // HasTape only changes on Mount/Eject, but re-sync at ~5 Hz to stay consistent.
        if (++_frameTick < 10) return;
        _frameTick = 0;
        HasTape = _runner.Machine.Mdcr.HasTape;
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task MountAsync()
    {
        // Get any available TopLevel (main window on a classic desktop lifetime).
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var topLevel = mainWindow as Avalonia.Controls.TopLevel ?? Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Mount cassette",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("P2000T Cassette") { Patterns = ["*.cas", "*.p2000t"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var name = Path.GetFileNameWithoutExtension(files[0].Name);

        MountBytes(bytes, name);
    }

    [RelayCommand(CanExecute = nameof(CanEject))]
    private void Eject()
    {
        _runner.Machine.Mdcr.EjectTape();
        _casBytes = null;
        HasTape = false;
        TapeLabel = "No cassette";
        IsWriteProtected = false;
        Programs = [];
        EjectCommand.NotifyCanExecuteChanged();
    }

    private bool CanEject() => HasTape;

    // ── Public host-face (called by drag-drop handler in the view) ──────────────────────────

    /// <summary>Inserts a <c>.cas</c> image at runtime (live CIP transition). Safe to call
    /// from the UI thread. Also called by the drag-drop handler in
    /// <see cref="Views.DisplayWindow"/>.</summary>
    public void MountBytes(byte[] casImage, string filename)
    {
        _runner.Machine.Mdcr.InsertTape(casImage, writeProtect: true);
        _casBytes = casImage;
        HasTape = true;
        IsWriteProtected = true;
        TapeLabel = filename;
        Programs = ParseDirectory(casImage);
        EjectCommand.NotifyCanExecuteChanged();
    }

    // ── CommunityToolkit hooks ───────────────────────────────────────────────────────────────

    partial void OnHasTapeChanged(bool value) => EjectCommand.NotifyCanExecuteChanged();

    // ── Directory parsing ────────────────────────────────────────────────────────────────────

    /// <summary>Returns the list of program names from a <c>.cas</c> image. Each 1280-byte
    /// block has a 32-byte header at offset 0x30; the program name occupies header[6..13]
    /// (8 bytes, ASCII). Multi-block programs repeat the same header, so names are
    /// de-duplicated — one entry per unique program name.</summary>
    private static IReadOnlyList<string> ParseDirectory(byte[] casImage)
    {
        var programs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blockCount = casImage.Length / 1280;
        for (var i = 0; i < blockCount; i++)
        {
            var hdr = i * 1280 + 0x30;
            if (hdr + 32 > casImage.Length) break;
            // Name occupies bytes 6-13 of the 32-byte header.
            var name = Encoding.ASCII.GetString(casImage, hdr + 6, 8).TrimEnd('\0', ' ');
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                programs.Add(name);
        }
        return programs;
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────────────────────

    public void Detach() => _runner.FrameReady -= OnFrameReady;
}

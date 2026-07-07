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

    /// <summary>Fixed header row aligned to the formatted program entries below it.</summary>
    public static string DirectoryHeader { get; } =
        $"{"Filename",-16} {"Ext",-4} {"Cr",-2} {"Size",8} {"Blk",4}";

    public CassetteDeckVm(EmulationRunner runner)
    {
        _runner = runner;
        runner.FrameReady += OnFrameReady;
    }

    // ── Frame callback (runs on UI thread via Dispatcher.UIThread.Post in EmulationRunner) ──

    private void OnFrameReady(uint[] _, bool __, bool[] ___)
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

    /// <summary>Returns formatted directory entries from a <c>.cas</c> image.
    /// Each 1280-byte block has a 32-byte header at offset 0x30:
    ///   bytes 06-0D: first 8 chars of filename · 0E-10: extension (3) · 11: creator ID ·
    ///   17-1E: last 8 chars of filename · 1F: block counter · 04-05: file size (word LE).
    /// Multi-block programs share the same header — de-duplicated, block count summed.</summary>
    private static IReadOnlyList<string> ParseDirectory(byte[] casImage)
    {
        var order = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        var totalBlocks = casImage.Length / 1280;
        for (var i = 0; i < totalBlocks; i++)
        {
            var hdr = i * 1280 + 0x30;
            if (hdr + 32 > casImage.Length) break;

            // Full 16-char filename: header bytes 06-0D (first 8) + 17-1E (last 8).
            var first    = Encoding.ASCII.GetString(casImage, hdr + 6,  8);
            var last     = Encoding.ASCII.GetString(casImage, hdr + 23, 8);
            var fullName = (first + last).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(fullName)) continue;
            if (!seen.Add(fullName)) continue;

            var ext     = Encoding.ASCII.GetString(casImage, hdr + 14, 3).TrimEnd('\0', ' ');
            var creator = (char)casImage[hdr + 17];
            var size    = casImage[hdr + 4] | (casImage[hdr + 5] << 8);
            // Bytes 02-03: space occupied on tape (may be larger than file if a shorter file
            // was written over a larger one). Divide by 1024 to get blocks occupied.
            var occupied = casImage[hdr + 2] | (casImage[hdr + 3] << 8);
            var blocks   = occupied / 1024;

            var displayName = fullName.Length > 16 ? fullName[..16] : fullName;
            var dotExt = $".{ext}";
            order.Add($"{displayName,-16} {dotExt,-4} {creator,-2} {FormatSize(size),8} {blocks,4}");
        }

        return order;
    }

    private static string FormatSize(int size) =>
        size >= 1_000_000 ? $"{size / 1_000_000}.{size / 1000 % 1000:D3}.{size % 1000:D3}" :
        size >= 1_000     ? $"{size / 1000}.{size % 1000:D3}" :
        size.ToString();

    // ── Cleanup ─────────────────────────────────────────────────────────────────────────────

    public void Detach() => _runner.FrameReady -= OnFrameReady;
}

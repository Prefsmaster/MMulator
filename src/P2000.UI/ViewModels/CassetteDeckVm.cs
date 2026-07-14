using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.UI.Runner;
using System.Text;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the cassette deck window. Subscribes to FrameReady to reflect
/// live motor direction and R/W activity. Mount/Eject/New/Save are the only mutations: all
/// call <c>MdcrDevice</c> directly (CIP is the one runtime exception per machine CLAUDE.md §7).
/// "New (blank) tape" + "Save"/"Save as…" are UI milestone 13 — host-side container operations
/// in the same category as mount/eject, not physical MDCR controls (§5).</summary>
public sealed partial class CassetteDeckVm : ObservableObject
{
    private readonly EmulationRunner _runner;
    private byte[]? _casBytes;
    private int _frameTick;
    private bool _wasActive;

    /// <summary>The file this tape was loaded from or last saved to; null when the mounted
    /// tape is unbacked (fresh off "New (blank) tape"). Drives "Save" vs "Save as…" (§14
    /// milestone 13): "Save" reuses this path directly; only prompts when null.</summary>
    private IStorageFile? _backingFile;

    [ObservableProperty] private bool _hasTape;
    [ObservableProperty] private string _tapeLabel = "No cassette";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _directionText = "— Stopped";

    /// <summary>The mounted tape's write-protect state — a live, host-side "physical tab"
    /// control (§14 milestone 13a), two-way bound to a toggle in the deck window. Reflects
    /// the machine's WEN bit; setting it (from the UI or internally after mount/new/eject)
    /// pushes to <c>MdcrDevice.SetWriteProtected</c> via <see cref="OnIsWriteProtectedChanged"/>.
    /// Meaningless with no tape mounted — the toggle is disabled via <c>HasTape</c> binding.</summary>
    [ObservableProperty] private bool _isWriteProtected;

    [ObservableProperty] private IReadOnlyList<string> _programs = [];

    /// <summary>Fixed header row aligned to the formatted program entries below it.</summary>
    public static string DirectoryHeader { get; } =
        $"{"Filename",-16} {"Ext",-4} {"Cr",-2} {"Size",8} {"Blk",4}";

    /// <summary>Raised when a save error should be surfaced as a dialog.</summary>
    public event Action<string>? ShowMessageRequested;

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

        // A live CSAVE (typed in BASIC) mutates the tape's bitstream directly through the
        // ROM/MdcrDevice — the directory list built at mount time has no way to know that
        // happened. Re-derive it from the live tape on the falling edge of activity (motor
        // just stopped), which covers both CLOAD and CSAVE finishing.
        if (_wasActive && !IsActive && HasTape)
            RefreshDirectoryFromLiveTape();
        _wasActive = IsActive;

        // HasTape only changes on Mount/Eject, but re-sync at ~5 Hz to stay consistent.
        if (++_frameTick < 10) return;
        _frameTick = 0;
        HasTape = _runner.Machine.Mdcr.HasTape;
    }

    /// <summary>Re-decodes the mounted tape's current phase bitstream (<c>SaveTape</c> — the
    /// same host-side, always-fast serializer "Save"/"Save as…" use) and refreshes
    /// <see cref="Programs"/> from it, so the directory reflects a CSAVE that just ran on the
    /// live machine rather than a stale mount-time snapshot.</summary>
    private void RefreshDirectoryFromLiveTape()
    {
        var casImage = _runner.Machine.Mdcr.SaveTape();
        if (casImage is not null)
            Programs = ParseDirectory(casImage);
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task MountAsync()
    {
        var topLevel = GetTopLevel();
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

        MountBytes(bytes, name, files[0]);
    }

    [RelayCommand(CanExecute = nameof(CanEject))]
    private void Eject()
    {
        _runner.Machine.Mdcr.EjectTape();
        _casBytes = null;
        _backingFile = null;
        HasTape = false;
        TapeLabel = "No cassette";
        IsWriteProtected = false;
        Programs = [];
        EjectCommand.NotifyCanExecuteChanged();
    }

    private bool CanEject() => HasTape;

    /// <summary>Flips the mounted tape's write-protect state (the clickable padlock, §14
    /// milestone 13a). Setting <see cref="IsWriteProtected"/> is itself what pushes to the
    /// live machine via <see cref="OnIsWriteProtectedChanged"/> — this command just flips it.</summary>
    [RelayCommand(CanExecute = nameof(HasTape))]
    private void ToggleWriteProtect() => IsWriteProtected = !IsWriteProtected;

    /// <summary>"New (blank) tape" (§14 milestone 13): mounts a fresh, empty, unbacked tape
    /// live. Mounting straight over an already-mounted tape is a single CIP transition (the
    /// machine layer's <c>InsertBlankTape</c> never observes "absent" in between) — the same
    /// live runtime exception a file-dialog mount already uses, not a topology change. No
    /// format step: the blank tape is immediately writable (CSAVE appends at BOT).</summary>
    [RelayCommand]
    private void NewBlankTape()
    {
        _runner.Machine.Mdcr.InsertBlankTape();
        _casBytes = null;
        _backingFile = null;
        HasTape = true;
        IsWriteProtected = false;
        TapeLabel = "(blank tape)";
        Programs = [];
        EjectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes the mounted tape back to its existing backing file; behaves like
    /// "Save as…" when the tape is unbacked (fresh off "New (blank) tape").</summary>
    [RelayCommand(CanExecute = nameof(HasTape))]
    private async Task SaveAsync()
    {
        if (_backingFile is null)
        {
            await SaveAsAsync();
            return;
        }
        await WriteTapeToFileAsync(_backingFile);
    }

    /// <summary>Always prompts for a destination, then adopts it as the new backing file
    /// (so a subsequent plain "Save" writes back to it without re-prompting).</summary>
    [RelayCommand(CanExecute = nameof(HasTape))]
    private async Task SaveAsAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Cassette As",
            SuggestedFileName = $"{SuggestedFileNameStem()}.cas",
            FileTypeChoices = [new FilePickerFileType("P2000T Cassette") { Patterns = ["*.cas"] }],
            DefaultExtension = "cas",
        });
        if (file is null) return;

        if (!await WriteTapeToFileAsync(file)) return;

        _backingFile = file;
        TapeLabel = Path.GetFileNameWithoutExtension(file.Name);
    }

    private string SuggestedFileNameStem() =>
        _backingFile is not null ? Path.GetFileNameWithoutExtension(_backingFile.Name) : "tape";

    /// <summary>Serializes the mounted tape (<c>MdcrDevice.SaveTape</c> — machine ms.9a, no
    /// machine change needed here) and writes it to <paramref name="file"/>. Returns false
    /// (and surfaces a message) on failure or when the tape has no valid blocks to save.</summary>
    private async Task<bool> WriteTapeToFileAsync(IStorageFile file)
    {
        var bytes = _runner.Machine.Mdcr.SaveTape();
        if (bytes is null)
        {
            ShowMessageRequested?.Invoke("No valid blocks found on tape — nothing to save.");
            return false;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            return true;
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Save failed:\n{ex.Message}");
            return false;
        }
    }

    private static Avalonia.Controls.TopLevel? GetTopLevel()
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return mainWindow as Avalonia.Controls.TopLevel ?? Avalonia.Controls.TopLevel.GetTopLevel(mainWindow);
    }

    // ── Public host-face (called by drag-drop handler in the view) ──────────────────────────

    /// <summary>Inserts a <c>.cas</c> image at runtime (live CIP transition). Safe to call
    /// from the UI thread. Also called by the drag-drop handler in
    /// <see cref="Views.DisplayWindow"/>. <paramref name="backingFile"/> is the source file
    /// when known (file dialog/drag-drop) — it becomes the tape's Save target; pass null for
    /// an unbacked mount.</summary>
    public void MountBytes(byte[] casImage, string filename, IStorageFile? backingFile = null)
    {
        // Write-protect is read from the file itself (record offset 0x50, bit 0 — machine
        // CLAUDE.md §17, 2026-07-14); an unset/absent bit defaults writable. Read it back
        // from the machine rather than assuming — do NOT hardcode true here.
        _runner.Machine.Mdcr.InsertTape(casImage);
        _casBytes = casImage;
        _backingFile = backingFile;
        HasTape = true;
        IsWriteProtected = _runner.Machine.Mdcr.IsWriteProtected;
        TapeLabel = filename;
        Programs = ParseDirectory(casImage);
        EjectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
    }

    // ── CommunityToolkit hooks ───────────────────────────────────────────────────────────────

    partial void OnHasTapeChanged(bool value)
    {
        EjectCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        ToggleWriteProtectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Pushes a write-protect change (from the UI toggle, or our own post-mount
    /// sync) to the live machine. Guarded by <see cref="HasTape"/> so the initial
    /// property-init/Eject transitions never call into a null tape (harmless either way —
    /// <c>SetWriteProtected</c> no-ops with no tape mounted — but this keeps intent explicit).</summary>
    partial void OnIsWriteProtectedChanged(bool value)
    {
        if (HasTape) _runner.Machine.Mdcr.SetWriteProtected(value);
    }

    // ── Directory parsing ────────────────────────────────────────────────────────────────────

    /// <summary>Returns formatted directory entries from a <c>.cas</c> image.
    /// Each 1280-byte block has a 32-byte header at offset 0x30:
    ///   bytes 06-0D: first 8 chars of filename · 0E-10: extension (3) · 11: creator ID ·
    ///   17-1E: last 8 chars of filename · 02-03: space occupied on tape · 04-05: file size
    ///   (word LE). Byte 1F ("block counter") is NOT usable — confirmed empirically
    ///   (2026-07-14, owner inspection of a real .cas file) to be always zero; likely the
    ///   write-time `block_counter` RAM variable (Cassette.asm) captured at whatever transient
    ///   state it lands in, not a meaningful per-file total. Block count is derived from the
    ///   occupied-bytes field instead (ceiling division — see below).
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
            // was written over a larger one — confirmed expected/authentic behavior, not a
            // bug). Block count is a CEILING division, not floor — matches the ROM's own
            // get_length_blocks (Cassette.asm): occupied isn't always an exact multiple of
            // 1024, and a partial trailing block still counts as one whole block.
            var occupied = casImage[hdr + 2] | (casImage[hdr + 3] << 8);
            var blocks   = (occupied + 1023) / 1024;

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

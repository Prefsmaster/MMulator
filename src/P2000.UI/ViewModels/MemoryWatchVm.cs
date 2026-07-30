using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine.Debug;
using P2000.Machine.Memory;
using P2000.UI.Runner;

namespace P2000.UI.ViewModels;

/// <summary>
/// Single memory watch window (freely spawnable, §10). Shows a configurable region
/// (<see cref="BaseAddress"/> + <see cref="Length"/>, default 256 bytes / 16 rows) in hex +
/// ASCII, with bytes changed since the last refresh highlighted. Optionally follows a
/// register pair so the view tracks HL/SP as the machine runs.
/// </summary>
public sealed partial class MemoryWatchVm : ObservableObject
{
    public static readonly IReadOnlyList<string> FollowOptions =
        new[] { "None", "HL", "SP", "BC", "DE" };

    private const int BytesPerRow = 16;
    private const int DefaultLength = 256;
    private const int MaxLength = 0x10000; // the whole address space

    private readonly EmulationRunner _runner;
    private byte[] _prev = Array.Empty<byte>();
    private byte[] _curr = Array.Empty<byte>();
    private bool _firstUpdate = true;

    [ObservableProperty] private ushort _baseAddress = 0x5000;

    /// <summary>Number of bytes watched/exported (rounded up to whole 16-byte rows for
    /// display — the extra trailing bytes of a partial last row are real memory, just past
    /// the requested length; export/import use the exact requested length). Editable via the
    /// "Length" field alongside "Base" (§14 milestone 12 — configurable range, not a fixed
    /// 256 bytes).</summary>
    [ObservableProperty] private int _length = DefaultLength;

    /// <summary>Register-pair to follow: "None", "HL", "SP", "BC", or "DE".</summary>
    [ObservableProperty] private string _follow = "None";

    [ObservableProperty] private string _title = "Memory 5000h";

    // ── Per-window bank override (project CLAUDE.md §14 milestone 17; machine ms.24) ────────

    public const string AutoBankOption = "Auto";

    /// <summary>"Auto" (default — today's existing behavior: shows whatever's live-active) plus
    /// one option per populated bank, from the live machine's <c>PageTable.BankCount</c>.
    /// Recomputed on every <see cref="Update"/> call so it stays correct across a live topology
    /// change, not just at window-open.</summary>
    public ObservableCollection<string> BankOptions { get; } = new() { AutoBankOption };

    /// <summary>"Auto" or "Bank N" — selecting a specific bank pins this window's rendering of
    /// addresses inside the banked window (0xE000-0xFFFF) to that bank's raw bytes
    /// (<see cref="PageTable.GetBankRaw"/>) regardless of what's actually live-active; any part
    /// of this window's own [<see cref="BaseAddress"/>, BaseAddress+<see cref="Length"/>) range
    /// outside the banked window is unaffected either way. Per-window state — two windows over
    /// the same banked range, pinned to different banks, show both simultaneously side by side.</summary>
    [ObservableProperty] private string _selectedBankOption = AutoBankOption;

    /// <summary>Shown/enabled only when this window's configured range intersects the banked
    /// window AND the installed card has at least one bank — hidden otherwise (nothing to
    /// select for a window that never touches the banked region, or a card with no banking).</summary>
    [ObservableProperty] private bool _showBankSelector;

    /// <summary>Target address (hex text) for "Load file to address…"; defaults to the
    /// window's own range start but is independently editable (§14 milestone 12).</summary>
    [ObservableProperty] private string _loadAddressText = "5000";

    /// <summary>Raised when a save/load error should be surfaced as a dialog.</summary>
    public event Action<string>? ShowMessageRequested;

    /// <summary>One row per 16-byte block; resized as <see cref="Length"/> changes.</summary>
    public ObservableCollection<MemoryWatchRow> Rows { get; } = new();

    public MemoryWatchVm(EmulationRunner runner)
    {
        _runner = runner;
        LoadAddressText = $"{BaseAddress:X4}";
        ResizeBuffers();
    }

    partial void OnBaseAddressChanged(ushort value)
        => Title = $"Memory {value:X4}h";

    partial void OnLengthChanged(int value)
        => ResizeBuffers();

    /// <summary>Sets base address and length together (the "Go" action) and resizes the
    /// display grid accordingly. Length is clamped to [1, 0x10000].</summary>
    public void SetRange(ushort baseAddress, int length)
    {
        BaseAddress = baseAddress;
        Length = Math.Clamp(length, 1, MaxLength);
    }

    private void ResizeBuffers()
    {
        int rowCount = (Length + BytesPerRow - 1) / BytesPerRow;
        int bufSize = rowCount * BytesPerRow;
        _prev = new byte[bufSize];
        _curr = new byte[bufSize];
        _firstUpdate = true;

        while (Rows.Count < rowCount) Rows.Add(new MemoryWatchRow());
        while (Rows.Count > rowCount) Rows.RemoveAt(Rows.Count - 1);
    }

    // ── Update ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads memory starting at <paramref name="baseOverride"/> (or <see cref="BaseAddress"/>
    /// if null) and refreshes all rows. Call from the UI thread (or post if coming from
    /// emulation thread). Any address inside the banked window (0xE000-0xFFFF) is read from
    /// <see cref="SelectedBankOption"/>'s pinned bank instead of <paramref name="readMemory"/>
    /// when a specific bank (not "Auto") is selected (project CLAUDE.md §14 milestone 17).
    /// </summary>
    public void Update(Func<ushort, byte> readMemory, ushort? baseOverride = null)
    {
        ushort addr = baseOverride ?? BaseAddress;
        int bufSize = _curr.Length;

        Array.Copy(_curr, _prev, bufSize);

        RefreshBankOptions();
        var pinnedBank = ParseSelectedBank();
        byte[]? pinnedBankBytes = pinnedBank is int b ? _runner.Machine.Memory.GetBankRaw(b) : null;

        for (int i = 0; i < bufSize; i++)
        {
            var a = (ushort)(addr + i);
            _curr[i] = pinnedBankBytes is not null
                && a >= PageTable.BankedWindowStart && a <= PageTable.BankedWindowEnd
                ? pinnedBankBytes[a - PageTable.BankedWindowStart]
                : readMemory(a);
        }

        for (int row = 0; row < Rows.Count; row++)
        {
            int offset = row * BytesPerRow;
            Rows[row].Refresh((ushort)(addr + offset),
                              _curr, _prev, offset, _firstUpdate);
        }

        _firstUpdate = false;
    }

    /// <summary>Rebuilds <see cref="BankOptions"/>/<see cref="ShowBankSelector"/> from the live
    /// machine's bank count and this window's own configured range — called every
    /// <see cref="Update"/>, so it stays correct across both a live topology change and the user
    /// editing Base/Length, not just at window-open.</summary>
    private void RefreshBankOptions()
    {
        var bankCount = _runner.Machine.Memory.BankCount;
        ShowBankSelector = bankCount > 0 && RangeIntersectsBankedWindow(BaseAddress, Length);

        if (BankOptions.Count != bankCount + 1)
        {
            BankOptions.Clear();
            BankOptions.Add(AutoBankOption);
            for (var i = 0; i < bankCount; i++) BankOptions.Add($"Bank {i}");
        }

        if (!BankOptions.Contains(SelectedBankOption))
            SelectedBankOption = AutoBankOption;
    }

    private static bool RangeIntersectsBankedWindow(ushort baseAddress, int length)
    {
        var rangeEndInclusive = baseAddress + length - 1;
        return baseAddress <= PageTable.BankedWindowEnd && rangeEndInclusive >= PageTable.BankedWindowStart;
    }

    private int? ParseSelectedBank() =>
        SelectedBankOption == AutoBankOption ? null : int.Parse(SelectedBankOption.AsSpan("Bank ".Length));

    // ── Export / import (§14 milestone 12) ──────────────────────────────────

    /// <summary>
    /// Dumps <paramref name="length"/> bytes starting at <paramref name="start"/> — read fresh
    /// from the live machine (not the window's own displayed buffer, since the requested range
    /// may differ from what's currently on screen) — to a raw binary file, no header. The
    /// caller (view code-behind) prompts for start/length, defaulting to the window's own
    /// <see cref="BaseAddress"/>/<see cref="Length"/> but independently editable.
    /// </summary>
    public async Task SaveRangeToFileAsync(ushort start, int length)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Memory Range",
            SuggestedFileName = $"mem_{start:X4}_{length:X4}.bin",
            FileTypeChoices = [new FilePickerFileType("Binary dump") { Patterns = ["*.bin"] }],
            DefaultExtension = "bin",
        });
        if (file is null) return;

        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = _runner.Machine.Memory.Read((ushort)(start + i));

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(data);
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Save failed:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Loads a raw binary file into RAM at <see cref="LoadAddressText"/> via the machine's
    /// <see cref="LoadImageCommand"/> (already-shipped command queue, machine ms.15) — the
    /// first real caller of that command. Rejects a file whose length would run past the top
    /// of addressable RAM (0xFFFF) rather than silently truncating or wrapping.
    /// </summary>
    [RelayCommand]
    private async Task LoadFileToAddressAsync()
    {
        var text = LoadAddressText.Trim().TrimStart('0', 'x', 'X', '$');
        if (text.Length == 0 ||
            !ushort.TryParse(text, NumberStyles.HexNumber, null, out ushort address))
        {
            ShowMessageRequested?.Invoke($"Invalid target address: \"{LoadAddressText}\".");
            return;
        }

        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load File to Address",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Binary dump") { Patterns = ["*.bin"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0) return;

        byte[] data;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            data = ms.ToArray();
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Load failed:\n{ex.Message}");
            return;
        }

        if (address + data.Length > 0x10000)
        {
            ShowMessageRequested?.Invoke(
                $"Cannot load {data.Length} byte(s) at {address:X4}h — runs past the top of " +
                "addressable RAM (0xFFFF).");
            return;
        }

        _runner.Machine.Enqueue(new LoadImageCommand(address, data));
    }

    private static TopLevel? GetTopLevel()
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return mainWindow as TopLevel ?? TopLevel.GetTopLevel(mainWindow);
    }
}

// ────────────────────────────────────────────────────────────────────────────

/// <summary>One 16-byte row in a memory watch window.</summary>
public sealed class MemoryWatchRow : ObservableObject
{
    public HexByteVm[] Bytes { get; } = Enumerable.Range(0, 16)
        .Select(_ => new HexByteVm()).ToArray();

    private string _address = "0000";
    public string Address
    {
        get => _address;
        private set => SetProperty(ref _address, value);
    }

    private string _ascii = new string('.', 16);
    public string Ascii
    {
        get => _ascii;
        private set => SetProperty(ref _ascii, value);
    }

    internal void Refresh(ushort baseAddr, byte[] curr, byte[] prev, int offset, bool firstUpdate)
    {
        Address = $"{baseAddr:X4}";
        var ascii = new char[16];
        for (int i = 0; i < 16; i++)
        {
            byte b = curr[offset + i];
            bool changed = !firstUpdate && b != prev[offset + i];
            Bytes[i].Refresh(b, changed);
            ascii[i] = b is >= 0x20 and <= 0x7E ? (char)b : '.';
        }
        Ascii = new string(ascii);
    }
}

// ────────────────────────────────────────────────────────────────────────────

/// <summary>One byte cell in a memory watch row.</summary>
public sealed class HexByteVm : ObservableObject
{
    private string _hex = "00";
    public string Hex
    {
        get => _hex;
        private set => SetProperty(ref _hex, value);
    }

    private bool _isChanged;
    public bool IsChanged
    {
        get => _isChanged;
        private set => SetProperty(ref _isChanged, value);
    }

    internal void Refresh(byte value, bool changed)
    {
        Hex = value.ToString("X2");
        IsChanged = changed;
    }
}

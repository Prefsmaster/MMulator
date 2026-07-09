using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace P2000.UI.ViewModels;

/// <summary>
/// Single memory watch window (freely spawnable, §10). Shows a 256-byte region (16 rows × 16
/// bytes) in hex + ASCII, with bytes changed since the last refresh highlighted. Optionally
/// follows a register pair so the view tracks HL/SP as the machine runs.
/// </summary>
public sealed partial class MemoryWatchVm : ObservableObject
{
    public static readonly IReadOnlyList<string> FollowOptions =
        new[] { "None", "HL", "SP", "BC", "DE" };

    private readonly byte[] _prev = new byte[256];
    private readonly byte[] _curr = new byte[256];
    private bool _firstUpdate = true;

    [ObservableProperty] private ushort _baseAddress = 0x5000;

    /// <summary>Register-pair to follow: "None", "HL", "SP", "BC", or "DE".</summary>
    [ObservableProperty] private string _follow = "None";

    [ObservableProperty] private string _title = "Memory 5000h";

    /// <summary>16 rows, one per 16-byte block.</summary>
    public MemoryWatchRow[] Rows { get; } = Enumerable.Range(0, 16)
        .Select(_ => new MemoryWatchRow()).ToArray();

    partial void OnBaseAddressChanged(ushort value)
        => Title = $"Memory {value:X4}h";

    // ── Update ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads memory starting at <paramref name="baseOverride"/> (or <see cref="BaseAddress"/>
    /// if null) and refreshes all rows. Call from the UI thread (or post if coming from
    /// emulation thread).
    /// </summary>
    public void Update(Func<ushort, byte> readMemory, ushort? baseOverride = null)
    {
        ushort addr = baseOverride ?? BaseAddress;

        Array.Copy(_curr, _prev, 256);

        for (int i = 0; i < 256; i++)
            _curr[i] = readMemory((ushort)(addr + i));

        for (int row = 0; row < 16; row++)
        {
            int offset = row * 16;
            Rows[row].Refresh((ushort)(addr + offset),
                              _curr, _prev, offset, _firstUpdate);
        }

        _firstUpdate = false;
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

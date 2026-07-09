using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace P2000.UI.ViewModels;

/// <summary>
/// ViewModel for the special VRAM/pan window (§10). Holds the 80×24 VRAM byte array, the
/// corruption overlay snapshot, and the PanX viewport offset. The bound
/// <c>VramGridControl</c> re-renders whenever these properties change.
/// </summary>
public sealed partial class VramWindowVm : ObservableObject
{
    /// <summary>80 columns × 24 rows = 1920 bytes. Replaced on each update so Avalonia
    /// detects the property change and the bound control invalidates.</summary>
    [ObservableProperty] private byte[] _vramData = new byte[80 * 24];

    /// <summary>40×24 = 960 bools (index = row × 40 + viewportCol). Replaced on each update.</summary>
    [ObservableProperty] private bool[] _corruption = new bool[40 * 24];

    /// <summary>Upper-left column of the visible 40-column viewport, 0–79.</summary>
    [ObservableProperty] private int _panX;

    /// <summary>If true, show raw hex byte per cell; if false, show closest printable char.</summary>
    [ObservableProperty] private bool _showHex;

    // ── Update ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads VRAM (0x5000–0x577F) via <paramref name="readMemory"/>, snapshots
    /// <paramref name="panX"/> and <paramref name="corruption"/>. Call on the UI thread.
    /// </summary>
    public void Update(Func<ushort, byte> readMemory, int panX, bool[]? corruption)
    {
        const ushort VramStart = 0x5000;
        const int Cells = 80 * 24;

        var data = new byte[Cells];
        for (int i = 0; i < Cells; i++)
            data[i] = readMemory((ushort)(VramStart + i));

        VramData   = data;
        PanX       = panX;
        Corruption = corruption is { Length: 40 * 24 }
                     ? (bool[])corruption.Clone()
                     : new bool[40 * 24];
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleHex() => ShowHex = !ShowHex;
}

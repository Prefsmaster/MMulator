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

    /// <summary><see cref="ViewportWidth"/>×24 bools (index = row × ViewportWidth +
    /// viewportCol). Replaced on each update. <b>Do not assume 40 wide</b> — it is 80 while the
    /// 80-column board is enabled (machine milestone 25).</summary>
    [ObservableProperty] private bool[] _corruption = new bool[40 * 24];

    /// <summary>Visible viewport width in columns: 40 normally, 80 in 80-column mode (where the
    /// pan register is held cleared in hardware, so the whole buffer is on screen).</summary>
    [ObservableProperty] private int _viewportWidth = 40;

    /// <summary>Upper-left column of the visible 40-column viewport, 0–79.</summary>
    [ObservableProperty] private int _panX;

    /// <summary>If true, show raw hex byte per cell; if false, show closest printable char.</summary>
    [ObservableProperty] private bool _showHex;

    // ── Update ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads VRAM (0x5000–0x577F) via <paramref name="readMemory"/>, snapshots
    /// <paramref name="panX"/> and <paramref name="corruption"/>. Call on the UI thread.
    /// </summary>
    public void Update(Func<ushort, byte> readMemory, int panX, bool[]? corruption,
                       int corruptionWidth = 40)
    {
        const ushort VramStart = 0x5000;
        const int Cells = 80 * 24;

        var data = new byte[Cells];
        for (int i = 0; i < Cells; i++)
            data[i] = readMemory((ushort)(VramStart + i));

        int width  = corruptionWidth is 40 or 80 ? corruptionWidth : 40;
        VramData      = data;
        PanX          = panX;
        ViewportWidth = width;
        Corruption    = corruption is not null && corruption.Length >= width * 24
                        ? (bool[])corruption.Clone()
                        : new bool[width * 24];
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleHex() => ShowHex = !ShowHex;
}

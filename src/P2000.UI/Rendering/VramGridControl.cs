using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace P2000.UI.Rendering;

/// <summary>
/// Custom <see cref="Control"/> that renders the P2000T's 80×24 VRAM grid (§10):
/// <list type="bullet">
///   <item>Each cell shows the glyph character (printable ASCII) or its hex byte (toggle).</item>
///   <item>Cells inside the 40-column viewport have a subtle highlight background.</item>
///   <item>Cells corrupted by CPU-vs-video contention have a red background overlay.</item>
///   <item>A yellow border rectangle marks the exact visible viewport.</item>
/// </list>
/// All data is passed via Avalonia styled properties so data-binding updates trigger
/// <see cref="InvalidateVisual"/> automatically.
/// </summary>
public sealed class VramGridControl : Control
{
    // ── Constants ──────────────────────────────────────────────────────────

    private const int VramCols   = 80;
    private const int VramRows   = 24;
    private const int ViewportW  = 40;  // always 40 cols wide
    private const double CellH   = 14.0;
    private const double FontSz  = 10.5;

    private static readonly Typeface MonoFace =
        new Typeface(new FontFamily("Consolas,Courier New,monospace"));

    private static readonly IBrush TextBrush      = new SolidColorBrush(Color.Parse("#d4d4d4"));
    private static readonly IBrush ViewportBrush  = new SolidColorBrush(Color.FromArgb( 30, 100, 160, 255));
    private static readonly IBrush CorruptBrush   = new SolidColorBrush(Color.FromArgb(110, 200,  40,  40));
    private static readonly IPen   ViewportPen    = new Pen(new SolidColorBrush(Color.Parse("#ffd700")), 1.5);

    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<byte[]?> VramDataProperty =
        AvaloniaProperty.Register<VramGridControl, byte[]?>(nameof(VramData));

    public static readonly StyledProperty<bool[]?> CorruptionProperty =
        AvaloniaProperty.Register<VramGridControl, bool[]?>(nameof(Corruption));

    public static readonly StyledProperty<int> PanXProperty =
        AvaloniaProperty.Register<VramGridControl, int>(nameof(PanX));

    public static readonly StyledProperty<bool> ShowHexProperty =
        AvaloniaProperty.Register<VramGridControl, bool>(nameof(ShowHex));

    public byte[]? VramData
    {
        get => GetValue(VramDataProperty);
        set => SetValue(VramDataProperty, value);
    }

    public bool[]? Corruption
    {
        get => GetValue(CorruptionProperty);
        set => SetValue(CorruptionProperty, value);
    }

    public int PanX
    {
        get => GetValue(PanXProperty);
        set => SetValue(PanXProperty, value);
    }

    public bool ShowHex
    {
        get => GetValue(ShowHexProperty);
        set => SetValue(ShowHexProperty, value);
    }

    // ────────────────────────────────────────────────────────────────────────

    static VramGridControl()
    {
        // Invalidate on any data change so the control redraws.
        AffectsRender<VramGridControl>(VramDataProperty, CorruptionProperty,
                                       PanXProperty, ShowHexProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int charsPerCell = ShowHex ? 3 : 1;
        var ft = new FormattedText(
            new string('0', VramCols * charsPerCell),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoFace, FontSz, TextBrush);
        return new Size(ft.Width, VramRows * CellH);
    }

    public override void Render(DrawingContext ctx)
    {
        var vram       = VramData;
        var corruption = Corruption;
        int panX       = Math.Clamp(PanX, 0, VramCols - ViewportW);
        bool showHex   = ShowHex;

        // Measure the actual character pitch from the font — don't trust the hardcoded
        // CellW constant, which diverges from FormattedText at arbitrary DPI/font size.
        int charsPerCell = showHex ? 3 : 1;  // "HH " vs single glyph
        var measureFt = new FormattedText(
            new string('0', VramCols * charsPerCell),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            MonoFace, FontSz, TextBrush);
        double cellW = measureFt.Width / VramCols;

        if (vram == null || vram.Length < VramCols * VramRows)
        {
            // No data yet — blank grid
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#111")),
                new Rect(0, 0, VramCols * cellW, VramRows * CellH));
            return;
        }

        // ── 1. Viewport background highlight ──────────────────────────────
        double vpLeft = panX * cellW;
        ctx.FillRectangle(ViewportBrush,
            new Rect(vpLeft, 0, ViewportW * cellW, VramRows * CellH));

        // ── 2. Corruption backgrounds ─────────────────────────────────────
        if (corruption is { Length: VramRows * ViewportW })
        {
            for (int row = 0; row < VramRows; row++)
            for (int vcol = 0; vcol < ViewportW; vcol++)
            {
                if (corruption[row * ViewportW + vcol])
                {
                    int col = panX + vcol;
                    ctx.FillRectangle(CorruptBrush,
                        new Rect(col * cellW, row * CellH, cellW, CellH));
                }
            }
        }

        // ── 3. Text per row ───────────────────────────────────────────────
        Span<char> lineBuf = stackalloc char[showHex ? VramCols * 3 : VramCols];
        for (int row = 0; row < VramRows; row++)
        {
            int charsPerRow = 0;
            for (int col = 0; col < VramCols; col++)
            {
                byte b = vram[row * VramCols + col];
                if (showHex)
                {
                    lineBuf[charsPerRow++] = HexChar(b >> 4);
                    lineBuf[charsPerRow++] = HexChar(b & 0xF);
                    lineBuf[charsPerRow++] = ' ';
                }
                else
                {
                    lineBuf[charsPerRow++] = b is >= 0x20 and <= 0x7E ? (char)b : '·'; // ·
                }
            }

            var ft = new FormattedText(
                new string(lineBuf[..charsPerRow]),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                MonoFace,
                FontSz,
                TextBrush);

            ctx.DrawText(ft, new Point(0, row * CellH));
        }

        // ── 4. Viewport border ────────────────────────────────────────────
        ctx.DrawRectangle(null, ViewportPen,
            new Rect(vpLeft + 0.75, 0.75,
                     ViewportW * cellW - 1.5, VramRows * CellH - 1.5));
    }

    private static char HexChar(int nibble)
        => nibble < 10 ? (char)('0' + nibble) : (char)('A' + nibble - 10);
}

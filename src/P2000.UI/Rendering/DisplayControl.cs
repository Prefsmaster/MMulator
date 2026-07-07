using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using P2000.Machine.Contention;
using P2000.Machine.Devices;

namespace P2000.UI.Rendering;

/// <summary>
/// Renders the P2000T framebuffer into an Avalonia window (project CLAUDE.md §8).
/// Supports four display modes, integer scaling, PAL aspect, scanline effect, and a
/// contention-cell debug overlay. All settings are plain CLR properties updated by the
/// code-behind on each FrameReady callback.
/// </summary>
public sealed class DisplayControl : Control
{
    // ── Settings (updated by code-behind before each Present call) ────────────

    public DisplayMode Mode         { get; set; } = DisplayMode.Interlaced;
    public bool IntegerScale        { get; set; }
    public bool PalAspect           { get; set; } = true;
    public bool ShowScanlines       { get; set; }
    public bool ShowDebugOverlay    { get; set; }

    // ── Static brushes (allocated once) ──────────────────────────────────────

    // Dark bars between scanlines — subtle, 40% opacity black.
    private static readonly SolidColorBrush s_scanlineBrush =
        new(Color.FromArgb(102, 0, 0, 0));

    // Warm amber tint for corrupted character cells (contention debug overlay).
    private static readonly SolidColorBrush s_corruptionBrush =
        new(Color.FromArgb(140, 255, 140, 0));

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(Video.Width, Video.Height),
        new Vector(96, 96),
        PixelFormats.Bgra8888,
        AlphaFormat.Opaque);

    // Scratch buffer for line-doubling transforms (even-only / odd-only modes).
    private readonly uint[] _processed = new uint[Video.Width * Video.Height];

    // Stored per-Present call; used by Render() for the overlay and dest-rect drawing.
    private Rect _destRect;
    private bool[]? _pendingCorruption;

    // ── Present ───────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="pixels"/> into the bitmap and triggers a repaint,
    /// applying the current display mode. <paramref name="fieldWasOdd"/> is true when the
    /// ODD field just completed (used to gate Progressive / EvenOnly / OddOnly modes).
    /// <paramref name="corruption"/> is a 40×24 snapshot of the machine's CorruptionOverlay.
    /// Must be called on the UI thread.</summary>
    public void Present(uint[] pixels, bool fieldWasOdd, bool[] corruption)
    {
        bool shouldPresent = Mode switch
        {
            DisplayMode.Interlaced  => true,
            DisplayMode.Progressive => fieldWasOdd,
            DisplayMode.EvenOnly    => !fieldWasOdd,
            DisplayMode.OddOnly     => fieldWasOdd,
            _                       => true,
        };
        if (!shouldPresent) return;

        // Choose (possibly line-doubled) source array.
        uint[] source;
        if (Mode == DisplayMode.EvenOnly)
        {
            LineDoubleEvenRows(pixels, _processed);
            source = _processed;
        }
        else if (Mode == DisplayMode.OddOnly)
        {
            LineDoubleOddRows(pixels, _processed);
            source = _processed;
        }
        else
        {
            source = pixels;
        }

        CopyToWriteableBitmap(source);

        _pendingCorruption = ShowDebugOverlay ? corruption : null;

        InvalidateVisual();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        _destRect = ComputeDestRect(Bounds.Width, Bounds.Height);

        using (ctx.PushRenderOptions(new RenderOptions
               { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            ctx.DrawImage(_bitmap, _destRect);
        }

        if (ShowScanlines) DrawScanlines(ctx);
        if (ShowDebugOverlay && _pendingCorruption is not null) DrawCorruptionOverlay(ctx);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Computes the destination rectangle for the bitmap.
    /// IntegerScale: largest integer multiplier that fits, centered.
    /// PalAspect: maintain 4:3 ratio (letterbox/pillarbox), centered.
    /// Neither: stretch to fill.</summary>
    private Rect ComputeDestRect(double w, double h)
    {
        if (w <= 0 || h <= 0) return new Rect(0, 0, w, h);

        if (IntegerScale)
        {
            int n = Math.Max(1, (int)Math.Min(w / Video.Width, h / Video.Height));
            double sw = n * Video.Width, sh = n * Video.Height;
            return new Rect((w - sw) / 2, (h - sh) / 2, sw, sh);
        }
        if (PalAspect)
        {
            double ratio = Math.Min(w / Video.Width, h / Video.Height);
            double sw = Video.Width * ratio, sh = Video.Height * ratio;
            return new Rect((w - sw) / 2, (h - sh) / 2, sw, sh);
        }
        return new Rect(0, 0, w, h);
    }

    /// <summary>Draws semi-transparent dark bars over the lower half of each source scanline,
    /// simulating the gap between CRT scan lines.</summary>
    private void DrawScanlines(DrawingContext ctx)
    {
        double lineH = _destRect.Height / Video.Height;
        double barH  = lineH * 0.5;
        for (int row = 0; row < Video.Height; row++)
        {
            double y = _destRect.Y + row * lineH + barH;
            ctx.FillRectangle(s_scanlineBrush, new Rect(_destRect.X, y, _destRect.Width, barH));
        }
    }

    /// <summary>Overlays an amber tint on each character cell that was contention-corrupted
    /// in the last presented field (40×24 grid, each cell 16×20 source pixels).</summary>
    private void DrawCorruptionOverlay(DrawingContext ctx)
    {
        var overlay = _pendingCorruption!;
        double cellW = _destRect.Width  / VideoFetchUnit.Columns;
        double cellH = _destRect.Height / Video.CharRows;
        for (int row = 0; row < Video.CharRows; row++)
        for (int col = 0; col < VideoFetchUnit.Columns; col++)
        {
            if (!overlay[row * VideoFetchUnit.Columns + col]) continue;
            var r = new Rect(
                _destRect.X + col * cellW,
                _destRect.Y + row * cellH,
                cellW, cellH);
            ctx.FillRectangle(s_corruptionBrush, r);
        }
    }

    // ── Line-doubling transforms ──────────────────────────────────────────────

    /// <summary>Copies even source rows (0, 2, …) into pairs of dest rows, filling 480.</summary>
    private static void LineDoubleEvenRows(uint[] src, uint[] dst)
    {
        for (int r = 0; r < Video.Height; r += 2)
        {
            var srcRow = src.AsSpan(r * Video.Width, Video.Width);
            srcRow.CopyTo(dst.AsSpan(r       * Video.Width));
            srcRow.CopyTo(dst.AsSpan((r + 1) * Video.Width));
        }
    }

    /// <summary>Copies odd source rows (1, 3, …) into pairs of dest rows, filling 480.</summary>
    private static void LineDoubleOddRows(uint[] src, uint[] dst)
    {
        for (int r = 0; r < Video.Height; r += 2)
        {
            var srcRow = src.AsSpan((r + 1) * Video.Width, Video.Width);
            srcRow.CopyTo(dst.AsSpan(r       * Video.Width));
            srcRow.CopyTo(dst.AsSpan((r + 1) * Video.Width));
        }
    }

    // ── Bitmap copy ───────────────────────────────────────────────────────────

    private unsafe void CopyToWriteableBitmap(uint[] pixels)
    {
        using var fb = _bitmap.Lock();
        int srcStride = Video.Width * sizeof(uint);
        fixed (uint* src = pixels)
        {
            var srcPtr = (byte*)src;
            var dstPtr = (byte*)fb.Address;
            if (fb.RowBytes == srcStride)
            {
                Buffer.MemoryCopy(srcPtr, dstPtr,
                    (long)fb.RowBytes * fb.Size.Height,
                    (long)srcStride   * Video.Height);
            }
            else
            {
                for (int row = 0; row < Video.Height; row++)
                    Buffer.MemoryCopy(
                        srcPtr + row * srcStride,
                        dstPtr + row * fb.RowBytes,
                        srcStride, srcStride);
            }
        }
    }
}

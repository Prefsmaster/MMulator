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
/// Supports four display modes, integer scaling, PAL aspect, scanline effect, a
/// contention-cell debug overlay, and (2026-07-22) a Full-Field/Graphics-window crop toggle
/// orthogonal to the four display modes. All settings are plain CLR properties updated by the
/// code-behind on each FrameReady callback.
/// </summary>
public sealed class DisplayControl : Control
{
    // ── Settings (updated by code-behind before each Present call) ────────────

    public DisplayMode Mode         { get; set; } = DisplayMode.OddOnly;
    public bool IntegerScale        { get; set; }
    public bool PalAspect           { get; set; } = true;
    public bool ShowScanlines       { get; set; }
    public bool ShowDebugOverlay    { get; set; }

    private DisplayCrop _crop = DisplayCrop.GraphicsWindow;

    /// <summary>Full-Field vs Graphics-window (project CLAUDE.md §8). Reallocates the backing
    /// <see cref="WriteableBitmap"/> to match the new crop's pixel size when changed.</summary>
    public DisplayCrop Crop
    {
        get => _crop;
        set
        {
            if (_crop == value) return;
            _crop = value;
            ReallocateBitmap();
        }
    }

    private int DisplayWidth  => _crop == DisplayCrop.FullField ? Video.Width  : Video.ActiveWidth;
    private int DisplayHeight => _crop == DisplayCrop.FullField ? Video.Height : Video.ActiveHeight;

    // ── Static brushes (allocated once) ──────────────────────────────────────

    // Dark bars between scanlines — subtle, 40% opacity black.
    private static readonly SolidColorBrush s_scanlineBrush =
        new(Color.FromArgb(102, 0, 0, 0));

    // Warm amber tint for corrupted character cells (contention debug overlay).
    private static readonly SolidColorBrush s_corruptionBrush =
        new(Color.FromArgb(140, 255, 140, 0));

    // ── Internal state ────────────────────────────────────────────────────────

    // Sized to match the current Crop; reallocated by ReallocateBitmap() when Crop changes.
    private WriteableBitmap _bitmap = null!;

    // Scratch buffer for line-doubling transforms (even-only / odd-only modes). Always sized
    // to the FULL field buffer — line-doubling operates on the machine's raw source pixels
    // before cropping, which happens only at the final blit (CopyToWriteableBitmap).
    private readonly uint[] _processed = new uint[Video.Width * Video.Height];

    // Stored per-Present call; used by Render() for the overlay and dest-rect drawing.
    private Rect _destRect;
    private bool[]? _pendingCorruption;

    public DisplayControl()
    {
        ReallocateBitmap();
    }

    private void ReallocateBitmap()
    {
        _bitmap = new WriteableBitmap(
            new PixelSize(DisplayWidth, DisplayHeight),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);
    }

    // ── Present ───────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="pixels"/> into the bitmap and triggers a repaint,
    /// applying the current display mode. <paramref name="fieldWasOdd"/> is true when the
    /// ODD field just completed (used to gate Progressive / EvenOnly / OddOnly modes).
    /// <paramref name="corruption"/> is a 40×24 snapshot of the machine's CorruptionOverlay.
    /// <paramref name="pixels"/> is always the machine's full 928×626 field buffer, regardless
    /// of <see cref="Crop"/> — cropping happens at the final blit. Must be called on the UI
    /// thread.</summary>
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

        // Choose (possibly line-doubled) source array — always full-buffer-sized.
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
               { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
        {
            ctx.DrawImage(_bitmap, _destRect);
        }

        if (ShowScanlines) DrawScanlines(ctx);
        if (ShowDebugOverlay && _pendingCorruption is not null) DrawCorruptionOverlay(ctx);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Computes the destination rectangle for the bitmap.
    /// IntegerScale: largest integer multiplier that fits, centered.
    /// PalAspect (Graphics-window only — reference doc §3a/§4a: the blanking margins have no
    /// equivalent real-world standard to correct toward) OR Full-Field (always
    /// aspect-preserving, native pixel geometry, never a stretch): letterbox/pillarbox,
    /// centered, using the current crop's own true aspect ratio.
    /// Neither: stretch to fill.</summary>
    private Rect ComputeDestRect(double w, double h)
    {
        if (w <= 0 || h <= 0) return new Rect(0, 0, w, h);
        int dw = DisplayWidth, dh = DisplayHeight;

        if (IntegerScale)
        {
            // n must be an integer multiple of physical pixels, not logical pixels.
            // Bounds are in logical units; multiply by RenderScaling to get physical pixels,
            // compute n, then divide back so the Rect is in logical units.
            double scale = VisualRoot?.RenderScaling ?? 1.0;
            int n = Math.Max(1, (int)Math.Min(w * scale / dw, h * scale / dh));
            double sw = n * dw / scale;
            double sh = n * dh / scale;
            return new Rect((w - sw) / 2, (h - sh) / 2, sw, sh);
        }
        if (PalAspect || _crop == DisplayCrop.FullField)
        {
            double ratio = Math.Min(w / dw, h / dh);
            double sw = dw * ratio, sh = dh * ratio;
            return new Rect((w - sw) / 2, (h - sh) / 2, sw, sh);
        }
        return new Rect(0, 0, w, h);
    }

    /// <summary>Draws semi-transparent dark bars over the lower half of each source scanline,
    /// simulating the gap between CRT scan lines.</summary>
    private void DrawScanlines(DrawingContext ctx)
    {
        double lineH = _destRect.Height / DisplayHeight;
        double barH  = lineH * 0.5;
        for (int row = 0; row < DisplayHeight; row++)
        {
            double y = _destRect.Y + row * lineH + barH;
            ctx.FillRectangle(s_scanlineBrush, new Rect(_destRect.X, y, _destRect.Width, barH));
        }
    }

    /// <summary>Overlays an amber tint on each character cell that was contention-corrupted
    /// in the last presented field (40×24 grid, each cell 16×20 source pixels). Overlay
    /// indices are relative to the 640×480 active window, not the full buffer — when
    /// Full-Field is showing, the active window itself is a sub-rectangle of <see cref="_destRect"/>
    /// starting at (<see cref="Video.ActiveOffsetX"/>, <see cref="Video.ActiveOffsetY"/>);
    /// in Graphics-window mode that offset is zero since the whole dest rect already IS the
    /// active window.</summary>
    private void DrawCorruptionOverlay(DrawingContext ctx)
    {
        var overlay = _pendingCorruption!;
        double scaleX = _destRect.Width  / DisplayWidth;
        double scaleY = _destRect.Height / DisplayHeight;
        double activeOriginX = _destRect.X + (_crop == DisplayCrop.FullField ? Video.ActiveOffsetX * scaleX : 0);
        double activeOriginY = _destRect.Y + (_crop == DisplayCrop.FullField ? Video.ActiveOffsetY * scaleY : 0);
        double cellW = Video.ActiveWidth  * scaleX / VideoFetchUnit.Columns;
        double cellH = Video.ActiveHeight * scaleY / Video.CharRows;

        for (int row = 0; row < Video.CharRows; row++)
        for (int col = 0; col < VideoFetchUnit.Columns; col++)
        {
            if (!overlay[row * VideoFetchUnit.Columns + col]) continue;
            var r = new Rect(
                activeOriginX + col * cellW,
                activeOriginY + row * cellH,
                cellW, cellH);
            ctx.FillRectangle(s_corruptionBrush, r);
        }
    }

    // ── Line-doubling transforms ──────────────────────────────────────────────
    // Both operate on the FULL field buffer (Video.Width×Video.Height) unconditionally —
    // cropping happens later, at CopyToWriteableBitmap.

    /// <summary>Copies even source rows (0, 2, …) into pairs of dest rows, filling the buffer.</summary>
    private static void LineDoubleEvenRows(uint[] src, uint[] dst)
    {
        for (int r = 0; r < Video.Height; r += 2)
        {
            var srcRow = src.AsSpan(r * Video.Width, Video.Width);
            srcRow.CopyTo(dst.AsSpan(r       * Video.Width));
            srcRow.CopyTo(dst.AsSpan((r + 1) * Video.Width));
        }
    }

    /// <summary>Copies odd source rows (1, 3, …) into pairs of dest rows, filling the buffer.</summary>
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

    /// <summary>Copies <paramref name="pixels"/> (always full-buffer-sized) into <see cref="_bitmap"/>
    /// (sized to match <see cref="Crop"/>), cropping to the active window's sub-rectangle when
    /// <see cref="Crop"/> is <see cref="DisplayCrop.GraphicsWindow"/>.</summary>
    private unsafe void CopyToWriteableBitmap(uint[] pixels)
    {
        using var fb = _bitmap.Lock();
        int srcRowOffsetX = _crop == DisplayCrop.FullField ? 0 : Video.ActiveOffsetX;
        int srcRowOffsetY = _crop == DisplayCrop.FullField ? 0 : Video.ActiveOffsetY;
        int copyWidth = DisplayWidth;
        int copyStride = copyWidth * sizeof(uint);

        fixed (uint* src = pixels)
        {
            var dstPtr = (byte*)fb.Address;
            for (int row = 0; row < DisplayHeight; row++)
            {
                var srcPtr = (byte*)(src + (row + srcRowOffsetY) * Video.Width + srcRowOffsetX);
                Buffer.MemoryCopy(srcPtr, dstPtr + row * fb.RowBytes, fb.RowBytes, copyStride);
            }
        }
    }
}

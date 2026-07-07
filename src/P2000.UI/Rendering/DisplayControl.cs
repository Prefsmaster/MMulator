using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using P2000.Machine.Devices;

namespace P2000.UI.Rendering;

/// <summary>
/// Renders the P2000T framebuffer into an Avalonia window, scaled nearest-neighbour
/// to fill the control bounds (project CLAUDE.md §8: crisp pixel scaling at any size).
/// The 640×480 BGRA bitmap is created once; <see cref="Present"/> updates pixel data
/// and triggers a repaint.
/// </summary>
public sealed class DisplayControl : Control
{
    // Fixed 640×480 BGRA buffer — the machine's framebuffer dimensions are defined by
    // the SAA5050 renderer: 40 chars × 16 pixel-lanes wide, 24 rows × 20 scanlines high.
    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(Video.Width, Video.Height),
        new Vector(96, 96),
        PixelFormats.Bgra8888,
        AlphaFormat.Opaque);

    /// <summary>Copies <paramref name="pixels"/> (BGRA uint[640×480]) into the internal
    /// bitmap and schedules a repaint. Must be called on the UI thread.</summary>
    public unsafe void Present(uint[] pixels)
    {
        using var fb = _bitmap.Lock();
        int srcStride = Video.Width * sizeof(uint);

        fixed (uint* src = pixels)
        {
            var srcPtr = (byte*)src;
            var dstPtr = (byte*)fb.Address;

            if (fb.RowBytes == srcStride)
            {
                Buffer.MemoryCopy(srcPtr, dstPtr, (long)fb.RowBytes * fb.Size.Height, (long)srcStride * Video.Height);
            }
            else
            {
                // Stride padding present — copy row by row.
                for (int row = 0; row < Video.Height; row++)
                    Buffer.MemoryCopy(srcPtr + row * srcStride, dstPtr + row * fb.RowBytes, srcStride, srcStride);
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        using var _ = ctx.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None
        });
        ctx.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }
}

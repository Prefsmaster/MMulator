using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Devices;

public class VideoTests
{
    private static (Video Video, PageTable Memory) Create(MachineConfig? config = null)
    {
        var memory = new PageTable(config ?? new MachineConfig());
        return (new Video(memory), memory);
    }

    [Fact]
    public void FrontBuffer_IsSizedForTheConfirmedFramebufferContract()
    {
        var (video, _) = Create();

        Assert.Equal(Video.Width * Video.Height, video.FrontBuffer.Length);
        Assert.Equal(640, Video.Width);
        Assert.Equal(480, Video.Height);
    }

    [Fact]
    public void OneFrame_RendersKnownVramToExpectedPixels_AtRowZeroColumnZero()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'@'); // row 0, column 0 of the 80-wide buffer

        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            video.Tick();
        }

        var expected = ExpectedCellRow(Saa5050GlyphTables.Normal, '@', row: 0, fg: 7, bg: 0);
        var actual = new uint[16];
        Array.Copy(video.FrontBuffer, 0, actual, 0, 16); // y=0 (even field), x=0..15

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OneFrame_UnwrittenVram_RendersAsBlankSpace()
    {
        var (video, _) = Create();

        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            video.Tick();
        }

        var expectedIndex = (byte)((0 << 5) | (7 << 2)); // default fg=7, bg=0, blank glyph
        var expected = Saa5050Palette.ColorTable[expectedIndex];

        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.Equal(expected, video.FrontBuffer[pixel]);
        }
    }

    [Fact]
    public void PanX_ShiftsWhichVramColumnFeedsTheLeftmostOnScreenColumn()
    {
        var (video, memory) = Create();
        memory.Write((ushort)(PageTable.VideoRamStart + 5), (byte)'Z'); // buffer column 5
        video.PanX = 5;

        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            video.Tick();
        }

        var expected = ExpectedCellRow(Saa5050GlyphTables.Normal, 'Z', row: 0, fg: 7, bg: 0);
        var actual = new uint[16];
        Array.Copy(video.FrontBuffer, 0, actual, 0, 16); // panned into on-screen column 0

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Reset_ClearsBothBuffers()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'@');
        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            video.Tick();
        }

        video.Reset();

        Assert.All(video.FrontBuffer, pixel => Assert.Equal(0u, pixel));
    }

    private static uint[] ExpectedCellRow(uint[] glyphs, char code, int row, int fg, int bg)
    {
        var chardef = glyphs[(code - 0x20) * Saa5050GlyphTables.PackedRowsPerGlyph + row];
        var paletteIndex = (byte)((bg << 5) | (fg << 2));
        var expected = new uint[16];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            expected[pixel] = Saa5050Palette.ColorTable[paletteIndex + (chardef & 3)];
            chardef >>= 2;
        }

        return expected;
    }
}

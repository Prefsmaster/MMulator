using System.Reflection;
using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Devices;

public class VideoTests
{
    /// <summary>Flat pixel offset of VRAM row 0/column 0's EVEN sub-scanline within the
    /// full-field buffer — the active "graphics window" crop rectangle's origin (project
    /// CLAUDE.md §17, 2026-07-22 full-field change; reference doc §4a).</summary>
    private const int ActiveOrigin = Video.ActiveOffsetY * Video.Width + Video.ActiveOffsetX;

    /// <summary>Same cell's ODD sub-scanline (one output row below the even one).</summary>
    private const int OddRowOrigin = (Video.ActiveOffsetY + 1) * Video.Width + Video.ActiveOffsetX;

    private static (Video Video, PageTable Memory) Create(MachineConfig? config = null)
    {
        var memory = new PageTable(config ?? new MachineConfig());
        return (new Video(memory), memory);
    }

    private static void RunOneField(Video video)
    {
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            video.Tick();
        }
    }

    [Fact]
    public void Framebuffer_IsSizedForTheConfirmedFramebufferContract()
    {
        var (video, _) = Create();

        Assert.Equal(Video.Width * Video.Height, video.Framebuffer.Length);
        Assert.Equal(928, Video.Width);
        Assert.Equal(626, Video.Height);
        Assert.Equal(144, Video.ActiveOffsetX);
        Assert.Equal(98, Video.ActiveOffsetY);
        Assert.Equal(640, Video.ActiveWidth);
        Assert.Equal(480, Video.ActiveHeight);
    }

    [Fact]
    public void FirstField_IsEven_AndRendersOnlyEvenRows()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'@'); // row 0, column 0

        RunOneField(video);

        Assert.True(video.IsOddField); // toggled already, ready for the NEXT field to be odd
        var expected = ExpectedCellRow(Saa5050GlyphTables.Normal, '@', row: 0, fg: 7, bg: 0);
        var actualEvenRow = new uint[16];
        Array.Copy(video.Framebuffer, ActiveOrigin, actualEvenRow, 0, 16);
        Assert.Equal(expected, actualEvenRow);

        // The odd row (y=1) has not been touched by this field at all - still zero.
        for (var pixel = OddRowOrigin; pixel < OddRowOrigin + 16; pixel++)
        {
            Assert.Equal(0u, video.Framebuffer[pixel]);
        }
    }

    [Fact]
    public void SecondField_IsOdd_RendersOddRows_WithoutClearingTheEvenField()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'@');

        RunOneField(video); // even field
        var evenRowSnapshot = new uint[16];
        Array.Copy(video.Framebuffer, ActiveOrigin, evenRowSnapshot, 0, 16);

        RunOneField(video); // odd field

        // The even field's row is untouched (the "comb": no inter-field clear).
        var actualEvenRow = new uint[16];
        Array.Copy(video.Framebuffer, ActiveOrigin, actualEvenRow, 0, 16);
        Assert.Equal(evenRowSnapshot, actualEvenRow);

        // The odd row (y=1, glyph scan-line 1) is now populated.
        var expectedOddRow = ExpectedCellRow(Saa5050GlyphTables.Normal, '@', row: 1, fg: 7, bg: 0);
        var actualOddRow = new uint[16];
        Array.Copy(video.Framebuffer, OddRowOrigin, actualOddRow, 0, 16);
        Assert.Equal(expectedOddRow, actualOddRow);
    }

    [Fact]
    public void Comb_SecondField_ShowsUpdatedVram_WhileFirstFieldsRowStaysStale()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'A');

        RunOneField(video); // even field renders 'A' into row 0

        memory.Write(PageTable.VideoRamStart, (byte)'B'); // VRAM changes mid-motion
        RunOneField(video); // odd field renders 'B' into row 1

        var expectedStaleEvenRow = ExpectedCellRow(Saa5050GlyphTables.Normal, 'A', row: 0, fg: 7, bg: 0);
        var actualEvenRow = new uint[16];
        Array.Copy(video.Framebuffer, ActiveOrigin, actualEvenRow, 0, 16);
        Assert.Equal(expectedStaleEvenRow, actualEvenRow); // still 'A' - the comb

        var expectedFreshOddRow = ExpectedCellRow(Saa5050GlyphTables.Normal, 'B', row: 1, fg: 7, bg: 0);
        var actualOddRow = new uint[16];
        Array.Copy(video.Framebuffer, OddRowOrigin, actualOddRow, 0, 16);
        Assert.Equal(expectedFreshOddRow, actualOddRow);
    }

    [Fact]
    public void OneField_UnwrittenVram_RendersAsBlankSpace()
    {
        var (video, _) = Create();

        RunOneField(video);

        var expectedIndex = (byte)((0 << 5) | (7 << 2)); // default fg=7, bg=0, blank glyph
        var expected = Saa5050Palette.ColorTable[expectedIndex];

        for (var pixel = ActiveOrigin; pixel < ActiveOrigin + 16; pixel++)
        {
            Assert.Equal(expected, video.Framebuffer[pixel]);
        }
    }

    [Fact]
    public void PanX_ShiftsWhichVramColumnFeedsTheLeftmostOnScreenColumn()
    {
        var (video, memory) = Create();
        memory.Write((ushort)(PageTable.VideoRamStart + 5), (byte)'Z'); // buffer column 5
        video.PanX = 5;

        RunOneField(video);

        var expected = ExpectedCellRow(Saa5050GlyphTables.Normal, 'Z', row: 0, fg: 7, bg: 0);
        var actual = new uint[16];
        Array.Copy(video.Framebuffer, ActiveOrigin, actual, 0, 16); // panned into on-screen column 0

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FieldComplete_FiresOncePerField()
    {
        var (video, _) = Create();
        var completions = 0;
        video.FieldComplete += () => completions++;

        RunOneField(video);

        Assert.Equal(1, completions);
    }

    [Fact]
    public void FrameComplete_FiresOnlyAfterTheOddField_OnceEveryTwoFields()
    {
        var (video, _) = Create();
        var frameCompletions = 0;
        video.FrameComplete += () => frameCompletions++;

        RunOneField(video); // even field - not a complete frame yet
        Assert.Equal(0, frameCompletions);

        RunOneField(video); // odd field - both fields now rendered
        Assert.Equal(1, frameCompletions);

        RunOneField(video); // even field again
        Assert.Equal(1, frameCompletions);

        RunOneField(video); // odd field again
        Assert.Equal(2, frameCompletions);
    }

    [Fact]
    public void Reset_ClearsTheFramebuffer_AndReturnsToTheEvenField()
    {
        var (video, memory) = Create();
        memory.Write(PageTable.VideoRamStart, (byte)'@');
        RunOneField(video);
        RunOneField(video);

        video.Reset();

        Assert.All(video.Framebuffer, pixel => Assert.Equal(0u, pixel));
        Assert.False(video.IsOddField);
    }

    /// <summary>
    /// Real bug, owner-reported from a live screenshot (2026-07-22, after the full-field
    /// pre-roll fix landed): "the lowest scanline of a character row is swapped with the top."
    /// Root cause: <c>Saa5050Generator._scanLineCounter</c> (which of the 10 scanlines within
    /// the current character row is being rendered) advances once per
    /// <see cref="Video.OnLineComplete"/> call — and that fired unconditionally for EVERY raw
    /// line, including the 49-line vertical pre-roll now gating fetches
    /// (<see cref="VideoFetchUnit.VerticalBlankLines"/>). 49 unconditional advances before the
    /// first real scanline ever renders leaves the counter at 49 mod 10 = 9, not 0 — so the
    /// FIRST active scanline of every field renders using glyph row 9 (near the bottom of the
    /// character cell) instead of row 0 (the top), for the whole field.
    ///
    /// This is a DIRECT invariant check (reflection into the private counter), not a pixel
    /// comparison, because the existing pixel-based tests above (<c>FirstField_IsEven...</c>
    /// etc.) did NOT catch this bug when it was live: SAA5050 fonts pad the top and bottom
    /// scanlines of most glyphs with blank pixels, so "row 0" and "row 9" happened to render
    /// identically (both blank) for the specific test characters used, coincidentally masking
    /// exactly this class of off-by-N error. Confirmed by disabling the fix locally and
    /// re-running: this test failed (counter == 9), the pixel-based tests above did not.
    /// </summary>
    [Fact]
    public void FirstActiveFetch_ScanLineCounterIsZero_NotDesyncedByThePreRoll()
    {
        var (video, _) = Create();

        var generator = typeof(Video)
            .GetField("_generator", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(video)!;
        var counterField = generator.GetType()
            .GetField("_scanLineCounter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fetchUnit = (VideoFetchUnit)typeof(Video)
            .GetField("_fetchUnit", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(video)!;

        int? counterAtFirstFetch = null;
        fetchUnit.ColumnFetch += _ =>
            counterAtFirstFetch ??= (int)counterField.GetValue(generator)!;

        for (var i = 0; i < VideoFetchUnit.TStatesPerField && counterAtFirstFetch is null; i++)
        {
            video.Tick();
        }

        Assert.NotNull(counterAtFirstFetch);
        Assert.Equal(0, counterAtFirstFetch);
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

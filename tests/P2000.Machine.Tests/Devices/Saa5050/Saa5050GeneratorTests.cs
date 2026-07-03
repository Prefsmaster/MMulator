using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices.Saa5050;

public class Saa5050GeneratorTests
{
    // ---- Space is a blank glyph: every pixel is pure background --------------------------

    [Fact]
    public void Space_RendersPureBackground_DefaultAttributes()
    {
        var generator = new Saa5050Generator();
        generator.BeginCell(0x20, 0);
        var buffer = new uint[16];

        generator.RenderField(buffer, 0, oddField: false);

        var expectedIndex = (byte)((0 << 5) | (7 << 2)); // bg=0, fg=7 (defaults), pixel=0
        AssertAllPixels(buffer, Saa5050Palette.ColorTable[expectedIndex]);
    }

    [Fact]
    public void InvertedSpace_160Trick_SwapsForegroundAndBackgroundPositions()
    {
        var generator = new Saa5050Generator();
        generator.BeginCell(0xA0, 0); // 0x20 | 0x80 - inverted space (reference doc §5)
        var buffer = new uint[16];

        generator.RenderField(buffer, 0, oddField: false);

        var expectedIndex = (byte)((0 << 2) | (7 << 5)); // swapped shift positions
        AssertAllPixels(buffer, Saa5050Palette.ColorTable[expectedIndex]);
        // Not the same as the non-inverted case - the trick must be visible.
        Assert.NotEqual(Saa5050Palette.ColorTable[(0 << 5) | (7 << 2)], buffer[0]);
    }

    [Fact]
    public void ValueAbove160_RendersTheCharacter128Lower_WithForegroundAndBackgroundSwapped()
    {
        // 0xC1 = 'A' (0x41) + 0x80: must render 'A' (not literal byte 0xC1), with fg/bg
        // swapped (reference doc §5, confirmed: "value - 128, with inverted colours").
        var inverted = new Saa5050Generator();
        inverted.BeginCell(0xC1, 0);
        var invertedBuffer = new uint[16];
        inverted.RenderField(invertedBuffer, 0, oddField: false);

        // Swapping the fg/bg ARGUMENTS to ExpectedRow (fg=0, bg=7 instead of the generator's
        // actual fg=7, bg=0) reproduces the inverted palette-index formula exactly.
        var expected = ExpectedRow(Saa5050GlyphTables.Normal, 'A', row: 0, fg: 0, bg: 7);
        Assert.Equal(expected, invertedBuffer);
    }

    // ---- Control codes are set-after: the code cell itself doesn't show the new colour ----

    [Fact]
    public void ControlCode_SetsForeground_ForTheFollowingCellOnly()
    {
        var generator = new Saa5050Generator();

        generator.BeginCell(4, 0); // control code 4: foreground := 4 (set-after)
        generator.RenderField(new uint[16], 0, oddField: false); // consume the control cell

        generator.BeginCell((byte)'@', 1); // 0x40 - a non-blank alphanumeric glyph
        var buffer = new uint[16];
        generator.RenderField(buffer, 0, oddField: false);

        var expected = ExpectedRow(Saa5050GlyphTables.Normal, '@', row: 0, fg: 4, bg: 0);
        Assert.Equal(expected, buffer);
    }

    // ---- Hold-graphics: a control-code cell shows the last graphics glyph, not a gap -------

    [Fact]
    public void HoldGraphics_ControlCodeCell_ShowsTheHeldGraphicsGlyph()
    {
        var generator = new Saa5050Generator();

        generator.BeginCell(17, 0); // graphics mode, fg := 17 & 7 = 1 (set-after)
        generator.RenderField(new uint[16], 0, oddField: false);

        const byte graphicsChar = 0x25; // (0x25-0x20)=5, 5&32==0 -> a real graphics glyph
        generator.BeginCell(graphicsChar, 1);
        generator.RenderField(new uint[16], 0, oddField: false); // latches heldChar

        generator.BeginCell(30, 2); // control code 30: hold graphics
        var buffer = new uint[16];
        generator.RenderField(buffer, 0, oddField: false);

        var expected = ExpectedRow(Saa5050GlyphTables.Graphics, (char)graphicsChar, row: 0, fg: 1, bg: 0);
        Assert.Equal(expected, buffer);
    }

    // ---- Reset / EndLine / BeginFrame ------------------------------------------------------

    [Fact]
    public void EndLine_ResetsAttributesToRowDefaults()
    {
        var generator = new Saa5050Generator();
        generator.BeginCell(4, 0); // foreground := 4
        generator.RenderField(new uint[16], 0, oddField: false);

        generator.EndLine();

        generator.BeginCell(0x20, 0); // space, after the row reset
        var buffer = new uint[16];
        generator.RenderField(buffer, 0, oddField: false);

        var expectedIndex = (byte)((0 << 5) | (7 << 2)); // back to fg=7 default
        AssertAllPixels(buffer, Saa5050Palette.ColorTable[expectedIndex]);
    }

    [Fact]
    public void SaveState_ThenLoadState_RoundTripsRenderOutput()
    {
        var generator = new Saa5050Generator();
        generator.BeginCell(4, 0); // foreground := 4 (set-after; not yet visible)
        generator.RenderField(new uint[16], 0, oddField: false);

        var state = new InMemoryState();
        generator.SaveState(state);

        var restored = new Saa5050Generator();
        restored.LoadState(state.BeginRead());

        restored.BeginCell((byte)'@', 1);
        var buffer = new uint[16];
        restored.RenderField(buffer, 0, oddField: false);

        var expected = ExpectedRow(Saa5050GlyphTables.Normal, '@', row: 0, fg: 4, bg: 0);
        Assert.Equal(expected, buffer);
    }

    private static void AssertAllPixels(uint[] buffer, uint expected)
    {
        foreach (var pixel in buffer)
        {
            Assert.Equal(expected, pixel);
        }
    }

    private static uint[] ExpectedRow(uint[] glyphs, char code, int row, int fg, int bg)
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

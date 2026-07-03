namespace P2000.Machine.Devices.Saa5050;

/// <summary>
/// Pre-built hi-res glyph tables for the SAA5050 rounding pass (`docs/SAA5050-implementation.md`
/// §2) - ported from the project owner's C#/jsbeeb reference implementations, verified
/// bit-for-bit identical between the two. Each of the 96 characters gets 20 packed rows (10
/// physical scanlines × even/smoothed odd field, reference doc §4a / impl guide §1); each row
/// packs 16 pixel-lanes of 2 bits into a <c>uint</c> - the glyph-blending math that produces 16
/// lanes from 6 font columns is intentional (the anti-aliased "smooth teletext" trick), not a
/// bug; see the milestone 5 findings entry for how this was confirmed.
///
/// Three tables share one font (`Saa5050Font.CharacterData`): <see cref="Normal"/> (plain
/// alphanumerics), <see cref="Graphics"/> (contiguous 2×3 mosaic blocks), <see cref="Separated"/>
/// (mosaic blocks with a 1-dot gap). Graphics/separated characters 64-95 (`@A-Z` etc., reference
/// doc §5/impl guide §2) keep their alphanumeric glyphs and are still rounded - built once,
/// process-wide, since the table depends only on the fixed embedded font.
/// </summary>
internal static class Saa5050GlyphTables
{
    internal const int Columns = 6;
    internal const int Rows = 10;
    internal const int GlyphBytes = Columns * Rows;
    internal const int Characters = 96;
    internal const int PackedRowsPerGlyph = 20;

    internal static readonly uint[] Normal = BuildAlphanumeric();
    internal static readonly uint[] Graphics = BuildGraphics(separated: false);
    internal static readonly uint[] Separated = BuildGraphics(separated: true);

    internal static uint[] Select(Saa5050GlyphSet set) => set switch
    {
        Saa5050GlyphSet.Graphics => Graphics,
        Saa5050GlyphSet.Separated => Separated,
        _ => Normal,
    };

    private static uint[] BuildAlphanumeric()
    {
        var dest = new uint[Characters * PackedRowsPerGlyph];
        MakeHiResGlyphs(Saa5050Font.CharacterData, dest, roundGraphicsOnly: false);
        return dest;
    }

    private static uint[] BuildGraphics(bool separated)
    {
        var scratch = (byte[])Saa5050Font.CharacterData.Clone();

        for (var c = 0; c < Characters; c++)
        {
            if ((c & 32) != 0)
            {
                continue; // 64-95 ('@'..'_' etc.) keep their alphanumeric glyphs (confirmed, §2).
            }

            CreateGraphicsBlock(scratch, c, 0, 0, 3, 3, separated, (c & 1) != 0);
            CreateGraphicsBlock(scratch, c, 3, 0, 3, 3, separated, (c & 2) != 0);
            CreateGraphicsBlock(scratch, c, 0, 3, 3, 4, separated, (c & 4) != 0);
            CreateGraphicsBlock(scratch, c, 3, 3, 3, 4, separated, (c & 8) != 0);
            CreateGraphicsBlock(scratch, c, 0, 7, 3, 3, separated, (c & 16) != 0);
            CreateGraphicsBlock(scratch, c, 3, 7, 3, 3, separated, (c & 64) != 0);
        }

        var dest = new uint[Characters * PackedRowsPerGlyph];
        MakeHiResGlyphs(scratch, dest, roundGraphicsOnly: true);
        return dest;
    }

    private static void CreateGraphicsBlock(byte[] characterData, int glyph, int x, int y, int w, int h, bool separated, bool fill)
    {
        for (var yy = 0; yy < h; yy++)
        {
            for (var xx = 0; xx < w; xx++)
            {
                var offset = glyph * GlyphBytes + (y + yy) * Columns + (x + xx);
                var bit = separated && (xx == 0 || yy == h - 1) ? false : fill;
                characterData[offset] = (byte)(bit ? 1 : 0);
            }
        }
    }

    /// <summary>Six font columns, doubled to 12 raw bits, blended with the row above/below
    /// (<see cref="CombineRows"/>) and expanded into 16 anti-aliased 2-bit pixel lanes. The
    /// expansion multipliers are the hard-won part (impl guide §2/§9) - preserved verbatim.</summary>
    private static void MakeHiResGlyphs(byte[] characterData, uint[] dest, bool roundGraphicsOnly)
    {
        var index = 0;
        for (var c = 0; c < Characters; c++)
        {
            for (var row = 0; row < PackedRowsPerGlyph; row++)
            {
                ushort data;
                if (!roundGraphicsOnly || (c & 32) != 0)
                {
                    data = CombineRows(
                        GetLoResGlyphRow(characterData, c, row),
                        GetLoResGlyphRow(characterData, c, row + ((row & 1) != 0 ? 1 : -1)));
                }
                else
                {
                    data = GetLoResGlyphRow(characterData, c, row);
                }

                dest[index++] = (uint)((data & 0x1) * 0x7) +
                                (uint)((data & 0x2) * 0x14) +
                                (uint)((data & 0x4) * 0x34) +
                                (uint)((data & 0x8) * 0xE0) +
                                (uint)((data & 0x10) * 0x280) +
                                (uint)((data & 0x20) * 0x680) +
                                (uint)((data & 0x40) * 0x1C00) +
                                (uint)((data & 0x80) * 0x5000) +
                                (uint)((data & 0x100) * 0xD000) +
                                (uint)((data & 0x200) * 0x38000) +
                                (uint)((data & 0x400) * 0xA0000) +
                                (uint)((data & 0x800) * 0x1A0000);
            }
        }
    }

    private static ushort GetLoResGlyphRow(byte[] characterData, int glyph, int row)
    {
        if (row < 0 || row >= PackedRowsPerGlyph)
        {
            return 0;
        }

        var index = glyph * GlyphBytes + (row / 2) * Columns;
        ushort result = 0;
        for (var bit = 0; bit < 12; bit += 2)
        {
            result |= (ushort)((characterData[index++] * 3) << bit);
        }

        return result;
    }

    private static ushort CombineRows(ushort rowAbove, ushort rowBelow)
    {
        var part1 = rowAbove | ((rowAbove >> 1) & rowBelow & ~(rowBelow >> 1));
        var part2 = (rowAbove << 1) & rowBelow & ~(rowBelow << 1);
        return (ushort)(part1 | part2);
    }
}

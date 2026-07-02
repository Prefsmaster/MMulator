namespace Z80.Disassembler;

/// <summary>Hex rendering, "nnnnh" style (disassembler CLAUDE.md §8): zero-padded,
/// with a leading "0" inserted whenever the natural hex form would start with a
/// letter (A-F), so the token never looks like an identifier to an assembler.</summary>
public sealed partial class Disassembler
{
    private static string FormatImm8(byte v) => HexSafe(v.ToString("X2")) + "h";

    private static string FormatImm16(ushort v) => HexSafe(v.ToString("X4")) + "h";

    private static string FormatAddr(ushort v) => FormatImm16(v);

    /// <summary>Signed displacement for (IX+d)/(IY+d): e.g. "+05h" / "-03h".</summary>
    private static string FormatDisp(sbyte d) =>
        d >= 0 ? "+" + FormatImm8((byte)d) : "-" + FormatImm8((byte)-d);

    private static string HexSafe(string hex) => char.IsAsciiDigit(hex[0]) ? hex : "0" + hex;
}

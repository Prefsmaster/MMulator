using Z80.Core;
using CoreZ80 = Z80.Core.Z80;

namespace Z80.Disassembler;

/// <summary>
/// DD/FD-prefixed page (register substitution HL→IX/IY, H/L→IXH/IXL, (HL)→(IX+d)/
/// (IY+d)) and the DDCB/FDCB four-byte form. Mirrors Z80.Core/QuadrantDD.cs's
/// <c>DispatchIndexed</c>/<c>ExecuteIndexed</c>/<c>ExecuteDdCb</c> exactly,
/// including calling the SAME <see cref="CoreZ80.IsIndexAffected"/> classifier
/// (internal, exposed via <c>InternalsVisibleTo</c>) so "affected vs. wasted
/// prefix" can never drift from the core's decision.
/// </summary>
public sealed partial class Disassembler
{
    private static string DecodeIndexed(bool isIx, byte opcode, Session s)
    {
        // Opcodes the core doesn't route through DispatchIndexed just run as the
        // base-page instruction (wasted prefix — the DD/FD byte is still consumed
        // as part of this instruction's length, but contributes no substitution).
        if (!CoreZ80.IsIndexAffected(opcode))
            return DecodeBase(opcode, s);

        var indexName = isIx ? "IX" : "IY";

        // Quadrant-11 collision: must be checked BEFORE the z-field switch, same
        // as the core (their z/y values collide with quadrant-00 cases).
        if (opcode is >= 0xC0)
        {
            return opcode switch
            {
                0xE1 => $"POP {indexName}",
                0xE3 => $"EX (SP),{indexName}",
                0xE5 => $"PUSH {indexName}",
                0xE9 => $"JP ({indexName})",
                0xF9 => $"LD SP,{indexName}",
                _ => throw new InvalidOperationException($"DecodeIndexed: unhandled opcode 0x{opcode:X2}"),
            };
        }

        var z = opcode & 7;
        var y = (opcode >> 3) & 7;
        var q = (opcode >> 3) & 1;
        var p = (opcode >> 4) & 3;

        string IndexedR(int i) => i switch
        {
            4 => isIx ? "IXH" : "IYH",
            5 => isIx ? "IXL" : "IYL",
            _ => Z80Tables.R[i],
        };

        // LD r,r' block (0x40-0x7F, not 0x76): one or both sides touch H/L/(HL).
        if (opcode is >= 0x40 and <= 0x7F and not 0x76)
        {
            var dst = y;
            var src = z;
            if (dst != 6 && src != 6)
                return $"LD {IndexedR(dst)},{IndexedR(src)}";

            var d = s.Reader.NextSigned();
            var indexed = $"({indexName}{FormatDisp(d)})";
            // Mixed-operand quirk: the H/L side stays the REAL H/L (plain
            // Z80Tables.R), never IXH/IXL, when the other side is (IX+d).
            return src == 6 ? $"LD {Z80Tables.R[dst]},{indexed}" : $"LD {indexed},{Z80Tables.R[src]}";
        }

        // ALU A,r / A,(IX+d) (0x80-0xBF): src touches H/L/(HL).
        if (opcode is >= 0x80 and <= 0xBF)
        {
            var src = z;
            if (src != 6) return $"{Z80Tables.Alu[y]}{IndexedR(src)}";
            var d = s.Reader.NextSigned();
            return $"{Z80Tables.Alu[y]}({indexName}{FormatDisp(d)})";
        }

        // Quadrant-00 affected ops.
        switch (z)
        {
            case 1:
                if (q == 0 && p == 2)
                {
                    var nn = s.Reader.NextWord();
                    return $"LD {indexName},{FormatImm16(nn)}";
                }
                // ADD IX,rp: under this prefix, Get16(2) resolves to the index
                // register itself (self-add is possible: ADD IX,IX).
                var rpName = p == 2 ? indexName : Z80Tables.Rp[p];
                return $"ADD {indexName},{rpName}";

            case 2:
            {
                var nn = s.Reader.NextWord();
                s.SymbolTarget = nn;
                return q == 0 ? $"LD ({FormatAddr(nn)}),{indexName}" : $"LD {indexName},({FormatAddr(nn)})";
            }

            case 3:
                return q == 0 ? $"INC {indexName}" : $"DEC {indexName}";

            case 4:
                if (y == 6)
                {
                    var d = s.Reader.NextSigned();
                    return $"INC ({indexName}{FormatDisp(d)})";
                }
                return $"INC {IndexedR(y)}";

            case 5:
                if (y == 6)
                {
                    var d = s.Reader.NextSigned();
                    return $"DEC ({indexName}{FormatDisp(d)})";
                }
                return $"DEC {IndexedR(y)}";

            case 6:
                if (y == 6)
                {
                    var d = s.Reader.NextSigned();
                    var n = s.Reader.Next();
                    return $"LD ({indexName}{FormatDisp(d)}),{FormatImm8(n)}";
                }
                var imm = s.Reader.Next();
                return $"LD {IndexedR(y)},{FormatImm8(imm)}";

            default:
                throw new InvalidOperationException($"DecodeIndexed: unhandled opcode 0x{opcode:X2}");
        }
    }

    // ---- DDCB/FDCB ---------------------------------------------------------------

    /// <summary>The four-byte form (DD/FD, CB, d, op — displacement BEFORE the
    /// CB-table opcode byte, disassembler CLAUDE.md §9). Mirrors
    /// <c>Z80.ExecuteDdCb</c>: for z≠6 the result is ALSO stored into register
    /// r[z] (undocumented dual-write); BIT has no write-back so no dual-write.</summary>
    private static string DecodeIndexedCb(bool isIx, sbyte d, byte cbOp)
    {
        var indexName = isIx ? "IX" : "IY";
        var x = (cbOp >> 6) & 3;
        var y = (cbOp >> 3) & 7;
        var z = cbOp & 7;
        var target = $"({indexName}{FormatDisp(d)})";

        var text = x switch
        {
            0 => $"{Z80Tables.Rot[y]} {target}",
            1 => $"BIT {y},{target}",
            2 => $"RES {y},{target}",
            3 => $"SET {y},{target}",
            _ => throw new InvalidOperationException(),
        };

        if (x != 1 && z != 6) text += $",{Z80Tables.R[z]}"; // undocumented dual-write
        return text;
    }
}

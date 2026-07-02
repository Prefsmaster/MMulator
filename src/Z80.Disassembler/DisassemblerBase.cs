using Z80.Core;

namespace Z80.Disassembler;

/// <summary>
/// The unprefixed (base-page) decoder. Mirrors <c>Z80.Dispatch</c> in
/// Z80.Core/Opcodes.cs exactly: same HALT/LD-r,r'/ALU-A,r/quadrant-00/quadrant-11
/// routing, same z/y/p/q bit fields, reading operand order from
/// <see cref="Z80Tables"/> instead of executing behaviour.
/// </summary>
public sealed partial class Disassembler
{
    private static string DecodeBase(byte opcode, Session s)
    {
        if (opcode == 0x76) return "HALT";
        if (opcode is >= 0x40 and <= 0x7F) return DecodeLdRR(opcode);
        if (opcode is >= 0x80 and <= 0xBF) return DecodeAluR(opcode);
        if (opcode <= 0x3F) return DecodeQuadrant00(opcode, s);
        return DecodeQuadrant11(opcode, s);
    }

    // ---- 0x40-0x7F LD r,r' (0x76 HALT handled above) --------------------------

    private static string DecodeLdRR(byte opcode)
    {
        var dst = (opcode >> 3) & 7;
        var src = opcode & 7;
        return $"LD {Z80Tables.R[dst]},{Z80Tables.R[src]}";
    }

    // ---- 0x80-0xBF ALU A,r ------------------------------------------------------

    private static string DecodeAluR(byte opcode)
    {
        var y = (opcode >> 3) & 7;
        var src = opcode & 7;
        return $"{Z80Tables.Alu[y]}{Z80Tables.R[src]}";
    }

    // ---- Quadrant 00: 0x00-0x3F -------------------------------------------------

    private static string DecodeQuadrant00(byte opcode, Session s)
    {
        var z = opcode & 7;
        return z switch
        {
            0 => DecodeZ0(opcode, s),
            1 => DecodeZ1(opcode, s),
            2 => DecodeZ2(opcode, s),
            3 => DecodeIncDecRp(opcode),
            4 => $"INC {Z80Tables.R[(opcode >> 3) & 7]}",
            5 => $"DEC {Z80Tables.R[(opcode >> 3) & 7]}",
            6 => DecodeLdRN(opcode, s),
            7 => DecodeAccMisc(opcode),
            _ => throw new InvalidOperationException(),
        };
    }

    private static string DecodeZ0(byte opcode, Session s)
    {
        var y = (opcode >> 3) & 7;
        switch (y)
        {
            case 0: return "NOP";
            case 1: return "EX AF,AF'";
            case 2: return $"DJNZ {RelTarget(s)}";
            case 3: return $"JR {RelTarget(s)}";
            default: return $"JR {Z80Tables.Cc[y - 4]},{RelTarget(s)}";
        }
    }

    /// <summary>JR/DJNZ target: the displacement is relative to PC AFTER this
    /// (now-complete) 2-byte instruction, matching the core's
    /// <c>_reg.PC + _displacement</c> where PC was already advanced past both
    /// opcode and displacement bytes.</summary>
    private static string RelTarget(Session s)
    {
        var d = s.Reader.NextSigned();
        var target = (ushort)(s.BaseAddress + s.Reader.Length + d);
        s.SymbolTarget = target;
        return FormatAddr(target);
    }

    private static string DecodeZ1(byte opcode, Session s)
    {
        var q = (opcode >> 3) & 1;
        var p = (opcode >> 4) & 3;
        if (q == 1) return $"ADD HL,{Z80Tables.Rp[p]}";
        var nn = s.Reader.NextWord();
        return $"LD {Z80Tables.Rp[p]},{FormatImm16(nn)}";
    }

    private static string DecodeZ2(byte opcode, Session s)
    {
        var q = (opcode >> 3) & 1;
        var p = (opcode >> 4) & 3;

        if (p < 2)
        {
            var rp = p == 0 ? "BC" : "DE";
            return q == 0 ? $"LD ({rp}),A" : $"LD A,({rp})";
        }

        var nn = s.Reader.NextWord();
        s.SymbolTarget = nn;
        if (p == 3) return q == 0 ? $"LD ({FormatAddr(nn)}),A" : $"LD A,({FormatAddr(nn)})";
        return q == 0 ? $"LD ({FormatAddr(nn)}),HL" : $"LD HL,({FormatAddr(nn)})";
    }

    private static string DecodeIncDecRp(byte opcode)
    {
        var p = (opcode >> 4) & 3;
        var q = (opcode >> 3) & 1;
        return q == 0 ? $"INC {Z80Tables.Rp[p]}" : $"DEC {Z80Tables.Rp[p]}";
    }

    private static string DecodeLdRN(byte opcode, Session s)
    {
        var y = (opcode >> 3) & 7;
        var n = s.Reader.Next();
        return $"LD {Z80Tables.R[y]},{FormatImm8(n)}";
    }

    private static string DecodeAccMisc(byte opcode)
    {
        var y = (opcode >> 3) & 7;
        return y switch
        {
            0 => "RLCA",
            1 => "RRCA",
            2 => "RLA",
            3 => "RRA",
            4 => "DAA",
            5 => "CPL",
            6 => "SCF",
            7 => "CCF",
            _ => throw new InvalidOperationException(),
        };
    }

    // ---- Quadrant 11: 0xC0-0xFF --------------------------------------------------

    private static string DecodeQuadrant11(byte opcode, Session s)
    {
        var z = opcode & 7;
        return z switch
        {
            0 => $"RET {Z80Tables.Cc[(opcode >> 3) & 7]}",
            1 => DecodeZ1Quadrant11(opcode),
            2 => DecodeJpCc(opcode, s),
            3 => DecodeZ3(opcode, s),
            4 => DecodeCallCc(opcode, s),
            5 => DecodeZ5(opcode, s),
            6 => DecodeAluN(opcode, s),
            7 => DecodeRst(opcode, s),
            _ => throw new InvalidOperationException(),
        };
    }

    private static string DecodeZ1Quadrant11(byte opcode)
    {
        var q = (opcode >> 3) & 1;
        var p = (opcode >> 4) & 3;
        if (q == 0) return $"POP {Z80Tables.Rp2[p]}";
        return p switch
        {
            0 => "RET",
            1 => "EXX",
            2 => "JP (HL)",
            3 => "LD SP,HL",
            _ => throw new InvalidOperationException(),
        };
    }

    private static string DecodeJpCc(byte opcode, Session s)
    {
        var cc = (opcode >> 3) & 7;
        var nn = s.Reader.NextWord();
        s.SymbolTarget = nn;
        return $"JP {Z80Tables.Cc[cc]},{FormatAddr(nn)}";
    }

    private static string DecodeZ3(byte opcode, Session s)
    {
        var y = (opcode >> 3) & 7;
        switch (y)
        {
            case 0:
                var nn = s.Reader.NextWord();
                s.SymbolTarget = nn;
                return $"JP {FormatAddr(nn)}";
            case 2:
            {
                var n = s.Reader.Next();
                s.PortTarget = n;
                return $"OUT ({FormatImm8(n)}),A";
            }
            case 3:
            {
                var n = s.Reader.Next();
                s.PortTarget = n;
                return $"IN A,({FormatImm8(n)})";
            }
            case 4: return "EX (SP),HL";
            case 5: return "EX DE,HL";
            case 6: return "DI";
            case 7: return "EI";
            default: throw new InvalidOperationException($"DecodeZ3: unreachable y={y} (CB is intercepted before reaching here).");
        }
    }

    private static string DecodeCallCc(byte opcode, Session s)
    {
        var cc = (opcode >> 3) & 7;
        var nn = s.Reader.NextWord();
        s.SymbolTarget = nn;
        return $"CALL {Z80Tables.Cc[cc]},{FormatAddr(nn)}";
    }

    private static string DecodeZ5(byte opcode, Session s)
    {
        var q = (opcode >> 3) & 1;
        if (q == 0)
        {
            var p = (opcode >> 4) & 3;
            return $"PUSH {Z80Tables.Rp2[p]}";
        }
        var nn = s.Reader.NextWord();
        s.SymbolTarget = nn;
        return $"CALL {FormatAddr(nn)}";
    }

    private static string DecodeAluN(byte opcode, Session s)
    {
        var y = (opcode >> 3) & 7;
        var n = s.Reader.Next();
        return $"{Z80Tables.Alu[y]}{FormatImm8(n)}";
    }

    private static string DecodeRst(byte opcode, Session s)
    {
        var y = (opcode >> 3) & 7;
        var target = (ushort)(y * 8);
        s.SymbolTarget = target;
        return $"RST {FormatAddr(target)}";
    }
}

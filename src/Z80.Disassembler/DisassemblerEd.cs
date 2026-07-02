using Z80.Core;

namespace Z80.Disassembler;

/// <summary>The ED-prefixed page. Mirrors <c>Z80.DispatchED</c>/<c>ExecuteED</c> in
/// Z80.Core/QuadrantED.cs: x==1 is the "main" ED group (IN/OUT, SBC/ADC HL,rp,
/// LD (nn)/rp, NEG, IM, LD I/R/A, RRD/RLD, RETN/RETI); x==2 with y>=4,z&lt;=3 is
/// the 16 block instructions; everything else is an undefined ED opcode that
/// behaves as (and is rendered as) a 2-byte NOP on real silicon.</summary>
public sealed partial class Disassembler
{
    private static string DecodeEd(byte op, Session s)
    {
        var x = (op >> 6) & 3;
        var y = (op >> 3) & 7;
        var z = op & 7;

        if (x == 1) return DecodeEdMain(op, y, z, s);
        if (x == 2 && y >= 4 && z <= 3) return DecodeEdBlock(y, z);
        return UndefinedEd(op);
    }

    private static string UndefinedEd(byte op) => $"DB {FormatImm8(0xED)},{FormatImm8(op)}";

    private static string DecodeEdMain(byte op, int y, int z, Session s)
    {
        switch (z)
        {
            case 0: return y == 6 ? "IN (C)" : $"IN {Z80Tables.R[y]},(C)"; // y=6: undocumented, flags only
            case 1: return y == 6 ? "OUT (C),0" : $"OUT (C),{Z80Tables.R[y]}"; // y=6: undocumented
            case 2:
            {
                var p = y >> 1;
                var mnemonic = (y & 1) == 0 ? "SBC HL," : "ADC HL,";
                return mnemonic + Z80Tables.Rp[p];
            }
            case 3:
            {
                var p = y >> 1;
                var nn = s.Reader.NextWord();
                s.SymbolTarget = nn;
                return (y & 1) == 0
                    ? $"LD ({FormatAddr(nn)}),{Z80Tables.Rp[p]}"
                    : $"LD {Z80Tables.Rp[p]},({FormatAddr(nn)})";
            }
            case 4: return "NEG";
            case 5: return y == 1 ? "RETI" : "RETN"; // y!=1: undocumented RETN duplicate
            case 6: return $"IM {Z80Tables.Im[y]}";
            case 7:
                return y switch
                {
                    0 => "LD I,A",
                    1 => "LD R,A",
                    2 => "LD A,I",
                    3 => "LD A,R",
                    4 => "RRD",
                    5 => "RLD",
                    _ => UndefinedEd(op), // y=6,7: undefined, 2-byte NOP (matches core's DispatchEDMain z=7 default)
                };
            default: throw new InvalidOperationException();
        }
    }

    private static string DecodeEdBlock(int y, int z) => Z80Tables.BlockOps[z, y - 4];
}

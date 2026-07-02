using Z80.Core;

namespace Z80.Disassembler;

/// <summary>The CB-prefixed page: rotate/shift, BIT, RES, SET. Mirrors
/// <c>Z80.DispatchCB</c>/<c>ApplyCbRotate</c> in Z80.Core/QuadrantCB.cs.</summary>
public sealed partial class Disassembler
{
    private static string DecodeCb(byte op)
    {
        var x = (op >> 6) & 3;
        var y = (op >> 3) & 7;
        var z = op & 7;
        var r = Z80Tables.R[z];

        return x switch
        {
            0 => $"{Z80Tables.Rot[y]} {r}",
            1 => $"BIT {y},{r}",
            2 => $"RES {y},{r}",
            3 => $"SET {y},{r}",
            _ => throw new InvalidOperationException(),
        };
    }
}

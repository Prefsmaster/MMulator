using System.Numerics;

namespace Z80.Core;

/// <summary>
/// Pure 8/16-bit ALU operations and flag computation, including the undocumented
/// bit 3/5 (XF/YF) flags. Every method is a pure function of its inputs (plus,
/// where the real chip preserves or depends on prior flags, an explicit
/// <c>oldFlags</c> parameter) — no CPU state is touched here. See CLAUDE.md §6.
/// </summary>
public static class Alu
{
    public const byte CF = 0x01; // Carry
    public const byte NF = 0x02; // Add/Subtract
    public const byte PF = 0x04; // Parity/Overflow
    public const byte XF = 0x08; // Undocumented, = bit 3 of result
    public const byte HF = 0x10; // Half carry
    public const byte YF = 0x20; // Undocumented, = bit 5 of result
    public const byte ZF = 0x40; // Zero
    public const byte SF = 0x80; // Sign

    private static bool Parity(byte v) => (BitOperations.PopCount(v) & 1) == 0;

    /// <summary>Sign + Zero + the undocumented Y/X flags, all derived from the result byte.</summary>
    private static byte SZYX(byte result) =>
        (byte)((result & (SF | YF | XF)) | (result == 0 ? ZF : 0));

    // ---- 8-bit add/subtract -------------------------------------------------

    public static (byte Result, byte Flags) Add8(byte a, byte b) => AddCore(a, b, false);

    public static (byte Result, byte Flags) Adc8(byte a, byte b, bool carryIn) => AddCore(a, b, carryIn);

    private static (byte Result, byte Flags) AddCore(byte a, byte b, bool carryIn)
    {
        int c = carryIn ? 1 : 0;
        int sum = a + b + c;
        var result = (byte)sum;
        var flags = SZYX(result);
        if (((a & 0x0F) + (b & 0x0F) + c) > 0x0F) flags |= HF;
        if (sum > 0xFF) flags |= CF;
        if (((a ^ result) & (b ^ result) & 0x80) != 0) flags |= PF;
        return (result, flags);
    }

    public static (byte Result, byte Flags) Sub8(byte a, byte b) => SubCore(a, b, false);

    public static (byte Result, byte Flags) Sbc8(byte a, byte b, bool carryIn) => SubCore(a, b, carryIn);

    /// <summary>NEG: A = 0 - A.</summary>
    public static (byte Result, byte Flags) Neg8(byte a) => SubCore(0, a, false);

    private static (byte Result, byte Flags) SubCore(byte a, byte b, bool carryIn)
    {
        int c = carryIn ? 1 : 0;
        int diff = a - b - c;
        var result = (byte)diff;
        var flags = (byte)(SZYX(result) | NF);
        if (((a & 0x0F) - (b & 0x0F) - c) < 0) flags |= HF;
        if (diff < 0) flags |= CF;
        if (((a ^ b) & (a ^ result) & 0x80) != 0) flags |= PF;
        return (result, flags);
    }

    /// <summary>CP: like SUB but the result is discarded (A unchanged), and unlike
    /// every other arithmetic op the undocumented Y/X flags come from the
    /// *operand*, not the result (Sean Young, "The Undocumented Z80 Documented").</summary>
    public static byte Cp8(byte a, byte b)
    {
        var (_, flags) = SubCore(a, b, false);
        return (byte)((flags & ~(YF | XF)) | (b & (YF | XF)));
    }

    // ---- 8-bit logic ----------------------------------------------------------

    public static (byte Result, byte Flags) And8(byte a, byte b)
    {
        var result = (byte)(a & b);
        var flags = (byte)(SZYX(result) | HF | (Parity(result) ? PF : 0));
        return (result, flags);
    }

    public static (byte Result, byte Flags) Or8(byte a, byte b)
    {
        var result = (byte)(a | b);
        var flags = (byte)(SZYX(result) | (Parity(result) ? PF : 0));
        return (result, flags);
    }

    public static (byte Result, byte Flags) Xor8(byte a, byte b)
    {
        var result = (byte)(a ^ b);
        var flags = (byte)(SZYX(result) | (Parity(result) ? PF : 0));
        return (result, flags);
    }

    // ---- 8-bit increment/decrement (carry is preserved, not computed) --------

    public static (byte Result, byte Flags) Inc8(byte a, byte oldFlags)
    {
        var result = (byte)(a + 1);
        var flags = (byte)(SZYX(result) | (oldFlags & CF));
        if ((a & 0x0F) == 0x0F) flags |= HF;
        if (a == 0x7F) flags |= PF;
        return (result, flags);
    }

    public static (byte Result, byte Flags) Dec8(byte a, byte oldFlags)
    {
        var result = (byte)(a - 1);
        var flags = (byte)(SZYX(result) | NF | (oldFlags & CF));
        if ((a & 0x0F) == 0x00) flags |= HF;
        if (a == 0x80) flags |= PF;
        return (result, flags);
    }

    // ---- 16-bit add/adc/sbc ----------------------------------------------------

    /// <summary>ADD HL,ss / ADD IX,pp / ADD IY,pp: only H, N, C (and Y/X from the
    /// high byte of the result) are affected; S, Z, P/V are left untouched.</summary>
    public static (ushort Result, byte Flags) Add16(ushort a, ushort b, byte oldFlags)
    {
        int sum = a + b;
        var result = (ushort)sum;
        var hi = (byte)(result >> 8);
        var flags = (byte)((oldFlags & (SF | ZF | PF)) | (hi & (YF | XF)));
        if (((a & 0x0FFF) + (b & 0x0FFF)) > 0x0FFF) flags |= HF;
        if (sum > 0xFFFF) flags |= CF;
        return (result, flags);
    }

    /// <summary>ADC HL,ss (ED-prefixed): full 16-bit flag set.</summary>
    public static (ushort Result, byte Flags) Adc16(ushort a, ushort b, bool carryIn) => AddCore16(a, b, carryIn);

    /// <summary>SBC HL,ss (ED-prefixed): full 16-bit flag set.</summary>
    public static (ushort Result, byte Flags) Sbc16(ushort a, ushort b, bool carryIn) => SubCore16(a, b, carryIn);

    private static (ushort Result, byte Flags) AddCore16(ushort a, ushort b, bool carryIn)
    {
        int c = carryIn ? 1 : 0;
        int sum = a + b + c;
        var result = (ushort)sum;
        var hi = (byte)(result >> 8);
        var flags = (byte)((hi & (SF | YF | XF)) | (result == 0 ? ZF : 0));
        if (((a & 0x0FFF) + (b & 0x0FFF) + c) > 0x0FFF) flags |= HF;
        if (sum > 0xFFFF) flags |= CF;
        if (((a ^ result) & (b ^ result) & 0x8000) != 0) flags |= PF;
        return (result, flags);
    }

    private static (ushort Result, byte Flags) SubCore16(ushort a, ushort b, bool carryIn)
    {
        int c = carryIn ? 1 : 0;
        int diff = a - b - c;
        var result = (ushort)diff;
        var hi = (byte)(result >> 8);
        var flags = (byte)(NF | (hi & (SF | YF | XF)) | (result == 0 ? ZF : 0));
        if (((a & 0x0FFF) - (b & 0x0FFF) - c) < 0) flags |= HF;
        if (diff < 0) flags |= CF;
        if (((a ^ b) & (a ^ result) & 0x8000) != 0) flags |= PF;
        return (result, flags);
    }

    // ---- rotate/shift (generic, CB-prefixed form: full S/Z/Y/H=0/X/P/N=0/C) --

    public static (byte Result, byte Flags) Rlc8(byte a)
    {
        var carry = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)((a << 1) | carry);
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Rrc8(byte a)
    {
        var carry = (byte)(a & 0x01);
        var result = (byte)((a >> 1) | (carry << 7));
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Rl8(byte a, bool carryIn)
    {
        var carry = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)((a << 1) | (carryIn ? 1 : 0));
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Rr8(byte a, bool carryIn)
    {
        var carry = (byte)(a & 0x01);
        var result = (byte)((a >> 1) | (carryIn ? 0x80 : 0));
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Sla8(byte a)
    {
        var carry = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)(a << 1);
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Sra8(byte a)
    {
        var carry = (byte)(a & 0x01);
        var result = (byte)((a >> 1) | (a & 0x80));
        return (result, ShiftFlags(result, carry));
    }

    /// <summary>SLL/SLI: undocumented shift-left that shifts a 1 into bit 0.</summary>
    public static (byte Result, byte Flags) Sll8(byte a)
    {
        var carry = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)((a << 1) | 1);
        return (result, ShiftFlags(result, carry));
    }

    public static (byte Result, byte Flags) Srl8(byte a)
    {
        var carry = (byte)(a & 0x01);
        var result = (byte)(a >> 1);
        return (result, ShiftFlags(result, carry));
    }

    private static byte ShiftFlags(byte result, byte carryOut) =>
        (byte)(SZYX(result) | (Parity(result) ? PF : 0) | carryOut);

    // ---- accumulator-only rotates (RLCA/RLA/RRCA/RRA): S/Z/P-V preserved -----

    public static (byte Result, byte Flags) Rlca(byte a, byte oldFlags)
    {
        var carry = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)((a << 1) | carry);
        return (result, AccRotateFlags(result, carry, oldFlags));
    }

    public static (byte Result, byte Flags) Rrca(byte a, byte oldFlags)
    {
        var carry = (byte)(a & 0x01);
        var result = (byte)((a >> 1) | (carry << 7));
        return (result, AccRotateFlags(result, carry, oldFlags));
    }

    public static (byte Result, byte Flags) Rla(byte a, byte oldFlags)
    {
        var carryIn = (oldFlags & CF) != 0;
        var carryOut = (byte)((a & 0x80) != 0 ? 1 : 0);
        var result = (byte)((a << 1) | (carryIn ? 1 : 0));
        return (result, AccRotateFlags(result, carryOut, oldFlags));
    }

    public static (byte Result, byte Flags) Rra(byte a, byte oldFlags)
    {
        var carryIn = (oldFlags & CF) != 0;
        var carryOut = (byte)(a & 0x01);
        var result = (byte)((a >> 1) | (carryIn ? 0x80 : 0));
        return (result, AccRotateFlags(result, carryOut, oldFlags));
    }

    private static byte AccRotateFlags(byte result, byte carryOut, byte oldFlags) =>
        (byte)((oldFlags & (SF | ZF | PF)) | (result & (YF | XF)) | carryOut);

    // ---- misc accumulator ops --------------------------------------------------

    /// <summary>CPL: A = ~A. S, Z, P/V, C preserved; H and N set; Y/X from the result.</summary>
    public static (byte Result, byte Flags) Cpl(byte a, byte oldFlags)
    {
        var result = (byte)~a;
        var flags = (byte)((oldFlags & (SF | ZF | PF | CF)) | HF | NF | (result & (YF | XF)));
        return (result, flags);
    }

    /// <summary>
    /// SCF: set carry. S, Z, P/V preserved; H and N cleared; C set.
    /// Y/X here use the simple, widely-documented "from A" rule. The real chip's
    /// behaviour additionally depends on the Q register (Patrik Rak's research) in
    /// ways not yet verified against SingleStepTests' own scf/ccf case data — revisit
    /// when SCF/CCF are wired up in milestone 5, the same way the M1 timing was
    /// confirmed against real JSON before relying on it (see CLAUDE.md §5).
    /// </summary>
    public static byte Scf(byte a, byte oldFlags) =>
        (byte)((oldFlags & (SF | ZF | PF)) | CF | (a & (YF | XF)));

    /// <summary>CCF: complement carry. H takes the old carry; same unverified Y/X
    /// caveat as <see cref="Scf"/>.</summary>
    public static byte Ccf(byte a, byte oldFlags)
    {
        var oldCarry = (oldFlags & CF) != 0;
        return (byte)((oldFlags & (SF | ZF | PF)) | (oldCarry ? HF : 0) | (a & (YF | XF)) | (oldCarry ? 0 : CF));
    }

    // ---- DAA --------------------------------------------------------------------

    /// <summary>Decimal-adjust A after a BCD ADD/ADC/SUB/SBC, per the standard
    /// correction table (Sean Young, "The Undocumented Z80 Documented").</summary>
    public static (byte Result, byte Flags) Daa(byte a, byte oldFlags)
    {
        var n = (oldFlags & NF) != 0;
        var c = (oldFlags & CF) != 0;
        var h = (oldFlags & HF) != 0;
        var lowNibble = a & 0x0F;

        byte diff = 0;
        if (h || lowNibble > 9) diff |= 0x06;
        if (c || a > 0x99) diff |= 0x60;

        var newC = c || a > 0x99;
        var newH = n ? (h && lowNibble < 6) : (lowNibble > 9);

        var result = n ? (byte)(a - diff) : (byte)(a + diff);
        var flags = (byte)(SZYX(result) | (Parity(result) ? PF : 0)
            | (n ? NF : 0) | (newH ? HF : 0) | (newC ? CF : 0));
        return (result, flags);
    }

    // ---- BIT --------------------------------------------------------------------

    /// <summary>
    /// BIT n,x flag computation. <paramref name="flagSource"/> supplies the
    /// undocumented Y/X bits: for BIT n,r it's <paramref name="value"/> itself;
    /// for BIT n,(HL) and BIT n,(IX+d)/(IY+d) it's the high byte of WZ instead,
    /// per CLAUDE.md §6. C is preserved; the result is not written back.
    /// </summary>
    public static byte Bit(byte value, int bitIndex, byte flagSource, byte oldFlags)
    {
        var isSet = (value & (1 << bitIndex)) != 0;
        var flags = (byte)((oldFlags & CF) | HF | (flagSource & (YF | XF)));
        if (!isSet) flags |= ZF | PF;
        if (bitIndex == 7 && isSet) flags |= SF;
        return flags;
    }
}

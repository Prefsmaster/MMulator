using Z80.Core;

namespace Z80.Tests;

public class AluTests
{
    // ---- 8-bit add/adc ---------------------------------------------------

    [Fact]
    public void Add8_HalfCarry_NoOverflow()
    {
        var (result, flags) = Alu.Add8(0x0F, 0x01);
        Assert.Equal(0x10, result);
        Assert.Equal(Alu.HF, flags); // only half-carry; no S/Z/Y/X/C/P
    }

    [Fact]
    public void Add8_SignedOverflow_0x7F_Plus_1()
    {
        var (result, flags) = Alu.Add8(0x7F, 0x01);
        Assert.Equal(0x80, result);
        Assert.Equal(Alu.SF | Alu.HF | Alu.PF, flags);
    }

    [Fact]
    public void Adc8_CarryIn_Propagates()
    {
        var (result, flags) = Alu.Adc8(0xFF, 0x00, true);
        Assert.Equal(0x00, result);
        Assert.Equal(Alu.ZF | Alu.HF | Alu.CF, flags);
    }

    // ---- 8-bit subtract/cp -------------------------------------------------

    [Fact]
    public void Sub8_Underflow_SetsCarryAndBorrowFlags()
    {
        var (result, flags) = Alu.Sub8(0x00, 0x01);
        Assert.Equal(0xFF, result);
        Assert.Equal(Alu.SF | Alu.YF | Alu.XF | Alu.NF | Alu.HF | Alu.CF, flags);
    }

    [Fact]
    public void Cp8_UndocumentedFlags_ComeFromOperand_NotResult()
    {
        // result = 0x01 - 0x08 = 0xF9 (Y=1,X=1 on the result byte itself),
        // but CP must report Y/X from the operand 0x08 (Y=0,X=1) instead.
        var flags = Alu.Cp8(0x01, 0x08);
        Assert.Equal(Alu.SF | Alu.XF | Alu.NF | Alu.HF | Alu.CF, flags);
    }

    [Fact]
    public void Cp8_DoesNotMatchPlainSub_WhenOperandAndResultXYDiffer()
    {
        var (_, subFlags) = Alu.Sub8(0x01, 0x08);
        var cpFlags = Alu.Cp8(0x01, 0x08);
        Assert.NotEqual(subFlags, cpFlags);
    }

    [Fact]
    public void Cp8_Equal_SetsZero()
    {
        var flags = Alu.Cp8(0x42, 0x42);
        Assert.Equal(Alu.ZF | Alu.NF, flags & (Alu.ZF | Alu.NF | Alu.CF));
        Assert.True((flags & Alu.ZF) != 0);
    }

    // ---- 8-bit logic -------------------------------------------------------

    [Fact]
    public void And8_AlwaysSetsHalfCarry()
    {
        var (result, flags) = Alu.And8(0xFF, 0x0F);
        Assert.Equal(0x0F, result);
        Assert.Equal(Alu.HF | Alu.PF | Alu.XF, flags); // 0x0F has odd parity bits(4)=even -> PF set, X set
    }

    [Fact]
    public void Or8_Zero_SetsZeroAndParity()
    {
        var (result, flags) = Alu.Or8(0x00, 0x00);
        Assert.Equal(0x00, result);
        Assert.Equal(Alu.ZF | Alu.PF, flags); // parity(0) = even -> PF set
    }

    [Fact]
    public void Xor8_WithSelf_IsZero()
    {
        var (result, flags) = Alu.Xor8(0x5A, 0x5A);
        Assert.Equal(0x00, result);
        Assert.Equal(Alu.ZF | Alu.PF, flags);
    }

    // ---- inc/dec: carry preserved, not computed ----------------------------

    [Fact]
    public void Inc8_0x7F_To_0x80_SetsOverflowAndSign()
    {
        var (result, flags) = Alu.Inc8(0x7F, 0);
        Assert.Equal(0x80, result);
        Assert.Equal(Alu.SF | Alu.HF | Alu.PF, flags);
    }

    [Fact]
    public void Inc8_PreservesIncomingCarry()
    {
        var (_, flags) = Alu.Inc8(0x00, Alu.CF);
        Assert.True((flags & Alu.CF) != 0);
    }

    [Fact]
    public void Dec8_0x80_To_0x7F_SetsOverflow()
    {
        // 0x7F = 01111111: Y and X both come from the result, and the low nibble
        // of the original 0x80 was 0, so half-carry is also set.
        var (result, flags) = Alu.Dec8(0x80, 0);
        Assert.Equal(0x7F, result);
        Assert.Equal(Alu.YF | Alu.XF | Alu.HF | Alu.NF | Alu.PF, flags);
    }

    [Fact]
    public void Dec8_0x01_To_0x00_SetsZero()
    {
        var (result, flags) = Alu.Dec8(0x01, 0);
        Assert.Equal(0x00, result);
        Assert.Equal(Alu.ZF | Alu.NF, flags); // no overflow: only 0x80 decrementing overflows
    }

    // ---- 16-bit add/adc/sbc -------------------------------------------------

    [Fact]
    public void Add16_PreservesSignZeroParityFromOldFlags()
    {
        var (result, flags) = Alu.Add16(0x0FFF, 0x0001, Alu.SF | Alu.ZF | Alu.PF);
        Assert.Equal(0x1000, result);
        Assert.Equal(Alu.SF | Alu.ZF | Alu.PF | Alu.HF, flags);
    }

    [Fact]
    public void Add16_CarryOut()
    {
        var (result, flags) = Alu.Add16(0xFFFF, 0x0001, 0);
        Assert.Equal(0x0000, result);
        Assert.Equal(Alu.HF | Alu.CF, flags);
    }

    [Fact]
    public void Adc16_ComputesFullFlagSet()
    {
        var (result, flags) = Alu.Adc16(0xFFFF, 0x0000, true);
        Assert.Equal(0x0000, result);
        Assert.Equal(Alu.ZF | Alu.HF | Alu.CF, flags);
    }

    [Fact]
    public void Sbc16_Underflow()
    {
        var (result, flags) = Alu.Sbc16(0x0000, 0x0001, false);
        Assert.Equal(0xFFFF, result);
        Assert.Equal(Alu.SF | Alu.YF | Alu.XF | Alu.NF | Alu.HF | Alu.CF, flags);
    }

    // ---- rotate/shift (generic CB form) -------------------------------------

    [Fact]
    public void Rlc8_RotatesMsbIntoCarryAndLsb()
    {
        var (result, flags) = Alu.Rlc8(0x80);
        Assert.Equal(0x01, result);
        Assert.True((flags & Alu.CF) != 0);
    }

    [Fact]
    public void Rrc8_RotatesLsbIntoCarryAndMsb()
    {
        var (result, flags) = Alu.Rrc8(0x01);
        Assert.Equal(0x80, result);
        Assert.True((flags & Alu.CF) != 0);
        Assert.True((flags & Alu.SF) != 0);
    }

    [Fact]
    public void Sla8_ShiftsZeroIntoLsb()
    {
        var (result, flags) = Alu.Sla8(0x81);
        Assert.Equal(0x02, result);
        Assert.True((flags & Alu.CF) != 0); // bit 7 of 0x81 shifted out
    }

    [Fact]
    public void Sra8_PreservesSignBit()
    {
        var (result, flags) = Alu.Sra8(0x81);
        Assert.Equal(0xC0, result); // sign-extended
        Assert.True((flags & Alu.CF) != 0); // bit 0 shifted out
    }

    [Fact]
    public void Sll8_ShiftsOneIntoLsb()
    {
        var (result, _) = Alu.Sll8(0x00);
        Assert.Equal(0x01, result);
    }

    [Fact]
    public void Srl8_ShiftsZeroIntoMsb()
    {
        var (result, flags) = Alu.Srl8(0x81);
        Assert.Equal(0x40, result);
        Assert.True((flags & Alu.CF) != 0);
        Assert.True((flags & Alu.SF) == 0);
    }

    // ---- accumulator rotates: S/Z/P-V preserved, unlike the CB form ---------

    [Fact]
    public void Rlca_PreservesSignZeroParity_UnlikeRlc8()
    {
        var oldFlags = (byte)(Alu.SF | Alu.ZF | Alu.PF);
        var (result, flags) = Alu.Rlca(0x80, oldFlags);
        Assert.Equal(0x01, result);
        Assert.Equal(Alu.SF | Alu.ZF | Alu.PF | Alu.CF, flags);

        var (_, genericFlags) = Alu.Rlc8(0x80);
        Assert.NotEqual(flags, genericFlags); // CB form would clear S/Z, set P from result parity
    }

    [Fact]
    public void Rla_UsesOldCarryAsCarryIn()
    {
        var (result, flags) = Alu.Rla(0x00, Alu.CF);
        Assert.Equal(0x01, result);
        Assert.True((flags & Alu.CF) == 0); // bit 7 of 0x00 was 0, so carry out is 0
    }

    [Fact]
    public void Rra_UsesOldCarryAsCarryIn()
    {
        var (result, flags) = Alu.Rra(0x00, Alu.CF);
        Assert.Equal(0x80, result);
        Assert.True((flags & Alu.CF) == 0);
    }

    // ---- misc accumulator ops -----------------------------------------------

    [Fact]
    public void Cpl_ComplementsAndSetsHN()
    {
        var (result, flags) = Alu.Cpl(0x5A, Alu.SF | Alu.CF);
        Assert.Equal(0xA5, result);
        Assert.Equal(Alu.SF | Alu.HF | Alu.NF | Alu.CF | Alu.YF, flags); // 0xA5 has bit5 set
    }

    [Fact]
    public void Scf_SetsCarryClearsHN()
    {
        var flags = Alu.Scf(0x00, Alu.HF | Alu.NF | Alu.SF);
        Assert.Equal(Alu.SF | Alu.CF, flags);
    }

    [Fact]
    public void Ccf_TogglesCarryAndMovesOldCarryToHalfCarry()
    {
        var flags = Alu.Ccf(0x00, Alu.CF);
        Assert.True((flags & Alu.CF) == 0);
        Assert.True((flags & Alu.HF) != 0);
    }

    [Fact]
    public void Neg8_OfZero_LeavesZeroAndClearsCarry()
    {
        var (result, flags) = Alu.Neg8(0x00);
        Assert.Equal(0x00, result);
        Assert.Equal(Alu.ZF | Alu.NF, flags);
    }

    [Fact]
    public void Neg8_Of0x80_SetsOverflowSinceItHasNoPositiveCounterpart()
    {
        var (result, flags) = Alu.Neg8(0x80);
        Assert.Equal(0x80, result);
        Assert.Equal(Alu.SF | Alu.PF | Alu.NF | Alu.CF, flags);
    }

    // ---- DAA -------------------------------------------------------------------

    [Fact]
    public void Daa_AfterBinaryAdd_9Plus1_ProducesBcd10()
    {
        var (sum, addFlags) = Alu.Add8(0x09, 0x01);
        var (result, flags) = Alu.Daa(sum, addFlags);
        Assert.Equal(0x10, result);
        Assert.Equal(Alu.HF, flags);
    }

    [Fact]
    public void Daa_AfterBinaryAdd_15Plus27_ProducesBcd42()
    {
        var (sum, addFlags) = Alu.Add8(0x15, 0x27); // BCD 15 + 27
        var (result, _) = Alu.Daa(sum, addFlags);
        Assert.Equal(0x42, result);
    }

    [Fact]
    public void Daa_AfterBinarySubtract_BcdDigitsAdjustDown()
    {
        var (diff, subFlags) = Alu.Sub8(0x42, 0x15); // BCD 42 - 15 = 0x2D binary
        var (result, _) = Alu.Daa(diff, subFlags);
        Assert.Equal(0x27, result); // BCD "27"
    }

    // ---- BIT --------------------------------------------------------------------

    [Fact]
    public void Bit_SetBit_ClearsZero()
    {
        var flags = Alu.Bit(0x80, 7, 0x80, 0);
        Assert.True((flags & Alu.ZF) == 0);
        Assert.True((flags & Alu.SF) != 0); // S only set for bit 7
        Assert.True((flags & Alu.HF) != 0);
    }

    [Fact]
    public void Bit_ClearBit_SetsZeroAndParity()
    {
        var flags = Alu.Bit(0x00, 7, 0x00, 0);
        Assert.True((flags & Alu.ZF) != 0);
        Assert.True((flags & Alu.PF) != 0);
        Assert.True((flags & Alu.SF) == 0);
    }

    [Fact]
    public void Bit_UsesFlagSourceForXY_NotTheTestedValue()
    {
        // BIT n,(HL): Y/X must come from WZ's high byte, not from the tested byte.
        var flags = Alu.Bit(0x00, 0, flagSource: 0x28, oldFlags: 0);
        Assert.Equal(Alu.YF | Alu.XF, flags & (Alu.YF | Alu.XF));
    }

    [Fact]
    public void Bit_PreservesIncomingCarry()
    {
        var flags = Alu.Bit(0xFF, 0, 0xFF, Alu.CF);
        Assert.True((flags & Alu.CF) != 0);
    }
}

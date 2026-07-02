using Z80.Disassembler;

namespace Z80.Disassembler.Tests;

/// <summary>Hand-verified (bytes → text) cases per quadrant and per prefix page,
/// per disassembler CLAUDE.md §10. These pin down the exact mnemonic rendering;
/// ConformanceTests.cs separately proves length agreement across the FULL opcode
/// set against the core.</summary>
public class GoldenTests
{
    private static Func<ushort, byte> Reader(ushort baseAddr, params byte[] bytes) =>
        addr => bytes[(ushort)(addr - baseAddr)];

    private static DisasmLine Decode(ushort addr, params byte[] bytes) =>
        new Z80.Disassembler.Disassembler().Decode(addr, Reader(addr, bytes));

    // ---- Base page ----------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0x00 }, "NOP")]
    [InlineData(new byte[] { 0x21, 0x34, 0x12 }, "LD HL,1234h")]
    [InlineData(new byte[] { 0x3E, 0x05 }, "LD A,05h")]
    [InlineData(new byte[] { 0x06, 0xFF }, "LD B,0FFh")] // hex-safe leading zero
    [InlineData(new byte[] { 0x80 }, "ADD A,B")]
    [InlineData(new byte[] { 0x91 }, "SUB C")]
    [InlineData(new byte[] { 0xFE, 0x10 }, "CP 10h")]
    [InlineData(new byte[] { 0xC3, 0x00, 0x80 }, "JP 8000h")]
    [InlineData(new byte[] { 0xC9 }, "RET")]
    [InlineData(new byte[] { 0x76 }, "HALT")]
    [InlineData(new byte[] { 0xF3 }, "DI")]
    [InlineData(new byte[] { 0xFB }, "EI")]
    [InlineData(new byte[] { 0xE3 }, "EX (SP),HL")]
    [InlineData(new byte[] { 0xEB }, "EX DE,HL")]
    [InlineData(new byte[] { 0x08 }, "EX AF,AF'")]
    [InlineData(new byte[] { 0xD9 }, "EXX")]
    [InlineData(new byte[] { 0xE9 }, "JP (HL)")]
    [InlineData(new byte[] { 0xF9 }, "LD SP,HL")]
    [InlineData(new byte[] { 0xC5 }, "PUSH BC")]
    [InlineData(new byte[] { 0xF1 }, "POP AF")]
    [InlineData(new byte[] { 0xFF }, "RST 0038h")]
    [InlineData(new byte[] { 0x27 }, "DAA")]
    [InlineData(new byte[] { 0x2F }, "CPL")]
    [InlineData(new byte[] { 0x37 }, "SCF")]
    [InlineData(new byte[] { 0x3F }, "CCF")]
    public void BasePageMnemonics(byte[] bytes, string expected)
    {
        var line = Decode(0x1000, bytes);
        Assert.Equal(expected, line.Text);
        Assert.Equal(bytes.Length, line.Length);
        Assert.Equal(bytes, line.Bytes);
    }

    [Fact]
    public void JrUnconditional_TargetsSelfOnMinusTwoDisplacement()
    {
        // JR $-2 (opcode 0x18, displacement 0xFE=-2): 2-byte instruction, target
        // = PC-after-fetch (addr+2) + (-2) = addr, i.e. an infinite self-loop.
        var line = Decode(0x1000, 0x18, 0xFE);
        Assert.Equal("JR 1000h", line.Text);
    }

    [Fact]
    public void Djnz_TargetIsRelativeToInstructionEnd()
    {
        var line = Decode(0x1000, 0x10, 0x05); // DJNZ +5 -> 0x1000+2+5
        Assert.Equal("DJNZ 1007h", line.Text);
    }

    [Fact]
    public void JrConditional_TargetIsRelativeToInstructionEnd()
    {
        var line = Decode(0x1000, 0x20, 0x03); // JR NZ,+3 -> 0x1000+2+3
        Assert.Equal("JR NZ,1005h", line.Text);
    }

    // ---- CB page --------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0xCB, 0x00 }, "RLC B")]
    [InlineData(new byte[] { 0xCB, 0x06 }, "RLC (HL)")]
    [InlineData(new byte[] { 0xCB, 0x46 }, "BIT 0,(HL)")]
    [InlineData(new byte[] { 0xCB, 0x87 }, "RES 0,A")]
    [InlineData(new byte[] { 0xCB, 0xFF }, "SET 7,A")]
    [InlineData(new byte[] { 0xCB, 0x37 }, "SLL A")] // undocumented
    public void CbPageMnemonics(byte[] bytes, string expected)
    {
        var line = Decode(0x1000, bytes);
        Assert.Equal(expected, line.Text);
        Assert.Equal(bytes.Length, line.Length);
    }

    // ---- ED page --------------------------------------------------------------

    [Theory]
    [InlineData(new byte[] { 0xED, 0x44 }, "NEG")]
    [InlineData(new byte[] { 0xED, 0x4D }, "RETI")]
    [InlineData(new byte[] { 0xED, 0x45 }, "RETN")]
    [InlineData(new byte[] { 0xED, 0x46 }, "IM 0")]
    [InlineData(new byte[] { 0xED, 0x4E }, "IM 0/1")] // undocumented duplicate
    [InlineData(new byte[] { 0xED, 0x56 }, "IM 1")]
    [InlineData(new byte[] { 0xED, 0x5E }, "IM 2")]
    [InlineData(new byte[] { 0xED, 0x47 }, "LD I,A")]
    [InlineData(new byte[] { 0xED, 0x4F }, "LD R,A")]
    [InlineData(new byte[] { 0xED, 0x57 }, "LD A,I")]
    [InlineData(new byte[] { 0xED, 0x5F }, "LD A,R")]
    [InlineData(new byte[] { 0xED, 0x67 }, "RRD")]
    [InlineData(new byte[] { 0xED, 0x6F }, "RLD")]
    [InlineData(new byte[] { 0xED, 0xA0 }, "LDI")]
    [InlineData(new byte[] { 0xED, 0xB0 }, "LDIR")]
    [InlineData(new byte[] { 0xED, 0xA1 }, "CPI")]
    [InlineData(new byte[] { 0xED, 0xB1 }, "CPIR")]
    [InlineData(new byte[] { 0xED, 0xA2 }, "INI")]
    [InlineData(new byte[] { 0xED, 0xB2 }, "INIR")]
    [InlineData(new byte[] { 0xED, 0xA3 }, "OUTI")]
    [InlineData(new byte[] { 0xED, 0xB3 }, "OTIR")] // not OUTIR
    [InlineData(new byte[] { 0xED, 0x78 }, "IN A,(C)")]
    [InlineData(new byte[] { 0xED, 0x70 }, "IN (C)")] // undocumented, flags-only
    [InlineData(new byte[] { 0xED, 0x79 }, "OUT (C),A")]
    [InlineData(new byte[] { 0xED, 0x71 }, "OUT (C),0")] // undocumented
    [InlineData(new byte[] { 0xED, 0x00 }, "DB 0EDh,00h")] // undefined ED opcode
    public void EdPageMnemonics(byte[] bytes, string expected)
    {
        var line = Decode(0x1000, bytes);
        Assert.Equal(expected, line.Text);
        Assert.Equal(bytes.Length, line.Length);
    }

    [Fact]
    public void EdLdRpNn_RoundTrip()
    {
        Assert.Equal("LD BC,(1234h)", Decode(0x1000, 0xED, 0x4B, 0x34, 0x12).Text);
        Assert.Equal("LD (1234h),BC", Decode(0x1000, 0xED, 0x43, 0x34, 0x12).Text);
    }

    // ---- DD/FD page -------------------------------------------------------------

    [Fact]
    public void DdLdIxNn()
    {
        var line = Decode(0x1000, 0xDD, 0x21, 0x34, 0x12);
        Assert.Equal("LD IX,1234h", line.Text);
        Assert.Equal(4, line.Length);
    }

    [Fact]
    public void FdLdIyNn()
    {
        Assert.Equal("LD IY,1234h", Decode(0x1000, 0xFD, 0x21, 0x34, 0x12).Text);
    }

    [Fact]
    public void DdLdAFromIndexed_PositiveDisplacement()
    {
        Assert.Equal("LD A,(IX+05h)", Decode(0x1000, 0xDD, 0x7E, 0x05).Text);
    }

    [Fact]
    public void DdLdIndexedFromA()
    {
        Assert.Equal("LD (IX+05h),A", Decode(0x1000, 0xDD, 0x77, 0x05).Text);
    }

    [Fact]
    public void DdMixedOperandQuirk_RealHNotIxh()
    {
        // LD (IX+d),H: H stays the REAL H register, never IXH, because the other
        // operand is (IX+d) (disassembler CLAUDE.md §9 mixed-operand quirk).
        Assert.Equal("LD (IX+05h),H", Decode(0x1000, 0xDD, 0x74, 0x05).Text);
    }

    [Fact]
    public void DdPureRegisterPair_SubstitutesBothSides()
    {
        // LD H,L under DD, with NEITHER side being (HL): both substitute to IXH/IXL.
        Assert.Equal("LD IXH,IXL", Decode(0x1000, 0xDD, 0x65).Text);
    }

    [Theory]
    [InlineData(new byte[] { 0xDD, 0x26, 0x05 }, "LD IXH,05h")]
    [InlineData(new byte[] { 0xDD, 0x2E, 0x05 }, "LD IXL,05h")]
    [InlineData(new byte[] { 0xDD, 0x34, 0x05 }, "INC (IX+05h)")]
    [InlineData(new byte[] { 0xDD, 0x24 }, "INC IXH")]
    [InlineData(new byte[] { 0xDD, 0x2D }, "DEC IXL")]
    [InlineData(new byte[] { 0xDD, 0x09 }, "ADD IX,BC")]
    [InlineData(new byte[] { 0xDD, 0x29 }, "ADD IX,IX")] // self-add via prefix-aware Get16(2)
    [InlineData(new byte[] { 0xDD, 0xE1 }, "POP IX")]
    [InlineData(new byte[] { 0xDD, 0xE5 }, "PUSH IX")]
    [InlineData(new byte[] { 0xDD, 0xE9 }, "JP (IX)")]
    [InlineData(new byte[] { 0xDD, 0xF9 }, "LD SP,IX")]
    [InlineData(new byte[] { 0xDD, 0xE3 }, "EX (SP),IX")]
    [InlineData(new byte[] { 0xDD, 0xA4 }, "AND IXH")]
    [InlineData(new byte[] { 0xFD, 0x7C }, "LD A,IYH")]
    public void DdFdIndexedMnemonics(byte[] bytes, string expected)
    {
        var line = Decode(0x1000, bytes);
        Assert.Equal(expected, line.Text);
        Assert.Equal(bytes.Length, line.Length);
    }

    [Fact]
    public void DdAluIndexed()
    {
        Assert.Equal("ADD A,(IX+05h)", Decode(0x1000, 0xDD, 0x86, 0x05).Text);
    }

    [Fact]
    public void DdLdIndexedImmediate()
    {
        Assert.Equal("LD (IX+05h),0Ah", Decode(0x1000, 0xDD, 0x36, 0x05, 0x0A).Text);
    }

    [Fact]
    public void DdWastedPrefix_FallsThroughToBasePage()
    {
        // DD NOP: 0x00 is not IsIndexAffected, so it executes (and disassembles)
        // as plain NOP, but the DD byte is still consumed (length 2).
        var line = Decode(0x1000, 0xDD, 0x00);
        Assert.Equal("NOP", line.Text);
        Assert.Equal(2, line.Length);
    }

    // ---- DDCB/FDCB ----------------------------------------------------------

    [Fact]
    public void DdCbRotate()
    {
        Assert.Equal("RLC (IX+05h)", Decode(0x1000, 0xDD, 0xCB, 0x05, 0x06).Text);
    }

    [Fact]
    public void DdCbBit_NoDualWrite()
    {
        Assert.Equal("BIT 0,(IX+05h)", Decode(0x1000, 0xDD, 0xCB, 0x05, 0x46).Text);
    }

    [Fact]
    public void DdCbRes_DualWriteToRegister()
    {
        // RES 0,(IX+05h) with z=0(B): undocumented dual-write also stores into B.
        Assert.Equal("RES 0,(IX+05h),B", Decode(0x1000, 0xDD, 0xCB, 0x05, 0x80).Text);
    }

    [Fact]
    public void DdCbSet_ZEquals6_NoDualWrite()
    {
        Assert.Equal("SET 0,(IX+05h)", Decode(0x1000, 0xDD, 0xCB, 0x05, 0xC6).Text);
    }

    [Fact]
    public void FdCbNegativeDisplacement_DualWrite()
    {
        // SRA (IY-05h),B: displacement 0xFB = -5, dual-write to B (z=0).
        Assert.Equal("SRA (IY-05h),B", Decode(0x1000, 0xFD, 0xCB, 0xFB, 0x28).Text);
    }

    // ---- Symbol / port injection (disassembler CLAUDE.md §7) -------------------

    [Fact]
    public void SymbolLookup_AnnotatesCallTarget()
    {
        var line = new Z80.Disassembler.Disassembler().Decode(
            0x1000,
            Reader(0x1000, 0xCD, 0x38, 0x00),
            symbolLookup: addr => addr == 0x0038 ? "ROM_ENTRY" : null);
        Assert.Equal("CALL 0038h ; ROM_ENTRY", line.Text);
    }

    [Fact]
    public void SymbolLookup_NoMatch_RendersBareHex()
    {
        var line = new Z80.Disassembler.Disassembler().Decode(
            0x1000,
            Reader(0x1000, 0xCD, 0x38, 0x00),
            symbolLookup: _ => null);
        Assert.Equal("CALL 0038h", line.Text);
    }

    [Fact]
    public void PortNameLookup_AnnotatesImmediatePort()
    {
        var line = new Z80.Disassembler.Disassembler().Decode(
            0x1000,
            Reader(0x1000, 0xDB, 0x10),
            portName: p => p == 0x10 ? "KBD" : null);
        Assert.Equal("IN A,(10h) ; KBD", line.Text);
    }
}

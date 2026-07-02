namespace Z80.Disassembler.Tests;

public class SyncToPcTests
{
    // 0x1000 NOP; 0x1001 NOP; 0x1002 LD A,05h; 0x1004 LD HL,1234h; 0x1007 NOP; 0x1008 RET
    private static readonly byte[] Program = { 0x00, 0x00, 0x3E, 0x05, 0x21, 0x34, 0x12, 0x00, 0xC9 };
    private const ushort BaseAddr = 0x1000;

    private static byte Read(ushort addr) => Program[addr - BaseAddr];

    [Fact]
    public void DecodeAround_AlignsWhenHeuristicAnchorLandsExactlyOnPc()
    {
        var pc = (ushort)(BaseAddr + 7); // the NOP after LD HL,1234h

        var lines = Z80.Disassembler.SyncToPc.DecodeAround(
            new Z80.Disassembler.Disassembler(), pc, Read,
            linesBefore: 2, linesAfter: 1, minLookback: 7, maxLookback: 7);

        Assert.Equal(4, lines.Count);
        Assert.Equal("LD A,05h", lines[0].Text);
        Assert.Equal("LD HL,1234h", lines[1].Text);
        Assert.Equal(pc, lines[2].Address);
        Assert.Equal("NOP", lines[2].Text);
        Assert.Equal("RET", lines[3].Text);
    }

    [Fact]
    public void DecodeAround_UsesProvidedAnchorSource_BypassingHeuristic()
    {
        var pc = (ushort)(BaseAddr + 7);

        var lines = Z80.Disassembler.SyncToPc.DecodeAround(
            new Z80.Disassembler.Disassembler(), pc, Read,
            linesBefore: 4, linesAfter: 0,
            anchorSource: _ => BaseAddr);

        Assert.Equal(5, lines.Count);
        Assert.Equal(BaseAddr, lines[0].Address);
        Assert.Equal(pc, lines[4].Address);
        Assert.Equal("NOP", lines[4].Text);
    }

    [Fact]
    public void DecodeAround_PcLineIsAlwaysExact_EvenWithMisalignedAnchor()
    {
        // Force a deliberately mid-instruction anchor (0x1003 is the immediate
        // operand byte of "LD A,05h", not a real instruction boundary). Whatever
        // garbage the "before" lines decode to, the PC line and everything after
        // must still be exact (disassembler CLAUDE.md §6: "a mis-decoded leading
        // line or two ... is tolerable; the PC line is not") — DecodeAround
        // always decodes forward from PC independently of the anchor.
        var pc = (ushort)(BaseAddr + 7);
        var misalignedAnchor = (ushort)(BaseAddr + 3);

        var lines = Z80.Disassembler.SyncToPc.DecodeAround(
            new Z80.Disassembler.Disassembler(), pc, Read,
            linesBefore: 1, linesAfter: 1,
            anchorSource: _ => misalignedAnchor);

        var pcLine = lines.Single(l => l.Address == pc);
        Assert.Equal("NOP", pcLine.Text);
        Assert.Equal("RET", lines[^1].Text);
    }
}

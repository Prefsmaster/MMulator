namespace Z80.Disassembler.Tests;

public class SymbolTableTests
{
    [Fact]
    public void LooksUpAddressesAndPorts()
    {
        var table = new Z80.Disassembler.SymbolTable();
        table.AddAddress(0x0038, "ROM_ENTRY");
        table.AddPort(0x10, "KBD");

        Assert.Equal("ROM_ENTRY", table.LookupAddress(0x0038));
        Assert.Null(table.LookupAddress(0x0039));
        Assert.Equal("KBD", table.LookupPort(0x10));
        Assert.Null(table.LookupPort(0x11));
    }

    [Fact]
    public void WorksAsDecodeInjection()
    {
        var table = new Z80.Disassembler.SymbolTable();
        table.AddAddress(0x0038, "ROM_ENTRY");

        byte[] bytes = { 0xCD, 0x38, 0x00 }; // CALL 0038h
        var line = new Z80.Disassembler.Disassembler().Decode(
            0x1000, addr => bytes[addr - 0x1000], table.LookupAddress);

        Assert.Equal("CALL 0038h ; ROM_ENTRY", line.Text);
    }
}

using P2000.Machine.Memory;
using P2000.Machine.Slots;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Slots;

public class Slot1CartridgeTests
{
    private static Slot1Cartridge FromSpan(ReadOnlySpan<byte> image) => new(image);

    // ---- Interface contract (IMemorySlot) ----------------------------------------

    [Fact]
    public void AddressStart_IsCartridgeStart()
    {
        var cart = FromSpan(new byte[] { 0x42 });
        Assert.Equal(PageTable.CartridgeStart, cart.AddressStart);
    }

    [Fact]
    public void AddressEnd_IsCartridgeEnd()
    {
        var cart = FromSpan(new byte[] { 0x42 });
        Assert.Equal(PageTable.CartridgeEnd, cart.AddressEnd);
    }

    // ---- Read -------------------------------------------------------------------

    [Fact]
    public void Read_WithinImage_ReturnsByte()
    {
        var image = new byte[16];
        image[0] = 0xAB;
        image[15] = 0xCD;
        var cart = FromSpan(image);

        Assert.Equal(0xAB, cart.Read(PageTable.CartridgeStart));
        Assert.Equal(0xCD, cart.Read(PageTable.CartridgeStart + 15));
    }

    [Fact]
    public void Read_BeyondImage_ReturnsOpenBus()
    {
        var image = new byte[] { 0x01, 0x02 }; // only 2 bytes
        var cart = FromSpan(image);

        // Byte 2 onward (0x1002+) is beyond the image → open bus
        Assert.Equal(PageTable.OpenBus, cart.Read(0x1002));
        Assert.Equal(PageTable.OpenBus, cart.Read(PageTable.CartridgeEnd));
    }

    [Fact]
    public void Read_FullSizeImage_ReturnsLastByte()
    {
        const int CartSize = PageTable.CartridgeEnd - PageTable.CartridgeStart + 1;
        var image = new byte[CartSize];
        image[^1] = 0xFE;
        var cart = FromSpan(image);

        Assert.Equal(0xFE, cart.Read(PageTable.CartridgeEnd));
    }

    // ---- Write ------------------------------------------------------------------

    [Fact]
    public void Write_IsDiscarded_ReadReturnsOriginal()
    {
        var image = new byte[] { 0x55 };
        var cart = FromSpan(image);

        cart.Write(PageTable.CartridgeStart, 0xAA); // SLOT1 has no WR pin

        Assert.Equal(0x55, cart.Read(PageTable.CartridgeStart));
    }

    // ---- SaveState / LoadState --------------------------------------------------

    [Fact]
    public void SaveLoadState_IsNoOp_ContentUnchanged()
    {
        var image = new byte[] { 0x11, 0x22, 0x33 };
        var cart = FromSpan(image);
        var state = new InMemoryState();

        cart.SaveState(state);
        cart.LoadState(state.BeginRead()); // nothing was written — reader at start

        Assert.Equal(0x11, cart.Read(PageTable.CartridgeStart));
    }

    // ---- Integration with PageTable ---------------------------------------------

    [Fact]
    public void PageTable_WithCartridge_ReadsCartridgeBytes()
    {
        var image = new byte[PageTable.CartridgeEnd - PageTable.CartridgeStart + 1];
        image[0] = 0x7E;
        image[^1] = 0x3C;
        var cart = new Slot1Cartridge(image.AsSpan());
        var pageTable = new PageTable(new MachineConfig(), cart);

        Assert.Equal(0x7E, pageTable.Read(PageTable.CartridgeStart));
        Assert.Equal(0x3C, pageTable.Read(PageTable.CartridgeEnd));
    }

    [Fact]
    public void PageTable_WithoutCartridge_ReadsOpenBus()
    {
        var pageTable = new PageTable(new MachineConfig()); // no cartridge

        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeStart));
        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeEnd));
    }

    [Fact]
    public void PageTable_WithShortCartridge_BeyondImageIsOpenBus()
    {
        var image = new byte[] { 0x10, 0x20 }; // 2-byte image in a 16 KB slot
        var cart = new Slot1Cartridge(image.AsSpan());
        var pageTable = new PageTable(new MachineConfig(), cart);

        Assert.Equal(0x10, pageTable.Read(PageTable.CartridgeStart));
        Assert.Equal(0x20, pageTable.Read(PageTable.CartridgeStart + 1));
        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeStart + 2));
        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeEnd));
    }

    // ---- Machine integration ----------------------------------------------------

    [Fact]
    public void Machine_Slot1Property_IsNullOnBareMachine()
    {
        var machine = new Machine();
        Assert.Null(machine.Slot1);
    }

    [Fact]
    public void Machine_Slot2Property_AlwaysNull()
    {
        var machine = new Machine();
        Assert.Null(machine.Slot2);
    }
}

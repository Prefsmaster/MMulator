using P2000.Machine.Io;

namespace P2000.Machine.Tests.Io;

public class PortDispatchTests
{
    // ---- No listeners registered - open bus, like the page table's unpopulated regions ---

    [Fact]
    public void Read_NoRegisteredSource_IsOpenBus()
    {
        var dispatch = new PortDispatch();

        Assert.Equal(PortDispatch.OpenBus, dispatch.Read(0x00));
    }

    [Fact]
    public void Write_NoRegisteredListener_DoesNotThrow()
    {
        var dispatch = new PortDispatch();

        dispatch.Write(0x00, 0x42);
    }

    // ---- Write fan-out: every listener on the port fires ---------------------------------

    [Fact]
    public void Write_FansOutToEveryListenerOnThatPort()
    {
        var dispatch = new PortDispatch();
        byte? first = null;
        byte? second = null;
        dispatch.RegisterWrite(0x10, v => first = v);
        dispatch.RegisterWrite(0x10, v => second = v);

        dispatch.Write(0x10, 0x99);

        Assert.Equal((byte?)0x99, first);
        Assert.Equal((byte?)0x99, second);
    }

    [Fact]
    public void Write_OnlyInvokesListenersRegisteredOnThatPort()
    {
        var dispatch = new PortDispatch();
        var otherPortFired = false;
        dispatch.RegisterWrite(0x20, _ => otherPortFired = true);

        dispatch.Write(0x10, 0x01);

        Assert.False(otherPortFired);
    }

    // ---- Read combine: sources OR together, each owning its own bits ---------------------

    [Fact]
    public void Read_CombinesMultipleSources_ByBitwiseOr()
    {
        var dispatch = new PortDispatch();
        dispatch.RegisterRead(0x20, () => 0x01);
        dispatch.RegisterRead(0x20, () => 0x80);

        Assert.Equal(0x81, dispatch.Read(0x20));
    }

    [Fact]
    public void Read_OnlyCombinesSourcesRegisteredOnThatPort()
    {
        var dispatch = new PortDispatch();
        dispatch.RegisterRead(0x20, () => 0xFF);
        dispatch.RegisterRead(0x21, () => 0x00);

        Assert.Equal(0x00, dispatch.Read(0x21));
    }
}

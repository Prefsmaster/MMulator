using P2000.Machine.Io;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Io;

/// <summary>
/// CprinReader is now printer-only (bits 0–2). The cassette status bits (3–7) moved to
/// MdcrDevice at milestone 9; cassette tests live in Devices/MdcrDeviceTests.cs.
/// </summary>
public class CprinReaderTests
{
    [Fact]
    public void Read_ReturnsZero_PrinterDeferred()
    {
        var reader = new CprinReader();
        Assert.Equal(0x00, reader.Read());
    }

    [Fact]
    public void Reset_IsNoOp_ReadStillZero()
    {
        var reader = new CprinReader();
        reader.Reset();
        Assert.Equal(0x00, reader.Read());
    }

    [Fact]
    public void SaveLoad_RoundTrip_NoStateToSerialize()
    {
        var reader = new CprinReader();
        var state = new InMemoryState();
        reader.SaveState(state);

        var restored = new CprinReader();
        restored.LoadState(state.BeginRead());

        Assert.Equal(0x00, restored.Read());
    }
}

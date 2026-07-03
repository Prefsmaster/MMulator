using P2000.Machine.Io;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Io;

public class CprinReaderTests
{
    // ---- Bare-machine default: no cassette mounted (reference doc §5f) -------------------

    [Fact]
    public void BareDefault_CipReadsAsserted_NoCassette()
    {
        var reader = new CprinReader();

        Assert.Equal(0x10, reader.Read() & 0x10); // CIP is active-low: bit set = no cassette
    }

    [Fact]
    public void BareDefault_BetReadsAsserted_TapeOk()
    {
        var reader = new CprinReader();

        Assert.Equal(0x20, reader.Read() & 0x20); // BET is active-low: bit set = tape OK
    }

    [Fact]
    public void BareDefault_AllCassetteBitsAtRest()
    {
        var reader = new CprinReader();

        // CIP + BET set (no cassette, tape "ok"); WEN/RDC/RDA clear.
        Assert.Equal(0x30, reader.Read());
    }

    // ---- Mounting a cassette flips CIP live (reference doc §5b: CIP is a live transition) --

    [Fact]
    public void CassettePresent_ClearsCip()
    {
        var reader = new CprinReader();

        reader.CassettePresent = true;

        Assert.Equal(0x00, reader.Read() & 0x10);
    }

    [Fact]
    public void TapeAtEnd_ClearsBet()
    {
        var reader = new CprinReader();

        reader.TapeAtEnd = true;

        Assert.Equal(0x00, reader.Read() & 0x20);
    }

    [Fact]
    public void WriteProtected_SetsWen()
    {
        var reader = new CprinReader();

        reader.WriteProtected = true;

        Assert.Equal(0x08, reader.Read() & 0x08);
    }

    // ---- RDC/RDA are levels sampled straight through (reference doc §5f self-clocking pair)

    [Fact]
    public void ReadClockAndReadData_ReflectDirectly()
    {
        var reader = new CprinReader();

        reader.ReadClock = true;
        reader.ReadData = true;

        Assert.Equal(0xC0, reader.Read() & 0xC0);
    }

    // ---- Reset returns to the bare-machine default -----------------------------------------

    [Fact]
    public void Reset_ReturnsToBareMachineDefault()
    {
        var reader = new CprinReader
        {
            CassettePresent = true,
            TapeAtEnd = true,
            WriteProtected = true,
            ReadClock = true,
            ReadData = true,
        };

        reader.Reset();

        Assert.Equal(0x30, reader.Read());
    }

    // ---- SaveState/LoadState round-trip every field ----------------------------------------

    [Fact]
    public void SaveState_ThenLoadState_RestoresAllFields()
    {
        var reader = new CprinReader
        {
            CassettePresent = true,
            TapeAtEnd = false,
            WriteProtected = true,
            ReadClock = true,
            ReadData = false,
        };
        var state = new InMemoryState();
        reader.SaveState(state);

        var restored = new CprinReader();
        restored.LoadState(state.BeginRead());

        Assert.Equal(reader.Read(), restored.Read());
    }
}

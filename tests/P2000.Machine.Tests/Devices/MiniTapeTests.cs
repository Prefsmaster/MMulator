using P2000.Machine.Devices.Cassette;

namespace P2000.Machine.Tests.Devices;

public class MiniTapeTests
{
    // ---- Construction -----------------------------------------------------------

    [Fact]
    public void NewTape_StartsAtPosition0_Side0()
    {
        var tape = new MiniTape();
        Assert.Equal(0, tape.Position);
        Assert.Equal(0, tape.Side);
    }

    [Fact]
    public void NewTape_Position0_IsAtEnd_BOT()
    {
        var tape = new MiniTape();
        Assert.True(tape.IsAtEnd); // position 0 = BOT
    }

    [Fact]
    public void NewTape_NotProtected()
    {
        var tape = new MiniTape();
        Assert.False(tape.IsProtected);
    }

    // ---- Motion -----------------------------------------------------------------

    [Fact]
    public void Forward_AdvancesPosition()
    {
        var tape = new MiniTape();
        tape.Forward();
        Assert.Equal(1, tape.Position);
        Assert.False(tape.IsAtEnd);
    }

    [Fact]
    public void Reverse_AtBOT_DoesNotUnderflow()
    {
        var tape = new MiniTape();
        tape.Reverse(); // already at 0
        Assert.Equal(0, tape.Position);
    }

    [Fact]
    public void Forward_AtEOT_DoesNotOverflow()
    {
        var tape = new MiniTape();
        tape.SeekTo(MiniTape.PhasesPerSide - 1, 0);
        tape.Forward();
        Assert.Equal(MiniTape.PhasesPerSide - 1, tape.Position);
        Assert.True(tape.IsAtEnd);
    }

    [Fact]
    public void IsAtEnd_AtBOT_True()
    {
        var tape = new MiniTape();
        Assert.True(tape.IsAtEnd);
    }

    [Fact]
    public void IsAtEnd_AtEOT_True()
    {
        var tape = new MiniTape();
        tape.SeekTo(MiniTape.PhasesPerSide - 1, 0);
        Assert.True(tape.IsAtEnd);
    }

    [Fact]
    public void IsAtEnd_InMiddle_False()
    {
        var tape = new MiniTape();
        tape.SeekTo(MiniTape.PhasesPerSide / 2, 0);
        Assert.False(tape.IsAtEnd);
    }

    // ---- Write/Read round-trip --------------------------------------------------

    [Fact]
    public void WriteRead_Unprotected_RoundTrips()
    {
        var tape = new MiniTape();
        tape.Forward(); // move off BOT so Forward during write is valid
        tape.Reverse(); // back to 1 — actually just use SeekTo

        tape.SeekTo(100, 0);
        tape.Write(true);
        tape.SeekTo(100, 0);
        Assert.True(tape.Read());

        tape.SeekTo(100, 0);
        tape.Write(false);
        tape.SeekTo(100, 0);
        Assert.False(tape.Read());
    }

    [Fact]
    public void Write_WhenProtected_IsIgnored()
    {
        var tape = new MiniTape();
        tape.SeekTo(1000, 0); // middle of noise
        var before = tape.Read();

        // Build a protected tape (LoadCasImage sets protection)
        tape.LoadCasImage(new byte[1280], writeProtect: true);
        tape.SeekTo(1000, 0);
        tape.Write(!before); // attempt flip
        tape.SeekTo(1000, 0);

        Assert.NotEqual(before, tape.Read()); // value changed by LoadCasImage encoding, not our Write
        // The important thing: Write() must be no-op when protected:
        var afterLoad = tape.Read();
        tape.Write(!afterLoad);
        tape.SeekTo(1000, 0);
        Assert.Equal(afterLoad, tape.Read()); // Write was blocked
    }

    // ---- Deterministic noise fill -----------------------------------------------

    [Fact]
    public void NewTape_SameSeed_SameContent()
    {
        var t1 = new MiniTape(seed: 7);
        var t2 = new MiniTape(seed: 7);

        t1.SeekTo(500_000, 0);
        t2.SeekTo(500_000, 0);
        Assert.Equal(t1.Read(), t2.Read());
    }

    [Fact]
    public void NewTape_DifferentSeeds_DifferentContent()
    {
        var t1 = new MiniTape(seed: 1);
        var t2 = new MiniTape(seed: 2);

        // Sample many positions; at least one must differ
        var differ = false;
        for (var i = 1; i < 10000; i++)
        {
            t1.SeekTo(i, 0);
            t2.SeekTo(i, 0);
            if (t1.Read() != t2.Read()) { differ = true; break; }
        }
        Assert.True(differ);
    }

    // ---- LoadCasImage -----------------------------------------------------------

    [Fact]
    public void LoadCasImage_Rewinds_ToPosition0()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280]); // one block
        Assert.Equal(0, tape.Position);
    }

    [Fact]
    public void LoadCasImage_SetsProtection()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280], writeProtect: true);
        Assert.True(tape.IsProtected);
    }

    [Fact]
    public void LoadCasImage_WithWriteProtectFalse_NotProtected()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280], writeProtect: false);
        Assert.False(tape.IsProtected);
    }

    [Fact]
    public void LoadCasImage_EmptyImage_DoesNotThrow()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(Array.Empty<byte>());
    }

    // ---- Checksum ---------------------------------------------------------------

    [Fact]
    public void UpdateChecksum_ZeroByteZeroCs_ProducesExpectedValue()
    {
        // Verify the CRC-16 variant: one zero byte from cs=0
        // Per algorithm: for each of 8 zero bits, cs ^= 0, cs bit0 stays 0, just rotate.
        // Rotating 0 right 8 times stays 0.
        var cs = MiniTape.UpdateChecksum(0, 0x00);
        Assert.Equal(0x0000, cs);
    }

    [Fact]
    public void UpdateChecksum_SameInput_Deterministic()
    {
        var cs1 = MiniTape.UpdateChecksum(0, 0xAA);
        var cs2 = MiniTape.UpdateChecksum(0, 0xAA);
        Assert.Equal(cs1, cs2);
    }

    [Fact]
    public void UpdateChecksum_DifferentBytes_DifferentValues()
    {
        var cs1 = MiniTape.UpdateChecksum(0, 0x01);
        var cs2 = MiniTape.UpdateChecksum(0, 0x02);
        Assert.NotEqual(cs1, cs2);
    }
}

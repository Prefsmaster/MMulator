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

    // ---- Save (bitstream → .cas) ------------------------------------------------

    [Fact]
    public void Save_BlankTape_ReturnsNull()
    {
        // Random noise has no valid framed blocks
        var tape = new MiniTape(seed: 42);
        Assert.Null(tape.Save());
    }

    [Fact]
    public void Save_EmptyImage_ReturnsNull()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(Array.Empty<byte>()); // no blocks → no frames on tape
        Assert.Null(tape.Save());
    }

    [Fact]
    public void Save_ZeroedSingleBlock_RoundTrips()
    {
        var original = new byte[1280]; // header at +0x30 and data at +0x100 are all zeros
        var tape = new MiniTape();
        tape.LoadCasImage(original, writeProtect: false);

        var saved = tape.Save();

        Assert.NotNull(saved);
        Assert.Equal(1280, saved!.Length);
        Assert.Equal(original[0x30..0x50], saved[0x30..0x50]);   // 32-byte header
        Assert.Equal(original[0x100..0x500], saved[0x100..0x500]); // 1024-byte data
    }

    [Fact]
    public void Save_KnownDataSingleBlock_RoundTrips()
    {
        var original = new byte[1280];
        for (var i = 0; i < 32; i++) original[0x30 + i] = (byte)(i + 1);
        for (var i = 0; i < 1024; i++) original[0x100 + i] = (byte)(i & 0xFF);

        var tape = new MiniTape();
        tape.LoadCasImage(original, writeProtect: false);

        var saved = tape.Save();

        Assert.NotNull(saved);
        Assert.Equal(original[0x30..0x50], saved![0x30..0x50]);
        Assert.Equal(original[0x100..0x500], saved[0x100..0x500]);
    }

    [Fact]
    public void Save_MultipleBlocks_RoundTrips()
    {
        const int blockCount = 3;
        var original = new byte[blockCount * 1280];
        for (var b = 0; b < blockCount; b++)
        {
            for (var i = 0; i < 32; i++) original[b * 1280 + 0x30 + i] = (byte)(b * 10 + i);
            for (var i = 0; i < 1024; i++) original[b * 1280 + 0x100 + i] = (byte)(b + i);
        }

        var tape = new MiniTape();
        tape.LoadCasImage(original, writeProtect: false);

        var saved = tape.Save();

        Assert.NotNull(saved);
        Assert.Equal(blockCount * 1280, saved!.Length);
        for (var b = 0; b < blockCount; b++)
        {
            Assert.Equal(
                original[(b * 1280 + 0x30)..(b * 1280 + 0x50)],
                saved[(b * 1280 + 0x30)..(b * 1280 + 0x50)]);
            Assert.Equal(
                original[(b * 1280 + 0x100)..(b * 1280 + 0x500)],
                saved[(b * 1280 + 0x100)..(b * 1280 + 0x500)]);
        }
    }

    [Fact]
    public void Save_DoesNotMoveHead()
    {
        var original = new byte[1280];
        var tape = new MiniTape();
        tape.LoadCasImage(original, writeProtect: false); // rewinds to 0

        tape.Save();

        Assert.Equal(0, tape.Position); // head position unchanged
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

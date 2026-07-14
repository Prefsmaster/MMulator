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
        // Build a protected tape: protect byte (offset 0x50, bit 0) set in the record itself
        // (machine CLAUDE.md §17, 2026-07-14 — protect is read from the file, not a param).
        var cas = new byte[1280];
        cas[0x50] |= 0x01;
        var tape = new MiniTape();
        tape.LoadCasImage(cas);

        tape.SeekTo(1000, 0);
        var before = tape.Read();
        tape.Write(!before); // attempt flip
        tape.SeekTo(1000, 0);
        Assert.Equal(before, tape.Read()); // Write must be a no-op when protected
    }

    // ---- Blank tape = silence (CORRECTED 2026-07-14, machine CLAUDE.md §17) -----

    [Fact]
    public void NewTape_IsSilent_AcrossBothSides()
    {
        // Matches BASIC's own "Tape init" (writes silence so the wait-for-first-marker
        // times out) and a genuinely erased cassette (no flux transitions). An earlier
        // design used deterministic pseudo-noise instead; that broke cas_Write's own
        // forward scan (noise never presents the "nothing recorded here" signal the ROM
        // looks for, so the scan ran to the physical end of the tape before giving up).
        var tape = new MiniTape();
        for (var side = 0; side < MiniTape.Sides; side++)
        {
            tape.SeekTo(0, side);
            for (var i = 0; i < 1000; i++)
            {
                tape.SeekTo(i, side);
                Assert.False(tape.Read());
            }
            tape.SeekTo(MiniTape.PhasesPerSide - 1, side);
            Assert.False(tape.Read());
        }
    }

    // ---- LoadCasImage -----------------------------------------------------------

    [Fact]
    public void LoadCasImage_PositionsAt1_JustPastBotSensor()
    {
        // LoadCasImage ends at position 1, not 0.  At 0, IsAtEnd=true → BET=0, which the
        // ROM interprets as "tape at EOT / removed" and aborts motor start. Position 1 means
        // IsAtEnd=false → BET=1 = tape OK as soon as it is inserted.
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280]);
        Assert.Equal(1, tape.Position);
    }

    [Fact]
    public void LoadCasImage_ProtectByteSet_IsProtected()
    {
        var cas = new byte[1280];
        cas[0x50] |= 0x01;
        var tape = new MiniTape();
        tape.LoadCasImage(cas);
        Assert.True(tape.IsProtected);
    }

    [Fact]
    public void LoadCasImage_NoProtectByte_NotProtected()
    {
        // Default writable for any file that never sets the protect byte (new saves, older
        // saves, or files from other tools) — machine CLAUDE.md §17, 2026-07-14.
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280]);
        Assert.False(tape.IsProtected);
    }

    [Fact]
    public void LoadCasImage_ProtectBitOnlyReadFromFirstRecord()
    {
        // The protect byte lives in the FIRST 1280-byte record only; setting it in a later
        // record must have no effect.
        var cas = new byte[2 * 1280];
        cas[1280 + 0x50] |= 0x01; // second record's would-be protect byte
        var tape = new MiniTape();
        tape.LoadCasImage(cas);
        Assert.False(tape.IsProtected);
    }

    [Fact]
    public void SetProtected_TogglesLive()
    {
        var tape = new MiniTape();
        tape.LoadCasImage(new byte[1280]);
        Assert.False(tape.IsProtected);

        tape.SetProtected(true);
        Assert.True(tape.IsProtected);

        tape.SetProtected(false);
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
        // Silence has no valid framed blocks
        var tape = new MiniTape();
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
        tape.LoadCasImage(original);

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
        tape.LoadCasImage(original);

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
        tape.LoadCasImage(original);

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
        tape.LoadCasImage(original); // positions at 1

        var positionBeforeSave = tape.Position;
        tape.Save();

        Assert.Equal(positionBeforeSave, tape.Position); // Save() must not move the head
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

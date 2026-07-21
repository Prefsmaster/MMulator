using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices.Fdc;

/// <summary>
/// Unit tests for <see cref="Upd765"/> in isolation (no Machine/CPU) — project CLAUDE.md §13
/// milestone 19: presence-probe exact-byte behaviour, the confirmed command subset, the
/// 0x90 OUT-vs-IN dual-register split, semi-DMA pacing under both timing policies, and state
/// round-trip.
/// </summary>
public class Upd765Tests
{
    // ---- Presence probe ---------------------------------------------------------------------

    [Fact]
    public void Reset_MsrReadsExactly0x80()
    {
        var fdc = new Upd765();
        Assert.Equal(0x80, fdc.ReadStatus());
    }

    [Fact]
    public void WriteControl_Reset_ReturnsToIdle_MsrExactly0x80()
    {
        var fdc = new Upd765();
        // Mid-command, then a control-latch RESET (bit2) — the ROM's exact presence-probe
        // sequence (reference doc §5d): OUT (0x90),0x04 with no interrupt wait.
        fdc.WriteData(0x03); // SPECIFY opcode — now mid-command-phase
        fdc.WriteControl(0x04);
        Assert.Equal(0x80, fdc.ReadStatus());
    }

    [Fact]
    public void Reset_Method_AlsoReturnsToIdle()
    {
        var fdc = new Upd765();
        fdc.WriteData(0x03);
        fdc.Reset();
        Assert.Equal(0x80, fdc.ReadStatus());
    }

    // ---- 0x90 dual-register split -----------------------------------------------------------

    [Fact]
    public void ControlPort_OutIsLatch_InIsSeparateSemiDmaFlag()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        // Live OUT value with ENABLE=1 permanently set would make a read-back poll never wait
        // (reference doc §5d) — confirms IN is NOT a read-back of the OUT latch.
        fdc.WriteControl(0x01); // ENABLE=1
        Assert.Equal(0x00, fdc.ReadControl()); // no transfer in progress: bit0 clear regardless
    }

    // ---- SPECIFY (no interrupt, no result phase) ---------------------------------------------

    [Fact]
    public void Specify_CompletesImmediately_NoResultReady()
    {
        var fdc = new Upd765();
        var fired = false;
        fdc.ResultReady += () => fired = true;

        fdc.WriteData(0x03); // SPECIFY
        fdc.WriteData(0x60); // SRT/HUT
        fdc.WriteData(0x34); // HLT/ND

        Assert.Equal(0x80, fdc.ReadStatus());
        Assert.False(fired);
    }

    // ---- RECALIBRATE / SEEK / SENSE INTERRUPT STATUS -----------------------------------------

    [Fact]
    public void Recalibrate_Turbo_CompletesSynchronously_FiresResultReady()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        var fired = false;
        fdc.ResultReady += () => fired = true;

        fdc.WriteData(0x07); // RECALIBRATE
        fdc.WriteData(0x01); // unit

        Assert.True(fired);
        Assert.Equal(0x80, fdc.ReadStatus()); // back to idle, ready for Sense Interrupt Status
    }

    [Fact]
    public void Recalibrate_Authentic_DoesNotCompleteBeforeSettleDelay()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Authentic };
        var fired = false;
        fdc.ResultReady += () => fired = true;

        fdc.WriteData(0x07);
        fdc.WriteData(0x01);

        Assert.False(fired); // still settling — Authentic honours real seek/settle time
        Assert.NotEqual(0x80, fdc.ReadStatus()); // busy

        for (var i = 0; i < 100_000; i++) fdc.Tick();
        Assert.True(fired);
    }

    [Fact]
    public void SenseInterruptStatus_AfterSeek_ReportsSeekEndAndCylinder()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };

        fdc.WriteData(0x0F); // SEEK
        fdc.WriteData(0x01); // unit
        fdc.WriteData(0x05); // target cylinder 5

        fdc.WriteData(0x08); // SENSE INTERRUPT STATUS
        Assert.Equal(0x21, fdc.ReadData()); // ST0: seek-end (0x20) | unit 1
        Assert.Equal(0x05, fdc.ReadData()); // PCN
    }

    [Fact]
    public void SenseInterruptStatus_WithNothingPending_ReportsInvalidCommand()
    {
        var fdc = new Upd765();
        fdc.WriteData(0x08);
        Assert.Equal(0x80, fdc.ReadData()); // ST0 = invalid command
    }

    // ---- READ DATA / WRITE DATA + semi-DMA polling -------------------------------------------

    private static byte[] BuildSyntheticImage(int tracks, int sides)
    {
        var image = new byte[tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector];
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    [Fact]
    public void ReadData_Turbo_ReturnsExactSectorBytes()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        // Sector 1 of cylinder 0, head 0: raw offset 0x0000-0x00FF. Poke a known pattern there.
        for (var i = 0; i < 256; i++) image[i] = (byte)(i ^ 0xA5);
        var disk = new DskImage(image);

        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        var fired = false;
        fdc.ResultReady += () => fired = true;

        // READ DATA: unit=0, cylinder=0, head=0, sector=1, N=1(256B), EOT=1 (one sector).
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(0x01, fdc.ReadControl()); // semi-DMA byte-ready flag
            read[i] = fdc.ReadData();
        }

        Assert.Equal(image[..256], read);
        Assert.True(fired);
        Assert.Equal(0x80, fdc.ReadStatus());
    }

    [Fact]
    public void ReadData_Authentic_BytePacing_NotReadyUntilTickElapses()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Authentic };
        fdc.MountDisk(0, disk);

        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        Assert.Equal(0x00, fdc.ReadControl()); // first byte not ready yet
        for (var i = 0; i < 100; i++) fdc.Tick();
        Assert.Equal(0x01, fdc.ReadControl()); // now ready
    }

    [Fact]
    public void WriteData_Turbo_CommitsExactBytesToDisk()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        var fired = false;
        fdc.ResultReady += () => fired = true;

        // WRITE DATA: unit=0, cylinder=2, head=0, sector=1, N=1, EOT=1.
        fdc.WriteData(0x45);
        fdc.WriteData(0x00);
        fdc.WriteData(0x02);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var pattern = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            pattern[i] = (byte)(i * 3 + 7);
            Assert.Equal(0x01, fdc.ReadControl());
            fdc.WriteData(pattern[i]);
        }

        Assert.True(fired);
        Assert.Equal(pattern, disk.ReadSector(cylinder: 2, head: 0, sector: 1).ToArray());
    }

    [Fact]
    public void WriteData_Turbo_DiskWriteProtected_DoesNotModifyDisk()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image) { WriteProtected = true };
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        fdc.WriteData(0x45);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        for (var i = 0; i < 256; i++) fdc.WriteData(0xFF);

        foreach (var b in disk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);
    }

    // ---- Unknown opcode -----------------------------------------------------------------------

    [Fact]
    public void UnknownOpcode_ReportsInvalidCommand_OneResultByte()
    {
        var fdc = new Upd765();
        fdc.WriteData(0xFE); // not in the ROM's confirmed subset
        Assert.Equal(0x80, fdc.ReadData());
        Assert.Equal(0x80, fdc.ReadStatus()); // back to idle after the single result byte
    }

    // ---- State round-trip ----------------------------------------------------------------------

    [Fact]
    public void SaveLoad_MidTransfer_RestoresExactState()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 256; i++) image[i] = (byte)i;
        var disk = new DskImage(image);

        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        // Consume 10 bytes, then snapshot mid-transfer.
        for (var i = 0; i < 10; i++) fdc.ReadData();

        var state = new InMemoryState();
        fdc.SaveState(state);

        var restored = new Upd765 { Policy = TimingPolicy.Turbo };
        restored.MountDisk(0, disk); // mount survives config-driven reconstruction in Machine
        restored.LoadState(state.BeginRead());

        for (var i = 10; i < 256; i++)
        {
            Assert.Equal((byte)i, restored.ReadData());
        }
    }
}

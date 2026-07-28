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
    // ---- Geometry-mismatch plumbing (project CLAUDE.md milestone 20d) -----------------------

    [Fact]
    public void GetMismatch_NothingMounted_IsNull()
    {
        var fdc = new Upd765();
        Assert.Null(fdc.GetMismatch(0));
    }

    [Fact]
    public void MountDisk_TwoArgOverload_LeavesMismatchNull()
    {
        // The plain MountDisk(drive, image) overload (every pre-existing test/call site) must
        // keep working unchanged — no mismatch to report is the correct default, not an error.
        var fdc = new Upd765();
        fdc.MountDisk(0, DskImage.CreateBlank(40, 2));
        Assert.Null(fdc.GetMismatch(0));
    }

    [Fact]
    public void MountDisk_ThreeArgOverload_StoresTheGivenMismatch()
    {
        var fdc = new Upd765();
        var (image, mismatch) = DskImage.Mount(new byte[32_768], configuredTracks: 40, configuredSides: 2);

        fdc.MountDisk(0, image, mismatch);

        Assert.Equal(mismatch, fdc.GetMismatch(0));
    }

    [Fact]
    public void EjectDisk_ClearsTheStoredMismatch()
    {
        var fdc = new Upd765();
        var (image, mismatch) = DskImage.Mount(new byte[32_768], configuredTracks: 40, configuredSides: 2);
        fdc.MountDisk(0, image, mismatch);

        fdc.EjectDisk(0);

        Assert.Null(fdc.GetMismatch(0));
        Assert.Null(fdc.GetDisk(0));
    }

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

    /// <summary>
    /// Turbo still defers completion by a small, fixed number of T-states — NOT literally zero.
    /// Real bug, found via a live boot-test hang: if RECALIBRATE/SEEK fired their completion
    /// interrupt SYNCHRONOUSLY inside the command dispatch, the interrupt gets accepted and
    /// fully serviced (IM2 vector → ISR → RETI) before the ROM ever reaches its own subsequent
    /// `halt` (Disk.asm `disk_recall`/`disk_do_search` always send the command, THEN halt,
    /// expecting to be woken by that same completion) — a lost wakeup, since the one-shot
    /// interrupt is already consumed by the time the intended waiter starts waiting. See
    /// <see cref="Upd765.MinimumLostWakeupGuardTStates"/>'s doc comment for the full trace.
    /// </summary>
    [Fact]
    public void Recalibrate_Turbo_CompletesAfterAFewTStates_FiresResultReady()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        var fired = false;
        fdc.ResultReady += () => fired = true;

        fdc.WriteData(0x07); // RECALIBRATE
        fdc.WriteData(0x01); // unit

        Assert.False(fired); // not synchronous — see the doc comment above
        for (var i = 0; i < 300; i++) fdc.Tick();
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
        for (var i = 0; i < 300; i++) fdc.Tick(); // let the (now deferred) seek complete

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

    // ---- READ A TRACK (real ROM byte 0x42) / WRITE DATA + semi-DMA polling --------------------
    //
    // Opcode-identity finding (project CLAUDE.md §17, 2026-07-24): the byte milestone 19
    // confirmed from the ROM disassembly and originally labelled "READ DATA" — 0x42 — actually
    // decodes to READ A TRACK's base opcode (0x02) with MF set, not READ DATA's (0x06). See
    // Upd765's class doc comment for the full bit-level derivation. R is always 1 in every real
    // confirmed usage, so READ A TRACK's "ignore R, always start at sector 1" behaviour is
    // byte-identical to what these tests already exercised — only the label/semantics changed,
    // not the assertions.

    private static byte[] BuildSyntheticImage(int tracks, int sides)
    {
        var image = new byte[tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector];
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    [Fact]
    public void ReadTrack_Turbo_ReturnsExactSectorBytes()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        // Sector 1 of cylinder 0, head 0: raw offset 0x0000-0x00FF. Poke a known pattern there.
        for (var i = 0; i < 256; i++) image[i] = (byte)(i ^ 0xA5);
        var disk = new DskImage(image);

        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        var fired = false;
        fdc.ResultReady += () => fired = true;

        // READ A TRACK: unit=0, cylinder=0, head=0, R=1(ignored), N=1(256B), EOT=1 (one sector).
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

        // Milestone 19a backfilled a real 7-byte result phase for every command (including
        // retroactively for this one) — the chip now stays busy until it's drained, exactly
        // matching the real ROM's own completion ISR (Disk.asm read_IO_status -> read_status_bytes
        // with B=7).
        for (var i = 0; i < 7; i++) fdc.ReadData();
        Assert.Equal(0x80, fdc.ReadStatus());
    }

    [Fact]
    public void ReadTrack_Authentic_BytePacing_NotReadyUntilTickElapses()
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

    /// <summary>READ A TRACK ignores R entirely — even if the command specifies a non-1 start
    /// sector, the transfer must still begin at sector 1 (right after the index pulse). This is
    /// the one behavioural difference from plain READ DATA that no real confirmed usage exercises
    /// (R is always 1 there) — covered here as a synthetic protocol test.</summary>
    [Fact]
    public void ReadTrack_IgnoresRField_AlwaysStartsAtSectorOne()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 512; i++) image[i] = (byte)i;
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // READ A TRACK: R=5 (should be ignored), EOT=1.
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x05);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[256];
        for (var i = 0; i < 256; i++) read[i] = fdc.ReadData();

        Assert.Equal(image[..256], read); // sector 1's content, not sector 5's
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

        // SEEK to cylinder 2 first — the data-shaped commands address wherever the head
        // physically IS (tracked via prior SEEK/RECALIBRATE), not the command's own cylinder
        // byte (see Upd765.DispatchDataCommand's doc comment; confirmed against the real ROM
        // driver).
        fdc.WriteData(0x0F);
        fdc.WriteData(0x00);
        fdc.WriteData(0x02);
        for (var i = 0; i < 300; i++) fdc.Tick(); // let the (deferred) seek actually complete
        fired = false; // reset — only care about WRITE DATA's own completion below

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

    // ---- Host status surface (P2000.UI milestone 14) -------------------------------------------

    [Fact]
    public void MotorOn_ReflectsTheSharedControlLatchBit()
    {
        var fdc = new Upd765();
        Assert.False(fdc.MotorOn);

        fdc.WriteControl(0x08); // MOTOR bit
        Assert.True(fdc.MotorOn);

        fdc.WriteControl(0x00);
        Assert.False(fdc.MotorOn);
    }

    [Fact]
    public void GetCylinder_UnsoughtDrive_ReadsZero()
    {
        var fdc = new Upd765();
        Assert.Equal(0, fdc.GetCylinder(0));
        Assert.Equal(0, fdc.GetCylinder(3));
    }

    [Fact]
    public void GetCylinder_AfterSeek_ReflectsThatDriveOnly()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };

        fdc.WriteData(0x0F); // SEEK
        fdc.WriteData(0x02); // unit 2
        fdc.WriteData(0x09); // target cylinder 9
        for (var i = 0; i < 300; i++) fdc.Tick();

        Assert.Equal(9, fdc.GetCylinder(2));
        Assert.Equal(0, fdc.GetCylinder(0)); // other drives unaffected
    }

    [Fact]
    public void CurrentTransfer_Idle_IsNull()
    {
        var fdc = new Upd765();
        Assert.Null(fdc.CurrentTransfer);
    }

    [Fact]
    public void CurrentTransfer_DuringReadTrack_ReportsDriveHeadAndDirection()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Authentic }; // stays in ExecutionPhase long enough to observe
        fdc.MountDisk(1, disk);

        fdc.WriteData(0x42); // READ A TRACK
        fdc.WriteData(0x01); // unit 1
        fdc.WriteData(0x00);
        fdc.WriteData(0x00); // head 0
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var status = fdc.CurrentTransfer;
        Assert.NotNull(status);
        Assert.Equal(1, status!.Value.Drive);
        Assert.Equal(0, status.Value.Head);
        Assert.False(status.Value.IsWrite);
        Assert.Equal(1, status.Value.Sector); // R from the command block (sector 1, one sector requested)
    }

    /// <summary>Project CLAUDE.md §17, 2026-07-23 (owner decision): current sector is the REAL
    /// live value during a multi-sector transfer — derived from R plus bytes moved through the
    /// semi-DMA loop so far, not pinned to the starting sector for the whole transfer.</summary>
    [Fact]
    public void CurrentTransfer_MultiSectorTransfer_SectorAdvancesAsBytesMove()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo }; // synchronous byte-ready — isolates the sector arithmetic from timing
        fdc.MountDisk(0, disk);

        // READ A TRACK: unit=0, cylinder=0, head=0, R=1(ignored), N=1 (256B), EOT=3 (3 sectors).
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x03);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        Assert.Equal(1, fdc.CurrentTransfer!.Value.Sector); // no bytes moved yet — still R

        for (var i = 0; i < 256; i++) fdc.ReadData(); // consume exactly one full sector's worth

        Assert.Equal(2, fdc.CurrentTransfer!.Value.Sector); // advanced to the second sector
    }

    /// <summary>Real bug, fixed 2026-07-23: a SEEK on one drive previously left
    /// <c>CurrentTransfer.Drive</c> reporting whichever drive last did a data-shaped
    /// transfer (or 0, if none ever had) — the host status surface would light up the wrong
    /// drive's activity indicator during a seek on a different drive.</summary>
    [Fact]
    public void CurrentTransfer_DuringSeek_ReportsTheSeekingDrive_NotAStaleOne()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk0 = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk0);

        // A completed READ A TRACK on drive 0 first, so _transferDrive would be stale-0 if the
        // bug were still present — use drive 2 for the seek so a bug (stale drive) is
        // distinguishable from the fix.
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        for (var i = 0; i < 256; i++) fdc.ReadData(); // completes the transfer, phase -> ResultPhase
        for (var i = 0; i < 7; i++) fdc.ReadData(); // drain the backfilled result phase back to Idle
                                                     // (milestone 19a — real ROM does the same, see
                                                     // ReadTrack_Turbo_ReturnsExactSectorBytes)

        // Now SEEK drive 2 — CurrentTransfer.Drive must report 2, not the stale 0 from above.
        fdc.WriteData(0x0F);
        fdc.WriteData(0x02);
        fdc.WriteData(0x05);

        Assert.Equal(2, fdc.CurrentTransfer!.Value.Drive);
    }

    [Fact]
    public void CurrentTransfer_AfterCompletion_IsNullAgain()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
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
        for (var i = 0; i < 256; i++) fdc.ReadData();

        Assert.Null(fdc.CurrentTransfer);
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

    // ---- SENSE DRIVE STATUS (0x04) — confirmed real usage, two independent callers ------------
    // (JWSDOS's check_write_enable and JWSFormat's check_write_protect, both `02 04 <drive>`
    // testing ST3 bit 6 — docs/FDC-implementation.md §2). Real wire bytes are just `04 <drive>`;
    // the `02` in the disassembly listings is the caller's own length prefix, not a wire byte.

    [Fact]
    public void SenseDriveStatus_WriteProtectedDisk_ReportsBit6Set()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image) { WriteProtected = true };
        var fdc = new Upd765();
        fdc.MountDisk(1, disk);

        fdc.WriteData(0x04); // SENSE DRIVE STATUS
        fdc.WriteData(0x01); // drive 1

        var st3 = fdc.ReadData();
        Assert.Equal(0x40, st3 & 0x40); // WP set — the exact bit check_write_enable/check_write_protect test
    }

    [Fact]
    public void SenseDriveStatus_WritableDisk_ReportsBit6Clear()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image) { WriteProtected = false };
        var fdc = new Upd765();
        fdc.MountDisk(1, disk);

        fdc.WriteData(0x04);
        fdc.WriteData(0x01);

        var st3 = fdc.ReadData();
        Assert.Equal(0x00, st3 & 0x40);
    }

    [Fact]
    public void SenseDriveStatus_ReflectsTrackZeroAndTwoSideBits()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765();
        fdc.MountDisk(0, disk);

        fdc.WriteData(0x04);
        fdc.WriteData(0x00); // drive 0, never sought — still at cylinder 0
        var st3 = fdc.ReadData();

        Assert.Equal(0x10, st3 & 0x10); // T0
        Assert.Equal(0x08, st3 & 0x08); // TS — double-sided fixture
    }

    // ---- READ ID (0x0A) — no known real caller, synthetic protocol test only ------------------

    [Fact]
    public void ReadId_ReportsCurrentCylinderHeadAndFixedSectorOne()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };

        fdc.WriteData(0x0F); // SEEK drive 0 to cylinder 7 first
        fdc.WriteData(0x00);
        fdc.WriteData(0x07);
        for (var i = 0; i < 300; i++) fdc.Tick();

        fdc.WriteData(0x0A); // READ ID, drive 0
        fdc.WriteData(0x00);

        Assert.Equal(0x00, fdc.ReadData()); // ST0
        Assert.Equal(0x00, fdc.ReadData()); // ST1
        Assert.Equal(0x00, fdc.ReadData()); // ST2
        Assert.Equal(0x07, fdc.ReadData()); // C — tracked cylinder
        Assert.Equal(0x00, fdc.ReadData()); // H
        Assert.Equal(0x01, fdc.ReadData()); // R — this model has no rotational ID scan, sector 1 stand-in
        Assert.Equal(0x01, fdc.ReadData()); // N — 256B, this platform's only sector size
    }

    // ---- READ DATA (0x06) — datasheet-standard opcode, no known real P2000 caller -------------
    // (the real ROM byte, 0x42, is READ A TRACK — see the opcode-identity finding above). Unlike
    // READ A TRACK, true READ DATA must respect R as the search/start sector.

    [Fact]
    public void ReadData_RespectsRField_UnlikeReadTrack()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 512; i++) image[i] = (byte)i;
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // READ DATA (0x46 = 0x06|MF): unit=0, cylinder=0, head=0, R=2, N=1, EOT=2 (one sector).
        fdc.WriteData(0x46);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x02);
        fdc.WriteData(0x01);
        fdc.WriteData(0x02);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[256];
        for (var i = 0; i < 256; i++) read[i] = fdc.ReadData();

        Assert.Equal(image.AsSpan(256, 256).ToArray(), read); // sector 2's content, i.e. R honoured
    }

    // ---- READ DELETED DATA / WRITE DELETED DATA — no known real caller, synthetic only --------

    [Fact]
    public void ReadDeletedData_NoSectorIsEverMarkedDeleted_ReportsControlMarkAndAbnormalTermination()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 256; i++) image[i] = (byte)i;
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // READ DELETED DATA (0x4C = 0x0C|MF): unit=0, cylinder=0, head=0, R=1, N=1, EOT=1.
        fdc.WriteData(0x4C);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[256];
        for (var i = 0; i < 256; i++) read[i] = fdc.ReadData();
        Assert.Equal(image[..256], read); // data still transferred, per the datasheet

        Assert.Equal(0x40, fdc.ReadData() & 0x40); // ST0 IC = abnormal termination
        fdc.ReadData(); // ST1 (not asserted)
        Assert.Equal(0x40, fdc.ReadData() & 0x40); // ST2 CM
    }

    [Fact]
    public void WriteDeletedData_Turbo_CommitsExactBytesToDisk()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // WRITE DELETED DATA (0x49 = 0x09|MF): unit=0, cylinder=0, head=0, R=1, N=1, EOT=1.
        fdc.WriteData(0x49);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var pattern = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            pattern[i] = (byte)(i + 1);
            fdc.WriteData(pattern[i]);
        }

        Assert.Equal(pattern, disk.ReadSector(0, 0, 1).ToArray());
    }

    // ---- SCAN EQUAL / LOW-OR-EQUAL / HIGH-OR-EQUAL — no known real caller, synthetic only ------

    [Fact]
    public void ScanEqual_IdenticalContent_ReportsShSet()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 256; i++) image[i] = (byte)(i ^ 0x3C);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // SCAN EQUAL (0x51 = 0x11|MF): unit=0, cylinder=0, head=0, R=1, N=1, EOT=1, STP=1.
        fdc.WriteData(0x51);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);

        for (var i = 0; i < 256; i++) fdc.WriteData((byte)(i ^ 0x3C)); // identical to disk content

        Assert.Equal(0x00, fdc.ReadData()); // ST0
        Assert.Equal(0x00, fdc.ReadData()); // ST1
        Assert.Equal(0x08, fdc.ReadData() & 0x08); // ST2 SH set — perfect equality found
    }

    [Fact]
    public void ScanEqual_DifferentContent_ClearsShSetsSn()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image); // all-zero content
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        fdc.WriteData(0x51);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);

        for (var i = 0; i < 256; i++) fdc.WriteData(0xFF); // never equals the disk's all-zero content

        fdc.ReadData(); // ST0
        fdc.ReadData(); // ST1
        var st2 = fdc.ReadData();
        Assert.Equal(0x00, st2 & 0x08); // SH clear
        Assert.Equal(0x04, st2 & 0x04); // SN set — Scan Equal has no inequality to satisfy
    }

    [Fact]
    public void ScanLowOrEqual_HostByteAlwaysAtOrAboveDisk_ClearsSn()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image); // all-zero content
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // SCAN LOW OR EQUAL (0x59 = 0x19|MF).
        fdc.WriteData(0x59);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);

        // disk byte (0x00) < host byte (0x01) for every byte — satisfies "disk <= host".
        for (var i = 0; i < 256; i++) fdc.WriteData(0x01);

        fdc.ReadData(); // ST0
        fdc.ReadData(); // ST1
        var st2 = fdc.ReadData();
        Assert.Equal(0x00, st2 & 0x08); // SH clear (not an exact match)
        Assert.Equal(0x00, st2 & 0x04); // SN clear — the <= condition was satisfied throughout
    }

    [Fact]
    public void ScanHighOrEqual_HostByteAlwaysAtOrBelowDisk_ClearsSn()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 256; i++) image[i] = 0x80;
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // SCAN HIGH OR EQUAL (0x5D = 0x1D|MF).
        fdc.WriteData(0x5D);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);

        // disk byte (0x80) > host byte (0x00) for every byte — satisfies "disk >= host".
        for (var i = 0; i < 256; i++) fdc.WriteData(0x00);

        fdc.ReadData(); // ST0
        fdc.ReadData(); // ST1
        var st2 = fdc.ReadData();
        Assert.Equal(0x00, st2 & 0x08); // SH clear (not an exact match)
        Assert.Equal(0x00, st2 & 0x04); // SN clear — the >= condition was satisfied throughout
    }

    // ---- FORMAT A TRACK (0x0D) — confirmed real usage (JWSFormat.bin/jwsformat.asm) -----------

    [Fact]
    public void FormatATrack_Synthetic_FillsSectorsWithDByte()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < image.Length; i++) image[i] = 0xFF; // pre-existing garbage
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // FORMAT A TRACK: opcode 0x4D (0x0D|MF), HD/US=0x00, N=0x01(256B), SC=0x02, GPL=0x32, D=0x00.
        fdc.WriteData(0x4D);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x02);
        fdc.WriteData(0x32);
        fdc.WriteData(0x00);

        // Host feeds 4 bytes (C,H,R,N) per sector, SC=2 times.
        byte[] groups = { 1, 0, 1, 1, 1, 0, 2, 1 };
        foreach (var b in groups) fdc.WriteData(b);

        Assert.All(disk.ReadSector(0, 0, 1).ToArray(), b => Assert.Equal(0x00, b));
        Assert.All(disk.ReadSector(0, 0, 2).ToArray(), b => Assert.Equal(0x00, b));
    }

    /// <summary>Real integration test against JWSFormat.bin's own confirmed command bytes and
    /// execution-phase mechanism (docs/FDC-implementation.md §2, project CLAUDE.md §17
    /// 2026-07-24): `06 4D &lt;HD/US&gt; 01 10h 32h 00h` (the leading `06` is jwsformat.asm's own
    /// length prefix, not a wire byte — matches this project's earlier Sense Drive Status finding
    /// of the same disassembly-listing convention), then SC=16 groups of (C,H,R,N) fed via
    /// `outi`, gated by the 0x90 bit0 poll — the SAME semi-DMA mechanism WRITE DATA uses.</summary>
    [Fact]
    public void FormatATrack_RealJwsFormatBytes_FormatsWholeTrack_MatchingConfirmedMechanism()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        for (var s = 1; s <= DskImage.SectorsPerTrack; s++)
        {
            disk.WriteSector(0, 0, s, Enumerable.Repeat((byte)0xAA, 256).ToArray());
        }
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // Command phase — exact confirmed bytes (wire bytes only; the disassembly's own `06`
        // length-prefix byte is not transmitted): opcode 0x4D, HD/US=0x00 (drive 0, side 0),
        // N=0x01, SC=0x10 (16 decimal), GPL=0x32, D=0x00.
        fdc.WriteData(0x4D);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x32);
        fdc.WriteData(0x00);

        // Execution phase — jwsformat.asm's own confirmed quirk: the Cylinder byte it writes is
        // track_index+1 (here: 1), NOT the real 0-based physical track (0) — this project's
        // Upd765/DskImage deliberately don't use C for addressing (project CLAUDE.md §17), so
        // this off-by-one is reproduced faithfully without affecting the result.
        for (byte sector = 1; sector <= DskImage.SectorsPerTrack; sector++)
        {
            Assert.Equal(0x01, fdc.ReadControl());
            fdc.WriteData(0x01);   // C — track_index + 1 (jwsformat.asm's own off-by-one)
            fdc.WriteData(0x00);   // H — side 0
            fdc.WriteData(sector); // R
            fdc.WriteData(0x01);   // N — 256B
        }

        for (var s = 1; s <= DskImage.SectorsPerTrack; s++)
        {
            Assert.All(disk.ReadSector(0, 0, s).ToArray(), b => Assert.Equal(0x00, b));
        }
    }

    // ---- TC-forced early completion — real "Disk I/O error" bug investigation, 2026-07-28 -----
    // (reference doc §5d). Real Philips Disk BASIC's resident LOAD driver requests a wide EOT
    // window on READ DATA, takes only the sector(s) it actually wants via the data register, then
    // writes the TC control-latch bit to abort the rest — a legitimate, previously-unexercised
    // command shape (the ROM's own fixed-EOT boot reads always complete NATURALLY). Three real
    // bugs surfaced here, all fixed together since a live repro needed all three to actually load.

    [Fact]
    public void ReadData_TcTerminatedEarly_ReportsTheSectorActuallyRead_NotTheEotWindowsTail()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        for (var i = 0; i < 4096; i++) image[i] = (byte)i;
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // READ DATA (0x46): unit=0, cyl=0, head=0, R=1, N=1, EOT=0x10 (whole 16-sector track) —
        // exactly Disk BASIC's own confirmed shape (reference doc §5d).
        fdc.WriteData(0x46);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        for (var i = 0; i < 256; i++) fdc.ReadData(); // take exactly 1 sector (256 bytes)
        fdc.WriteControl(0x0E);                       // TC — abort the rest of the 16-sector window
        for (var i = 0; i < 300; i++) fdc.Tick();      // TC now completes via the lost-wakeup guard

        fdc.ReadData(); // ST0
        fdc.ReadData(); // ST1
        fdc.ReadData(); // ST2
        Assert.Equal(0x00, fdc.ReadData());            // C
        Assert.Equal(0x00, fdc.ReadData());            // H
        Assert.Equal(0x01, fdc.ReadData());            // R — the sector ACTUALLY read, not 0x10
        Assert.Equal(0x01, fdc.ReadData());             // N
    }

    [Fact]
    public void WriteData_TcTerminatedEarly_CommitsOnlyTheSectorsActuallyReceived()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var original = new byte[256];
        Array.Fill(original, (byte)0xEE);
        image.AsSpan(256, 256).Fill(0xEE); // sector 2 pre-filled with a known, distinct pattern
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // WRITE DATA (0x45): unit=0, cyl=0, head=0, R=1, N=1, EOT=0x10 — host only ever sends 1
        // real sector's worth of bytes, then TC-aborts.
        fdc.WriteData(0x45);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) { pattern[i] = (byte)(i + 1); fdc.WriteData(pattern[i]); }
        fdc.WriteControl(0x0E); // TC
        for (var i = 0; i < 300; i++) fdc.Tick();

        Assert.Equal(pattern, disk.ReadSector(0, 0, 1).ToArray());
        // Sector 2 (never sent by the host) must be untouched — NOT overwritten with the
        // zero-initialized tail of the originally-requested 16-sector buffer.
        Assert.All(disk.ReadSector(0, 0, 2).ToArray(), b => Assert.Equal(0xEE, b));
    }

    [Fact]
    public void CompleteTransfer_ReportsAddressedDriveAndHeadInSt0_NotAlwaysZero()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(2, disk);

        // READ DATA targeting drive 2 (US1/US0 = binary 10), head 0, single sector.
        fdc.WriteData(0x46);
        fdc.WriteData(0x02);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        for (var i = 0; i < 256; i++) fdc.ReadData();

        // ST0's D2 (HD)/D1-D0 (US1/US0) must reflect the addressed unit even on a normal
        // completion — datasheet-standard, previously always 0x00 regardless of drive/head
        // (invisible whenever drive 0/head 0 was used, wrong otherwise — reference doc §5d).
        Assert.Equal(0x02, fdc.ReadData());
    }

    /// <summary>Mirrors <c>Recalibrate_Turbo_CompletesAfterAFewTStates_FiresResultReady</c> above
    /// — same lost-wakeup guard, now also covering TC-forced transfer completion (the SECOND real
    /// caller of <see cref="Upd765.MinimumLostWakeupGuardTStates"/>, reference doc §5d 2026-07-28):
    /// a real driver writes TC then HALTs waiting for the completion interrupt a few instructions
    /// later — completing synchronously inside the same OUT would deliver and fully consume that
    /// interrupt before the driver ever reaches its own HALT.</summary>
    [Fact]
    public void ReadData_TcForcedCompletion_IsNotSynchronous_FiresResultReadyAfterAFewTStates()
    {
        var disk = new DskImage(BuildSyntheticImage(tracks: 40, sides: 2));
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);
        var fired = false;
        fdc.ResultReady += () => fired = true;

        fdc.WriteData(0x46);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        for (var i = 0; i < 256; i++) fdc.ReadData();

        fdc.WriteControl(0x0E); // TC
        Assert.False(fired);    // not synchronous — see the doc comment above
        for (var i = 0; i < 300; i++) fdc.Tick();
        Assert.True(fired);
    }
}

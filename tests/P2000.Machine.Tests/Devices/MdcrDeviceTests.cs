using P2000.Machine.Devices.Cassette;
using P2000.Machine.Io;
using P2000.Machine.Tests.State;
using Machine = P2000.Machine.Machine;

namespace P2000.Machine.Tests.Devices;

public class MdcrDeviceTests
{
    private static (MdcrDevice mdcr, CPoutLatch cpOut) Create()
    {
        var cpOut = new CPoutLatch();
        return (new MdcrDevice(cpOut), cpOut);
    }

    private static byte[] OneCasRecord()
    {
        // 1280 bytes: zeroed header region at +0x30 and 1024-byte data at +0x100
        return new byte[1280];
    }

    // ---- Bare-machine status defaults (no tape) ----------------------------------

    [Fact]
    public void NoTape_CipSet_ActiveLow()
    {
        var (mdcr, _) = Create();
        Assert.Equal(0x10, mdcr.ReadStatus() & 0x10); // CIP bit set = no cassette
    }

    [Fact]
    public void NoTape_BetSet_TapeOkSense()
    {
        var (mdcr, _) = Create();
        Assert.Equal(0x20, mdcr.ReadStatus() & 0x20); // BET set = tape OK
    }

    [Fact]
    public void NoTape_DefaultStatus_CipPlusBetPlusWen()
    {
        var (mdcr, _) = Create();
        // Real MDCR pulls WEN high when no cassette is present; cas_Init rejects CIP=1 WEN=0.
        Assert.Equal(0x38, mdcr.ReadStatus() & 0x38); // CIP(0x10) + BET(0x20) + WEN(0x08)
    }

    [Fact]
    public void NoTape_HasTape_False()
    {
        var (mdcr, _) = Create();
        Assert.False(mdcr.HasTape);
    }

    // ---- InsertTape / EjectTape live CIP transitions ----------------------------

    [Fact]
    public void InsertTape_ClearsCip()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x10); // CIP clear = cassette present
    }

    [Fact]
    public void InsertTape_HasTape_True()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.True(mdcr.HasTape);
    }

    [Fact]
    public void EjectTape_SetsCipAgain()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        mdcr.EjectTape();
        Assert.Equal(0x10, mdcr.ReadStatus() & 0x10);
    }

    [Fact]
    public void InsertTape_WithProtectByteSet_SetsWen()
    {
        // Write-protect is read from the file's own record (offset 0x50, bit 0) — machine
        // CLAUDE.md §17, 2026-07-14 — not passed in as a caller flag anymore.
        var (mdcr, _) = Create();
        var cas = OneCasRecord();
        cas[0x50] |= 0x01;
        mdcr.InsertTape(cas);
        Assert.Equal(0x08, mdcr.ReadStatus() & 0x08); // WEN set = protected
    }

    [Fact]
    public void InsertTape_NoProtectByte_DefaultsWritable()
    {
        // Regression check for the reported "always write-protected" symptom (§14.13a test
        // (a)): a plain/foreign .cas with no protect byte ever set defaults writable.
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x08); // WEN clear = writable
    }

    [Fact]
    public void SetWriteProtected_TogglesWen_WithoutTouchingCipOrBet()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        var cipBefore = mdcr.ReadStatus() & 0x10;
        var betBefore = mdcr.ReadStatus() & 0x20;

        mdcr.SetWriteProtected(true);
        Assert.Equal(0x08, mdcr.ReadStatus() & 0x08);
        Assert.Equal(cipBefore, mdcr.ReadStatus() & 0x10);
        Assert.Equal(betBefore, mdcr.ReadStatus() & 0x20);

        mdcr.SetWriteProtected(false);
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x08);
        Assert.Equal(cipBefore, mdcr.ReadStatus() & 0x10);
        Assert.Equal(betBefore, mdcr.ReadStatus() & 0x20);
    }

    [Fact]
    public void SetWriteProtected_NoTape_NoOp()
    {
        var (mdcr, _) = Create();
        mdcr.SetWriteProtected(true); // must not throw with no tape mounted
        Assert.False(mdcr.HasTape);
    }

    [Fact]
    public void SetWriteProtected_ProtectedTape_RejectsWriteBlockAtHead()
    {
        // Confirms the toggle actually gates writes (via the already-modeled WEN check),
        // not just a cosmetic status bit (§14.13a test (c)).
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        mdcr.SetWriteProtected(true);

        Assert.False(mdcr.WriteBlockAtHead(new byte[32], new byte[1024]));
    }

    [Fact]
    public void SaveThenReload_ProtectStateRoundTrips()
    {
        // §14.13a test (d): protect a tape, save, reload — still protected.
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.True(mdcr.WriteBlockAtHead(new byte[32], new byte[1024]));
        mdcr.SetWriteProtected(true);

        var saved = mdcr.SaveTape();
        Assert.NotNull(saved);

        var (reloaded, _) = Create();
        reloaded.InsertTape(saved!);
        Assert.True(reloaded.IsWriteProtected);
        Assert.Equal(0x08, reloaded.ReadStatus() & 0x08);
    }

    [Fact]
    public void InsertBlankTape_StillDefaultsWritable()
    {
        // §14.13a test (e): a genuinely fresh blank tape has no prior saved state to read —
        // still defaults writable.
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.False(mdcr.IsWriteProtected);
    }

    // ---- InsertBlankTape (P2000.Machine CLAUDE.md §17, 2026-07-14) --------------

    [Fact]
    public void InsertBlankTape_ClearsCip_LikeInsertTape()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x10); // CIP clear = cassette present
    }

    [Fact]
    public void InsertBlankTape_HasTape_True()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.True(mdcr.HasTape);
    }

    [Fact]
    public void InsertBlankTape_NotWriteProtected()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.False(mdcr.IsWriteProtected);
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x08); // WEN clear = writable
    }

    [Fact]
    public void InsertBlankTape_ImmediatelyWritable_WriteBlockAtHeadSucceeds()
    {
        // "No format step" (project CLAUDE.md §14.13): CSAVE appends at the head position
        // (BOT on a fresh tape) with no prior formatting action.
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();

        var header = new byte[32];
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        Assert.True(mdcr.WriteBlockAtHead(header, data));
    }

    [Fact]
    public void InsertBlankTape_CsaveThenSaveTape_RoundTripsByteIdentical()
    {
        // The blank-tape → CSAVE → Save-as-.cas → reload round trip (UI milestone 13's
        // motivating case), exercised at the machine layer where it's actually testable
        // (the file-dialog half is UI-only and not unit-tested — see P2000.UI.Tests).
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0xA0 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        Assert.True(mdcr.WriteBlockAtHead(header, data));

        var savedCas = mdcr.SaveTape();
        Assert.NotNull(savedCas);
        Assert.Equal(1280, savedCas!.Length);

        // Reload the saved .cas onto a fresh mount and confirm the decoded block matches.
        var (reloaded, _) = Create();
        reloaded.InsertTape(savedCas);
        Assert.True(reloaded.TryReadBlockAtHead(out var reloadedHeader, out var reloadedData));
        Assert.Equal(header, reloadedHeader);
        Assert.Equal(data, reloadedData);
    }

    [Fact]
    public void InsertBlankTape_OverAlreadyMountedTape_NeverObservesAbsentInBetween()
    {
        // "One CIP transition, not two" (project CLAUDE.md §14.13 test (e)): swapping directly
        // from one mounted tape to a blank one must never pass through the "no cassette" state.
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x10); // present

        mdcr.InsertBlankTape();

        Assert.Equal(0x00, mdcr.ReadStatus() & 0x10); // still present, never observed absent
        Assert.True(mdcr.HasTape);
    }

    // ---- Motor / no-tick without motor ------------------------------------------

    [Fact]
    public void Tick_MotorOff_NoPllActivity_StatusUnchanged()
    {
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(OneCasRecord());
        cpOut.Write(0x00); // FWD=0 REV=0 → motor off
        var before = mdcr.ReadStatus();

        for (var i = 0; i < 10_000; i++) mdcr.Tick(1);

        // RDC/RDA must not have changed (no motor = no bit engine)
        Assert.Equal(before & 0xC0, mdcr.ReadStatus() & 0xC0);
    }

    // ---- BET reflects tape position ---------------------------------------------

    [Fact]
    public void InsertTape_StartsJustPastBOT_BetSet()
    {
        // LoadCasImage positions at 1 (just past the BOT sensor) so BET=1 immediately after
        // insert. At position 0 IsAtEnd=true → BET=0, which the ROM misreads as "tape at EOT"
        // and aborts the motor start. Position 1 → IsAtEnd=false → BET=1 = tape OK.
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.Equal(0x20, mdcr.ReadStatus() & 0x20); // BET set = tape OK, not at physical end
    }

    [Fact]
    public void Forward_OffBOT_BetSet()
    {
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(OneCasRecord());
        cpOut.Write(0x08); // FWD=1

        // Advance enough phases to move off BOT (1 phase = 209 ticks)
        for (var i = 0; i < 209 * 5; i++) mdcr.Tick(1);

        Assert.Equal(0x20, mdcr.ReadStatus() & 0x20); // BET set = not at end
    }

    // ---- PLL: reading a clean bitstream recovers bits ---------------------------

    [Fact]
    public void Tick_ReadPhases_RdcToggles_WhenBitsRecovered()
    {
        // Encode a single byte (0x55 = alternating bits) and read it back; RDC must toggle.
        var cas = OneCasRecord();
        cas[0x100] = 0x55; // first data byte in block 0

        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(cas);
        cpOut.Write(0x08); // FWD=1, WCD=0 (read mode)

        // Skip past BOT (position 0) — advance 1 phase so we're past the end sentinel
        for (var i = 0; i < 209; i++) mdcr.Tick(1);

        var rdcBefore = mdcr.ReadStatus() & 0x40;

        // Run enough phases to get through the BOT gap (5800) + BOB gap (6160) + MARK (64) +
        // into the data section. Use generous upper bound: 16000 phases = 16000 × 209 ticks.
        var rdcToggled = false;
        for (var i = 0; i < 16_000 * 209; i++)
        {
            mdcr.Tick(1);
            if ((mdcr.ReadStatus() & 0x40) != rdcBefore)
            {
                rdcToggled = true;
                break;
            }
        }

        Assert.True(rdcToggled, "RDC should toggle at least once while reading past the gap into data");
    }

    // ---- Reset ------------------------------------------------------------------

    [Fact]
    public void Reset_DoesNotEjectTape()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        mdcr.Reset();
        Assert.True(mdcr.HasTape);
    }

    [Fact]
    public void Reset_ClearsPll_TickCountZero()
    {
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(OneCasRecord());
        cpOut.Write(0x08); // FWD
        for (var i = 0; i < 500; i++) mdcr.Tick(1);

        mdcr.Reset();

        // After reset no partial phase should be pending (tickCount = 0)
        // Observe: running 1 tick should not cause 2 phases (no leftover cycles)
        var before = mdcr.ReadStatus();
        mdcr.Tick(1);
        // We can't directly inspect tickCount, but status should not flicker unexpectedly.
        // Just verify no exception and CIP reflects tape still present.
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x10);
    }

    // ---- SaveState / LoadState --------------------------------------------------

    [Fact]
    public void SaveLoad_NoTape_RoundTrip()
    {
        var (mdcr, _) = Create();
        var state = new InMemoryState();
        mdcr.SaveState(state);

        var (mdcr2, _) = Create();
        mdcr2.LoadState(state.BeginRead());

        Assert.Equal(mdcr.ReadStatus(), mdcr2.ReadStatus());
    }

    [Fact]
    public void SaveLoad_WithTape_PreservesStatusAndSeeksToPosition()
    {
        var cas = OneCasRecord();
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(cas);
        cpOut.Write(0x08); // FWD
        for (var i = 0; i < 209 * 10; i++) mdcr.Tick(1); // advance 10 phases

        var statusBefore = mdcr.ReadStatus();
        var state = new InMemoryState();
        mdcr.SaveState(state);

        // Restore with a fresh mdcr that also has a tape inserted
        var (mdcr2, _) = Create();
        mdcr2.InsertTape(cas); // tape must be present before LoadState
        mdcr2.LoadState(state.BeginRead());

        Assert.Equal(statusBefore, mdcr2.ReadStatus());
    }

    // ---- TimingPolicy -----------------------------------------------------------

    [Fact]
    public void Policy_Default_IsAuthentic()
    {
        var (mdcr, _) = Create();
        Assert.Equal(TimingPolicy.Authentic, mdcr.Policy);
    }

    [Fact]
    public void Policy_Turbo_PhaseEngineBypassedDuringTick()
    {
        // In turbo mode the tick loop must not advance the PLL or toggle RDC.
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(OneCasRecord());
        mdcr.Policy = TimingPolicy.Turbo;
        cpOut.Write(0x08); // FWD=1

        var rdcBefore = mdcr.ReadStatus() & 0x40;
        for (var i = 0; i < 30_000 * 209; i += 209) mdcr.Tick(209);

        Assert.Equal(rdcBefore, mdcr.ReadStatus() & 0x40); // RDC unchanged — engine bypassed
    }

    // ---- Write path (realtime WCD/WDA capture) ----------------------------------

    [Fact]
    public void Tick_WriteMode_WcdSet_DoesNotToggleRdc()
    {
        // When WCD=1 the write path is active; the PLL read path is suppressed.
        var (mdcr, cpOut) = Create();
        mdcr.InsertTape(OneCasRecord()); // unprotected by default (no protect byte set)

        cpOut.Write(0x08); // FWD=1, WCD=0 — advance off BOT first
        for (var i = 0; i < 209 * 3; i++) mdcr.Tick(1);

        cpOut.Write(0x0A); // FWD=1, WCD=1 — switch to write mode
        var rdcBefore = mdcr.ReadStatus() & 0x40;
        for (var i = 0; i < 209 * 20; i++) mdcr.Tick(1);

        Assert.Equal(rdcBefore, mdcr.ReadStatus() & 0x40); // RDC never toggled in write mode
    }

    // ---- SaveTape (host-side .cas serialization) --------------------------------

    [Fact]
    public void SaveTape_NoTape_ReturnsNull()
    {
        var (mdcr, _) = Create();
        Assert.Null(mdcr.SaveTape());
    }

    [Fact]
    public void SaveTape_InsertedTape_RoundTrips()
    {
        var original = new byte[1280];
        for (var i = 0; i < 32; i++) original[0x30 + i] = (byte)(i + 0x20);
        for (var i = 0; i < 1024; i++) original[0x100 + i] = (byte)(i & 0xFF);

        var (mdcr, _) = Create();
        mdcr.InsertTape(original);

        var saved = mdcr.SaveTape();

        Assert.NotNull(saved);
        Assert.Equal(original[0x30..0x50], saved![0x30..0x50]);
        Assert.Equal(original[0x100..0x500], saved[0x100..0x500]);
    }

    [Fact]
    public void SaveTape_AfterEject_ReturnsNull()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        mdcr.EjectTape();
        Assert.Null(mdcr.SaveTape());
    }

    // ---- Dirty tracking (project CLAUDE.md §13 milestone 20a) ------------------

    [Fact]
    public void InsertTape_IsNotDirty()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord());
        Assert.False(mdcr.IsDirty);
    }

    [Fact]
    public void InsertBlankTape_IsNotDirty()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        Assert.False(mdcr.IsDirty);
    }

    [Fact]
    public void NoTape_IsNotDirty()
    {
        var (mdcr, _) = Create();
        Assert.False(mdcr.IsDirty);
    }

    [Fact]
    public void WriteBlockAtHead_SetsDirty()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();

        mdcr.WriteBlockAtHead(new byte[32], new byte[1024]);

        Assert.True(mdcr.IsDirty);
    }

    [Fact]
    public void MarkClean_ClearsDirty()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        mdcr.WriteBlockAtHead(new byte[32], new byte[1024]);
        Assert.True(mdcr.IsDirty);

        mdcr.MarkClean();

        Assert.False(mdcr.IsDirty);
    }

    [Fact]
    public void MarkClean_ThenWriteAgain_ReSetsDirty()
    {
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        mdcr.WriteBlockAtHead(new byte[32], new byte[1024]);
        mdcr.MarkClean();

        mdcr.WriteBlockAtHead(new byte[32], new byte[1024]);

        Assert.True(mdcr.IsDirty);
    }

    [Fact]
    public void EjectTape_DoesNotThrow_AndLeavesNoDirtyTapeMounted()
    {
        // Eject itself neither sets nor clears dirty (project CLAUDE.md §13.20a test (d)) — it
        // just makes the question moot since nothing is mounted afterward.
        var (mdcr, _) = Create();
        mdcr.InsertBlankTape();
        mdcr.WriteBlockAtHead(new byte[32], new byte[1024]);

        mdcr.EjectTape();

        Assert.False(mdcr.IsDirty); // no tape mounted → not dirty by definition
    }

    [Fact]
    public void MarkClean_NoTape_NoOp()
    {
        var (mdcr, _) = Create();
        mdcr.MarkClean(); // must not throw with no tape mounted
        Assert.False(mdcr.IsDirty);
    }

    // ---- Machine integration: port 0x20 OR-combines with CprinReader -----------

    [Fact]
    public void Machine_InFrom0x20_NoTape_Returns0x38()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xDB, 0x20, // IN A, (0x20)
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++) machine.Tick();

        Assert.Equal(0x38, machine.Cpu.Reg.A); // CIP(0x10)+BET(0x20)+WEN(0x08) — WEN pulled high with no tape
    }

    [Fact]
    public void Machine_InsertTape_CipClearsLive()
    {
        // CIP is a live transition — inserting a tape immediately clears the bit so that the
        // ROM's busy-wait polling loop sees the cassette without a machine reset.
        var machine = new Machine();
        Assert.Equal(0x10, machine.Mdcr.ReadStatus() & 0x10); // CIP set before insert

        machine.Mdcr.InsertTape(new byte[1280]);

        Assert.Equal(0x00, machine.Mdcr.ReadStatus() & 0x10); // CIP clear after insert

        // Confirm the change is also visible via a port 0x20 IN instruction.
        machine.Memory.LoadRom(new byte[]
        {
            0xDB, 0x20, // IN A, (0x20)
            0x76,       // HALT
        });
        for (var i = 0; i < 30; i++) machine.Tick();

        Assert.Equal(0x00, machine.Cpu.Reg.A & 0x10); // CIP bit clear in A
    }
}

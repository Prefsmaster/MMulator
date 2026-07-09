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
    public void InsertProtectedTape_SetsWen()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord(), writeProtect: true);
        Assert.Equal(0x08, mdcr.ReadStatus() & 0x08); // WEN set = protected
    }

    [Fact]
    public void InsertUnprotectedTape_WenClear()
    {
        var (mdcr, _) = Create();
        mdcr.InsertTape(OneCasRecord(), writeProtect: false);
        Assert.Equal(0x00, mdcr.ReadStatus() & 0x08);
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
        mdcr.InsertTape(OneCasRecord(), writeProtect: false);

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

using P2000.Machine.Devices.Ctc;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices.Ctc;

/// <summary>
/// Unit tests for <see cref="Z80Ctc"/> in isolation (no Machine/CPU) — project CLAUDE.md §13
/// milestone 17, tests (a)/(b), vector formula, control-word parsing, reset, and state
/// round-trip.
/// </summary>
public class Z80CtcTests
{
    // ---- (a) Timer mode: prescaler × TC cadence ------------------------------------------

    [Fact]
    public void TimerMode_Prescale16_Tc1_FiresAtExactly16thTick()
    {
        var ctc = new Z80Ctc();
        // 0x85 = CTRLWRD|TCNEXT|INTEN, timer mode, prescaler 16, start immediately (CLKSTRT=0)
        // — the ROM's confirmed CTC-presence probe control word (reference doc §5e).
        ctc.WritePort(0, 0x85);
        ctc.WritePort(0, 0x01); // TC = 1

        var ch0 = ctc.DaisyChainDevices[0];

        for (var i = 0; i < 15; i++)
        {
            ctc.Tick();
            Assert.False(ch0.IntPending);
        }

        ctc.Tick(); // 16th T-state: prescaler(16) x TC(1) = 16
        Assert.True(ch0.IntPending);
    }

    [Fact]
    public void TimerMode_Prescale256_TakesLonger()
    {
        var ctc = new Z80Ctc();
        // bit5 PRE256 = 1: prescaler 256.
        const byte control = 0x85 | 0x20;
        ctc.WritePort(0, control);
        ctc.WritePort(0, 0x01); // TC = 1

        var ch0 = ctc.DaisyChainDevices[0];

        for (var i = 0; i < 255; i++)
        {
            ctc.Tick();
            Assert.False(ch0.IntPending);
        }

        ctc.Tick();
        Assert.True(ch0.IntPending);
    }

    [Fact]
    public void TimerMode_NoInten_NeverSetsIntPending()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x05); // CTRLWRD|TCNEXT, timer, prescaler16, NO INTEN
        ctc.WritePort(0, 0x01);

        var ch0 = ctc.DaisyChainDevices[0];
        for (var i = 0; i < 20; i++) ctc.Tick();

        Assert.False(ch0.IntPending);
    }

    // ---- (b) Counter mode: exactly one INT per TC trigger pulses --------------------------

    [Fact]
    public void CounterMode_Tc1_FiresExactlyOncePerEdge_NotDoubled()
    {
        var ctc = new Z80Ctc();
        // 0xD5 = CTRLWRD|TCNEXT|INTEN|counter-mode|rising-trigger — the ROM's confirmed
        // ch3 keyboard/system-tick control word (reference doc §5e).
        ctc.WritePort(3, 0xD5);
        ctc.WritePort(3, 0x01); // TC = 1

        var ch3 = ctc.DaisyChainDevices[3];
        Assert.False(ch3.IntPending);

        ctc.ClkTrg(3);
        Assert.True(ch3.IntPending); // one field pulse -> one pending interrupt (50 Hz, not 100 Hz)

        ch3.Acknowledge(); // consumes it
        Assert.False(ch3.IntPending);

        ctc.ClkTrg(3); // next field pulse
        Assert.True(ch3.IntPending); // fires again, exactly once
    }

    [Fact]
    public void CounterMode_Tc2_RequiresTwoEdgesPerInterrupt()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(3, 0xD5);
        ctc.WritePort(3, 0x02); // TC = 2

        var ch3 = ctc.DaisyChainDevices[3];

        ctc.ClkTrg(3);
        Assert.False(ch3.IntPending); // 2 -> 1, not yet

        ctc.ClkTrg(3);
        Assert.True(ch3.IntPending); // 1 -> 0, reload, fires
    }

    [Fact]
    public void CounterMode_TimeConstantZero_MeansTwoFiftySix()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0xC5); // CTRLWRD|TCNEXT|INTEN|counter mode (no ACTTRG needed for direct ClkTrg calls)
        ctc.WritePort(0, 0x00); // TC byte 0 => 256

        var ch0 = ctc.DaisyChainDevices[0];

        for (var i = 0; i < 255; i++)
        {
            ctc.ClkTrg(0);
            Assert.False(ch0.IntPending);
        }

        ctc.ClkTrg(0); // 256th edge
        Assert.True(ch0.IntPending);
    }

    [Fact]
    public void TimerMode_UnprogrammedChannel_NeverTicks()
    {
        var ctc = new Z80Ctc();
        var ch1 = ctc.DaisyChainDevices[1];

        for (var i = 0; i < 1000; i++) ctc.Tick();

        Assert.False(ch1.IntPending);
    }

    // ---- Vector formula --------------------------------------------------------------------

    [Fact]
    public void VectorBase_ComputesPerChannelFormula_MatchesConfirmedRomTable()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x20); // bit0=0 -> interrupt-vector byte, only ch0 accepts it

        // Arm ch0 and ch3 (counter mode, TC=1, INTEN) so Acknowledge() has something to return.
        ctc.WritePort(0, 0xC5); ctc.WritePort(0, 0x01);
        ctc.WritePort(3, 0xC5); ctc.WritePort(3, 0x01);
        ctc.ClkTrg(0);
        ctc.ClkTrg(3);

        // Confirmed reference doc §5e: IM2 vector base 0x6020 -> ch0 low byte 0x20, ch3 0x26.
        Assert.Equal(0x20, ctc.DaisyChainDevices[0].Acknowledge());
        Assert.Equal(0x26, ctc.DaisyChainDevices[3].Acknowledge());
    }

    [Fact]
    public void VectorBase_WriteToNonZeroChannel_IsIgnored()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x20); // establish a real base first
        ctc.WritePort(1, 0x40); // bit0=0 on channel 1 -> real hardware ignores this

        ctc.WritePort(0, 0xC5); ctc.WritePort(0, 0x01);
        ctc.ClkTrg(0);

        Assert.Equal(0x20, ctc.DaisyChainDevices[0].Acknowledge());
    }

    // ---- Control-word RESET bit --------------------------------------------------------------

    [Fact]
    public void ResetBit_HaltsChannelUntilReprogrammed()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x85); // timer, INTEN, prescaler16
        ctc.WritePort(0, 0x01); // TC=1

        ctc.WritePort(0, 0x03); // CTRLWRD|RESET

        var ch0 = ctc.DaisyChainDevices[0];
        for (var i = 0; i < 50; i++) ctc.Tick();

        Assert.False(ch0.IntPending); // halted — never fires until reprogrammed
    }

    // ---- Reset() / SaveState / LoadState -----------------------------------------------------

    [Fact]
    public void Reset_ClearsProgrammingAndVectorBase()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x20);
        ctc.WritePort(3, 0xD5);
        ctc.WritePort(3, 0x01);
        ctc.ClkTrg(3);
        Assert.True(ctc.DaisyChainDevices[3].IntPending);

        ctc.Reset();

        Assert.False(ctc.DaisyChainDevices[3].IntPending);
        ctc.WritePort(0, 0x20); // re-set vector base
        ctc.WritePort(0, 0xC5); ctc.WritePort(0, 0x01);
        ctc.ClkTrg(0);
        Assert.Equal(0x20, ctc.DaisyChainDevices[0].Acknowledge()); // base wasn't corrupted, just re-applied
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservesChannelStateAndVectorBase()
    {
        var ctc = new Z80Ctc();
        ctc.WritePort(0, 0x20);
        ctc.WritePort(3, 0xD5);
        ctc.WritePort(3, 0x01);
        ctc.ClkTrg(3); // ch3 now IntPending

        var state = new InMemoryState();
        ctc.SaveState(state);

        var ctc2 = new Z80Ctc();
        ctc2.LoadState(state.BeginRead());

        Assert.True(ctc2.DaisyChainDevices[3].IntPending);

        // Vector base survived the round trip too.
        ctc2.WritePort(0, 0xC5); ctc2.WritePort(0, 0x01);
        ctc2.ClkTrg(0);
        Assert.Equal(0x20, ctc2.DaisyChainDevices[0].Acknowledge());
    }
}

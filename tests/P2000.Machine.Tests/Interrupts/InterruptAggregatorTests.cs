using P2000.Machine.Contention;
using P2000.Machine.Interrupts;
using P2000.Machine.Tests.State;
using Z80.Core;

namespace P2000.Machine.Tests.Interrupts;

public class InterruptAggregatorTests
{
    // ---- Unit: aggregator alone ---------------------------------------------------

    [Fact]
    public void InitialState_IntNotPending()
    {
        var agg = new InterruptAggregator();
        Assert.False(agg.IntPending);
    }

    [Fact]
    public void RaiseInt_SetsPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        Assert.True(agg.IntPending);
    }

    [Fact]
    public void Acknowledge_ClearsPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        agg.Acknowledge();
        Assert.False(agg.IntPending);
    }

    [Fact]
    public void Acknowledge_ReturnsPullUpByte_ForIm1()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        Assert.Equal(0xFF, agg.Acknowledge());
    }

    [Fact]
    public void RaiseInt_IsIdempotent_MultipleRaisesStayPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        agg.RaiseInt();
        Assert.True(agg.IntPending);
        agg.Acknowledge();
        Assert.False(agg.IntPending);
    }

    [Fact]
    public void Reset_ClearsPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        agg.Reset();
        Assert.False(agg.IntPending);
    }

    [Fact]
    public void SaveLoad_RoundTrip_PendingTrue()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();

        var state = new InMemoryState();
        agg.SaveState(state);

        var agg2 = new InterruptAggregator();
        agg2.LoadState(state.BeginRead());

        Assert.True(agg2.IntPending);
    }

    [Fact]
    public void SaveLoad_RoundTrip_PendingFalse()
    {
        var agg = new InterruptAggregator();

        var state = new InMemoryState();
        agg.SaveState(state);

        var agg2 = new InterruptAggregator();
        agg2.RaiseInt(); // pre-pollute
        agg2.LoadState(state.BeginRead());

        Assert.False(agg2.IntPending);
    }

    // ---- NMI: unit tests ----------------------------------------------------------

    [Fact]
    public void InitialState_NmiNotPending()
    {
        var agg = new InterruptAggregator();
        Assert.False(agg.NmiPending);
    }

    [Fact]
    public void RaiseNmi_SetsPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseNmi();
        Assert.True(agg.NmiPending);
    }

    [Fact]
    public void ClearNmi_ClearsLatch()
    {
        var agg = new InterruptAggregator();
        agg.RaiseNmi();
        agg.ClearNmi();
        Assert.False(agg.NmiPending);
    }

    [Fact]
    public void Reset_ClearsNmiPending()
    {
        var agg = new InterruptAggregator();
        agg.RaiseNmi();
        agg.Reset();
        Assert.False(agg.NmiPending);
    }

    [Fact]
    public void SaveLoad_RoundTrip_NmiPendingTrue()
    {
        var agg = new InterruptAggregator();
        agg.RaiseNmi();

        var state = new InMemoryState();
        agg.SaveState(state);

        var agg2 = new InterruptAggregator();
        agg2.LoadState(state.BeginRead());

        Assert.True(agg2.NmiPending);
    }

    [Fact]
    public void SaveLoad_RoundTrip_NmiPendingFalse()
    {
        var agg = new InterruptAggregator();

        var state = new InMemoryState();
        agg.SaveState(state);

        var agg2 = new InterruptAggregator();
        agg2.RaiseNmi(); // pre-pollute
        agg2.LoadState(state.BeginRead());

        Assert.False(agg2.NmiPending);
    }

    [Fact]
    public void SaveLoad_BothIntAndNmi_RoundTrip()
    {
        var agg = new InterruptAggregator();
        agg.RaiseInt();
        agg.RaiseNmi();

        var state = new InMemoryState();
        agg.SaveState(state);

        var agg2 = new InterruptAggregator();
        agg2.LoadState(state.BeginRead());

        Assert.True(agg2.IntPending);
        Assert.True(agg2.NmiPending);
    }

    // ---- NMI: machine-level wiring ------------------------------------------------

    /// <summary>
    /// RaiseNmi causes the NMI pin to be asserted for exactly one tick (edge-triggered).
    /// After that one tick, ClearNmi has been called and NmiPending is false again.
    /// </summary>
    [Fact]
    public void RaiseNmi_ConsumedAfterOneTick()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x00 }); // NOP loop

        machine.Interrupts.RaiseNmi();
        Assert.True(machine.Interrupts.NmiPending);

        machine.Tick(); // drives NMI=1 then calls ClearNmi inside the tick

        Assert.False(machine.Interrupts.NmiPending);
    }

    /// <summary>
    /// With the CPU halted, a raised NMI must vector it to 0x0066 (the fixed NMI vector).
    /// The NMI handler is a tight JR -2 spin so we can confirm the CPU is spinning there
    /// without needing a valid stack: the push goes to 0xFFFF/0xFFFE (banked window, no banks
    /// in T38 → writes discarded) but the jump to 0x0066 still fires and the spin runs.
    /// </summary>
    [Fact]
    public void RaiseNmi_VectorsToNmiHandler_At0x0066()
    {
        var machine = new Machine();

        // ROM layout:
        //   0x0000: FB        EI
        //   0x0001: 76        HALT      ← CPU parks here
        //   0x0066: 18 FE     JR -2     ← NMI handler: infinite spin at 0x0066
        var rom = new byte[0x1000];
        rom[0x0000] = 0xFB; // EI
        rom[0x0001] = 0x76; // HALT
        rom[0x0066] = 0x18; // JR e
        rom[0x0067] = 0xFE; // e = -2  → loops back to 0x0066 forever
        machine.Memory.LoadRom(rom);

        // Run past EI+HALT (≈ 8-12 T-states), then raise NMI and tick enough for the
        // NMI sequence (11 T-states) plus a JR iteration (12 T-states) — well within 30.
        for (var i = 0; i < 20; i++) machine.Tick();

        machine.Interrupts.RaiseNmi();

        for (var i = 0; i < 30; i++) machine.Tick();

        // CPU must be inside the NMI handler: spinning at 0x0066 or mid-JR (fetched 0x0067/0x0068).
        Assert.InRange(machine.Cpu.Reg.PC, (ushort)0x0066, (ushort)0x0068);
    }

    // ---- Integration: machine-level wiring ----------------------------------------

    /// <summary>
    /// After exactly one field (50,000 T-states) the video device fires FieldComplete, which
    /// raises INT via the aggregator. The machine should assert the INT pin so the CPU can
    /// sample it at the next instruction boundary.
    /// </summary>
    [Fact]
    public void AfterOneField_IntIsPending()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x76 }); // HALT immediately (IFF1=0 → no accept)

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            machine.Tick();
        }

        Assert.True(machine.Interrupts.IntPending);
    }

    /// <summary>
    /// With interrupts enabled and the CPU halted, the 50 Hz VBLANK INT must vector the CPU
    /// to 0x0038 (IM1 fixed vector, project CLAUDE.md §8). The ISR at 0x0038 does EI+RETI so
    /// the test can verify the return address was pushed correctly.
    /// </summary>
    [Fact]
    public void VideoFieldComplete_VectorsToRst38_InIm1()
    {
        var machine = new Machine();

        // ROM layout:
        //   0x0000: FB        EI
        //   0x0001: 76        HALT      ← CPU parks here with IFF1=1; return address = 0x0002
        //   0x0002: 76        HALT      ← (never reached — just padding)
        //   ...
        //   0x0038: FB        EI        ← ISR: re-enable and return
        //   0x0039: ED 4D     RETI
        var rom = new byte[0x1000]; // 4 KB ROM
        rom[0x0000] = 0xFB; // EI
        rom[0x0001] = 0x76; // HALT
        rom[0x0038] = 0xFB; // EI  (ISR body — re-enable for next field)
        rom[0x0039] = 0xED; // RETI
        rom[0x003A] = 0x4D;
        machine.Memory.LoadRom(rom);

        // Run EI + HALT (a few T-states), then one full field to trigger INT, then enough
        // T-states for the int-ack (13 T-states) and ISR entry (EI = 4T).
        var limit = VideoFetchUnit.TStatesPerField + 100;
        for (var i = 0; i < limit; i++)
        {
            machine.Tick();
        }

        // CPU must be executing inside the ISR at 0x0038 (EI done, now at or past RETI).
        Assert.InRange(machine.Cpu.Reg.PC, (ushort)0x0038, (ushort)0x0050);

        // INT must have been acknowledged (no longer pending).
        Assert.False(machine.Interrupts.IntPending);
    }

    /// <summary>
    /// With interrupts disabled (DI), the INT line stays asserted across the field boundary
    /// until the software re-enables interrupts and the CPU can take the request.
    /// </summary>
    [Fact]
    public void WithInterruptsDisabled_IntRemainsAssertedAcrossField()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0xF3, 0x76 }); // DI; HALT  (IFF1=0)

        for (var i = 0; i < VideoFetchUnit.TStatesPerField + 100; i++)
        {
            machine.Tick();
        }

        // INT is pending but CPU has not taken it (IFF1 is 0).
        Assert.True(machine.Interrupts.IntPending);
        Assert.False(machine.Cpu.Reg.IFF1);
    }

    /// <summary>
    /// Reset clears a pending INT so the machine starts clean (project CLAUDE.md §2.3).
    /// </summary>
    [Fact]
    public void Reset_ClearsAnyPendingInt()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x76 }); // HALT (IFF1=0)

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            machine.Tick();
        }

        Assert.True(machine.Interrupts.IntPending);

        machine.Reset();

        Assert.False(machine.Interrupts.IntPending);
    }
}

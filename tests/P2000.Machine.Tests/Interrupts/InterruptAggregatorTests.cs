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

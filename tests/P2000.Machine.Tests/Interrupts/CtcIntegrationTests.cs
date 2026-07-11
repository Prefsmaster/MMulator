using P2000.Machine.Contention;

namespace P2000.Machine.Tests.Interrupts;

/// <summary>
/// Machine-level integration tests for the CTC + IM2 daisy chain + Lock interlock (project
/// CLAUDE.md §13 milestone 17, tests (c)/(e)/(f)): the whole pipeline — port dispatch, ch3
/// CLK/TRG fed by the video field pulse, real Z80 IM2 vectoring through the core, and the RETI
/// snoop — driven with actual Z80 opcodes, exactly like the existing IM1 VBLANK tests.
/// </summary>
public class CtcIntegrationTests
{
    private static Machine BuildFloppyRamMachine() =>
        new(new MachineConfig { Board = InternalBoard.FloppyRam });

    // ---- (c) Full IM2 vectoring pipeline ---------------------------------------------------

    /// <summary>
    /// Reproduces the ROM's confirmed CTC-presence probe (reference doc §5e): set I=0x60,
    /// IM 2, write the vector base (0x20) to ch0, program ch3 as a fast timer (control 0x85,
    /// TC 1 -> fires within 16 T-states), EI, HALT. The IM2 vector-table entry at 0x6026
    /// (I&lt;&lt;8 | ch3 low byte 0x26) points at a handler that increments A then RETIs — a
    /// present CTC must divert execution there.
    /// </summary>
    [Fact]
    public void CtcPresenceProbe_VectorsThroughIm2ToHandler()
    {
        var machine = BuildFloppyRamMachine();

        var rom = new byte[0x1000];
        var pc = 0;
        void Emit(params byte[] bytes) { foreach (var b in bytes) rom[pc++] = b; }

        // T38's default reset SP is 0x0000 (project CLAUDE.md §17 finding, milestone 12) —
        // pushing there lands in the unbanked E000-FFFF window and is discarded, corrupting
        // RETI's pop. Set a real stack in base RAM first.
        Emit(0x31, 0xFE, 0x9F); // LD SP,0x9FFE
        Emit(0x3E, 0x60);       // LD A,0x60
        Emit(0xED, 0x47);       // LD I,A
        Emit(0xED, 0x5E);       // IM 2
        Emit(0x3E, 0x20);       // LD A,0x20        ; vector base
        Emit(0xD3, 0x88);       // OUT (0x88),A     ; ch0 port -> sets CTC vector base
        Emit(0x3E, 0x85);       // LD A,0x85        ; ch3 control: timer, prescaler16, TCNEXT, INTEN
        Emit(0xD3, 0x8B);       // OUT (0x8B),A
        Emit(0x3E, 0x01);       // LD A,0x01        ; TC = 1
        Emit(0xD3, 0x8B);       // OUT (0x8B),A
        Emit(0xFB);             // EI
        Emit(0x76);             // HALT

        // ISR at 0x0100: INC A; RETI.
        rom[0x0100] = 0x3C; // INC A
        rom[0x0101] = 0xED;
        rom[0x0102] = 0x4D; // RETI

        machine.Memory.LoadRom(rom);
        // IM2 vector-table slot for ch3: (I<<8)|0x26 = 0x6026 -> pointer to the ISR at 0x0100.
        machine.Memory.Write(0x6026, 0x00);
        machine.Memory.Write(0x6027, 0x01);

        for (var i = 0; i < 200; i++) machine.Tick();

        // A was last loaded with 0x01 before HALT; the ISR's INC A makes it 0x02 only if the
        // CPU actually vectored through the CTC's IM2 table entry.
        Assert.Equal(0x02, machine.Cpu.Reg.A);

        // The channel must be back out of service after the ISR's RETI (snoop worked).
        Assert.False(machine.Ctc!.DaisyChainDevices[3].InService);
    }

    /// <summary>RETI's in-service clear must let a second interrupt vector correctly — proves
    /// the RETI snoop actually re-enables the channel rather than leaving it stuck.</summary>
    [Fact]
    public void CtcPresenceProbe_SecondInterrupt_VectorsAgainAfterReti()
    {
        var machine = BuildFloppyRamMachine();

        var rom = new byte[0x1000];
        var pc = 0;
        void Emit(params byte[] bytes) { foreach (var b in bytes) rom[pc++] = b; }

        Emit(0x31, 0xFE, 0x9F); // LD SP,0x9FFE (see the first test's comment)
        Emit(0x3E, 0x60);       // LD A,0x60
        Emit(0xED, 0x47);       // LD I,A
        Emit(0xED, 0x5E);       // IM 2
        Emit(0x3E, 0x20);       // LD A,0x20
        Emit(0xD3, 0x88);       // OUT (0x88),A     ; vector base
        Emit(0x3E, 0xD5);       // LD A,0xD5        ; ch3 counter mode, INTEN, TCNEXT, rising trig
        Emit(0xD3, 0x8B);       // OUT (0x8B),A
        Emit(0x3E, 0x01);       // LD A,0x01        ; TC = 1
        Emit(0xD3, 0x8B);       // OUT (0x8B),A
        Emit(0xFB);             // EI
        Emit(0x18, 0xFE);       // JR -2 (tight spin — NOT HALT: this core's HALT does not
                                 // auto-resume halting after an interrupt return, so RETI would
                                 // fall through into whatever follows and wander off; a spin
                                 // loop's RETI naturally resumes the same loop, matching the
                                 // proven pattern in RaiseNmi_VectorsToNmiHandler_At0x0066)

        // INT acceptance clears BOTH IFF1 and IFF2 (Z80.Core CLAUDE.md §5 int-ack T1:
        // "IFF1/IFF2<-0"), so RETI's IFF1:=IFF2 alone would leave interrupts disabled — the
        // ISR needs an explicit EI first, exactly like the standard "EI; RETI" Z80 idiom
        // (reference doc §5e: enable_interrupts = EI + RETI).
        rom[0x0100] = 0x3C; // INC A
        rom[0x0101] = 0xFB; // EI
        rom[0x0102] = 0xED;
        rom[0x0103] = 0x4D; // RETI

        machine.Memory.LoadRom(rom);
        machine.Memory.Write(0x6026, 0x00);
        machine.Memory.Write(0x6027, 0x01);

        // Run past setup + one field boundary (fires ch3 via the video CLK/TRG wiring) + ISR.
        for (var i = 0; i < VideoFetchUnit.TStatesPerField + 200; i++) machine.Tick();
        Assert.Equal(0x02, machine.Cpu.Reg.A);

        // A second field boundary must vector again (channel unblocked by the ISR's RETI).
        for (var i = 0; i < VideoFetchUnit.TStatesPerField + 200; i++) machine.Tick();
        Assert.Equal(0x03, machine.Cpu.Reg.A);
    }

    // ---- (e) Lock interlock ----------------------------------------------------------------

    [Fact]
    public void FloppyRamBoard_AssertsLock_AndHasCtc()
    {
        var machine = BuildFloppyRamMachine();
        Assert.True(machine.Interrupts.LockAsserted);
        Assert.NotNull(machine.Ctc);
    }

    [Fact]
    public void Lock_SuppressesVideoInt_WhileBoardFitted()
    {
        var machine = BuildFloppyRamMachine();
        machine.Memory.LoadRom(new byte[] { 0x76 }); // HALT — IFF1=0, never accepts anyway

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) machine.Tick();

        // Video still raises its request internally, but Lock gates it out of IntPending —
        // with nothing programmed on the CTC, no other source is pending either.
        Assert.False(machine.Interrupts.IntPending);
    }

    [Fact]
    public void BareT_VideoIntStillReachesAggregator_Unaffected()
    {
        var machine = new Machine(); // Board = None (default)
        machine.Memory.LoadRom(new byte[] { 0x76 });

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) machine.Tick();

        Assert.True(machine.Interrupts.IntPending); // unchanged regression baseline
    }

    // ---- (f) Fallback / absent-CTC regression ----------------------------------------------

    [Fact]
    public void BareMachine_HasNoCtc_PortsOpenBus_LockNotAsserted()
    {
        var machine = new Machine(new MachineConfig { Board = InternalBoard.None });

        Assert.Null(machine.Ctc);
        Assert.False(machine.Interrupts.LockAsserted);
        Assert.Equal(0xFF, machine.Ports.Read(0x88));
        Assert.Equal(0xFF, machine.Ports.Read(0x8B));
    }

    [Fact]
    public void RamOnlyBoard_HasNoCtc_LockNotAsserted()
    {
        var machine = new Machine(new MachineConfig { Board = InternalBoard.RamOnly });

        Assert.Null(machine.Ctc);
        Assert.False(machine.Interrupts.LockAsserted);
    }
}

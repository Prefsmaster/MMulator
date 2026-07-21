using P2000.Machine.Devices.Cassette;

namespace P2000.Machine.Tests.Interrupts;

/// <summary>
/// Machine-level integration tests for the FDC (project CLAUDE.md §13 milestone 19): the FDC
/// has no direct CPU INT line (reference doc §5d) — its result-phase completion must reach the
/// CPU through CTC ch0 and the SAME IM2 daisy chain M17 built, exactly like the video field
/// pulse reaches ch3. Also the config-validation gate and the "genuine silence" regression for
/// machines without the floppy+RAM board.
/// </summary>
public class FdcIntegrationTests
{
    private static Machine BuildFloppyRamMachine() =>
        new(new MachineConfig { Board = InternalBoard.FloppyRam, RamVariant = RamVariant.T102 });

    [Fact]
    public void FloppyRamBoard_HasFdc_MsrIdleAtExactly0x80()
    {
        var machine = BuildFloppyRamMachine();
        Assert.NotNull(machine.Fdc);
        Assert.Equal(0x80, machine.Ports.Read(0x8C));
    }

    [Fact]
    public void FloppyRamBoard_WithNonT102Ram_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Machine(new MachineConfig { Board = InternalBoard.FloppyRam, RamVariant = RamVariant.T38 }));
    }

    /// <summary>
    /// FDC RECALIBRATE completion → CTC ch0 (control 0xD5, TC 1 — reference doc §5d confirmed
    /// disk-channel control words) → IM2 vector at 0x6020 (I=0x60, ch0 low byte 0x20) — the
    /// exact pipeline M17's daisy chain was built to carry, now proven with a real second
    /// source registered on it.
    /// </summary>
    [Fact]
    public void FdcResultReady_VectorsThroughCtcCh0ToIm2Handler()
    {
        var machine = BuildFloppyRamMachine();
        machine.Fdc!.Policy = TimingPolicy.Turbo; // completes RECALIBRATE synchronously

        var rom = new byte[0x1000];
        var pc = 0;
        void Emit(params byte[] bytes) { foreach (var b in bytes) rom[pc++] = b; }

        Emit(0x31, 0xFE, 0x9F); // LD SP,0x9FFE (T38-style unbanked-stack guard, same as M17 tests)
        Emit(0x3E, 0x60);       // LD A,0x60
        Emit(0xED, 0x47);       // LD I,A
        Emit(0xED, 0x5E);       // IM 2
        Emit(0x3E, 0x20);       // LD A,0x20
        Emit(0xD3, 0x88);       // OUT (0x88),A     ; CTC vector base
        Emit(0x3E, 0xD5);       // LD A,0xD5        ; ch0: counter mode, INTEN, rising edge, TCNEXT
        Emit(0xD3, 0x88);       // OUT (0x88),A
        Emit(0x3E, 0x01);       // LD A,0x01        ; TC = 1
        Emit(0xD3, 0x88);       // OUT (0x88),A
        Emit(0x3E, 0x07);       // LD A,0x07        ; RECALIBRATE opcode
        Emit(0xD3, 0x8D);       // OUT (0x8D),A
        Emit(0x3E, 0x01);       // LD A,0x01        ; unit
        Emit(0xD3, 0x8D);       // OUT (0x8D),A     ; Turbo: FDC.ResultReady fires here, synchronously
        Emit(0xFB);             // EI
        Emit(0x76);             // HALT

        rom[0x0100] = 0x3C; // INC A
        rom[0x0101] = 0xED;
        rom[0x0102] = 0x4D; // RETI

        machine.Memory.LoadRom(rom);
        machine.Memory.Write(0x6020, 0x00);
        machine.Memory.Write(0x6021, 0x01);

        for (var i = 0; i < 200; i++) machine.Tick();

        // A was last loaded 0x01 (the unit byte) before HALT; the ISR's INC A makes it 0x02
        // only if the FDC's completion actually vectored through ch0's IM2 table entry.
        Assert.Equal(0x02, machine.Cpu.Reg.A);
        Assert.False(machine.Ctc!.DaisyChainDevices[0].InService);
    }

    // ---- Genuine silence: absent board / no FDC ------------------------------------------------

    [Fact]
    public void BareMachine_HasNoFdc_PortsOpenBus()
    {
        var machine = new Machine();
        Assert.Null(machine.Fdc);
        Assert.Equal(0xFF, machine.Ports.Read(0x8C));
        Assert.Equal(0xFF, machine.Ports.Read(0x8D));
        Assert.Equal(0xFF, machine.Ports.Read(0x90));
    }

    [Fact]
    public void RamOnlyBoard_HasNoFdc()
    {
        var machine = new Machine(new MachineConfig { Board = InternalBoard.RamOnly });
        Assert.Null(machine.Fdc);
    }
}

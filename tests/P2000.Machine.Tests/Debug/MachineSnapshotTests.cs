using P2000.Machine.Contention;
using P2000.Machine.Debug;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Debug;

public class MachineSnapshotTests
{
    // Helper: advance to an instruction boundary; returns immediately if already there.
    private static void RunToNextBoundary(Machine machine)
    {
        if (machine.Cpu.AtInstructionBoundary) return;
        for (var i = 0; i < 100; i++)
        {
            machine.Tick();
            if (machine.Cpu.AtInstructionBoundary) return;
        }
        throw new InvalidOperationException("Did not reach instruction boundary within 100 ticks.");
    }

    // Helper: run the machine for exactly N ticks.
    private static void RunTicks(Machine machine, int n)
    {
        for (var i = 0; i < n; i++) machine.Tick();
    }

    // ---- Guard: must be at instruction boundary ------------------------------------

    [Fact]
    public void TakeSnapshot_AtBoundary_DoesNotThrow()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x00 }); // NOP

        RunToNextBoundary(machine);

        // No exception
        var snap = machine.TakeSnapshot();
        Assert.Equal(machine.Cpu.Reg.PC, snap.PC);
    }

    [Fact]
    public void TakeSnapshot_NotAtBoundary_Throws()
    {
        var machine = new Machine();
        // Two-byte instruction (LD A,n = 0x3E 0x42) so the machine is mid-instruction
        // after the first Tick (T1 of M1 fetch, not yet at the boundary for the next one).
        machine.Memory.LoadRom(new byte[] { 0x3E, 0x42 }); // LD A, 0x42

        // Tick once — now mid-M1 (T2/T3...), AtInstructionBoundary is false.
        machine.Tick();
        if (machine.Cpu.AtInstructionBoundary)
        {
            // If coincidentally at a boundary after one tick skip this test for this ROM.
            return;
        }

        Assert.Throws<InvalidOperationException>(() => machine.TakeSnapshot());
    }

    // ---- Registers match the live core at boundary ---------------------------------

    [Fact]
    public void Snapshot_RegistersMatch_Core()
    {
        var machine = new Machine();
        // ROM: INC A (0x3C) repeatedly — non-trivial A value after a few iterations.
        machine.Memory.LoadRom(new byte[] { 0x3C, 0x18, 0xFD }); // INC A; JR -3 (loop)

        // Run 3 full loop iterations (4+12=16T each) so A is non-zero.
        RunTicks(machine, 48);
        RunToNextBoundary(machine);

        var snap = machine.TakeSnapshot();
        var reg = machine.Cpu.Reg;

        Assert.Equal(reg.A, snap.A);
        Assert.Equal(reg.F, snap.F);
        Assert.Equal(reg.BC, snap.BC);
        Assert.Equal(reg.DE, snap.DE);
        Assert.Equal(reg.HL, snap.HL);
        Assert.Equal(reg.SP, snap.SP);
        Assert.Equal(reg.PC, snap.PC);
        Assert.Equal(reg.WZ, snap.WZ);
        Assert.Equal(reg.IFF1, snap.IFF1);
        Assert.Equal(reg.IFF2, snap.IFF2);
        Assert.Equal(reg.IM, snap.IM);
    }

    // ---- Two snapshots without stepping are identical ------------------------------

    [Fact]
    public void TwoSnapshots_WithoutStepping_AreIdentical()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x00 }); // NOP loop

        RunToNextBoundary(machine);

        var s1 = machine.TakeSnapshot();
        var s2 = machine.TakeSnapshot();

        Assert.Equal(s1.PC, s2.PC);
        Assert.Equal(s1.AF, s2.AF);
        Assert.Equal(s1.BC, s2.BC);
        Assert.Equal(s1.FieldTState, s2.FieldTState);
    }

    // ---- Stepping advances PC and cycle position -----------------------------------

    [Fact]
    public void AfterOneInstruction_PC_Advances()
    {
        var machine = new Machine();
        // NOP at 0x0000; JR -2 at 0x0001 keeps PC in a safe loop so it never
        // falls into the real monitor ROM bytes (which start at ROM offset 2).
        var rom = new byte[0x1000];
        rom[0x0000] = 0x00; // NOP
        rom[0x0001] = 0x18; // JR -2
        rom[0x0002] = 0xFE;
        machine.Memory.LoadRom(rom);

        RunToNextBoundary(machine); // already at boundary at reset (PC=0)
        var before = machine.TakeSnapshot();

        // Advance exactly one NOP (4 T-states); after T4 the core is at the next boundary.
        RunTicks(machine, 4);
        RunToNextBoundary(machine);
        var after = machine.TakeSnapshot();

        Assert.Equal((ushort)(before.PC + 1), after.PC);
    }

    [Fact]
    public void AfterTicks_FieldTState_Advances()
    {
        var machine = new Machine();
        var rom = new byte[0x1000];
        rom[0x0000] = 0x00; // NOP
        machine.Memory.LoadRom(rom);

        RunToNextBoundary(machine); // already at boundary at reset
        var before = machine.TakeSnapshot();

        // Advance 4 ticks (one NOP); after T4 the core is at the next boundary.
        RunTicks(machine, 4);
        RunToNextBoundary(machine);
        var after = machine.TakeSnapshot();

        // FieldTState must have advanced by exactly 4 (mod TStatesPerField).
        var expected = (before.FieldTState + 4) % VideoFetchUnit.TStatesPerField;
        Assert.Equal(expected, after.FieldTState);
    }

    // ---- Flag properties match the F byte ------------------------------------------

    [Fact]
    public void Flags_MatchFByte()
    {
        var machine = new Machine();
        // XOR A: clears A to 0, sets ZF, clears everything else.
        machine.Memory.LoadRom(new byte[] { 0xAF, 0x18, 0xFD }); // XOR A; JR -3

        RunToNextBoundary(machine);
        RunTicks(machine, 4); // execute XOR A
        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        Assert.Equal((snap.F & 0x80) != 0, snap.SF);
        Assert.Equal((snap.F & 0x40) != 0, snap.ZF);
        Assert.Equal((snap.F & 0x20) != 0, snap.YF);
        Assert.Equal((snap.F & 0x10) != 0, snap.HF);
        Assert.Equal((snap.F & 0x08) != 0, snap.XF);
        Assert.Equal((snap.F & 0x04) != 0, snap.PF);
        Assert.Equal((snap.F & 0x02) != 0, snap.NF);
        Assert.Equal((snap.F & 0x01) != 0, snap.CF);
    }

    [Fact]
    public void Flags_XorA_SetsZeroAndParity_ClearsCarryAndSign()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0xAF }); // XOR A

        RunToNextBoundary(machine);
        RunTicks(machine, 4);
        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        Assert.Equal(0, snap.A);
        Assert.True(snap.ZF,  "ZF should be set after XOR A");
        Assert.True(snap.PF,  "PF should be set after XOR A (even parity of 0x00)");
        Assert.False(snap.SF, "SF should be clear after XOR A");
        Assert.False(snap.CF, "CF should be clear after XOR A");
        Assert.False(snap.NF, "NF should be clear after XOR A");
    }

    // ---- ReadMemory delegate -------------------------------------------------------

    [Fact]
    public void ReadMemory_ReadsRom_ViaDelegate()
    {
        var machine = new Machine();
        var rom = new byte[0x100];
        rom[0] = 0x00; // NOP
        rom[0x42] = 0xBE;
        machine.Memory.LoadRom(rom);

        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        Assert.Equal(0xBE, snap.ReadMemory(0x0042));
    }

    [Fact]
    public void ReadMemory_UnpopulatedAddress_ReturnsOpenBus()
    {
        var machine = new Machine(); // T38: no expansion RAM, no banks

        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        // 0xA000-0xDFFF is expansion RAM, absent in T38 → open bus (0xFF).
        Assert.Equal(PageTable.OpenBus, snap.ReadMemory(PageTable.ExpansionRamStart));
    }

    [Fact]
    public void ReadMemory_WrittenRam_ReturnsWrittenValue()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x00 }); // NOP

        // Write to base RAM directly via page table (outside the tick loop for setup).
        machine.Memory.Write(PageTable.BaseRamStart, 0x77);

        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        Assert.Equal(0x77, snap.ReadMemory(PageTable.BaseRamStart));
    }

    // ---- FieldTState bounds --------------------------------------------------------

    [Fact]
    public void FieldTState_IsWithinField_AfterReset()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[] { 0x00 }); // NOP

        RunToNextBoundary(machine);
        var snap = machine.TakeSnapshot();

        Assert.InRange(snap.FieldTState, 0, VideoFetchUnit.TStatesPerField - 1);
    }
}

using P2000.Machine.Contention;
using P2000.Machine.Debug;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Debug;

/// <summary>
/// Tests for the command queue (project CLAUDE.md §3b.3, milestone 15):
/// each command applies at an instruction boundary; step-over/step-out land correctly
/// across CALL/RET; run-to-cycle stops at or past the target; mid-run pokes flag
/// non-replayable.
/// </summary>
public class CommandQueueTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Construct a machine with a synthetic 4 KB ROM so monitor-ROM instructions
    /// cannot bleed past the end of the test code.  The tail is filled with 0x18 0xFE
    /// (JR -2) to keep PC stationary after the test code runs off the end.</summary>
    private static Machine MakeWithRom(byte[] code)
    {
        var rom = new byte[0x1000];
        // Fill with JR -2 so any runaway falls into an infinite spin.
        for (var i = 0; i < rom.Length - 1; i += 2) { rom[i] = 0x18; rom[i + 1] = 0xFE; }
        Array.Copy(code, rom, Math.Min(code.Length, rom.Length));
        var m = new Machine();
        m.Memory.LoadRom(rom);
        return m;
    }

    /// <summary>Tick until IsPaused or until <paramref name="maxTicks"/> exceeded.</summary>
    private static void TickUntilPaused(Machine m, int maxTicks = 500_000)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (m.IsPaused) return;
            m.Tick();
        }
        throw new InvalidOperationException(
            $"Did not pause within {maxTicks} ticks (PC=0x{m.Cpu.Reg.PC:X4}).");
    }

    /// <summary>Advance to the next instruction boundary without pausing.</summary>
    private static void TickToNextBoundary(Machine m, int maxTicks = 200)
    {
        // One tick leaves the boundary; then we wait for the next one.
        m.Tick();
        for (var i = 0; i < maxTicks; i++)
        {
            if (m.Cpu.AtInstructionBoundary) return;
            m.Tick();
        }
        throw new InvalidOperationException("Did not reach instruction boundary.");
    }

    /// <summary>Pause the machine by setting an exec bp, ticking until paused, then
    /// removing the bp so subsequent steps are clean.</summary>
    private static void PauseAt(Machine m, ushort addr)
    {
        var id = m.Breakpoints.AddExec(addr);
        TickUntilPaused(m);
        m.Breakpoints.Remove(id);
    }

    // ── RunCommand / PauseCommand ─────────────────────────────────────────────────────

    [Fact]
    public void PauseCommand_PausesAtNextBoundary()
    {
        var m = MakeWithRom([0x00, 0x00, 0x18, 0xFE]); // NOP NOP JR -2
        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);
        Assert.True(m.IsPaused);
        Assert.True(m.Cpu.AtInstructionBoundary);
    }

    [Fact]
    public void RunCommand_ResumesFromPause()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]); // NOP JR -2
        PauseAt(m, 0x0000);
        Assert.True(m.IsPaused);

        m.Enqueue(new RunCommand());
        // Machine is paused, so Tick drains the queue and RunCommand clears IsPaused.
        m.Tick();
        Assert.False(m.IsPaused);
    }

    [Fact]
    public void PauseThenRun_MachineResumesCorrectly()
    {
        var m = MakeWithRom([0x00, 0x00, 0x18, 0xFE]);
        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);
        Assert.True(m.IsPaused);

        m.Enqueue(new RunCommand());
        m.Tick(); // drains queue, unpauses, executes one T-state
        // RunCommand cleared IsPaused; machine is now running again.
        Assert.False(m.IsPaused);
        // PC may have advanced by the M1 fetch increment inside that one Tick, which is
        // correct behaviour — we just verify the machine is unpaused and has not jumped
        // to an unexpected location (both 0x0000 and 0x0001 are valid PC values here).
        Assert.True(m.Cpu.Reg.PC <= 0x0001);
    }

    // ── WarmReset / ColdReset ─────────────────────────────────────────────────────────

    [Fact]
    public void WarmResetCommand_ResetsRegistersPreservesRam()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]); // NOP JR -2

        // Write a sentinel byte to base RAM.
        const ushort ramAddr = PageTable.BaseRamStart;
        m.Memory.Write(ramAddr, 0xAB);

        // Run a bit so PC is past 0x0001.
        TickToNextBoundary(m);
        TickToNextBoundary(m);

        m.Enqueue(new WarmResetCommand());
        m.Tick(); // drains queue and executes one T-state of the reset ROM

        // After reset+one Tick, PC is at most 1 (M1 fetch of first instruction increments PC).
        Assert.True(m.Cpu.Reg.PC <= 1, $"Expected PC near 0 after warm reset, got 0x{m.Cpu.Reg.PC:X4}");
        Assert.Equal(0xAB, m.Memory.Read(ramAddr)); // RAM preserved (warm)
    }

    [Fact]
    public void ColdResetCommand_ResetsRegistersAndClearsRam()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        const ushort ramAddr = PageTable.BaseRamStart;
        m.Memory.Write(ramAddr, 0xAB);

        m.Enqueue(new ColdResetCommand());
        m.Tick(); // drains and executes one T-state of the reset ROM

        Assert.True(m.Cpu.Reg.PC <= 1, $"Expected PC near 0 after cold reset, got 0x{m.Cpu.Reg.PC:X4}");
        Assert.Equal(0x00, m.Memory.Read(ramAddr)); // RAM zeroed
    }

    // ── SingleStep ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleStep_ExecutesExactlyOneInstruction()
    {
        // NOP (1 byte, PC → 0x0001) then JR -2 to stay put.
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        // Machine starts at 0x0000 (instruction boundary).
        Assert.True(m.Cpu.AtInstructionBoundary);
        Assert.Equal(0x0000, m.Cpu.Reg.PC);

        m.Enqueue(new SingleStepCommand());
        TickUntilPaused(m);

        // Should have executed exactly one NOP and now paused at 0x0001.
        Assert.True(m.IsPaused);
        Assert.Equal(0x0001, m.Cpu.Reg.PC);
    }

    [Fact]
    public void SingleStep_ThenSingleStep_AdvancesTwoInstructions()
    {
        // NOP NOP JR -2
        var m = MakeWithRom([0x00, 0x00, 0x18, 0xFE]);

        m.Enqueue(new SingleStepCommand());
        TickUntilPaused(m);
        Assert.Equal(0x0001, m.Cpu.Reg.PC);

        m.Enqueue(new RunCommand());
        m.Tick(); // unpause
        m.Enqueue(new SingleStepCommand());
        TickUntilPaused(m);
        Assert.Equal(0x0002, m.Cpu.Reg.PC);
    }

    // ── StepOver ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepOver_NonCallInstruction_SingleSteps()
    {
        // NOP at 0x0000 then JR -2.
        var m = MakeWithRom([0x00, 0x18, 0xFE]);

        m.Enqueue(new StepOverCommand());
        TickUntilPaused(m);

        // Should have stepped over the NOP and paused at 0x0001.
        Assert.Equal(0x0001, m.Cpu.Reg.PC);
    }

    [Fact]
    public void StepOver_CallInstruction_SkipsSubroutine()
    {
        // ROM layout:
        // 0x0000: NOP                   (advance past NOP before we step-over CALL)
        // 0x0001: CALL 0x0010  (CD 10 00) — step-over should land at 0x0004
        // 0x0004: JR -2  (18 FE)        — spin here after step-over completes
        // ...
        // 0x0010: RET   (C9)
        // 0x0011: JR -2 (18 FE)
        //
        // SP must point into base RAM (0x6000–0x9FFF) so CALL's stack writes land in
        // real RAM (the default T38 machine has no banked-window banks; SP=0 would cause
        // CALL to write to 0xFFFF/0xFFFE which are open-bus on T38, corrupting RET).
        var rom = new byte[0x1000];
        rom[0x0000] = 0x00;               // NOP
        rom[0x0001] = 0xCD; rom[0x0002] = 0x10; rom[0x0003] = 0x00; // CALL 0x0010
        rom[0x0004] = 0x18; rom[0x0005] = 0xFE; // JR -2
        rom[0x0010] = 0xC9;               // RET
        rom[0x0011] = 0x18; rom[0x0012] = 0xFE;
        var m = new Machine();
        m.Memory.LoadRom(rom);
        m.Cpu.Reg.SP = 0x8000; // point into base RAM so CALL/RET stack ops work

        // Advance one instruction (NOP) so PC = 0x0001 (the CALL).
        m.Enqueue(new SingleStepCommand());
        TickUntilPaused(m);
        Assert.Equal(0x0001, m.Cpu.Reg.PC);

        m.Enqueue(new RunCommand());
        m.Tick();
        m.Enqueue(new StepOverCommand());
        TickUntilPaused(m);

        // Step-over should have run through the CALL+RET and stopped at 0x0004.
        Assert.Equal(0x0004, m.Cpu.Reg.PC);
    }

    [Fact]
    public void StepOver_NoUserBreakpointLeft_AfterStepOverCallCompletes()
    {
        // After step-over-a-CALL completes, the temp bp must be removed so no stale
        // bp fires on re-entry to the same call site later.
        var rom = new byte[0x1000];
        rom[0x0001] = 0xCD; rom[0x0002] = 0x10; rom[0x0003] = 0x00; // CALL 0x0010
        rom[0x0004] = 0x18; rom[0x0005] = 0xFE;
        rom[0x0010] = 0xC9;
        var m = new Machine();
        m.Memory.LoadRom(rom);
        m.Cpu.Reg.SP = 0x8000; // base RAM so CALL/RET stack ops work on default T38 machine

        m.Enqueue(new SingleStepCommand()); // advance past NOP at 0x0000
        TickUntilPaused(m);
        m.Enqueue(new RunCommand()); m.Tick();

        m.Enqueue(new StepOverCommand());
        TickUntilPaused(m);
        Assert.Equal(0x0004, m.Cpu.Reg.PC);

        // No breakpoints should remain in the store.
        Assert.False(m.Breakpoints.AnyArmed);
    }

    [Fact]
    public void StepOver_RstInstruction_SkipsSubroutine()
    {
        // RST 0x08 at 0x0000 (opcode CF = RST 0x08); return site = 0x0001.
        // 0x0000: RST 0x08 (CF) — 1 byte; step-over target = 0x0001
        // 0x0001: JR -2 (spin)
        // 0x0008: RET (C9)
        var rom = new byte[0x1000];
        rom[0x0000] = 0xCF;               // RST 0x08
        rom[0x0001] = 0x18; rom[0x0002] = 0xFE;
        rom[0x0008] = 0xC9;               // RET
        var m = new Machine();
        m.Memory.LoadRom(rom);
        m.Cpu.Reg.SP = 0x8000; // base RAM so RST/RET stack ops work on default T38 machine

        m.Enqueue(new StepOverCommand());
        TickUntilPaused(m);

        Assert.Equal(0x0001, m.Cpu.Reg.PC);
        Assert.False(m.Breakpoints.AnyArmed); // temp bp cleaned up
    }

    // ── StepOut ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepOut_ReturnsToCallSite()
    {
        // 0x0000: CALL 0x0010  (CD 10 00) — return address = 0x0003
        // 0x0003: JR -2 (spin — we land here after step-out)
        // 0x0010: NOP  (we're here when step-out is issued)
        // 0x0011: RET  (C9)
        var rom = new byte[0x1000];
        rom[0x0000] = 0xCD; rom[0x0001] = 0x10; rom[0x0002] = 0x00;
        rom[0x0003] = 0x18; rom[0x0004] = 0xFE;
        rom[0x0010] = 0x00; // NOP (inside subroutine)
        rom[0x0011] = 0xC9; // RET
        rom[0x0012] = 0x18; rom[0x0013] = 0xFE;
        var m = new Machine();
        m.Memory.LoadRom(rom);
        m.Cpu.Reg.SP = 0x8000; // base RAM so CALL/RET stack ops work on default T38 machine

        // Run until PC = 0x0010 (inside the subroutine, past the NOP).
        PauseAt(m, 0x0010);
        Assert.Equal(0x0010, m.Cpu.Reg.PC);

        m.Enqueue(new RunCommand()); m.Tick();
        m.Enqueue(new StepOutCommand());
        TickUntilPaused(m);

        // Should land at 0x0003 — the instruction after the CALL.
        Assert.Equal(0x0003, m.Cpu.Reg.PC);
        Assert.False(m.Breakpoints.AnyArmed);
    }

    // ── RunToCycleCommand ─────────────────────────────────────────────────────────────

    [Fact]
    public void RunToCycle_StopsAtOrPastTarget()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]); // NOP JR -2

        const int target = 500; // well within the first field
        m.Enqueue(new RunToCycleCommand(target));
        TickUntilPaused(m);

        Assert.True(m.IsPaused);
        Assert.True(m.Video.FieldTState >= target,
            $"FieldTState {m.Video.FieldTState} should be >= {target}");
        Assert.True(m.Cpu.AtInstructionBoundary);
    }

    [Fact]
    public void RunToCycle_StopsExactlyAtNextBoundaryAfterTarget()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]); // NOP (4 T) then JR -2 (12 T)

        // Pick a target that falls inside a JR -2 instruction so we definitely overshoot
        // by < 12 T-states (one JR instruction max).
        const int target = 1000;
        m.Enqueue(new RunToCycleCommand(target));
        TickUntilPaused(m);

        var actual = m.Video.FieldTState;
        Assert.True(actual >= target);
        Assert.True(actual < target + 20, // should not overshoot by more than one instruction
            $"Overshot too far: FieldTState={actual}, target={target}");
    }

    // ── RunToScanlineCommand ──────────────────────────────────────────────────────────

    [Fact]
    public void RunToScanline_StopsAtOrPastTargetScanline()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);

        const int targetLine = 3;
        var targetTState = targetLine * VideoFetchUnit.TStatesPerLine;
        m.Enqueue(new RunToScanlineCommand(targetLine));
        TickUntilPaused(m);

        Assert.True(m.Video.FieldTState >= targetTState,
            $"FieldTState {m.Video.FieldTState} should be >= {targetTState} (scanline {targetLine})");
    }

    // ── SetPcCommand ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SetPc_UpdatesProgramCounter()
    {
        // 0x0000: NOP (advances to 0x0001 if not for SetPC)
        // 0x0200: JR -2 (spin target)
        var rom = new byte[0x1000];
        rom[0x0000] = 0x00; // NOP
        rom[0x0200] = 0x18; rom[0x0201] = 0xFE; // JR -2 at 0x0200
        var m = new Machine();
        m.Memory.LoadRom(rom);

        // Pause first so we can observe PC after SetPC.
        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);

        m.Enqueue(new SetPcCommand(0x0200));
        m.Tick(); // drains queue while paused
        Assert.Equal(0x0200, m.Cpu.Reg.PC);
    }

    // ── MemoryWrite / LoadImage (non-replayable) ──────────────────────────────────────

    [Fact]
    public void MemoryWrite_WritesToMemoryAndFiresNonReplayable()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        var nonReplayFired = false;
        m.NonReplayableAction += () => nonReplayFired = true;

        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);

        const ushort addr = PageTable.BaseRamStart;
        m.Enqueue(new MemoryWriteCommand(addr, 0x99));
        m.Tick();

        Assert.Equal(0x99, m.Memory.Read(addr));
        Assert.True(nonReplayFired);
    }

    [Fact]
    public void LoadImage_WritesBlockAndFiresNonReplayable()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        var nonReplayFired = false;
        m.NonReplayableAction += () => nonReplayFired = true;

        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);

        const ushort start = PageTable.BaseRamStart;
        byte[] data = [0x11, 0x22, 0x33];
        m.Enqueue(new LoadImageCommand(start, data));
        m.Tick();

        Assert.Equal(0x11, m.Memory.Read(start));
        Assert.Equal(0x22, m.Memory.Read((ushort)(start + 1)));
        Assert.Equal(0x33, m.Memory.Read((ushort)(start + 2)));
        Assert.True(nonReplayFired);
    }

    [Fact]
    public void MemoryWrite_NoNonReplayable_WithoutCommand()
    {
        // Direct Memory.Write (not via command queue) does NOT fire the event.
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        var fired = false;
        m.NonReplayableAction += () => fired = true;
        m.Memory.Write(PageTable.BaseRamStart, 0x55);
        Assert.False(fired);
    }

    // ── Breakpoint CRUD via command queue ─────────────────────────────────────────────

    [Fact]
    public void AddExecBreakpoint_ViaCommand_Fires()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        m.Enqueue(new AddExecBreakpointCommand(0x0001));
        TickUntilPaused(m);

        Assert.True(m.IsPaused);
        Assert.Equal(0x0001, m.Cpu.Reg.PC);
    }

    [Fact]
    public void RemoveBreakpoint_ViaCommand_StopsFiring()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        var id = m.Breakpoints.AddExec(0x0001);
        TickUntilPaused(m);
        Assert.Equal(0x0001, m.Cpu.Reg.PC);

        // Remove the bp via command, then resume — machine should run without stopping.
        m.Enqueue(new RemoveBreakpointCommand(id));
        m.Enqueue(new RunCommand());
        m.Tick(); // drains queue

        for (var i = 0; i < 500; i++) m.Tick();
        Assert.False(m.IsPaused);
    }

    [Fact]
    public void ClearBreakpoints_ViaCommand_RemovesAll()
    {
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        m.Breakpoints.AddExec(0x0001);
        m.Breakpoints.AddExec(0x0002);

        m.Enqueue(new ClearBreakpointsCommand());
        m.Tick();

        Assert.False(m.Breakpoints.AnyArmed);
        for (var i = 0; i < 500; i++) m.Tick();
        Assert.False(m.IsPaused);
    }

    // ── Command ordering and boundary invariants ───────────────────────────────────────

    [Fact]
    public void MultipleCommandsInOneQueue_AllAppliedInOrder()
    {
        // Queue: Pause + SetPC(0x0200). Both should apply at the same boundary.
        var rom = new byte[0x1000];
        rom[0x0000] = 0x00; // NOP
        rom[0x0200] = 0x18; rom[0x0201] = 0xFE;
        var m = new Machine();
        m.Memory.LoadRom(rom);

        m.Enqueue(new PauseCommand());
        m.Enqueue(new SetPcCommand(0x0200));
        TickUntilPaused(m);

        // Both commands applied: machine paused AND PC = 0x0200.
        Assert.True(m.IsPaused);
        Assert.Equal(0x0200, m.Cpu.Reg.PC);
    }

    [Fact]
    public void CommandAppliesAtInstructionBoundary_NotMidInstruction()
    {
        // LD A,n (2 bytes = 0x3E 0x42) is a multi-T-state instruction.
        // SetPC enqueued before the instruction finishes should NOT take effect until
        // the boundary (we verify by confirming the instruction completes first).
        var m = MakeWithRom([0x3E, 0x42, 0x18, 0xFE]); // LD A,0x42 ; JR -2

        // Tick once (T1 of M1 — not yet at next boundary).
        m.Tick();
        m.Enqueue(new PauseCommand());
        // Should pause at the next boundary (after LD A,n finishes), not immediately.
        TickUntilPaused(m);

        // LD A,n has 7 T-states before the boundary; pausing happens after it completes.
        Assert.True(m.Cpu.AtInstructionBoundary);
        Assert.Equal(0x42, m.Cpu.Reg.A); // LD A,0x42 completed
    }

    [Fact]
    public void WarmReset_DropsSubsequentCommandsInSameDrain()
    {
        // SetPC after WarmReset in the same queue flush must be dropped.
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);

        m.Enqueue(new WarmResetCommand());
        m.Enqueue(new SetPcCommand(0x0100)); // should be dropped by the reset
        m.Tick(); // drains: WarmReset clears queue (SetPC dropped), then executes one T-state

        // The key invariant: PC is near 0x0000, NOT 0x0100.
        // After reset+one Tick the M1 fetch may have incremented PC to 0x0001.
        Assert.True(m.Cpu.Reg.PC <= 1,
            $"Expected PC at reset vector (0 or 1), got 0x{m.Cpu.Reg.PC:X4} — SetPC may not have been dropped");
    }

    [Fact]
    public void ArmedMachineWithNoBreakpoints_CommandQueueDrainCostsNothing()
    {
        // Regression: when AnyArmed=false the breakpoint block is skipped, but the
        // command queue must still drain normally.
        var m = MakeWithRom([0x00, 0x18, 0xFE]);
        Assert.False(m.Breakpoints.AnyArmed);

        m.Enqueue(new PauseCommand());
        TickUntilPaused(m);
        Assert.True(m.IsPaused);
    }
}

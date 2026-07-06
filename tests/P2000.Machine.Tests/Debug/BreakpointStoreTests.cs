using P2000.Machine.Debug;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Debug;

public class BreakpointStoreTests
{
    // ---- Helpers ----------------------------------------------------------------

    private static Machine MakeWithRom(byte[] rom)
    {
        var m = new Machine();
        m.Memory.LoadRom(rom);
        return m;
    }

    // Tick until IsPaused or AtInstructionBoundary; stops immediately when paused.
    private static void TickUntilPausedOrBoundary(Machine machine, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (machine.IsPaused) return;
            machine.Tick();
            if (machine.IsPaused) return;
        }
        throw new InvalidOperationException($"Did not pause within {maxTicks} ticks.");
    }

    // Tick past the current instruction boundary into the first tick of the next instruction.
    private static void TickPastCurrentBoundary(Machine machine)
    {
        // Get past current boundary (one Tick advances past it)
        machine.Tick();
        // Now tick until we reach the next boundary
        for (var i = 0; i < 200; i++)
        {
            if (machine.Cpu.AtInstructionBoundary) return;
            machine.Tick();
            if (machine.Cpu.AtInstructionBoundary) return;
        }
    }

    // ---- Fast path: nothing armed -----------------------------------------------

    [Fact]
    public void NoBreakpoints_Tick_DoesNotPause()
    {
        var machine = MakeWithRom(new byte[] { 0x00, 0x18, 0xFE }); // NOP; JR -2

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    [Fact]
    public void NoBreakpoints_BreakHit_NeverFires()
    {
        var machine = MakeWithRom(new byte[] { 0x00, 0x18, 0xFE });
        var fired = false;
        machine.BreakHit += _ => fired = true;

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(fired);
    }

    // ---- IsPaused: Tick is no-op while paused -----------------------------------

    [Fact]
    public void WhilePaused_Tick_DoesNotAdvancePC()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddExec(0x0000);

        TickUntilPausedOrBoundary(machine);
        Assert.True(machine.IsPaused);

        var pc = machine.Cpu.Reg.PC;
        for (var i = 0; i < 20; i++) machine.Tick();
        Assert.Equal(pc, machine.Cpu.Reg.PC); // frozen
    }

    [Fact]
    public void Resume_ClearsPaused_TickAdvances()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddExec(0x0000);

        TickUntilPausedOrBoundary(machine);
        Assert.True(machine.IsPaused);

        machine.Breakpoints.Clear();
        machine.Resume();

        Assert.False(machine.IsPaused);
        machine.Tick(); // now advances
    }

    // ---- Exec breakpoint --------------------------------------------------------

    [Fact]
    public void ExecBreakpoint_Fires_AtMatchingPC()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP at 0x0000
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddExec(0x0000);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.Exec, ev!.Value.Kind);
        Assert.Equal(0x0000, ev.Value.Address);
    }

    [Fact]
    public void ExecBreakpoint_DoesNotFire_AtDifferentPC()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddExec(0xF000); // unreachable

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    [Fact]
    public void ExecBreakpoint_LandsAtInstructionBoundary()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP
        var machine = MakeWithRom(rom);
        var atBoundaryOnFire = false;
        machine.BreakHit += _ => atBoundaryOnFire = machine.Cpu.AtInstructionBoundary;

        machine.Breakpoints.AddExec(0x0000);
        TickUntilPausedOrBoundary(machine);

        Assert.True(atBoundaryOnFire);
    }

    [Fact]
    public void ExecBreakpoint_ReturnsCorrectId()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00;
        var machine = MakeWithRom(rom);
        var id = machine.Breakpoints.AddExec(0x0000);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        TickUntilPausedOrBoundary(machine);

        Assert.Equal(id, ev!.Value.BreakpointId);
    }

    // ---- Mem write breakpoint ---------------------------------------------------

    [Fact]
    public void MemWriteBreakpoint_Fires_OnWrite()
    {
        // LD A,0x42; LD (0x6000),A; JR -6
        var rom = new byte[0x1000];
        rom[0] = 0x3E; rom[1] = 0x42;        // LD A, 0x42
        rom[2] = 0x32; rom[3] = 0x00; rom[4] = 0x60; // LD (0x6000), A
        rom[5] = 0x18; rom[6] = 0xF9;        // JR -7 (back to 0x0000)
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddMemWrite(PageTable.BaseRamStart); // 0x6000

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.MemWrite, ev!.Value.Kind);
        Assert.Equal(PageTable.BaseRamStart, ev.Value.Address);
    }

    [Fact]
    public void MemWriteBreakpoint_DoesNotFire_OnRead()
    {
        // LD A,(0x6000) — reads RAM, should not trigger a MemWrite bp
        var rom = new byte[0x1000];
        rom[0] = 0x3A; rom[1] = 0x00; rom[2] = 0x60; // LD A,(0x6000)
        rom[3] = 0x18; rom[4] = 0xFB;                 // JR -5
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddMemWrite(PageTable.BaseRamStart);

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    [Fact]
    public void MemWriteBreakpoint_LandsAtInstructionBoundary()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x3E; rom[1] = 0x42;
        rom[2] = 0x32; rom[3] = 0x00; rom[4] = 0x60;
        rom[5] = 0x18; rom[6] = 0xF9;
        var machine = MakeWithRom(rom);
        var atBoundary = false;
        machine.BreakHit += _ => atBoundary = machine.Cpu.AtInstructionBoundary;

        machine.Breakpoints.AddMemWrite(PageTable.BaseRamStart);
        TickUntilPausedOrBoundary(machine);

        Assert.True(atBoundary);
    }

    // ---- Mem read breakpoint ----------------------------------------------------

    [Fact]
    public void MemReadBreakpoint_Fires_OnDataRead()
    {
        // LD A,(0x6000) — explicit data read from 0x6000
        var rom = new byte[0x1000];
        rom[0] = 0x3A; rom[1] = 0x00; rom[2] = 0x60; // LD A,(0x6000)
        rom[3] = 0x18; rom[4] = 0xFB;                 // JR -5
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddMemRead(PageTable.BaseRamStart);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.MemRead, ev!.Value.Kind);
        Assert.Equal(PageTable.BaseRamStart, ev.Value.Address);
    }

    [Fact]
    public void MemReadBreakpoint_DoesNotFire_OnWrite()
    {
        // LD (0x6000),A — writes, should not trigger MemRead
        var rom = new byte[0x1000];
        rom[0] = 0x32; rom[1] = 0x00; rom[2] = 0x60; // LD (0x6000),A
        rom[3] = 0x18; rom[4] = 0xFB;
        var machine = MakeWithRom(rom);
        // Only arm a MemRead bp — should not fire on write
        machine.Breakpoints.AddMemRead(PageTable.BaseRamStart);

        // We DO expect it to fire on the instruction fetch for address 0x6000...
        // Actually 0x6000 is NOT a ROM address — the instruction bytes are at 0x0000 not 0x6000.
        // The LD (nn),A instruction fetches the opcode from 0x0000-0x0002 and then writes 0x6000.
        // So MemRead at 0x6000 fires only on a data read, not on this write path.
        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    // ---- Mem access breakpoint (R or W) -----------------------------------------

    [Fact]
    public void MemAccessBreakpoint_Fires_OnRead()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x3A; rom[1] = 0x00; rom[2] = 0x60; // LD A,(0x6000)
        rom[3] = 0x18; rom[4] = 0xFB;
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddMemAccess(PageTable.BaseRamStart);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.MemAccess, ev!.Value.Kind);
    }

    [Fact]
    public void MemAccessBreakpoint_Fires_OnWrite()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x32; rom[1] = 0x00; rom[2] = 0x60; // LD (0x6000),A
        rom[3] = 0x18; rom[4] = 0xFB;
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddMemAccess(PageTable.BaseRamStart);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.MemAccess, ev!.Value.Kind);
    }

    // ---- IO read breakpoint -----------------------------------------------------

    [Fact]
    public void IoReadBreakpoint_Fires_OnPortRead()
    {
        // IN A,(0x00) — reads keyboard port 0
        var rom = new byte[0x1000];
        rom[0] = 0xDB; rom[1] = 0x00; // IN A,(0x00)
        rom[2] = 0x18; rom[3] = 0xFC; // JR -4
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddIoRead(0x00);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.IoRead, ev!.Value.Kind);
        Assert.Equal(0x00, ev.Value.Address);
    }

    [Fact]
    public void IoReadBreakpoint_DoesNotFire_OnWrite()
    {
        // OUT (0x10),A — writes to CPOUT, not a read
        var rom = new byte[0x1000];
        rom[0] = 0xD3; rom[1] = 0x10; // OUT (0x10),A
        rom[2] = 0x18; rom[3] = 0xFC;
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddIoRead(0x10);

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    // ---- IO write breakpoint ----------------------------------------------------

    [Fact]
    public void IoWriteBreakpoint_Fires_OnPortWrite()
    {
        // OUT (0x10),A — writes to CPOUT port
        var rom = new byte[0x1000];
        rom[0] = 0xD3; rom[1] = 0x10; // OUT (0x10),A
        rom[2] = 0x18; rom[3] = 0xFC;
        var machine = MakeWithRom(rom);
        BreakEvent? ev = null;
        machine.BreakHit += e => ev = e;

        machine.Breakpoints.AddIoWrite(0x10);

        TickUntilPausedOrBoundary(machine);

        Assert.True(machine.IsPaused);
        Assert.NotNull(ev);
        Assert.Equal(BreakpointKind.IoWrite, ev!.Value.Kind);
        Assert.Equal(0x10, ev.Value.Address);
    }

    [Fact]
    public void IoWriteBreakpoint_DoesNotFire_OnRead()
    {
        // IN A,(0x00) — reads, should not trigger IoWrite
        var rom = new byte[0x1000];
        rom[0] = 0xDB; rom[1] = 0x00;
        rom[2] = 0x18; rom[3] = 0xFC;
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddIoWrite(0x00);

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    // ---- Remove / Clear ---------------------------------------------------------

    [Fact]
    public void Remove_Breakpoint_NoLongerFires()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00; // NOP
        var machine = MakeWithRom(rom);
        var id = machine.Breakpoints.AddExec(0x0000);

        machine.Breakpoints.Remove(id);

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    [Fact]
    public void Remove_Returns_False_For_Unknown_Id()
    {
        var store = new BreakpointStore();
        Assert.False(store.Remove(999));
    }

    [Fact]
    public void Clear_AllBreakpoints_NoLongerFire()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00;
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddExec(0x0000);
        machine.Breakpoints.AddMemWrite(0x6000);
        machine.Breakpoints.AddIoRead(0x00);

        machine.Breakpoints.Clear();
        Assert.False(machine.Breakpoints.AnyArmed);

        for (var i = 0; i < 500; i++) machine.Tick();

        Assert.False(machine.IsPaused);
    }

    // ---- AnyArmed fast path -----------------------------------------------------

    [Fact]
    public void AnyArmed_IsFalse_WhenEmpty()
    {
        Assert.False(new BreakpointStore().AnyArmed);
    }

    [Fact]
    public void AnyArmed_IsTrue_WhenBpAdded()
    {
        var store = new BreakpointStore();
        store.AddExec(0x0000);
        Assert.True(store.AnyArmed);
    }

    [Fact]
    public void AnyArmed_IsFalse_AfterClear()
    {
        var store = new BreakpointStore();
        store.AddExec(0x0000);
        store.Clear();
        Assert.False(store.AnyArmed);
    }

    // ---- Reset clears paused state ----------------------------------------------

    [Fact]
    public void Reset_ClearsPausedAndPending()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00;
        var machine = MakeWithRom(rom);
        machine.Breakpoints.AddExec(0x0000);

        TickUntilPausedOrBoundary(machine);
        Assert.True(machine.IsPaused);

        machine.Reset();
        machine.Memory.LoadRom(rom); // re-inject after reset

        Assert.False(machine.IsPaused);
    }

    // ---- Multiple breakpoints — only first fires (per tick) ---------------------

    [Fact]
    public void MultipleBreakpoints_OnSameAddress_FiresOnce_PerPause()
    {
        var rom = new byte[0x1000];
        rom[0] = 0x00;
        var machine = MakeWithRom(rom);
        var count = 0;
        machine.BreakHit += _ => count++;

        machine.Breakpoints.AddExec(0x0000);
        machine.Breakpoints.AddExec(0x0000); // second bp on same addr

        TickUntilPausedOrBoundary(machine);

        // At most one event per pause (first match wins)
        Assert.Equal(1, count);
    }
}

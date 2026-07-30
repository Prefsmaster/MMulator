using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="DebuggerWindowVm.RestoreMemoryWatch"/> (project CLAUDE.md milestone
/// 14b) — the <c>.uistate</c> sidecar's restore path for a memory watch pre-configured to a
/// saved range/follow-register, as opposed to <c>AddMemoryWatch</c>'s always-default-range
/// button path.
/// </summary>
public class DebuggerWindowVmTests
{
    [Fact]
    public void RestoreMemoryWatch_AppliesGivenRangeAndFollow_NotTheDefault()
    {
        var runner = new EmulationRunner();
        var vm = new DebuggerWindowVm(runner);

        var watch = vm.RestoreMemoryWatch(0x6000, 512, "HL");

        Assert.Equal(0x6000, watch.BaseAddress);
        Assert.Equal(512, watch.Length);
        Assert.Equal("HL", watch.Follow);
    }

    [Fact]
    public void RestoreMemoryWatch_AddsToMemoryWatchesCollection()
    {
        var runner = new EmulationRunner();
        var vm = new DebuggerWindowVm(runner);

        var watch = vm.RestoreMemoryWatch(0x5000, 256, "None");

        Assert.Contains(watch, vm.MemoryWatches);
    }

    [Fact]
    public void RestoreMemoryWatch_RaisesOpenMemoryWatchRequested_WithTheSameVm()
    {
        var runner = new EmulationRunner();
        var vm = new DebuggerWindowVm(runner);

        MemoryWatchVm? raised = null;
        vm.OpenMemoryWatchRequested += w => raised = w;

        var watch = vm.RestoreMemoryWatch(0x5000, 256, "SP");

        Assert.Same(watch, raised);
    }

    // ── Bank-qualified exec breakpoints (project CLAUDE.md §14 milestone 17; machine ms.24) ──

    private static byte[] JumpToBankedAddressRom()
    {
        var rom = new byte[0x1000];
        rom[0] = 0xC3; rom[1] = 0x10; rom[2] = 0xE0; // JP 0xE010
        return rom;
    }

    [Fact]
    public void ToggleExecBreakpoint_BankFilterSet_FiresOnlyWhenThatBankIsLiveActive()
    {
        // No real banked card needs to be installed for this — the breakpoint check only reads
        // PageTable.CurrentBank (the raw port-0x94 register), which SelectBank sets unconditionally
        // regardless of whether real backing storage exists for that index (mirrors the machine
        // layer's own BreakpointStoreTests, which uses the identical technique).
        var runnerWrongBank = new EmulationRunner();
        var vmWrongBank = new DebuggerWindowVm(runnerWrongBank);
        runnerWrongBank.Machine.Memory.LoadRom(JumpToBankedAddressRom());
        vmWrongBank.SelectedBreakpointBankOption = "Bank 2";
        vmWrongBank.ToggleExecBreakpoint(0xE010);
        runnerWrongBank.Machine.Memory.SelectBank(5); // NOT the qualified bank

        for (var i = 0; i < 500; i++) runnerWrongBank.Machine.Tick();
        Assert.False(runnerWrongBank.Machine.IsPaused);

        var runnerRightBank = new EmulationRunner();
        var vmRightBank = new DebuggerWindowVm(runnerRightBank);
        runnerRightBank.Machine.Memory.LoadRom(JumpToBankedAddressRom());
        vmRightBank.SelectedBreakpointBankOption = "Bank 2";
        vmRightBank.ToggleExecBreakpoint(0xE010);
        runnerRightBank.Machine.Memory.SelectBank(2); // the qualified bank

        for (var i = 0; i < 500 && !runnerRightBank.Machine.IsPaused; i++)
            runnerRightBank.Machine.Tick();
        Assert.True(runnerRightBank.Machine.IsPaused);
    }

    [Fact]
    public void ToggleExecBreakpoint_BankFilterSet_UnqualifiedComparisonFiresRegardlessOfActiveBank()
    {
        // Regression guard: the SAME address with "Any" selected (the pre-milestone-17 default)
        // must keep firing under every active bank — this milestone must not narrow existing
        // unqualified-breakpoint behavior.
        foreach (var activeBank in new byte[] { 0, 2, 5 })
        {
            var runner = new EmulationRunner();
            var vm = new DebuggerWindowVm(runner);
            runner.Machine.Memory.LoadRom(JumpToBankedAddressRom());
            Assert.Equal(DebuggerWindowVm.AnyBankOption, vm.SelectedBreakpointBankOption); // default
            vm.ToggleExecBreakpoint(0xE010);
            runner.Machine.Memory.SelectBank(activeBank);

            for (var i = 0; i < 500 && !runner.Machine.IsPaused; i++) runner.Machine.Tick();
            Assert.True(runner.Machine.IsPaused);
        }
    }

    [Fact]
    public void ToggleExecBreakpoint_AddressOutsideBankedWindow_IgnoresBankFilter_DoesNotThrow()
    {
        // Outside the banked window, the qualifier must never attach at all — the machine-layer
        // BreakpointStore.AddExec throws ArgumentException for a bank-qualified address outside
        // 0xE000-0xFFFF (see BreakpointStoreTests), so a wrongly-attached qualifier here would
        // surface as an uncaught exception when the queued command drains on the next Tick().
        var runner = new EmulationRunner();
        var vm = new DebuggerWindowVm(runner);
        vm.SelectedBreakpointBankOption = "Bank 3";

        vm.ToggleExecBreakpoint(0x6000); // base RAM — outside the banked window
        runner.Machine.Tick(); // must not throw

        Assert.True(runner.Machine.Breakpoints.AnyArmed);
    }

    [AvaloniaFact]
    public async Task ShowBreakpointBankFilter_And_BreakpointBankOptions_ReflectTheInstalledCardsBankCount()
    {
        var runner = new EmulationRunner();
        var vm = new DebuggerWindowVm(runner);
        runner.Start();
        await Task.Delay(60);

        Assert.False(vm.ShowBreakpointBankFilter); // bare T38 — no banking
        Assert.Equal(new[] { DebuggerWindowVm.AnyBankOption }, vm.BreakpointBankOptions);

        runner.Reconfigure(new MachineConfig { Board = InternalBoard.RamOnly, RamVariant = RamVariant.T102 });
        await Task.Delay(60);

        Assert.True(vm.ShowBreakpointBankFilter);
        Assert.Contains("Bank 0", vm.BreakpointBankOptions);
        Assert.Contains("Bank 5", vm.BreakpointBankOptions);

        runner.Dispose();
    }
}

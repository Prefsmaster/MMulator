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
}

using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="ConfigWindowVm"/>'s Internal-slot-board + Floppy-drives axis (project
/// CLAUDE.md §14 milestone 14) — the config-window prerequisite the disk drive window depends
/// on (drives only exist once <see cref="InternalBoard.FloppyRam"/> is selected).
/// </summary>
public class ConfigWindowVmTests
{
    private static ConfigWindowVm NewVm() => new(new EmulationRunner());

    [Fact]
    public void Default_BareMachine_NoFloppyDrives()
    {
        var vm = NewVm();
        Assert.Equal(InternalBoard.None, vm.Board);
        Assert.Equal(0, vm.FloppyDriveCount);
        Assert.Empty(vm.FloppyDriveRows);
        Assert.False(vm.ShowFloppyDrives);
    }

    [Fact]
    public void SelectingFloppyRamBoard_ForcesRamVariantToT102_AndDisablesRamEditing()
    {
        var vm = NewVm();
        vm.RamVariant = RamVariant.T38;

        vm.Board = InternalBoard.FloppyRam;

        Assert.Equal(RamVariant.T102, vm.RamVariant);
        Assert.False(vm.CanEditRamVariant);
        Assert.True(vm.ShowFloppyDrives);
    }

    [Fact]
    public void SelectingNonFloppyBoard_LeavesRamVariantEditable()
    {
        var vm = NewVm();
        vm.Board = InternalBoard.RamOnly;

        Assert.True(vm.CanEditRamVariant);
        Assert.False(vm.ShowFloppyDrives);
    }

    /// <summary>Project CLAUDE.md §17, 2026-07-23 (owner decision): switching the board away
    /// from Floppy+RAM must PRESERVE the configured drive list (just hide it), not clear it —
    /// switching back restores it exactly as it was.</summary>
    [Fact]
    public void SwitchingBoardAwayFromFloppyRam_PreservesDriveConfiguration()
    {
        var vm = NewVm();
        vm.Board = InternalBoard.FloppyRam;
        vm.FloppyDriveCount = 2;
        vm.FloppyDriveRows[1].Capacity = 80;
        vm.FloppyDriveRows[1].Sides = DiskSides.Double;

        vm.Board = InternalBoard.None;

        Assert.False(vm.ShowFloppyDrives); // hidden, not gone
        Assert.Equal(2, vm.FloppyDriveCount);
        Assert.Equal(2, vm.FloppyDriveRows.Count);
        Assert.Equal(80, vm.FloppyDriveRows[1].Capacity);
        Assert.Equal(DiskSides.Double, vm.FloppyDriveRows[1].Sides);

        vm.Board = InternalBoard.FloppyRam;

        Assert.True(vm.ShowFloppyDrives);
        Assert.Equal(2, vm.FloppyDriveCount);
        Assert.Equal(80, vm.FloppyDriveRows[1].Capacity);
    }

    [Fact]
    public void FloppyDriveCount_ResizesRowsWithSequentialIndices()
    {
        var vm = NewVm();

        vm.FloppyDriveCount = 3;

        Assert.Equal(3, vm.FloppyDriveRows.Count);
        Assert.Equal([0, 1, 2], vm.FloppyDriveRows.Select(r => r.DriveIndex));
    }

    [Fact]
    public void FloppyDriveCount_ShrinkingRemovesFromTheEnd_KeepsEarlierRowsIntact()
    {
        var vm = NewVm();
        vm.FloppyDriveCount = 4;
        vm.FloppyDriveRows[0].Capacity = 80;

        vm.FloppyDriveCount = 2;

        Assert.Equal(2, vm.FloppyDriveRows.Count);
        Assert.Equal(80, vm.FloppyDriveRows[0].Capacity);
    }

    [Fact]
    public void FloppyDriveRow_DefaultsToFortyTrackSingleSided()
    {
        var vm = NewVm();
        vm.FloppyDriveCount = 1;

        Assert.Equal(40, vm.FloppyDriveRows[0].Capacity);
        Assert.Equal(DiskSides.Single, vm.FloppyDriveRows[0].Sides);
    }

    /// <summary>Uses <see cref="AvaloniaFactAttribute"/> + async, same reason as
    /// <c>DiskDriveVmTests</c>: <c>Apply</c>'s <c>Reconfigure</c> only actually lands once the
    /// emulation thread is running and reaches a field boundary.</summary>
    [AvaloniaFact]
    public async Task Apply_FloppyRamWithTwoDrives_BuildsAMachineWithAnFdcAndTwoDrives()
    {
        var runner = new EmulationRunner();
        var vm = new ConfigWindowVm(runner);
        vm.Board = InternalBoard.FloppyRam; // auto-forces T102
        vm.FloppyDriveCount = 2;
        vm.FloppyDriveRows[1].Capacity = 80;
        vm.FloppyDriveRows[1].Sides = DiskSides.Double;
        runner.Start();
        await Task.Delay(60);

        vm.ApplyCommand.Execute(null);
        await Task.Delay(60); // let the swap land on the emulation thread

        Assert.NotNull(runner.Machine.Fdc);
        Assert.Equal(2, runner.Machine.Config.FloppyDrives.Count);
        Assert.Equal(80, runner.Machine.Config.FloppyDrives[1].Capacity);
        Assert.Equal(DiskSides.Double, runner.Machine.Config.FloppyDrives[1].Sides);
        Assert.Contains("Applied", vm.StatusMessage);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task LoadFromCurrentConfig_ReflectsAnAlreadyFloppyRamMachine()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 0, Capacity = 35, Sides = DiskSides.Single },
                new FloppyDriveConfig { DriveIndex = 1, Capacity = 80, Sides = DiskSides.Double },
            },
        });
        await Task.Delay(60);

        var vm = new ConfigWindowVm(runner);

        Assert.Equal(InternalBoard.FloppyRam, vm.Board);
        Assert.Equal(2, vm.FloppyDriveCount);
        Assert.Equal(35, vm.FloppyDriveRows[0].Capacity);
        Assert.Equal(80, vm.FloppyDriveRows[1].Capacity);
        Assert.Equal(DiskSides.Double, vm.FloppyDriveRows[1].Sides);

        runner.Dispose();
    }

    [Fact]
    public void Apply_InvalidCombination_SurfacesStatusMessage_DoesNotThrow()
    {
        // Can't actually be reached through normal UI interaction (Board forces T102), but
        // Apply must not crash the UI thread if it ever is — the machine's own validation
        // throws ArgumentException for FloppyRam + non-T102 (Machine.cs).
        var runner = new EmulationRunner();
        var vm = new ConfigWindowVm(runner);
        vm.Board = InternalBoard.FloppyRam;
        vm.RamVariant = RamVariant.T38; // fight the auto-force back to an invalid combo

        var exception = Record.Exception(() => vm.ApplyCommand.Execute(null));

        Assert.Null(exception);
        Assert.Contains("Could not apply", vm.StatusMessage);
    }
}

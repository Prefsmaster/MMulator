using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>Tests for <see cref="DiskDriveWindowVm"/> (project CLAUDE.md §14 milestone 14) —
/// the container VM that rebuilds its per-drive row collection whenever a topology change
/// (<c>Reconfigure</c>) swaps in a machine with a different drive count. Uses
/// <see cref="AvaloniaFactAttribute"/> + async for the same reason as <c>DiskDriveVmTests</c>:
/// a real board/drive-count swap only lands once the emulation thread is running.</summary>
public class DiskDriveWindowVmTests
{
    [Fact]
    public void BareMachine_NoFloppyBoard_NoDrives()
    {
        var runner = new EmulationRunner();
        var vm = new DiskDriveWindowVm(runner);

        Assert.False(vm.HasFloppyBoard);
        Assert.Empty(vm.Drives);
    }

    [AvaloniaFact]
    public async Task ReconfigureToFloppyRam_PopulatesOneRowPerConfiguredDrive()
    {
        var runner = new EmulationRunner();
        var vm = new DiskDriveWindowVm(runner);
        runner.Start();
        await Task.Delay(60);

        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 0 },
                new FloppyDriveConfig { DriveIndex = 1 },
            },
        });
        await Task.Delay(60); // let the swap land + at least one FrameReady tick rebuild the VM

        Assert.True(vm.HasFloppyBoard);
        Assert.Equal(2, vm.Drives.Count);
        Assert.Equal([0, 1], vm.Drives.Select(d => d.DriveIndex));

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task ReconfigureBackToBare_ClearsDrivesAndHasFloppyBoard()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0 } },
        });
        await Task.Delay(60);

        var vm = new DiskDriveWindowVm(runner);
        Assert.Single(vm.Drives);

        runner.Reconfigure(new MachineConfig());
        await Task.Delay(60);

        Assert.False(vm.HasFloppyBoard);
        Assert.Empty(vm.Drives);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task DisabledDriveInConfig_GetsNoRow()
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
                new FloppyDriveConfig { DriveIndex = 0 },
                new FloppyDriveConfig { DriveIndex = 1, Enabled = false },
            },
        });
        await Task.Delay(60);

        var vm = new DiskDriveWindowVm(runner);

        Assert.Single(vm.Drives);
        Assert.Equal(0, vm.Drives[0].DriveIndex);

        runner.Dispose();
    }
}

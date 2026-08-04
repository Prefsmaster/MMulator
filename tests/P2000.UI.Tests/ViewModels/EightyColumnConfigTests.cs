using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.Machine.Contention;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// UI milestone 20: the T-only "modifications" config axis (80-column board) and the
/// viewport-width plumbing the corrupted-cell overlay now needs (machine milestone 25).
/// </summary>
public class EightyColumnConfigTests
{
    private static ConfigWindowVm NewVm()
    {
        var runner = new EmulationRunner();
        return new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
    }

    [Fact]
    public void Default_NoBoardFitted_ArtifactsToggleOnButDisabled()
    {
        var vm = NewVm();

        Assert.False(vm.EightyColumnBoard);
        Assert.True(vm.ShowEightyColumnArtifacts);   // matches the machine-layer default
        Assert.True(vm.CanEditModifications);
        Assert.False(vm.CanEditEightyColumnArtifacts); // meaningless without the board
    }

    [Fact]
    public void FittingTheBoard_EnablesTheArtifactToggle()
    {
        var vm = NewVm();

        vm.EightyColumnBoard = true;

        Assert.True(vm.CanEditEightyColumnArtifacts);
    }

    [AvaloniaFact]
    public async Task Apply_FitsTheBoard_AndThePortsRespond()
    {
        var runner = new EmulationRunner();
        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
        runner.Start();
        try
        {
            vm.EightyColumnBoard = true;
            vm.ApplyCommand.Execute(null);
            await Task.Delay(60); // let the machine swap land at a field boundary

            Assert.True(runner.Machine.Config.Modifications.EightyColumnBoard);
            Assert.NotNull(runner.Machine.EightyColumn);
        }
        finally
        {
            runner.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Apply_WithoutTheBoard_LeavesPort70Unclaimed()
    {
        var runner = new EmulationRunner();
        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
        runner.Start();
        try
        {
            vm.ApplyCommand.Execute(null);
            await Task.Delay(60);

            Assert.False(runner.Machine.Config.Modifications.EightyColumnBoard);
            Assert.Null(runner.Machine.EightyColumn);
        }
        finally
        {
            runner.Dispose();
        }
    }

    [Fact]
    public void VramWindowVm_DefaultsToTheFortyColumnViewport()
    {
        var vm = new VramWindowVm();

        Assert.Equal(40, vm.ViewportWidth);
        Assert.Equal(40 * 24, vm.Corruption.Length);
    }

    [Fact]
    public void VramWindowVm_AcceptsTheEightyColumnViewportWidth()
    {
        var vm = new VramWindowVm();
        var overlay = new bool[VideoFetchUnit.ColumnsEightyColumn * 24];
        overlay[79] = true; // row 0, column 79 — unreachable under a 40-wide stride

        vm.Update(_ => 0, panX: 0, overlay, corruptionWidth: 80);

        Assert.Equal(80, vm.ViewportWidth);
        Assert.Equal(80 * 24, vm.Corruption.Length);
        Assert.True(vm.Corruption[79]);
    }

    [Fact]
    public void VramWindowVm_FallsBackCleanlyWhenTheOverlayIsTooShortForTheStatedWidth()
    {
        var vm = new VramWindowVm();

        vm.Update(_ => 0, panX: 0, new bool[40 * 24], corruptionWidth: 80);

        Assert.Equal(80, vm.ViewportWidth);
        Assert.Equal(80 * 24, vm.Corruption.Length);
        Assert.All(vm.Corruption, Assert.False);
    }
}

using CommunityToolkit.Mvvm.Input;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="CassetteDeckVm"/>'s "New (blank) tape" + Save/Save-as wiring (§14
/// milestone 13). The StorageProvider-driven halves of <c>SaveAsync</c>/<c>SaveAsAsync</c>/
/// <c>MountAsync</c> are not unit-tested here — same limitation noted for
/// <c>MemoryWatchVmTests</c>/<c>DisplayWindowVm</c>: a headless test run has no real desktop
/// <c>TopLevel</c>, so the file picker never fires. What IS tested: the VM's state transitions
/// (<c>HasTape</c>/<c>TapeLabel</c>/<c>IsWriteProtected</c>), the Save/SaveAs `CanExecute`
/// wiring, and — via the underlying <c>MdcrDevice</c> the VM drives — that "New (blank) tape"
/// never observes CIP go absent when swapping over an already-mounted tape.
/// </summary>
public class CassetteDeckVmTests
{
    private static CassetteDeckVm NewVm() => new(new EmulationRunner());

    private static byte[] OneCasRecord() => new byte[1280];

    [Fact]
    public void Initial_NoTape_SaveAndSaveAsDisabled()
    {
        var vm = NewVm();
        Assert.False(vm.HasTape);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.EjectCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_SetsHasTapeTrue_Unbacked_Writable()
    {
        var vm = NewVm();

        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(vm.HasTape);
        Assert.Equal("(blank tape)", vm.TapeLabel);
        Assert.False(vm.IsWriteProtected);
        Assert.Empty(vm.Programs);
    }

    [Fact]
    public void NewBlankTape_EnablesSaveAndSaveAs()
    {
        var vm = NewVm();
        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_MdcrHasTapeAndCipPresent()
    {
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);

        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(runner.Machine.Mdcr.HasTape);
        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // CIP clear = present
    }

    [Fact]
    public void MountBytes_WithoutBackingFile_HasTapeTrue_Protected()
    {
        var vm = NewVm();

        vm.MountBytes(OneCasRecord(), "GHOSTHUNT");

        Assert.True(vm.HasTape);
        Assert.Equal("GHOSTHUNT", vm.TapeLabel);
        Assert.True(vm.IsWriteProtected);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Eject_ClearsHasTapeAndDisablesSave()
    {
        var vm = NewVm();
        vm.NewBlankTapeCommand.Execute(null);
        Assert.True(vm.HasTape);

        vm.EjectCommand.Execute(null);

        Assert.False(vm.HasTape);
        Assert.Equal("No cassette", vm.TapeLabel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_OverAlreadyMountedTape_CipStaysPresentThroughout()
    {
        // "One CIP transition, not two" (§14 milestone 13 test (e)): swapping straight from a
        // mounted tape to a blank one must never pass through the "no cassette" state.
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);

        vm.MountBytes(OneCasRecord(), "SOMEPROG");
        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // present

        vm.NewBlankTapeCommand.Execute(null);

        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // still present
        Assert.True(vm.HasTape);
        Assert.Equal("(blank tape)", vm.TapeLabel);
    }

    [Fact]
    public void DirectoryHeader_IsFormattedColumnRow()
    {
        Assert.Contains("Filename", CassetteDeckVm.DirectoryHeader);
        Assert.Contains("Ext", CassetteDeckVm.DirectoryHeader);
    }
}

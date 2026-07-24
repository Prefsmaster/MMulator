using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="DiskDriveVm"/> (project CLAUDE.md §14 milestone 14) — mirrors
/// <c>CassetteDeckVmTests</c>' pattern. The StorageProvider-driven halves of
/// <c>MountAsync</c>/<c>SaveAsAsync</c> are not unit-tested here for the same reason noted
/// there: a headless test run has no real desktop <c>TopLevel</c>. What IS tested: state
/// transitions, the Save/Eject/ToggleWriteProtect `CanExecute` wiring, live status fields
/// against the real machine-layer <c>Upd765</c>, and per-drive independence.
///
/// <b>Uses <see cref="Avalonia.Headless.XUnit.AvaloniaFactAttribute"/> + async, same as
/// <c>EmulationRunnerStateTests</c>:</b> unlike <c>CassetteDeckVm</c> (which only ever mutates
/// the cassette directly, no reconfigure involved), getting a Floppy+RAM-board runner at all
/// requires a real <see cref="EmulationRunner.Reconfigure(MachineConfig)"/> topology swap,
/// which only lands once the emulation thread is actually running (<c>Start()</c>) and reaches
/// a field boundary — <c>Dispatcher.UIThread.Post</c> inside that path needs the headless
/// dispatcher context <c>[AvaloniaFact]</c> provides.
/// </summary>
public class DiskDriveVmTests
{
    private static async Task<EmulationRunner> NewFloppyRunnerAsync(int driveCount = 1)
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60); // let at least one field complete so the FIRST swap can land too

        var drives = new FloppyDriveConfig[driveCount];
        for (var i = 0; i < driveCount; i++)
            drives[i] = new FloppyDriveConfig { DriveIndex = i };
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = drives,
        });
        await Task.Delay(60); // let the swap land on the emulation thread

        return runner;
    }

    private static DiskDriveVm NewVm(EmulationRunner runner, int driveIndex = 0,
        int capacity = 40, DiskSides sides = DiskSides.Single) =>
        new(runner, driveIndex, capacity, sides);

    // ---- Initial state ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Initial_NoImage_EjectAndSaveDisabled()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);

        Assert.False(vm.HasImage);
        Assert.Equal("No disk", vm.ImageLabel);
        Assert.False(vm.EjectCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.ToggleWriteProtectCommand.CanExecute(null));

        runner.Dispose();
    }

    // ---- New (blank) disk ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task NewBlankDisk_MountsAtConfiguredGeometry_UnprotectedNoDirectory()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner, capacity: 80, sides: DiskSides.Double);

        vm.NewBlankDiskCommand.Execute(null);

        Assert.True(vm.HasImage);
        Assert.Equal("(blank disk)", vm.ImageLabel);
        Assert.False(vm.IsWriteProtected);
        Assert.Empty(vm.Programs);

        var disk = runner.Machine.Fdc!.GetDisk(0);
        Assert.NotNull(disk);
        Assert.Equal(80, disk!.Tracks);
        Assert.Equal(2, disk.Sides);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task NewBlankDisk_EnablesSaveAndEject()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));
        Assert.True(vm.EjectCommand.CanExecute(null));
        Assert.True(vm.ToggleWriteProtectCommand.CanExecute(null));

        runner.Dispose();
    }

    // ---- MountBytes -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task MountBytes_ValidImage_SetsHasImageAndParsesGeometry()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        var image = BuildSyntheticImage(tracks: 40, sides: 2);

        vm.MountBytes(image, "SPEL1");

        Assert.True(vm.HasImage);
        Assert.Equal("SPEL1", vm.ImageLabel);
        Assert.Equal(2, runner.Machine.Fdc!.GetDisk(0)!.Sides);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task MountBytes_TooShortForLabel_ShowsMessage_DoesNotMount()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        string? shownMessage = null;
        vm.ShowMessageRequested += m => shownMessage = m;

        vm.MountBytes(new byte[10], "BAD");

        Assert.False(vm.HasImage);
        Assert.NotNull(shownMessage);
        Assert.Null(runner.Machine.Fdc!.GetDisk(0));

        runner.Dispose();
    }

    // ---- Write-protect ----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ToggleWriteProtect_FlipsTheLiveMountedImage()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        vm.ToggleWriteProtectCommand.Execute(null);
        Assert.True(vm.IsWriteProtected);
        Assert.True(runner.Machine.Fdc!.GetDisk(0)!.WriteProtected);

        vm.ToggleWriteProtectCommand.Execute(null);
        Assert.False(vm.IsWriteProtected);
        Assert.False(runner.Machine.Fdc!.GetDisk(0)!.WriteProtected);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task WriteProtect_ActuallyGatesWrites_NotJustCosmetic()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        vm.IsWriteProtected = true;

        var disk = runner.Machine.Fdc!.GetDisk(0)!;
        disk.WriteSector(0, 0, 1, Enumerable.Repeat((byte)0xAA, 256).ToArray());

        var readBack = disk.ReadSector(0, 0, 1).ToArray(); // materialize — a Span can't cross an await state machine pre-C#13
        foreach (var b in readBack) Assert.Equal(0x00, b);

        runner.Dispose();
    }

    // ---- Eject ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Eject_ClearsHasImage_AndUnmountsFromTheMachine()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        vm.EjectCommand.Execute(null);

        Assert.False(vm.HasImage);
        Assert.Equal("No disk", vm.ImageLabel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Null(runner.Machine.Fdc!.GetDisk(0));

        runner.Dispose();
    }

    // ---- Live status (motor/cylinder/activity) ----------------------------------------------

    [AvaloniaFact]
    public async Task MotorOn_ReflectsTheMachinesSharedMotorLine()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        Assert.False(vm.IsMotorOn);

        runner.Machine.Ports.Write(0x90, 0x08); // MOTOR bit
        await Task.Delay(60); // let a live FrameReady tick refresh the VM

        Assert.True(vm.IsMotorOn);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task TwoDrives_MotorState_IsSharedAcrossBothRows()
    {
        var runner = await NewFloppyRunnerAsync(driveCount: 2);
        var vm0 = NewVm(runner, driveIndex: 0);
        var vm1 = NewVm(runner, driveIndex: 1);

        runner.Machine.Ports.Write(0x90, 0x08);
        await Task.Delay(60);

        Assert.True(vm0.IsMotorOn);
        Assert.True(vm1.IsMotorOn);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task TwoDrives_MountingOnOneDrive_DoesNotAffectTheOther()
    {
        var runner = await NewFloppyRunnerAsync(driveCount: 2);
        var vm0 = NewVm(runner, driveIndex: 0);
        var vm1 = NewVm(runner, driveIndex: 1);

        vm0.NewBlankDiskCommand.Execute(null);

        Assert.True(vm0.HasImage);
        Assert.False(vm1.HasImage);
        Assert.Null(runner.Machine.Fdc!.GetDisk(1));

        runner.Dispose();
    }

    // ---- Head/sector (project CLAUDE.md §17, 2026-07-23 owner decision) --------------------

    [AvaloniaFact]
    public async Task HeadAndSector_Idle_ShowDash()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        Assert.Equal("—", vm.HeadText);
        Assert.Equal("—", vm.SectorText);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task HeadAndSector_DuringActiveTransfer_ShowRealValues()
    {
        // Stop the background thread right after the FloppyRam swap lands, then drive the FDC
        // fully synchronously from here — writing ports while the emulation thread is
        // concurrently ticking would race against the transfer completing on its own (an
        // Authentic 256-byte transfer is only ~8k T-states, far less than even one 50 Hz
        // field's worth of ticking, so it could finish before this test ever reads the VM).
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        runner.Dispose();

        // READ DATA: unit 0, cylinder 0, head 0 (the default drive geometry is single-sided —
        // head 1 doesn't exist), start sector 3, N=1 (256B), EOT=1.
        var ports = runner.Machine.Ports;
        ports.Write(0x8D, 0x42);
        ports.Write(0x8D, 0x00);
        ports.Write(0x8D, 0x00);
        ports.Write(0x8D, 0x00); // head 0
        ports.Write(0x8D, 0x03); // start sector 3
        ports.Write(0x8D, 0x01);
        ports.Write(0x8D, 0x01);
        ports.Write(0x8D, 0x00);
        ports.Write(0x8D, 0x00);
        InvokeRefresh(vm);

        Assert.Equal("0", vm.HeadText);
        Assert.Equal("3", vm.SectorText);
    }

    private static void InvokeRefresh(DiskDriveVm vm)
    {
        var method = typeof(DiskDriveVm).GetMethod("RefreshFromMachine",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, null);
    }

    // ---- Dirty tracking (machine milestone 20a, surfaced project CLAUDE.md §14 2026-07-23) --

    [AvaloniaFact]
    public async Task IsDirty_FreshlyCreatedBlankDisk_IsFalse()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);

        vm.NewBlankDiskCommand.Execute(null);
        await Task.Delay(60);

        Assert.False(vm.IsDirty);
        Assert.DoesNotContain("*", vm.TabHeader);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task IsDirty_AfterAWrite_IsTrue_AndShowsInTabHeader()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        await Task.Delay(60);

        Assert.True(vm.IsDirty);
        Assert.Contains("*", vm.TabHeader);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task TabHeader_IncludesDriveIndexAndImageLabel()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner, driveIndex: 0);
        vm.MountBytes(BuildSyntheticImage(40, 2), "SPEL1");

        Assert.Equal("0: SPEL1", vm.TabHeader);

        runner.Dispose();
    }

    private static byte[] BuildSyntheticImage(int tracks, int sides)
    {
        var image = new byte[tracks * sides * 16 * 256];
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    [Fact]
    public void DirectoryHeader_IsFormattedColumnRow()
    {
        Assert.Contains("Filename", DiskDriveVm.DirectoryHeader);
    }
}

using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
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
    public async Task MountBytes_TooShortForLabel_MountsAnyway_ReportsNoCandidateMismatch()
    {
        // Project CLAUDE.md milestone 20d/14e: mounting never fails anymore — a too-short file
        // mounts using the drive's configured geometry and reports a mismatch instead of
        // rejecting the file outright (the owner's own real 32,768-byte test case).
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner); // configured 40-track/single-sided (163,840 bytes)
        DiskGeometryMismatch? reported = null;
        vm.GeometryMismatchDetected += m => reported = m;

        vm.MountBytes(new byte[10], "BAD");

        Assert.True(vm.HasImage);
        Assert.NotNull(runner.Machine.Fdc!.GetDisk(0));
        Assert.NotNull(reported);
        Assert.Equal(DiskGeometryMismatchKind.NoCandidate, reported!.Value.Kind);
        Assert.Equal(10, reported.Value.ActualLength);

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

        // READ DATA (0x46 = 0x06|MF — NOT the real ROM byte 0x42, which decodes to READ A TRACK
        // and ignores R; project CLAUDE.md §17 2026-07-24 opcode-identity finding): unit 0,
        // cylinder 0, head 0 (the default drive geometry is single-sided — head 1 doesn't
        // exist), start sector 3, N=1 (256B), EOT=1.
        var ports = runner.Machine.Ports;
        ports.Write(0x8D, 0x46);
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

    // ---- Unsaved-changes discard confirmation (§14 milestone 14a) --------------------------
    // ConfirmDiscardRequested is wired to the machine-layer DskImage.IsDirty (machine ms.20a);
    // WriteSector is the same write path DiskDriveVmTests' own dirty-tracking tests above use.

    [AvaloniaFact]
    public async Task Eject_CleanDisk_NoConfirmDialogShown()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        var confirmShown = false;
        vm.ConfirmDiscardRequested += _ => { confirmShown = true; return Task.FromResult(true); };

        vm.EjectCommand.Execute(null);

        Assert.False(confirmShown);
        Assert.False(vm.HasImage);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task Eject_DirtyDisk_ShowsConfirmDialog()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        var confirmShown = false;
        vm.ConfirmDiscardRequested += _ => { confirmShown = true; return Task.FromResult(true); };

        vm.EjectCommand.Execute(null);

        Assert.True(confirmShown);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task Eject_DirtyDisk_CancelLeavesImageMountedAndStillDirty()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        var disk = runner.Machine.Fdc!.GetDisk(0)!;
        disk.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(false); // Cancel

        vm.EjectCommand.Execute(null);

        Assert.True(vm.HasImage);
        Assert.True(disk.IsDirty);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task Eject_DirtyDisk_DiscardProceedsExactlyAsAnUnconfirmedEject()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(true); // Discard

        vm.EjectCommand.Execute(null);

        Assert.False(vm.HasImage);
        Assert.Null(runner.Machine.Fdc!.GetDisk(0));

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task Eject_AfterSaveClearsDirty_NoConfirmDialog()
    {
        // Stand-in for a real Save/Save-as (untestable headless — no desktop TopLevel, same
        // limitation noted at the top of this file): MarkClean() is exactly what a successful
        // save calls machine-side, so this isolates "clean again → no prompt" from the file I/O.
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        var disk = runner.Machine.Fdc!.GetDisk(0)!;
        disk.WriteSector(0, 0, 1, new byte[256]);
        disk.MarkClean();
        var confirmShown = false;
        vm.ConfirmDiscardRequested += _ => { confirmShown = true; return Task.FromResult(true); };

        vm.EjectCommand.Execute(null);

        Assert.False(confirmShown);
        Assert.False(vm.HasImage);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task NewBlankDisk_OverDirtyImage_CancelLeavesOriginalMounted()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(BuildSyntheticImage(40, 2), "ORIGINAL");
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(false);

        vm.NewBlankDiskCommand.Execute(null);

        Assert.Equal("ORIGINAL", vm.ImageLabel);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task NewBlankDisk_OverDirtyImage_DiscardReplacesIt()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(BuildSyntheticImage(40, 2), "ORIGINAL");
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(true);

        vm.NewBlankDiskCommand.Execute(null);

        Assert.Equal("(blank disk)", vm.ImageLabel);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task TryMountBytesAsync_OverDirtyImage_CancelLeavesOriginalMounted()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(BuildSyntheticImage(40, 2), "ORIGINAL");
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(false);

        var mounted = await vm.TryMountBytesAsync(BuildSyntheticImage(40, 2), "REPLACEMENT");

        Assert.False(mounted);
        Assert.Equal("ORIGINAL", vm.ImageLabel);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task TryMountBytesAsync_OverDirtyImage_DiscardReplacesIt()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(BuildSyntheticImage(40, 2), "ORIGINAL");
        runner.Machine.Fdc!.GetDisk(0)!.WriteSector(0, 0, 1, new byte[256]);
        vm.ConfirmDiscardRequested += _ => Task.FromResult(true);

        var mounted = await vm.TryMountBytesAsync(BuildSyntheticImage(40, 2), "REPLACEMENT");

        Assert.True(mounted);
        Assert.Equal("REPLACEMENT", vm.ImageLabel);

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

    // ---- Geometry-mismatch dialog decision logic (project CLAUDE.md milestone 14e; machine
    // ms.20d). Dialogs themselves aren't headlessly testable (no real StorageProvider/TopLevel —
    // same limitation SaveCfgAsync already has elsewhere), so these exercise the VM-level
    // decisions directly: which event fires for a given mismatch shape, and what each of
    // ReconfigureAndRemount/ContinueWithCurrentMount/ExtendMountedDiskToFullSize/CancelMount
    // actually does. -------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task MountBytes_LabeledCorrectlySizedImage_NoMismatchEvent_RegressionGuard()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner); // configured 40-track/single-sided
        var raised = false;
        vm.GeometryMismatchDetected += _ => raised = true;

        // A genuine JWSDOS label always wins regardless of the drive's configured geometry.
        vm.MountBytes(BuildSyntheticImage(tracks: 80, sides: 2), "GOOD");

        Assert.False(raised);
        Assert.True(vm.HasImage);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task MountBytes_SingleCandidateMismatch_ReportsExactlyOneCandidate()
    {
        var runner = await NewFloppyRunnerAsync(); // configured 40-track/single (163,840 B)
        var vm = NewVm(runner);
        DiskGeometryMismatch? reported = null;
        vm.GeometryMismatchDetected += m => reported = m;

        vm.MountBytes(new byte[35 * 1 * 16 * 256], "PDOS"); // 143,360 B — unique to 35-track/SS

        Assert.NotNull(reported);
        Assert.Equal(DiskGeometryMismatchKind.Candidate, reported!.Value.Kind);
        Assert.Equal(new[] { (35, 1) }, reported.Value.Candidates);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task MountBytes_TwoCandidateMismatch_ReportsBoth()
    {
        var runner = await NewFloppyRunnerAsync(); // configured 40-track/single — neither collider
        var vm = NewVm(runner);
        DiskGeometryMismatch? reported = null;
        vm.GeometryMismatchDetected += m => reported = m;

        vm.MountBytes(new byte[40 * 2 * 16 * 256], "PDOS"); // == 80*1*16*256 — the confirmed collision

        Assert.NotNull(reported);
        Assert.Equal(DiskGeometryMismatchKind.Candidate, reported!.Value.Kind);
        Assert.Equal(2, reported.Value.Candidates.Count);
        Assert.Contains((40, 2), reported.Value.Candidates);
        Assert.Contains((80, 1), reported.Value.Candidates);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task ReconfigureAndRemount_ChangesGeometry_AndClearsTheMismatch()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner); // configured 40-track/single
        vm.MountBytes(new byte[35 * 1 * 16 * 256], "PDOS"); // single-candidate mismatch: (35, 1)
        DiskGeometryMismatch? reRaised = null;
        vm.GeometryMismatchDetected += m => reRaised = m;

        vm.ReconfigureAndRemount(35, DiskSides.Single);

        Assert.Equal(35, vm.Capacity);
        Assert.Equal(DiskSides.Single, vm.Sides);
        Assert.Equal(35, runner.Machine.Fdc!.GetDisk(0)!.Tracks);
        Assert.Equal(1, runner.Machine.Fdc.GetDisk(0)!.Sides);
        Assert.Null(reRaised); // it now matches cleanly — no new mismatch
        Assert.Equal(DiskGeometryMismatchKind.None, runner.Machine.Fdc.GetMismatch(0)!.Value.Kind);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task ExtendMountedDiskToFullSize_PadsTheImage_AndClearsTheMismatch()
    {
        var runner = await NewFloppyRunnerAsync(); // configured 40-track/single = 163,840 B
        var vm = NewVm(runner);
        vm.MountBytes(new byte[32_768], "SHORT"); // no-candidate mismatch

        var mismatch = runner.Machine.Fdc!.GetMismatch(0)!.Value;
        Assert.Equal(DiskGeometryMismatchKind.NoCandidate, mismatch.Kind);

        vm.ExtendMountedDiskToFullSize(mismatch.ExpectedLength);

        Assert.Equal(mismatch.ExpectedLength, runner.Machine.Fdc.GetDisk(0)!.GetBytes().Length);
        Assert.Equal(DiskGeometryMismatchKind.None, runner.Machine.Fdc.GetMismatch(0)!.Value.Kind);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task ContinueWithCurrentMount_LeavesImageAndMismatchUnchanged()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(new byte[32_768], "SHORT");
        var diskBefore = runner.Machine.Fdc!.GetDisk(0);
        var mismatchBefore = runner.Machine.Fdc.GetMismatch(0);

        vm.ContinueWithCurrentMount();

        Assert.Same(diskBefore, runner.Machine.Fdc.GetDisk(0)); // untouched
        Assert.Equal(mismatchBefore, runner.Machine.Fdc.GetMismatch(0)); // preserved, not cleared
        Assert.True(vm.HasImage);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task CancelMount_EjectsTheJustMountedImage()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.MountBytes(new byte[32_768], "SHORT");

        vm.CancelMount();

        Assert.False(vm.HasImage);
        Assert.Null(runner.Machine.Fdc!.GetDisk(0));
        Assert.Null(runner.Machine.Fdc.GetMismatch(0));

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task PendingMismatch_FromConfigAuthoredMount_OnlyRaisedAfterSubscribing()
    {
        // Simulates the .cfg-authored construction-time mount path (machine ms.20d): the
        // mismatch must survive past construction and be raisable only once something has
        // actually subscribed (project CLAUDE.md milestone 14e) — raising it synchronously
        // inside the constructor would fire before DiskDriveWindowVm could possibly listen.
        var runner = await NewFloppyRunnerAsync();
        var fdc = runner.Machine.Fdc!;
        var (image, mismatch) = DskImage.Mount(new byte[32_768], configuredTracks: 40, configuredSides: 1);
        fdc.MountDisk(0, image, mismatch);

        var vm = new DiskDriveVm(runner, 0, 40, DiskSides.Single);
        Assert.NotNull(vm.PendingMismatch);

        DiskGeometryMismatch? raised = null;
        vm.GeometryMismatchDetected += m => raised = m;
        vm.RaisePendingMismatchIfAny();

        Assert.NotNull(raised);
        Assert.Null(vm.PendingMismatch); // consumed — a second call is a no-op

        runner.Dispose();
    }

    // ---- Save/Save-As format choice (project CLAUDE.md milestone 14f; machine ms.21) -------
    // Full Save/Save-As file I/O isn't headlessly testable (no real desktop TopLevel/
    // StorageProvider — same limitation noted at the top of this file). What IS reachable
    // without one: SaveAsAsync asks SaveAsFormatRequested for the format BEFORE it ever touches
    // GetTopLevel(), so the format-choice decision itself — which format is offered as
    // "current," and that a cancelled/absent choice leaves nothing changed — is directly
    // testable. The actual byte-level Dsk-vs-Imd write selection is covered by
    // `P2000.Machine.Tests`' `ImdFormatTests` (`DskImage.GetBytes`/`GetImdBytes`).

    [AvaloniaFact]
    public async Task SaveAsAsync_AsksForFormat_PassingDskAsCurrentFormat_ForADskBackedDrive()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null); // freshly-created -> DiskImageFormat.Dsk (default)
        DiskImageFormat? askedWith = null;
        vm.SaveAsFormatRequested += format =>
        {
            askedWith = format;
            return Task.FromResult<DiskImageFormat?>(null); // Cancel — nothing to write headless anyway
        };

        vm.SaveAsCommand.Execute(null);

        Assert.Equal(DiskImageFormat.Dsk, askedWith);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task SaveAsAsync_AsksForFormat_PassingImdAsCurrentFormat_ForAnImdBackedDrive()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        var imdBytes = DskImage.CreateBlank(tracks: 40, sides: 1).GetImdBytes();
        vm.MountBytes(imdBytes, "IMD_DISK"); // Mount sniffs the IMD header -> DiskImageFormat.Imd
        Assert.Equal(DiskImageFormat.Imd, runner.Machine.Fdc!.GetDisk(0)!.Format);
        DiskImageFormat? askedWith = null;
        vm.SaveAsFormatRequested += format =>
        {
            askedWith = format;
            return Task.FromResult<DiskImageFormat?>(null);
        };

        vm.SaveAsCommand.Execute(null);

        Assert.Equal(DiskImageFormat.Imd, askedWith);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task SaveAsAsync_NoSubscriber_DefaultsToKeepingCurrentFormat_DoesNotThrow()
    {
        // "No subscriber, proceed" — same shape as ConfirmDiscardRequested's own headless
        // default. Can't observe the write itself (no TopLevel headless), just that nothing
        // throws and the drive's own Format is left untouched.
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);

        vm.SaveAsCommand.Execute(null);

        Assert.Equal(DiskImageFormat.Dsk, runner.Machine.Fdc!.GetDisk(0)!.Format);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task SaveAsAsync_CancelledFormatChoice_LeavesFormatAndPathUnchanged()
    {
        var runner = await NewFloppyRunnerAsync();
        var vm = NewVm(runner);
        vm.NewBlankDiskCommand.Execute(null);
        vm.SaveAsFormatRequested += _ => Task.FromResult<DiskImageFormat?>(null); // Cancel

        vm.SaveAsCommand.Execute(null);

        Assert.Equal(DiskImageFormat.Dsk, runner.Machine.Fdc!.GetDisk(0)!.Format);

        runner.Dispose();
    }

    // ---- (e) mounting a real IMD file never triggers ms.14e's mismatch dialog ---------------

    [AvaloniaFact]
    public async Task MountBytes_RealImdFile_NeverRaisesGeometryMismatch_EvenWithDifferentConfiguredGeometry()
    {
        var runner = await NewFloppyRunnerAsync(); // configured 40-track/single-sided
        var vm = NewVm(runner);
        var imdBytes = DskImage.CreateBlank(tracks: 80, sides: 2).GetImdBytes(); // deliberately different geometry
        var raised = false;
        vm.GeometryMismatchDetected += _ => raised = true;

        vm.MountBytes(imdBytes, "IMD_DISK");

        Assert.False(raised);
        Assert.True(vm.HasImage);
        Assert.Equal(80, runner.Machine.Fdc!.GetDisk(0)!.Tracks);
        Assert.Equal(2, runner.Machine.Fdc.GetDisk(0)!.Sides);
        Assert.Equal(DiskImageFormat.Imd, runner.Machine.Fdc.GetDisk(0)!.Format);

        runner.Dispose();
    }
}

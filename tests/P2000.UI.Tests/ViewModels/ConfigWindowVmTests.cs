using Avalonia.Headless.XUnit;
using Avalonia.Platform.Storage;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
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
    private static ConfigWindowVm NewVm()
    {
        var runner = new EmulationRunner();
        return new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
    }

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
        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
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

        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));

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
        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
        vm.Board = InternalBoard.FloppyRam;
        vm.RamVariant = RamVariant.T38; // fight the auto-force back to an invalid combo

        var exception = Record.Exception(() => vm.ApplyCommand.Execute(null));

        Assert.Null(exception);
        Assert.Contains("Could not apply", vm.StatusMessage);
    }

    // ── Disk-image picking: live delegation vs. offline preview (project CLAUDE.md milestone
    // 14g) ───────────────────────────────────────────────────────────────────────────────────

    private static int LengthFor(int tracks, int sides) =>
        tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector;

    /// <summary>Minimal <see cref="IStorageFile"/> fake — only <see cref="Name"/>, <see cref="Path"/>,
    /// and <see cref="OpenReadAsync"/> are ever exercised by <c>ConfigWindowVm.PickImageForRowAsync</c>;
    /// everything else throws if a test ever accidentally depends on it.</summary>
    private sealed class FakeStorageFile : IStorageFile
    {
        private readonly byte[] _bytes;
        public FakeStorageFile(string absolutePath, byte[] bytes)
        {
            Path = new Uri(absolutePath);
            Name = System.IO.Path.GetFileName(absolutePath);
            _bytes = bytes;
        }
        public string Name { get; }
        public Uri Path { get; }
        public bool CanBookmark => false;
        public Task<StorageItemProperties> GetBasicPropertiesAsync() =>
            Task.FromResult(new StorageItemProperties(null, null, null));
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes));
        public Task<Stream> OpenWriteAsync() => throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>Test (a) of milestone 14g's list: a row backed by an already-existing live drive
    /// delegates straight into the SAME mount path (<c>DiskDriveVm.MountBytes</c>) the Disk Drives
    /// window itself uses, and a real mismatch raises <c>DiskDriveWindowVm.GeometryMismatchDetected</c>
    /// — identically to <c>DiskDriveVmTests</c>' own mismatch tests for the same scenario.</summary>
    [AvaloniaFact]
    public async Task PickImageForRow_LiveDrive_DelegatesToSameMountPath_CanRaiseMismatchDialog()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0, Capacity = 40, Sides = DiskSides.Double } },
        });
        await Task.Delay(60);

        var diskVm = new DiskDriveWindowVm(runner);
        var vm = new ConfigWindowVm(runner, diskVm);
        Assert.Single(vm.FloppyDriveRows); // row already reflects the live drive

        DiskDriveVm? mismatchDrive = null;
        DiskGeometryMismatch? mismatch = null;
        diskVm.GeometryMismatchDetected += (d, m) => { mismatchDrive = d; mismatch = m; };
        var offlineFired = false;
        vm.OfflineMismatchDetected += (_, _) => offlineFired = true;

        // 35-track/SS length -> a real Candidate mismatch against the configured 40-track/DS drive.
        var bytes = new byte[LengthFor(35, 1)];
        var tempPath = Path.GetTempFileName();
        var file = new FakeStorageFile(tempPath, bytes);

        await vm.PickImageForRowAsync(vm.FloppyDriveRows[0], file);

        Assert.NotNull(runner.Machine.Fdc?.GetDisk(0)); // really mounted, not just previewed
        // Mounted regardless, using the CONFIGURED geometry (never the mismatched file's own) —
        // same "nothing here blocks a mount" rule DskImage.Mount always applies (machine ms.20d).
        Assert.Equal(40, runner.Machine.Fdc!.GetDisk(0)!.Tracks);
        Assert.Equal(tempPath, vm.FloppyDriveRows[0].ImagePath); // read back from the real mount
        Assert.NotNull(mismatchDrive);
        Assert.Equal(0, mismatchDrive!.DriveIndex);
        Assert.Equal(DiskGeometryMismatchKind.Candidate, mismatch!.Value.Kind);
        Assert.False(offlineFired); // the live path never raises the offline event

        File.Delete(tempPath);
        runner.Dispose();
    }

    /// <summary>Test (b): the same action for a row with no live drive (board not Applied yet)
    /// only ever previews — never touches the machine, never mounts anything.</summary>
    [Fact]
    public async Task PickImageForRow_NoLiveDrive_OnlyPreviews_NeverMounts()
    {
        var runner = new EmulationRunner(); // never Start()ed/Reconfigure()d -> bare, no Fdc
        var diskVm = new DiskDriveWindowVm(runner);
        var vm = new ConfigWindowVm(runner, diskVm);
        vm.Board = InternalBoard.FloppyRam; // pending topology only -> not applied, no live FDC
        vm.FloppyDriveCount = 1;
        var row = vm.FloppyDriveRows[0]; // defaults: Capacity 40, Sides Single

        DiskGeometryMismatch? seen = null;
        vm.OfflineMismatchDetected += (_, m) => seen = m;

        var bytes = new byte[LengthFor(35, 1)]; // mismatched vs. the row's 40/Single default
        var tempPath = Path.GetTempFileName();
        var file = new FakeStorageFile(tempPath, bytes);

        await vm.PickImageForRowAsync(row, file);

        Assert.Equal(tempPath, row.ImagePath); // path recorded for a future Apply
        Assert.NotNull(seen);
        Assert.Equal(DiskGeometryMismatchKind.Candidate, seen!.Value.Kind);
        Assert.Null(runner.Machine.Fdc); // never mounted — no live drive existed to mount into

        File.Delete(tempPath);
    }

    /// <summary>Test (c): editing Capacity/Sides AFTER a path is already set re-runs the SAME
    /// preview check — both introducing and resolving a mismatch.</summary>
    [Fact]
    public async Task RecheckOfflineMismatch_CapacityChangeAfterPathSet_IntroducesThenResolvesMismatch()
    {
        var runner = new EmulationRunner();
        var diskVm = new DiskDriveWindowVm(runner);
        var vm = new ConfigWindowVm(runner, diskVm);
        vm.Board = InternalBoard.FloppyRam;
        vm.FloppyDriveCount = 1;
        var row = vm.FloppyDriveRows[0];
        row.Capacity = 40;
        row.Sides = DiskSides.Single;

        var tempPath = Path.GetTempFileName();
        try
        {
            var bytes40x1 = new byte[LengthFor(40, 1)];
            await File.WriteAllBytesAsync(tempPath, bytes40x1);

            var mismatches = new List<DiskGeometryMismatch>();
            vm.OfflineMismatchDetected += (_, m) => mismatches.Add(m);

            await vm.PickImageForRowAsync(row, new FakeStorageFile(tempPath, bytes40x1));
            Assert.Empty(mismatches); // exact match against the row's own configured geometry

            row.Capacity = 35; // introduces a mismatch: on-disk file is 40/1-sized
            await Task.Delay(100); // let the fire-and-forget recheck (async file read) complete

            Assert.Single(mismatches);
            Assert.Equal(DiskGeometryMismatchKind.Candidate, mismatches[0].Kind);

            mismatches.Clear();
            row.Capacity = 40; // resolves it back to matching
            await Task.Delay(100);

            Assert.Empty(mismatches); // None never raises
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>Test (d): Apply on a config with an unresolved offline mismatch surfaces the
    /// dialog machinery immediately after the reconfigure succeeds — <c>diskVm</c> here stands in
    /// for <c>DisplayWindowVm.DiskVm</c>, and no <c>DiskDriveWindow</c> view is ever constructed,
    /// matching "regardless of whether the Disk Drives window is open."</summary>
    [AvaloniaFact]
    public async Task Apply_ConfigWithUnresolvedMismatch_SurfacesDialogImmediately_NoDiskDriveWindowOpen()
    {
        var runner = new EmulationRunner();
        var diskVm = new DiskDriveWindowVm(runner);
        var vm = new ConfigWindowVm(runner, diskVm);
        runner.Start();
        await Task.Delay(60);

        DiskDriveVm? raisedDrive = null;
        DiskGeometryMismatch? raisedMismatch = null;
        diskVm.GeometryMismatchDetected += (d, m) => { raisedDrive = d; raisedMismatch = m; };

        var tempPath = Path.GetTempFileName();
        try
        {
            // 35-track/SS length -> a real Candidate mismatch against the row's own 40/Single.
            await File.WriteAllBytesAsync(tempPath, new byte[LengthFor(35, 1)]);

            vm.Board = InternalBoard.FloppyRam;
            vm.FloppyDriveCount = 1;
            vm.FloppyDriveRows[0].Capacity = 40;
            vm.FloppyDriveRows[0].Sides = DiskSides.Single;
            // Authored directly (the offline-authored-.cfg case, not a browse pick) — bypasses
            // PickImageForRowAsync/the offline preview entirely, same as hand-editing a .cfg file.
            vm.FloppyDriveRows[0].ImagePath = tempPath;

            // Reconfigure blocks internally until the swap lands; RebuildIfMachineChanged right
            // after is what raises the mismatch here — both run synchronously inside Apply().
            vm.ApplyCommand.Execute(null);

            Assert.NotNull(raisedDrive);
            Assert.Equal(0, raisedDrive!.DriveIndex);
            Assert.Equal(DiskGeometryMismatchKind.Candidate, raisedMismatch!.Value.Kind);
        }
        finally
        {
            File.Delete(tempPath);
        }

        runner.Dispose();
    }
}

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

    // ── Drive-count axis assigns real-hardware indices (project CLAUDE.md milestone 14d) ────────
    // The ROM's disk driver hardcodes unit-select to drive 1 and never addresses unit 0
    // (reference doc §5d), so the count axis must assign DriveIndex in the sequence 1, 2, 3, 0 —
    // never the old 0-based sequential 0, 1, 2, 3 — or a "1 drive" config silently fails to boot.

    /// <summary>Test (a): "1 drive" → DriveIndex 1, not 0.</summary>
    [Fact]
    public void FloppyDriveCount_OneDrive_TargetsIndex1()
    {
        var vm = NewVm();

        vm.FloppyDriveCount = 1;

        Assert.Equal([1], vm.FloppyDriveRows.Select(r => r.DriveIndex));
    }

    /// <summary>Test (b): "2 drives" → [1, 2].</summary>
    [Fact]
    public void FloppyDriveCount_TwoDrives_TargetsIndices1And2()
    {
        var vm = NewVm();

        vm.FloppyDriveCount = 2;

        Assert.Equal([1, 2], vm.FloppyDriveRows.Select(r => r.DriveIndex));
    }

    [Fact]
    public void FloppyDriveCount_ThreeDrives_TargetsIndices1Through3()
    {
        var vm = NewVm();

        vm.FloppyDriveCount = 3;

        Assert.Equal([1, 2, 3], vm.FloppyDriveRows.Select(r => r.DriveIndex));
    }

    /// <summary>Test (c): "4 drives" → [1, 2, 3, 0] — only the fourth row reaches the
    /// ROM-unaddressed index 0.</summary>
    [Fact]
    public void FloppyDriveCount_FourDrives_TargetsIndices1_2_3_0()
    {
        var vm = NewVm();

        vm.FloppyDriveCount = 4;

        Assert.Equal([1, 2, 3, 0], vm.FloppyDriveRows.Select(r => r.DriveIndex));
    }

    /// <summary>Test (d): round-trip at each count 1-4 — build via this window, Apply (which
    /// actually mounts each row's image into a real machine), then a freshly-opened
    /// <c>ConfigWindowVm</c> against that SAME live machine reflects the identical count and
    /// per-row <c>DriveIndex</c>/<c>ImagePath</c> back.</summary>
    [AvaloniaFact]
    public async Task FloppyDriveCount_RoundTripsThroughApply_AtEachCount()
    {
        foreach (var count in new[] { 1, 2, 3, 4 })
        {
            var runner = new EmulationRunner();
            var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));
            runner.Start();
            await Task.Delay(60);

            vm.Board = InternalBoard.FloppyRam;
            vm.FloppyDriveCount = count;
            var expectedIndices = vm.FloppyDriveRows.Select(r => r.DriveIndex).ToArray();
            var paths = new string[count];
            for (var i = 0; i < count; i++)
            {
                paths[i] = Path.GetTempFileName(); // must actually exist — Machine mounts it on Apply
                vm.FloppyDriveRows[i].ImagePath = paths[i];
            }

            try
            {
                vm.ApplyCommand.Execute(null);
                await Task.Delay(60);

                var reloadVm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));

                Assert.Equal(count, reloadVm.FloppyDriveCount);
                Assert.Equal(expectedIndices, reloadVm.FloppyDriveRows.Select(r => r.DriveIndex));
                for (var i = 0; i < count; i++)
                    Assert.Equal(paths[i], reloadVm.FloppyDriveRows[i].ImagePath);
            }
            finally
            {
                foreach (var p in paths) File.Delete(p);
                runner.Dispose();
            }
        }
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
            // Indices 1, 2 (not 0, 1) — the fixed [1, 2, 3, 0] sequence's first two positions
            // (milestone 14d), so this collapses to count 2, not 4.
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 1, Capacity = 35, Sides = DiskSides.Single },
                new FloppyDriveConfig { DriveIndex = 2, Capacity = 80, Sides = DiskSides.Double },
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

    /// <summary>Test (e): a `.cfg` with only index 1 enabled collapses to count 1 — the
    /// regression guard replacing the OLD "index 0 alone → count 1" case, which must no longer be
    /// how count-1 configs are produced or expected.</summary>
    [AvaloniaFact]
    public async Task LoadFromCurrentConfig_OnlyIndex1Enabled_CollapsesToCountOne()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 1 } },
        });
        await Task.Delay(60);

        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));

        Assert.Equal(1, vm.FloppyDriveCount);
        Assert.Equal(1, vm.FloppyDriveRows[0].DriveIndex);

        runner.Dispose();
    }

    /// <summary>Test (f): a `.cfg` with only index 0 enabled (irregular, hand-edited — never
    /// produced by this window itself) collapses to count 4 with drives 2/3 shown empty, not a
    /// crash — position of index 0 in the [1, 2, 3, 0] sequence is the LAST slot.</summary>
    [AvaloniaFact]
    public async Task LoadFromCurrentConfig_OnlyIndex0Enabled_CollapsesToCountFour_DrivesTwoAndThreeEmpty()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);
        runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0, Capacity = 80, Sides = DiskSides.Double } },
        });
        await Task.Delay(60);

        var exception = Record.Exception(() => new ConfigWindowVm(runner, new DiskDriveWindowVm(runner)));
        Assert.Null(exception);

        var vm = new ConfigWindowVm(runner, new DiskDriveWindowVm(runner));

        Assert.Equal(4, vm.FloppyDriveCount);
        Assert.Equal([1, 2, 3, 0], vm.FloppyDriveRows.Select(r => r.DriveIndex));
        // Drives 2/3 (rows 0/1, DriveIndex 1/2) are empty — the config only ever populated index 0.
        Assert.Equal(40, vm.FloppyDriveRows[0].Capacity); // default, never touched
        Assert.Equal(40, vm.FloppyDriveRows[1].Capacity); // default, never touched
        Assert.Equal(80, vm.FloppyDriveRows[3].Capacity); // the actual index-0 drive, last row
        Assert.Equal(DiskSides.Double, vm.FloppyDriveRows[3].Sides);

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
            // DriveIndex 1, not 0 — milestone 14d: internal index 0 is unaddressed by any real
            // boot path, so "1 drive" (this window's single-drive shape) always targets index 1.
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 1, Capacity = 40, Sides = DiskSides.Double } },
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

        Assert.NotNull(runner.Machine.Fdc?.GetDisk(1)); // really mounted, not just previewed
        // Mounted regardless, using the CONFIGURED geometry (never the mismatched file's own) —
        // same "nothing here blocks a mount" rule DskImage.Mount always applies (machine ms.20d).
        Assert.Equal(40, runner.Machine.Fdc!.GetDisk(1)!.Tracks);
        Assert.Equal(tempPath, vm.FloppyDriveRows[0].ImagePath); // read back from the real mount
        Assert.NotNull(mismatchDrive);
        Assert.Equal(1, mismatchDrive!.DriveIndex);
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
            // "1 drive" now targets index 1, not 0 (milestone 14d).
            Assert.Equal(1, raisedDrive!.DriveIndex);
            Assert.Equal(DiskGeometryMismatchKind.Candidate, raisedMismatch!.Value.Kind);
        }
        finally
        {
            File.Delete(tempPath);
        }

        runner.Dispose();
    }
}

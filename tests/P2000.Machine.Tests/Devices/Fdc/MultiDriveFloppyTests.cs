using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;

namespace P2000.Machine.Tests.Devices.Fdc;

/// <summary>
/// Machine-layer multi-drive floppy subsystem tests (project CLAUDE.md §13 milestone 20;
/// reference doc §5d's confirmed 4-position <c>DRISEL0</c>-<c>3</c> connector). The chip
/// (<see cref="Upd765"/>) already modelled 4 independent drives since milestone 19
/// (<c>Upd765Tests</c> covers its single-drive command behaviour) — these tests cover what's
/// actually new: <see cref="MachineConfig.FloppyDrives"/>'s config validation and multi-drive
/// wiring through <see cref="Machine"/>.
/// </summary>
public class MultiDriveFloppyTests
{
    private static byte[] BuildSyntheticImage(int tracks, int sides)
    {
        var image = new byte[tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector];
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    // ---- Config validation (test (a)) -----------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FloppyRamBoard_AcceptsUpToFourDrives(int driveCount)
    {
        var drives = new FloppyDriveConfig[driveCount];
        for (var i = 0; i < driveCount; i++) drives[i] = new FloppyDriveConfig { DriveIndex = i };

        var machine = new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = drives,
        });

        Assert.NotNull(machine.Fdc);
    }

    [Fact]
    public void FloppyRamBoard_MoreThanFourDrives_Throws()
    {
        var drives = new[]
        {
            new FloppyDriveConfig { DriveIndex = 0 },
            new FloppyDriveConfig { DriveIndex = 1 },
            new FloppyDriveConfig { DriveIndex = 2 },
            new FloppyDriveConfig { DriveIndex = 3 },
            new FloppyDriveConfig { DriveIndex = 0 }, // 5th entry — over the connector's ceiling
        };
        Assert.Throws<ArgumentException>(() => new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = drives,
        }));
    }

    [Fact]
    public void FloppyRamBoard_DuplicateDriveIndex_Throws()
    {
        var drives = new[]
        {
            new FloppyDriveConfig { DriveIndex = 1 },
            new FloppyDriveConfig { DriveIndex = 1 },
        };
        Assert.Throws<ArgumentException>(() => new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = drives,
        }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void FloppyRamBoard_DriveIndexOutOfRange_Throws(int badIndex)
    {
        var drives = new[] { new FloppyDriveConfig { DriveIndex = badIndex } };
        Assert.Throws<ArgumentException>(() => new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = drives,
        }));
    }

    // ---- Multi-drive mount from config ----------------------------------------------------

    [Fact]
    public void Machine_MountsEachConfiguredDriveAtItsOwnIndex()
    {
        var tempA = Path.GetTempFileName();
        var tempB = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempA, BuildSyntheticImage(tracks: 40, sides: 1));
            File.WriteAllBytes(tempB, BuildSyntheticImage(tracks: 80, sides: 2));

            var machine = new Machine(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[]
                {
                    new FloppyDriveConfig { DriveIndex = 0, ImagePath = tempA },
                    new FloppyDriveConfig { DriveIndex = 2, ImagePath = tempB },
                },
            });

            Assert.NotNull(machine.Fdc!.GetDisk(0));
            Assert.Null(machine.Fdc.GetDisk(1)); // not configured — present-but-empty is out of scope for "mount"
            Assert.NotNull(machine.Fdc.GetDisk(2));
            Assert.Null(machine.Fdc.GetDisk(3));

            Assert.Equal(40, machine.Fdc.GetDisk(0)!.Tracks);
            Assert.Equal(1, machine.Fdc.GetDisk(0)!.Sides);
            Assert.Equal(80, machine.Fdc.GetDisk(2)!.Tracks);
            Assert.Equal(2, machine.Fdc.GetDisk(2)!.Sides);
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void Machine_DisabledDrive_IsNeverMounted_EvenWithImagePathSet()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, BuildSyntheticImage(tracks: 40, sides: 1));
            var machine = new Machine(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[]
                {
                    new FloppyDriveConfig { DriveIndex = 1, Enabled = false, ImagePath = temp },
                },
            });

            Assert.Null(machine.Fdc!.GetDisk(1));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Machine_EnabledDriveWithNoImagePath_IsPresentButEmpty()
    {
        // The widened no-op rule (project CLAUDE.md §13.20): a configured/enabled drive with no
        // image mounted resolves like an absent drive — no exception, no special state.
        var machine = new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 1 } },
        });

        Assert.Null(machine.Fdc!.GetDisk(1));
    }

    // ---- No cross-talk between drives (test (b)) ------------------------------------------

    [Fact]
    public void TwoDrives_SeekIndependently_NoCrossTalkInCylinderTracking()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, DskImage.CreateBlank(40, 2));
        fdc.MountDisk(1, DskImage.CreateBlank(40, 2));

        // SEEK drive 0 to cylinder 5.
        fdc.WriteData(0x0F);
        fdc.WriteData(0x00);
        fdc.WriteData(0x05);
        for (var i = 0; i < 300; i++) fdc.Tick();
        fdc.WriteData(0x08); // SENSE INTERRUPT STATUS
        Assert.Equal(0x20, fdc.ReadData()); // ST0: seek-end | unit 0
        Assert.Equal(0x05, fdc.ReadData()); // PCN

        // SEEK drive 1 to cylinder 12 — must not disturb drive 0's already-tracked cylinder.
        fdc.WriteData(0x0F);
        fdc.WriteData(0x01);
        fdc.WriteData(0x0C);
        for (var i = 0; i < 300; i++) fdc.Tick();
        fdc.WriteData(0x08);
        Assert.Equal(0x21, fdc.ReadData()); // ST0: seek-end | unit 1
        Assert.Equal(0x0C, fdc.ReadData()); // PCN: drive 1's own cylinder

        // A READ DATA on drive 0 must address drive 0's cylinder (5), not drive 1's (12).
        var disk0 = fdc.GetDisk(0)!;
        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = (byte)(i + 1);
        disk0.WriteSector(5, 0, 1, pattern);

        fdc.WriteData(0x42); // READ DATA
        fdc.WriteData(0x00); // unit 0
        fdc.WriteData(0x00); // cylinder byte (not consulted — head physically at 5)
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[256];
        for (var i = 0; i < 256; i++) read[i] = fdc.ReadData();
        Assert.Equal(pattern, read);
    }

    // ---- Geometry auto-detect per drive, independent of the others (test (c)) -------------

    [Fact]
    public void GeometryAutoDetect_IsIndependentPerDrive()
    {
        var fdc = new Upd765();
        fdc.MountDisk(0, new DskImage(BuildSyntheticImage(tracks: 35, sides: 1)));
        fdc.MountDisk(1, new DskImage(BuildSyntheticImage(tracks: 80, sides: 2)));

        Assert.Equal(35, fdc.GetDisk(0)!.Tracks);
        Assert.Equal(1, fdc.GetDisk(0)!.Sides);
        Assert.Equal(80, fdc.GetDisk(1)!.Tracks);
        Assert.Equal(2, fdc.GetDisk(1)!.Sides);
    }

    // ---- Write-protect gates only the targeted drive (test (d)) ---------------------------

    [Fact]
    public void WriteProtect_OnOneDrive_BlocksOnlyThatDrivesWriteDataCommand()
    {
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        var protectedDisk = DskImage.CreateBlank(40, 2);
        protectedDisk.WriteProtected = true;
        var writableDisk = DskImage.CreateBlank(40, 2);
        fdc.MountDisk(0, protectedDisk);
        fdc.MountDisk(1, writableDisk);

        void WriteDataToDrive(int unit)
        {
            fdc.WriteData(0x45);
            fdc.WriteData((byte)unit);
            fdc.WriteData(0x00);
            fdc.WriteData(0x00);
            fdc.WriteData(0x01);
            fdc.WriteData(0x01);
            fdc.WriteData(0x01);
            fdc.WriteData(0x00);
            fdc.WriteData(0x00);
            for (var i = 0; i < 256; i++) fdc.WriteData(0xAA);
        }

        WriteDataToDrive(0);
        foreach (var b in protectedDisk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);

        WriteDataToDrive(1);
        foreach (var b in writableDisk.ReadSector(0, 0, 1)) Assert.Equal(0xAA, b);
    }

    // ---- Create-blank + Save round trip (test (h)) -----------------------------------------

    [Fact]
    public void CreateBlank_GuestWrite_ThenSave_RoundTripsByteIdentical_OnReload()
    {
        // A genuinely blank image has no label (project CLAUDE.md §13.20) — reloading its raw
        // bytes through the label-sniffing constructor only makes sense once a guest DOS has
        // formatted it (written the geometry label as part of formatting), same as real
        // hardware. Simulate that one-time format step directly (label bytes only, docs/
        // JWSDOS-format.md §3) before the guest write this test actually cares about.
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        var formatted = disk.GetBytes();
        formatted[0x0FEF] = (byte)'D';
        formatted[0x0FFF] = 40 + 1;
        disk = new DskImage(formatted);

        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = (byte)(i * 7 + 3);
        disk.WriteSector(4, 0, 2, pattern);

        var saved = disk.GetBytes();
        var reloaded = new DskImage(saved);

        Assert.Equal(pattern, reloaded.ReadSector(4, 0, 2).ToArray());
    }

    [Fact]
    public void CreateBlank_GuestWrite_EjectWithoutSaving_DiscardsInMemoryChanges()
    {
        // The buffered-write regression guard mirroring the cassette's own (project CLAUDE.md
        // §13.20's write-model bullet): ejecting before an explicit Save/GetBytes discards the
        // in-memory changes — EjectDisk drops the reference entirely, so a fresh mount of the
        // same geometry starts genuinely blank, not from whatever was written before the eject.
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        fdc.MountDisk(0, disk);
        disk.WriteSector(4, 0, 2, Enumerable.Repeat((byte)0xAA, 256).ToArray());

        fdc.EjectDisk(0);
        Assert.Null(fdc.GetDisk(0));

        fdc.MountDisk(0, DskImage.CreateBlank(tracks: 40, sides: 2));
        foreach (var b in fdc.GetDisk(0)!.ReadSector(4, 0, 2)) Assert.Equal(0x00, b);
    }
}

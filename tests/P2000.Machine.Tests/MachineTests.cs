using P2000.Machine.Contention;
using P2000.Machine.Debug;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Io;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests;

public class MachineTests
{
    [Fact]
    public void DefaultConfig_IsBare()
    {
        var config = new MachineConfig();

        Assert.Equal(MachineModel.P2000T, config.Model);
        Assert.Equal(InternalBoard.None, config.Board);
    }

    [Fact]
    public void Constructor_WithNoConfig_UsesBareDefault()
    {
        var machine = new Machine();

        Assert.Equal(MachineModel.P2000T, machine.Config.Model);
        Assert.Equal(InternalBoard.None, machine.Config.Board);
    }

    [Fact]
    public void Tick_AdvancesCpuOverManyTStatesWithoutThrowing()
    {
        var machine = new Machine();

        for (var i = 0; i < 50_000; i++)
        {
            machine.Tick();
        }
    }

    [Fact]
    public void Tick_ExecutesRomCode_ThroughThePageTable()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x42,       // LD A, 0x42
            0x06, 0x07,       // LD B, 0x07
            0x21, 0x00, 0x60, // LD HL, 0x6000
            0x77,             // LD (HL), A
            0x76,             // HALT
        });

        for (var i = 0; i < 60; i++)
        {
            machine.Tick();
        }

        Assert.Equal(0x42, machine.Cpu.Reg.A);
        Assert.Equal(0x07, machine.Cpu.Reg.B);
        Assert.Equal(0x6000, machine.Cpu.Reg.HL);
        Assert.Equal(0x42, machine.Memory.Read(0x6000));
    }

    [Fact]
    public void Reset_ReturnsCpuToPowerOnState()
    {
        var machine = new Machine();

        for (var i = 0; i < 1000; i++)
        {
            machine.Tick();
        }

        machine.Reset();

        Assert.Equal(0, machine.Cpu.Reg.PC);
        Assert.False(machine.Cpu.Reg.IFF1);
        Assert.False(machine.Cpu.Reg.IFF2);
    }

    [Fact]
    public void Reset_ClearsTheCpOutLatch()
    {
        var machine = new Machine();
        machine.CpOut.Write(0xFF);

        machine.Reset();

        Assert.Equal(0x00, machine.CpOut.Current);
    }

    // ---- I/O port dispatch, through the tick loop (milestone 4) --------------------------

    [Fact]
    public void Tick_OutTo0x10_ReachesTheCpOutLatch()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x55, // LD A, 0x55
            0xD3, 0x10, // OUT (0x10), A
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        Assert.Equal(0x55, machine.CpOut.Current);
    }

    [Fact]
    public void Tick_InFrom0x20_ReturnsCassetteStatus_BareMachineDefault()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xDB, 0x20, // IN A, (0x20)
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        // CIP+BET+WEN (0x38): no cassette (CIP active-low), tape-OK sense, WEN pulled high
        // by real MDCR hardware when no cassette is present (cas_Init rejects CIP=1 WEN=0).
        // MdcrDevice contributes bits 3–7; CprinReader (printer-deferred) contributes 0.
        Assert.Equal(0x38, machine.Cpu.Reg.A);
    }

    [Fact]
    public void Tick_InFromUnregisteredPort_ReadsOpenBus()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xDB, 0x50, // IN A, (0x50) - not a registered port
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        Assert.Equal(PortDispatch.OpenBus, machine.Cpu.Reg.A);
    }

    [Fact]
    public void Tick_OutTo0x94_SelectsBankThroughThePortDispatch()
    {
        var machine = new Machine(new MachineConfig { RamVariant = RamVariant.T102 });
        machine.Memory.SelectBank(1);
        machine.Memory.Write(PageTable.BankedWindowStart, 0xEE); // seed bank 1 directly
        machine.Memory.SelectBank(0);

        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01, // LD A, 0x01
            0xD3, 0x94, // OUT (0x94), A - selects bank 1 through the port dispatch
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        Assert.Equal(0xEE, machine.Memory.Read(PageTable.BankedWindowStart));
    }

    // ---- Bug-investigation regression: JWSDOS-activation hypothesis (CLAUDE.md §17,
    // 2026-07-30) — the bank-select device must respond to 0x94 ONLY, never 0x95-0x97 (the
    // M2200 RAM-disk ports JWSDOS's own init_ramdisk probes and which have no registered
    // listener in this project, per project CLAUDE.md §14 Deferred). -----------------------

    [Theory]
    [InlineData((byte)0x95)]
    [InlineData((byte)0x96)]
    [InlineData((byte)0x97)]
    public void Tick_OutToPort95To97_DoesNotAffectTheActiveBank_OnT102Card(byte port)
    {
        var machine = new Machine(new MachineConfig { RamVariant = RamVariant.T102 });
        machine.Memory.SelectBank(1);
        machine.Memory.Write(PageTable.BankedWindowStart, 0xEE); // seed bank 1
        machine.Memory.SelectBank(0);
        machine.Memory.Write(PageTable.BankedWindowStart, 0x11); // seed bank 0 differently

        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01, // LD A, 0x01
            0xD3, port, // OUT (port), A - 0x95/0x96/0x97, no registered listener
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        // Bank 0 must still be live-active: if the bank-select device over-listened on this
        // port, this OUT would have silently switched to bank 1 (or gone open-bus).
        Assert.Equal(0x11, machine.Memory.Read(PageTable.BankedWindowStart));
    }

    [Theory]
    [InlineData((byte)0x95)]
    [InlineData((byte)0x96)]
    [InlineData((byte)0x97)]
    public void Tick_OutToPort95To97_DoesNotAffectTheActiveBank_OnHomebrewCard(byte port)
    {
        var machine = new Machine(new MachineConfig { BankCount = 3 });
        machine.Memory.SelectBank(1);
        machine.Memory.Write(PageTable.BankedWindowStart, 0xEE); // seed bank 1
        machine.Memory.SelectBank(0);
        machine.Memory.Write(PageTable.BankedWindowStart, 0x11); // seed bank 0 differently

        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01, // LD A, 0x01
            0xD3, port, // OUT (port), A - 0x95/0x96/0x97, no registered listener
            0x76,       // HALT
        });

        for (var i = 0; i < 30; i++)
        {
            machine.Tick();
        }

        Assert.Equal(0x11, machine.Memory.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void Tick_OutTo0x94_StillWorksExactlyAsBefore_AlongsideInertPorts95To97()
    {
        // Regression guard: confirms the 0x95-0x97 inertness above isn't accidentally
        // achieved by breaking 0x94 itself.
        var machine = new Machine(new MachineConfig { RamVariant = RamVariant.T102 });
        machine.Memory.SelectBank(1);
        machine.Memory.Write(PageTable.BankedWindowStart, 0xEE);
        machine.Memory.SelectBank(0);

        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01, // LD A, 0x01
            0xD3, 0x95, // OUT (0x95), A - inert
            0xD3, 0x96, // OUT (0x96), A - inert
            0xD3, 0x97, // OUT (0x97), A - inert
            0xD3, 0x94, // OUT (0x94), A - selects bank 1 for real
            0x76,       // HALT
        });

        for (var i = 0; i < 80; i++)
        {
            machine.Tick();
        }

        Assert.Equal(0xEE, machine.Memory.Read(PageTable.BankedWindowStart));
    }

    // ---- Video wiring (milestone 5) --------------------------------------------------------

    [Fact]
    public void Tick_DrivesTheVideoDevice_AlongsideTheCpu()
    {
        var machine = new Machine();
        machine.Memory.Write(PageTable.VideoRamStart, (byte)'@');

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            machine.Tick();
        }

        // A frame's worth of master ticks must have completed and swapped in a rendered
        // front buffer - not still all-zero (Video's own tests pin the exact pixel values).
        Assert.Contains(machine.Video.Framebuffer, pixel => pixel != 0);
    }

    [Fact]
    public void Reset_FillsTheVideoFramebufferWithTheBlankingColor()
    {
        // Not literally zero since project CLAUDE.md §17 (2026-07-23): blanking margins fill
        // with a very dark grey (Video.BlankingColor), not pure black.
        var machine = new Machine();
        machine.Memory.Write(PageTable.VideoRamStart, (byte)'@');
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            machine.Tick();
        }

        machine.Reset();

        Assert.All(machine.Video.Framebuffer, pixel => Assert.Equal(Video.BlankingColor, pixel));
    }

    // ---- Keyboard wiring (milestone 8) --------------------------------------------

    /// <summary>
    /// A ROM that sets KBIEN=0 and reads port 0 must see 0xFF when no key is pressed.
    /// </summary>
    [Fact]
    public void Tick_KeyboardRead_NoPressedKey_Returns0xFF()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xF3,       // DI
            0x3E, 0x00, // LD A, 0x00  (KBIEN=0)
            0xD3, 0x10, // OUT (0x10), A
            0xDB, 0x00, // IN A, (0x00)  — keyboard row 0
            0x76,       // HALT
        });

        for (var i = 0; i < 60; i++) machine.Tick();

        Assert.Equal(0xFF, machine.Cpu.Reg.A);
    }

    /// <summary>
    /// A pressed key in row 0 col 2 must clear bit 2 of the port-0 read result (active-low).
    /// </summary>
    [Fact]
    public void Tick_KeyboardRead_PressedKey_BitCleared()
    {
        var machine = new Machine();
        machine.Keyboard.SetKey(row: 0, col: 2, pressed: true);
        machine.Memory.LoadRom(new byte[]
        {
            0xF3,       // DI
            0x3E, 0x00, // LD A, 0x00  (KBIEN=0)
            0xD3, 0x10, // OUT (0x10), A
            0xDB, 0x00, // IN A, (0x00)
            0x76,       // HALT
        });

        for (var i = 0; i < 60; i++) machine.Tick();

        Assert.Equal(0, machine.Cpu.Reg.A & (1 << 2)); // bit 2 cleared
        Assert.NotEqual(0xFF, machine.Cpu.Reg.A);
    }

    /// <summary>
    /// KBIEN=1 (scan ON): port 0 returns 0xFF when no key is down.
    /// </summary>
    [Fact]
    public void Tick_KeyboardRead_KbienOn_NoKey_Port0Returns0xFF()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xF3,       // DI
            0x3E, 0x40, // LD A, 0x40  (KBIEN=1)
            0xD3, 0x10, // OUT (0x10), A
            0xDB, 0x00, // IN A, (0x00)
            0x76,       // HALT
        });

        for (var i = 0; i < 60; i++) machine.Tick();

        Assert.Equal(0xFF, machine.Cpu.Reg.A);
    }

    /// <summary>
    /// KBIEN=1: port 0 returns non-0xFF when any key is pressed anywhere in the matrix.
    /// </summary>
    [Fact]
    public void Tick_KeyboardRead_KbienOn_AnyKey_Port0NonFF()
    {
        var machine = new Machine();
        machine.Keyboard.SetKey(row: 7, col: 5, pressed: true); // arbitrary key
        machine.Memory.LoadRom(new byte[]
        {
            0xF3,       // DI
            0x3E, 0x40, // LD A, 0x40  (KBIEN=1)
            0xD3, 0x10, // OUT (0x10), A
            0xDB, 0x00, // IN A, (0x00)  — AND of all rows
            0x76,       // HALT
        });

        for (var i = 0; i < 60; i++) machine.Tick();

        Assert.NotEqual(0xFF, machine.Cpu.Reg.A);
    }

    [Fact]
    public void Reset_ClearsKeyboardMatrix()
    {
        var machine = new Machine();
        machine.Keyboard.SetKey(0, 0, pressed: true);

        machine.Reset();

        Assert.False(machine.Keyboard.IsKeyPressed(0, 0));
    }

    // ---- RAM power-on fill (project CLAUDE.md §17, 2026-07-21/22 finding) -------------------

    [Fact]
    public void Constructor_FillsRamWithNonZeroContent_NotZeroInitialized()
    {
        var machine = new Machine();
        Assert.NotEqual(0x00, machine.Memory.Read(PageTable.BaseRamStart));
    }

    [Fact]
    public void Constructor_NoExplicitSeed_IsDeterministic_AcrossSeparateMachines()
    {
        var a = new Machine();
        var b = new Machine();

        Assert.Equal(a.Memory.Read(PageTable.BaseRamStart), b.Memory.Read(PageTable.BaseRamStart));
    }

    [Fact]
    public void Constructor_ExplicitRamSeed_OverridesDefault_AndIsReproducible()
    {
        var a = new Machine(new MachineConfig { RamSeed = 0xABCDEF });
        var b = new Machine(new MachineConfig { RamSeed = 0xABCDEF });
        var withDefault = new Machine();

        Assert.Equal(a.Memory.Read(PageTable.BaseRamStart), b.Memory.Read(PageTable.BaseRamStart));
        Assert.NotEqual(withDefault.Memory.Read(PageTable.BaseRamStart), a.Memory.Read(PageTable.BaseRamStart));
    }

    [Fact]
    public void ColdResetCommand_ExplicitSeed_OverridesConfigSeed()
    {
        var configSeeded = new Machine(new MachineConfig { RamSeed = 0x1111 });
        configSeeded.Enqueue(new ColdResetCommand(RamSeed: 0x2222));
        configSeeded.Tick();

        var directlySeeded = new Machine(new MachineConfig { RamSeed = 0x2222 });

        Assert.Equal(directlySeeded.Memory.Read(PageTable.BaseRamStart),
            configSeeded.Memory.Read(PageTable.BaseRamStart));
    }

    // ---- Cassette config-seeded mount (project CLAUDE.md milestone 20b; reference doc §3a
    // "RESOLVED — cassette gets the same treatment, not left asymmetric") ---------------------

    [Fact]
    public void Constructor_NullCassettePath_StaysBare()
    {
        var machine = new Machine();

        Assert.False(machine.Mdcr.HasTape);
        Assert.Equal(0x10, machine.Mdcr.ReadStatus() & 0x10); // CIP set (no cassette)
    }

    [Fact]
    public void Constructor_CassettePath_MountsTapeAtConstruction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cassette-config-seed-{Guid.NewGuid():N}.cas");
        File.WriteAllBytes(path, new byte[1280]); // one blank/unformatted .cas record
        try
        {
            var machine = new Machine(new MachineConfig { CassettePath = path });

            Assert.True(machine.Mdcr.HasTape);
            Assert.Equal(0x00, machine.Mdcr.ReadStatus() & 0x10); // CIP clear (cassette present)
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_CassettePath_LiveSwapStillWorksAfterConfigSeededMount()
    {
        // The runtime mount/eject/swap capability is untouched on top of a config-seeded
        // mount (reference doc §3a) — eject and re-insert must both still work live.
        var path = Path.Combine(Path.GetTempPath(), $"cassette-config-seed-{Guid.NewGuid():N}.cas");
        File.WriteAllBytes(path, new byte[1280]);
        try
        {
            var machine = new Machine(new MachineConfig { CassettePath = path });

            machine.Mdcr.EjectTape();
            Assert.False(machine.Mdcr.HasTape);

            machine.Mdcr.InsertTape(new byte[1280]);
            Assert.True(machine.Mdcr.HasTape);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- CaptureCurrentConfig (project CLAUDE.md milestone 20c; reference doc §3a "RESOLVED —
    // startup configuration") ------------------------------------------------------------------

    private static string TempCasPath() =>
        Path.Combine(Path.GetTempPath(), $"capture-test-{Guid.NewGuid():N}.cas");

    private static string TempDskPath(int tracks = 40, int sides = 1)
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture-test-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(path, DskImage.CreateBlank(tracks, sides).GetBytes());
        return path;
    }

    [Fact]
    public void CaptureCurrentConfig_BareMachine_ReturnsEquivalentOfItsOwnConfig()
    {
        var machine = new Machine();

        var captured = machine.CaptureCurrentConfig();

        Assert.Equal(machine.Config.Model, captured.Model);
        Assert.Equal(machine.Config.Board, captured.Board);
        Assert.Equal(machine.Config.RamVariant, captured.RamVariant);
        Assert.Empty(captured.FloppyDrives);
        Assert.Null(captured.CassettePath);
    }

    [Fact]
    public void CaptureCurrentConfig_DiskMountedLive_ReflectsLiveMount_NotTheStaleConstructionConfig()
    {
        var originalDiskPath = TempDskPath();
        var liveDiskPath = TempDskPath();
        try
        {
            var machine = new Machine(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0, ImagePath = originalDiskPath } },
            });

            // Live-swap drive 0's image — the runtime-swap capability §3a already locks in.
            machine.Fdc!.MountDisk(0, new DskImage(liveDiskPath));

            var captured = machine.CaptureCurrentConfig();

            Assert.Equal(liveDiskPath, captured.FloppyDrives[0].ImagePath);
            // The ORIGINAL config object is untouched — this is what makes it stale.
            Assert.Equal(originalDiskPath, machine.Config.FloppyDrives[0].ImagePath);
        }
        finally
        {
            File.Delete(originalDiskPath);
            File.Delete(liveDiskPath);
        }
    }

    [Fact]
    public void CaptureCurrentConfig_CassetteMountedLive_ReflectsLiveMount_NotTheStaleConstructionConfig()
    {
        var originalCasPath = TempCasPath();
        var liveCasPath = TempCasPath();
        try
        {
            File.WriteAllBytes(originalCasPath, new byte[1280]);
            File.WriteAllBytes(liveCasPath, new byte[1280]);

            var machine = new Machine(new MachineConfig { CassettePath = originalCasPath });

            // Live-swap the cassette — same runtime exception as disk.
            machine.Mdcr.InsertTape(File.ReadAllBytes(liveCasPath), liveCasPath);

            var captured = machine.CaptureCurrentConfig();

            Assert.Equal(liveCasPath, captured.CassettePath);
            Assert.Equal(originalCasPath, machine.Config.CassettePath);
        }
        finally
        {
            File.Delete(originalCasPath);
            File.Delete(liveCasPath);
        }
    }

    [Fact]
    public void CaptureCurrentConfig_SlotRamBoardFields_AlwaysEchoTheOriginalConfig()
    {
        var machine = new Machine(new MachineConfig
        {
            RamVariant = RamVariant.T54,
            BankCount = 3,
            RamSeed = 0xABCDEF,
        });

        var captured = machine.CaptureCurrentConfig();

        Assert.Equal(machine.Config.Model, captured.Model);
        Assert.Equal(machine.Config.RamVariant, captured.RamVariant);
        Assert.Equal(machine.Config.BankCount, captured.BankCount);
        Assert.Equal(machine.Config.MonitorRomPath, captured.MonitorRomPath);
        Assert.Equal(machine.Config.Slot1CartridgePath, captured.Slot1CartridgePath);
        Assert.Equal(machine.Config.RamSeed, captured.RamSeed);
    }

    [Fact]
    public void CaptureCurrentConfig_FedBackIntoNewMachine_MountsTheSameMedia()
    {
        var diskPath = TempDskPath();
        var casPath = TempCasPath();
        try
        {
            File.WriteAllBytes(casPath, new byte[1280]);

            var original = new Machine(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                CassettePath = casPath,
                FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0, ImagePath = diskPath } },
            });

            var captured = original.CaptureCurrentConfig();
            var restored = new Machine(captured);

            Assert.True(restored.Mdcr.HasTape);
            Assert.NotNull(restored.Fdc!.GetDisk(0));
        }
        finally
        {
            File.Delete(diskPath);
            File.Delete(casPath);
        }
    }
}

using P2000.Machine.Contention;
using P2000.Machine.Debug;
using P2000.Machine.Devices;
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
}

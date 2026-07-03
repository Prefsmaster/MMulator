using P2000.Machine.Contention;
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
    public void Tick_InFrom0x20_ReadsTheCprinReader_BareMachineDefault()
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

        Assert.Equal(0x30, machine.Cpu.Reg.A); // CIP+BET asserted: no cassette, "tape ok"
    }

    [Fact]
    public void Tick_InFromUnregisteredPort_ReadsOpenBus()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0xDB, 0x05, // IN A, (0x05) - keyboard row port, not wired until milestone 8
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

        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            machine.Tick();
        }

        // A frame's worth of master ticks must have completed and swapped in a rendered
        // front buffer - not still all-zero (Video's own tests pin the exact pixel values).
        Assert.Contains(machine.Video.FrontBuffer, pixel => pixel != 0);
    }

    [Fact]
    public void Reset_ClearsTheVideoFrontBuffer()
    {
        var machine = new Machine();
        machine.Memory.Write(PageTable.VideoRamStart, (byte)'@');
        for (var i = 0; i < VideoFetchUnit.TStatesPerFrame; i++)
        {
            machine.Tick();
        }

        machine.Reset();

        Assert.All(machine.Video.FrontBuffer, pixel => Assert.Equal(0u, pixel));
    }
}

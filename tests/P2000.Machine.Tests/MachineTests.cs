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
}

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

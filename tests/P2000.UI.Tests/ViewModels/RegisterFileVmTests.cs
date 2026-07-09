using P2000.Machine;
using P2000.Machine.Debug;
using P2000.UI.ViewModels;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.Tests.ViewModels;

/// <summary>Tests for <see cref="RegisterFileVm"/> (milestone 9).</summary>
public class RegisterFileVmTests
{
    private static MachineCore MakeRunningMachine()
    {
        var m = new MachineCore(new MachineConfig());
        // Advance to the first instruction boundary so TakeSnapshot works.
        while (!m.Cpu.AtInstructionBoundary) m.Tick();
        return m;
    }

    [Fact]
    public void InitialState_HasNoSnapshot()
    {
        var vm = new RegisterFileVm();
        Assert.False(vm.HasSnapshot);
        Assert.Equal("–", vm.AF);
        Assert.Equal("–", vm.PC);
        Assert.Equal("–", vm.FlagsText);
    }

    [Fact]
    public void Update_SetsHasSnapshotTrue()
    {
        var m  = MakeRunningMachine();
        var vm = new RegisterFileVm();
        vm.Update(m.TakeSnapshot());
        Assert.True(vm.HasSnapshot);
    }

    [Fact]
    public void Update_PC_ReflectsSnapshotPc()
    {
        var m  = MakeRunningMachine();
        var vm = new RegisterFileVm();
        var snap = m.TakeSnapshot();
        vm.Update(snap);
        Assert.Equal($"{snap.PC:X4}", vm.PC);
    }

    [Fact]
    public void Update_FlagsText_IsEightChars()
    {
        var m  = MakeRunningMachine();
        var vm = new RegisterFileVm();
        vm.Update(m.TakeSnapshot());
        // FlagsText = "SZYHXPNCv" — one char per flag, all uppercase or lowercase
        Assert.Equal(8, vm.FlagsText.Length);
        Assert.True(vm.FlagsText.All(c => char.IsLetter(c)));
    }

    [Fact]
    public void Clear_ResetsToNoSnapshot()
    {
        var m  = MakeRunningMachine();
        var vm = new RegisterFileVm();
        vm.Update(m.TakeSnapshot());
        Assert.True(vm.HasSnapshot);

        vm.Clear();

        Assert.False(vm.HasSnapshot);
        Assert.Equal("–", vm.AF);
        Assert.Equal("–", vm.PC);
    }

    [Fact]
    public void Update_AF_Equals_A_HighByte_F_LowByte()
    {
        var m = MakeRunningMachine();
        var snap = m.TakeSnapshot();
        var vm = new RegisterFileVm();
        vm.Update(snap);

        Assert.Equal($"{snap.AF:X4}", vm.AF);
        // A is high byte, F is low byte
        Assert.Equal($"{snap.A:X2}", vm.AF[..2]);
        Assert.Equal($"{snap.F:X2}", vm.AF[2..]);
    }
}

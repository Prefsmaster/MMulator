using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>Tests for <see cref="VramWindowVm"/> (milestone 9).</summary>
public class VramWindowVmTests
{
    [Fact]
    public void InitialVramData_Is1920Bytes_AllZero()
    {
        var vm = new VramWindowVm();
        Assert.Equal(80 * 24, vm.VramData.Length);
        Assert.All(vm.VramData, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Update_ReadsVramFrom0x5000()
    {
        var vm = new VramWindowVm();
        vm.Update(addr => (byte)(addr & 0xFF), panX: 0, corruption: null);

        // Cell at VRAM[0] = readMemory(0x5000) = 0x00
        Assert.Equal(0x00, vm.VramData[0]);
        // Cell at VRAM[1] = readMemory(0x5001) = 0x01
        Assert.Equal(0x01, vm.VramData[1]);
        // Cell at VRAM[79] = readMemory(0x504F) = 0x4F
        Assert.Equal(0x4F, vm.VramData[79]);
    }

    [Fact]
    public void Update_PanX_IsStored()
    {
        var vm = new VramWindowVm();
        vm.Update(_ => 0, panX: 20, corruption: null);
        Assert.Equal(20, vm.PanX);
    }

    [Fact]
    public void Update_NullCorruption_YieldsZeroArray()
    {
        var vm = new VramWindowVm();
        vm.Update(_ => 0, panX: 0, corruption: null);
        Assert.Equal(40 * 24, vm.Corruption.Length);
        Assert.All(vm.Corruption, b => Assert.False(b));
    }

    [Fact]
    public void Update_CorruptionSnapshot_IsCopied()
    {
        var vm = new VramWindowVm();
        var src = new bool[40 * 24];
        src[5] = true;

        vm.Update(_ => 0, panX: 0, corruption: src);

        Assert.True(vm.Corruption[5]);
        // Must be a copy, not the same reference
        Assert.NotSame(src, vm.Corruption);
    }

    [Fact]
    public void ToggleHex_FlipsShowHex()
    {
        var vm = new VramWindowVm();
        Assert.False(vm.ShowHex);

        vm.ToggleHexCommand.Execute(null);
        Assert.True(vm.ShowHex);

        vm.ToggleHexCommand.Execute(null);
        Assert.False(vm.ShowHex);
    }

    [Fact]
    public void Update_ReplacesArrayReference_SoBindingDetectsChange()
    {
        var vm = new VramWindowVm();
        var first = vm.VramData;

        vm.Update(_ => 0, panX: 0, corruption: null);
        var second = vm.VramData;

        Assert.NotSame(first, second);
    }
}

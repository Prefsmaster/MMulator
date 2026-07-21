using P2000.UI.Rendering;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="DisplayWindowVm"/>'s display-mode default and the new (2026-07-22)
/// Full-Field/Graphics-window crop toggle (project CLAUDE.md §8/§18).
/// </summary>
public class DisplayWindowVmTests
{
    [Fact]
    public void DisplayMode_DefaultsToOddOnly()
    {
        using var vm = new DisplayWindowVm();
        Assert.Equal(DisplayMode.OddOnly, vm.DisplayMode);
        Assert.True(vm.IsModeOddOnly);
        Assert.False(vm.IsModeInterlaced);
    }

    [Fact]
    public void Crop_DefaultsToGraphicsWindow()
    {
        using var vm = new DisplayWindowVm();
        Assert.Equal(DisplayCrop.GraphicsWindow, vm.Crop);
        Assert.True(vm.IsCropGraphicsWindow);
        Assert.False(vm.IsCropFullField);
        Assert.True(vm.CanTogglePalAspect);
    }

    [Fact]
    public void SetCropCommand_SwitchesToFullField_UpdatesComputedBools()
    {
        using var vm = new DisplayWindowVm();

        vm.SetCropCommand.Execute(DisplayCrop.FullField);

        Assert.Equal(DisplayCrop.FullField, vm.Crop);
        Assert.True(vm.IsCropFullField);
        Assert.False(vm.IsCropGraphicsWindow);
    }

    [Fact]
    public void FullFieldCrop_DisablesPalAspectToggle()
    {
        using var vm = new DisplayWindowVm();

        vm.SetCropCommand.Execute(DisplayCrop.FullField);

        Assert.False(vm.CanTogglePalAspect);
    }

    [Fact]
    public void SwitchingBackToGraphicsWindow_ReenablesPalAspectToggle()
    {
        using var vm = new DisplayWindowVm();

        vm.SetCropCommand.Execute(DisplayCrop.FullField);
        vm.SetCropCommand.Execute(DisplayCrop.GraphicsWindow);

        Assert.True(vm.CanTogglePalAspect);
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using P2000.UI.ViewModels;
using P2000.UI.Views;

namespace P2000.UI.Tests.Views;

/// <summary>
/// Regression coverage for the menu-bar consolidation (project CLAUDE.md milestone 14h,
/// 2026-07-27): <c>Config</c>/<c>Debug</c>/<c>Input</c>/<c>Disk</c> — each a top-level menu
/// gating exactly one child command — collapsed into a single <c>Windows</c> menu. This is a
/// pure UI reorganization: every relocated command must still call the exact same
/// <see cref="DisplayWindowVm"/> command it did under its old top-level menu, and
/// <c>Cassette</c>'s existing three items must be untouched.
/// </summary>
public class MenuBarTests
{
    private static (DisplayWindowVm Vm, Menu MainMenu) CreateShownWindow()
    {
        var vm = new DisplayWindowVm();
        var window = new DisplayWindow { DataContext = vm };
        window.Show();
        var menu = window.FindControl<Menu>("MainMenu");
        Assert.NotNull(menu);
        return (vm, menu!);
    }

    private static List<MenuItem> TopLevelItems(Menu menu) => menu.Items.OfType<MenuItem>().ToList();

    private static List<MenuItem> SubItems(MenuItem item) => item.Items.OfType<MenuItem>().ToList();

    /// <summary>Leading "_X" access-key convention used throughout this menu — extracts the
    /// uppercase mnemonic letter from a Header string, e.g. "_Machine configuration…" -> 'M'.</summary>
    private static char Mnemonic(string? header)
    {
        Assert.NotNull(header);
        var i = header!.IndexOf('_');
        Assert.True(i >= 0 && i + 1 < header.Length, $"Header '{header}' has no access-key marker");
        return char.ToUpperInvariant(header[i + 1]);
    }

    [AvaloniaFact]
    public void TopLevel_HasExactlyFourItemsInOrder_MachineCassetteViewWindows()
    {
        var (_, menu) = CreateShownWindow();
        var headers = TopLevelItems(menu).Select(i => i.Header as string).ToList();

        Assert.Equal(new[] { "_Machine", "_Cassette", "_View", "_Windows" }, headers);
    }

    [AvaloniaFact]
    public void WindowsMenu_HoldsTheFourRelocatedCommands_InOrder_SameCommandInstancesAsBefore()
    {
        var (vm, menu) = CreateShownWindow();
        var windowsMenu = TopLevelItems(menu).Single(i => (string?)i.Header == "_Windows");
        var items = SubItems(windowsMenu);

        Assert.Equal(
            new[] { "_Machine configuration…", "_Debugger…", "_Keyboard…", "_Floppy Drives…" },
            items.Select(i => i.Header as string));

        // Regression guard: pure move, zero functional change — each item must still invoke the
        // exact same DisplayWindowVm command it did under its old (now-removed) top-level menu.
        Assert.Same(vm.OpenConfigCommand, items[0].Command);
        Assert.Same(vm.OpenDebuggerCommand, items[1].Command);
        Assert.Same(vm.OpenKeyboardCommand, items[2].Command);
        Assert.Same(vm.OpenDiskDrivesCommand, items[3].Command);
    }

    [AvaloniaFact]
    public void CassetteMenu_IsUnchanged_ThreeItemsSameCommands()
    {
        var (vm, menu) = CreateShownWindow();
        var cassetteMenu = TopLevelItems(menu).Single(i => (string?)i.Header == "_Cassette");
        var items = SubItems(cassetteMenu);

        Assert.Equal(
            new[] { "_Open Deck window", "_Mount…", "_Eject" },
            items.Select(i => i.Header as string));

        Assert.Same(vm.OpenCassetteDeckCommand, items[0].Command);
        Assert.Same(vm.CassetteVm.MountCommand, items[1].Command);
        Assert.Same(vm.CassetteVm.EjectCommand, items[2].Command);
    }

    [AvaloniaFact]
    public void Mnemonics_NoCollisionAtTopLevel_OrWithinWindowsSubmenu()
    {
        var (_, menu) = CreateShownWindow();

        var topLevelMnemonics = TopLevelItems(menu).Select(i => Mnemonic(i.Header as string)).ToList();
        Assert.Equal(topLevelMnemonics.Distinct().Count(), topLevelMnemonics.Count);
        Assert.Equal(new[] { 'M', 'C', 'V', 'W' }, topLevelMnemonics);

        var windowsMenu = TopLevelItems(menu).Single(i => (string?)i.Header == "_Windows");
        var subMnemonics = SubItems(windowsMenu).Select(i => Mnemonic(i.Header as string)).ToList();
        Assert.Equal(subMnemonics.Distinct().Count(), subMnemonics.Count);
        Assert.Equal(new[] { 'M', 'D', 'K', 'F' }, subMnemonics);
    }
}

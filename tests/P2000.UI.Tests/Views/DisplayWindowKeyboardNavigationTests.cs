using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using P2000.UI.Input;
using P2000.UI.ViewModels;
using P2000.UI.Views;

namespace P2000.UI.Tests.Views;

/// <summary>
/// Regression coverage for the menu keyboard-navigation bug (project CLAUDE.md milestone 14i,
/// owner-found 2026-07-27): <c>DisplayWindow.OnPreviewKeyDown</c>/<c>OnPreviewKeyUp</c> are
/// Tunnel-routed handlers on the Window itself — the root of the visual tree — so they always ran
/// before Avalonia's own menu-mnemonic dispatch (<c>AccessKeyHandler.OnKeyDown</c>) and arrow-key
/// navigation (<c>Menu.KeyDown</c> / <c>DefaultMenuInteractionHandler</c>), both plain Bubble
/// handlers with no <c>handledEventsToo</c>. Every P2000 matrix key the menu also needs (arrows,
/// M/C/V/W/D/K/F, Enter) used to be marked Handled unconditionally, so those handlers never saw
/// it. Alt itself isn't a P2000 key, so the mnemonic underlines always worked — only the keys
/// after that were swallowed, matching the reported repro exactly. Fixed by yielding whenever
/// <c>MainMenu.IsOpen</c> is true.
///
/// These use <c>Avalonia.Headless</c>'s real <c>KeyPress</c>/<c>KeyRelease</c> simulation, so key
/// events travel through the actual Tunnel-then-Bubble routed pipeline and hit
/// <c>DisplayWindow</c>'s real production handlers — not a direct call into either handler.
/// <c>Menu.IsOpen</c> is driven via the same public <c>Open()</c>/<c>Close()</c> API
/// <c>AccessKeyHandler</c> itself calls (confirmed by reading Avalonia 11.1.0's own source), so
/// this is real menu state, not a fake. What these do NOT attempt is asserting on Avalonia's own
/// downstream selection/mnemonic-activation visuals (<c>SelectedIndex</c>,
/// <c>MenuItem.IsSubMenuOpen</c>) — those need a fully themed/templated control tree
/// (<c>AccessKeyHandler.Register</c> is only ever called from the <c>AccessText</c> template
/// part), which this project's minimal <c>TestApp</c> deliberately doesn't carry (adding
/// <c>FluentTheme</c> there was tried and made unrelated tests across the suite flaky — out of
/// scope for this bug fix). The regression these tests guard is unambiguous either way: whether
/// <c>DisplayWindow</c>'s own capture swallows the keystroke before Avalonia's menu machinery
/// ever sees it, which is exactly what <c>KeyTranslator.MatrixEvent</c> firing or not proves.
/// </summary>
public class DisplayWindowKeyboardNavigationTests
{
    /// <summary>Closes the window and disposes the VM (stops its background <c>EmulationRunner</c>
    /// thread) on <see cref="Dispose"/> — same cleanup <c>DisplayWindowTests</c>' own
    /// <c>using var vm = …</c> does, needed here too since every test in this file constructs one.</summary>
    private sealed class Fixture(DisplayWindowVm vm, DisplayWindow window, Menu mainMenu) : IDisposable
    {
        public DisplayWindowVm Vm { get; } = vm;
        public DisplayWindow Window { get; } = window;
        public Menu MainMenu { get; } = mainMenu;

        public static Fixture Create()
        {
            var vm = new DisplayWindowVm();
            var window = new DisplayWindow { DataContext = vm };
            window.Show();
            var menu = window.FindControl<Menu>("MainMenu");
            Assert.NotNull(menu);
            return new Fixture(vm, window, menu!);
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
        }
    }

    private static List<(int Row, int Col, bool Pressed)> TrackMatrixEvents(DisplayWindowVm vm)
    {
        var events = new List<(int Row, int Col, bool Pressed)>();
        vm.KeyTranslator.MatrixEvent += (r, c, p) => events.Add((r, c, p));
        return events;
    }

    private static void Press(DisplayWindow window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(key, modifiers, physicalKey, keySymbol: null!);

    private static void Release(DisplayWindow window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyRelease(key, modifiers, physicalKey, keySymbol: null!);

    /// <summary>(a) Regression guard: with the menu closed, ordinary key presses still reach the
    /// emulated keyboard matrix exactly as before this milestone.</summary>
    [AvaloniaFact]
    public void MenuClosed_OrdinaryKeyStillReachesTheEmulatedMatrix()
    {
        using var f = Fixture.Create();
        Assert.False(f.MainMenu.IsOpen);
        var events = TrackMatrixEvents(f.Vm);

        Press(f.Window, Key.A, PhysicalKey.A);

        var expected = KeyMap.Map(Key.A)!.Value;
        Assert.Contains(events, e => e.Row == expected.Row && e.Col == expected.Col && e.Pressed);
    }

    /// <summary>(b) With the menu open, an arrow key — also a P2000 matrix key — must NOT reach
    /// the emulated matrix, leaving it free for Avalonia's own arrow-key menu navigation.</summary>
    [AvaloniaFact]
    public void MenuOpen_ArrowKey_DoesNotReachTheMatrix()
    {
        using var f = Fixture.Create();
        var events = TrackMatrixEvents(f.Vm);

        f.MainMenu.Open(); // same public API AccessKeyHandler itself calls on Alt-release
        Assert.True(f.MainMenu.IsOpen);

        Press(f.Window, Key.Right, PhysicalKey.ArrowRight);

        var rightMatrixPos = KeyMap.Map(Key.Right)!.Value;
        Assert.DoesNotContain(events, e => e.Row == rightMatrixPos.Row && e.Col == rightMatrixPos.Col);
    }

    /// <summary>(c) With the menu open, a mnemonic letter — also a P2000 matrix key (here "W",
    /// the Windows menu's mnemonic) — must likewise NOT reach the emulated matrix, leaving it
    /// free for Avalonia's own access-key dispatch.</summary>
    [AvaloniaFact]
    public void MenuOpen_MnemonicLetter_DoesNotReachTheMatrix()
    {
        using var f = Fixture.Create();
        var events = TrackMatrixEvents(f.Vm);

        f.MainMenu.Open();
        Assert.True(f.MainMenu.IsOpen);

        Press(f.Window, Key.W, PhysicalKey.W);

        var wMatrixPos = KeyMap.Map(Key.W)!.Value;
        Assert.DoesNotContain(events, e => e.Row == wMatrixPos.Row && e.Col == wMatrixPos.Col);
    }

    /// <summary>(d) Closing the menu restores normal keyboard-to-emulator routing.</summary>
    [AvaloniaFact]
    public void ClosingTheMenu_RestoresNormalKeyboardRouting()
    {
        using var f = Fixture.Create();
        var events = TrackMatrixEvents(f.Vm);

        f.MainMenu.Open();
        Assert.True(f.MainMenu.IsOpen);

        f.MainMenu.Close();
        Assert.False(f.MainMenu.IsOpen);

        Press(f.Window, Key.A, PhysicalKey.A);
        var expected = KeyMap.Map(Key.A)!.Value;
        Assert.Contains(events, e => e.Row == expected.Row && e.Col == expected.Col && e.Pressed);
    }

    /// <summary>(e) Regression guard for a real bug FOUND in this same fix (owner-reported
    /// 2026-07-28, post-14i): gating <c>OnPreviewKeyUp</c> on <c>MainMenu.IsOpen</c> the same way
    /// as <c>OnPreviewKeyDown</c> can silently drop a key's release if the menu happens to open
    /// while that key is still physically held (e.g. tapping Alt without releasing an already-
    /// pressed key first) — leaking <c>HostKeyTranslator</c>'s forced-Shift bookkeeping
    /// (<c>_activePress</c>/<c>_activeForce</c>/the force-on/off counters) permanently, which then
    /// corrupts a LATER, unrelated keypress landing on the same matrix crosspoint (reported as
    /// Standard-Host <c>'</c>/<c>=</c> spuriously also emitting the base digit <c>7</c>/<c>0</c>
    /// they share a crosspoint with). <c>OnPreviewKeyUp</c> must NOT gate on <c>MainMenu.IsOpen</c>
    /// — only <c>OnPreviewKeyDown</c> should.</summary>
    [AvaloniaFact]
    public void KeyHeldAcrossMenuOpen_StillReleasesCleanly_NoStuckForcedShiftState()
    {
        using var f = Fixture.Create();
        f.Vm.KeyTranslator.Mode = KeyMappingMode.StandardHost;
        var events = TrackMatrixEvents(f.Vm);

        // OemQuotes unshifted needs a forced P2000 Shift (KeyMap's Standard-Host override) and
        // targets (0,6) — the SAME crosspoint as the plain digit key '7'.
        Press(f.Window, Key.OemQuotes, PhysicalKey.Quote);

        // Menu opens while OemQuotes is still physically held (e.g. the user taps Alt with the
        // other hand) — its KeyUp must still reach the translator despite this.
        f.MainMenu.Open();
        Release(f.Window, Key.OemQuotes, PhysicalKey.Quote);
        f.MainMenu.Close();

        events.Clear();

        // A later, unrelated OemQuotes press (menu fully closed again) must produce EXACTLY one
        // forced-shift press + one target press — no leaked state from the release above should
        // suppress the synthetic-Shift assertion this time.
        Press(f.Window, Key.OemQuotes, PhysicalKey.Quote);

        var target = KeyMap.MapStandardHost(Key.OemQuotes, shiftHeld: false)!.Value;
        Assert.Single(events, e => e.Row == target.Row && e.Col == target.Col && e.Pressed);
        // The synthetic-Shift crosspoint (9,0) must also have fired — proof the forced-Shift-ON
        // counter wasn't left non-zero by the earlier swallowed-then-fixed release.
        Assert.Contains(events, e => e.Row == 9 && e.Col == 0 && e.Pressed);
    }
}

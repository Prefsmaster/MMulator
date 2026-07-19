using P2000.UI.Input;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="KeyboardWindowVm"/> (project CLAUDE.md §14.3a test list (c)-(d)): a
/// soft-keyboard click produces the identical matrix event a host keypress for the same key
/// would, sticky Shift latches across a click and releases after exactly one subsequent key, and
/// the mode toggle changes both the translator's mode and the displayed legends.
/// </summary>
public class KeyboardWindowVmTests
{
    private static (KeyboardWindowVm Vm, HostKeyTranslator Translator, List<(int, int, bool)> Events) NewVm()
    {
        var translator = new HostKeyTranslator();
        var events = new List<(int, int, bool)>();
        translator.MatrixEvent += (r, c, p) => events.Add((r, c, p));
        return (new KeyboardWindowVm(translator), translator, events);
    }

    private static SoftKeyVm Find(KeyboardWindowVm vm, int row, int col)
        => vm.Rows.SelectMany(r => r).Concat(vm.Numpad.SelectMany(r => r))
            .Single(k => k.Def.Row == row && k.Def.Col == col);

    // ── (c) soft-keyboard click parity with the equivalent host key ───────────────────

    [Fact]
    public async Task RegularKeyClick_EmitsSamePositionAsHostKeyPress()
    {
        var (vm, _, events) = NewVm();
        var qKey = Find(vm, 0, 3); // 'Q'

        await vm.ActivateAsync(qKey);

        Assert.Equal(KeyMap.Map(Avalonia.Input.Key.Q)!.Value, (events[0].Item1, events[0].Item2));
        Assert.True(events[0].Item3);   // pressed
        Assert.False(events[1].Item3);  // then released
    }

    [Fact]
    public async Task KeyWithNoHostEquivalent_SendsFixedMatrixPositionDirectly()
    {
        var (vm, _, events) = NewVm();
        var envelopeKey = Find(vm, 5, 0); // np-centre/tab/envelope — HostKey is null

        await vm.ActivateAsync(envelopeKey);

        Assert.Equal((5, 0, true), events[0]);
        Assert.Equal((5, 0, false), events[1]);
    }

    // ── (d) sticky Shift: latch on click, release on click-again OR one subsequent key ──

    [Fact]
    public async Task StickyShift_LatchesOnClick_AndReleasesOnSecondClick()
    {
        var (vm, _, events) = NewVm();
        var shift = Find(vm, 9, 0);

        await vm.ActivateAsync(shift);
        Assert.True(shift.IsActive);
        Assert.Equal((9, 0, true), events[^1]);

        await vm.ActivateAsync(shift); // click again to unlatch
        Assert.False(shift.IsActive);
        Assert.Equal((9, 0, false), events[^1]);
    }

    [Fact]
    public async Task StickyShift_LatchesOnClick_AndReleasesAfterOneRegularKey()
    {
        var (vm, _, events) = NewVm();
        var shift = Find(vm, 9, 0);
        var d8 = Find(vm, 6, 6); // '8'

        await vm.ActivateAsync(shift);
        Assert.True(shift.IsActive);

        await vm.ActivateAsync(d8);

        Assert.False(shift.IsActive); // auto-released after the one regular key
        // P2000-Authentic default: shift down, 8's position, release, shift release.
        Assert.Equal(new[] { (9, 0, true), (6, 6, true), (6, 6, false), (9, 0, false) }, events);
    }

    [Fact]
    public async Task StickyCode_IndependentOfStickyShift_BothCanLatchTogether()
    {
        var (vm, _, _) = NewVm();
        var shift = Find(vm, 9, 0);
        var code = Find(vm, 4, 0);

        await vm.ActivateAsync(shift);
        await vm.ActivateAsync(code);

        Assert.True(shift.IsActive);
        Assert.True(code.IsActive);
    }

    // ── Mode toggle ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToggleMode_ChangesTranslatorModeAndDisplayedLegend()
    {
        var (vm, translator, _) = NewVm();
        var d8 = Find(vm, 6, 6); // '8' — P2000 Base='8' Shifted='(' ; Host Shifted='*'

        Assert.Equal("(", d8.ShiftedLabel);

        vm.ToggleModeCommand.Execute(null);

        Assert.Equal(KeyMappingMode.StandardHost, translator.Mode);
        Assert.Equal("*", d8.ShiftedLabel);
    }

    // ── ANSI reshaping in Standard-Host mode (owner-reported 2026-07-19: their host keyboard
    // has a wide left Shift and no key to the left of Z, unlike the P2000T's own ISO-style
    // layout) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Authentic_IsoKeyVisible_ShiftAtOwnWidth()
    {
        var (vm, _, _) = NewVm();
        var isoKey = Find(vm, 3, 2); // "<>" key
        var shift = Find(vm, 9, 0);

        Assert.True(isoKey.IsVisible);
        Assert.Equal(1.75 * 40, shift.PixelWidth);
    }

    [Fact]
    public void StandardHost_IsoKeyHidden_ShiftWidened()
    {
        var (vm, _, _) = NewVm();
        var isoKey = Find(vm, 3, 2);
        var shift = Find(vm, 9, 0);

        vm.ToggleModeCommand.Execute(null);

        Assert.False(isoKey.IsVisible);
        Assert.Equal(2.75 * 40, shift.PixelWidth);
    }

    [Fact]
    public void StandardHost_OtherKeys_KeepTheirOwnWidth()
    {
        // Only the ISO key and left Shift are affected — an unrelated key's width must not change.
        var (vm, _, _) = NewVm();
        var space = Find(vm, 2, 1);
        var before = space.PixelWidth;

        vm.ToggleModeCommand.Execute(null);

        Assert.Equal(before, space.PixelWidth);
    }
}

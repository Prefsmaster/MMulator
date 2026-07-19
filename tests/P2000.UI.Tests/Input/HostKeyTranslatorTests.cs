using Avalonia.Input;
using P2000.UI.Input;

namespace P2000.UI.Tests.Input;

/// <summary>
/// Tests for <see cref="HostKeyTranslator"/> and <see cref="KeyMap"/> (project CLAUDE.md
/// §14.3a). Covers the milestone's own test list (a)-(b): P2000-Authentic is unchanged
/// (regression guard) and Standard-Host produces the literal host-keycap character, including
/// the "force P2000 shift to a different state than the host's own Shift key" cases.
/// </summary>
public class HostKeyTranslatorTests
{
    private static (HostKeyTranslator T, List<(int Row, int Col, bool Pressed)> Events) NewTranslator(KeyMappingMode mode)
    {
        var t = new HostKeyTranslator { Mode = mode };
        var events = new List<(int, int, bool)>();
        t.MatrixEvent += (r, c, p) => events.Add((r, c, p));
        return (t, events);
    }

    // ── (a) P2000-Authentic regression guard ──────────────────────────────────────────

    [Fact]
    public void Authentic_Shift8_PressesPositionalKeyWhileShiftHeld_ProducesParenOnP2000()
    {
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.D8);
        t.KeyUp(Key.D8);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[]
        {
            (9, 0, true),   // Shift down
            (6, 6, true),   // 8's own P2000 position, unshifted-relative — P2000 shift already covers it
            (6, 6, false),
            (9, 0, false),
        }, events);
    }

    [Fact]
    public void Authentic_Shift2_MatchesShift8_SamePattern()
    {
        // Regression guard for the digit-row table already confirmed in §14.3a: Shift+2 -> '"'.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.D2);
        t.KeyUp(Key.D2);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (7, 7, true), (7, 7, false), (9, 0, false) }, events);
    }

    [Fact]
    public void Authentic_UnmappedKey_IsRecognizedFalse_NoEvents()
    {
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);
        bool recognized = t.KeyDown(Key.F5);
        Assert.False(recognized);
        Assert.Empty(events);
    }

    // ── (b) Standard-Host: literal host-keycap character ──────────────────────────────

    [Fact]
    public void StandardHost_Shift8_TargetsDifferentPositionThanAuthentic_ProducesAsterisk()
    {
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.D8);
        t.KeyUp(Key.D8);
        t.KeyUp(Key.LeftShift);

        // (8,7) shifted = '*', not the Authentic (6,6) that gives '('.
        Assert.Equal(new[] { (9, 0, true), (8, 7, true), (8, 7, false), (9, 0, false) }, events);
    }

    [Fact]
    public async Task StandardHost_Shift2_ForcesP2000ShiftOff_ForAtSign()
    {
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);   // host Shift physically down
        t.KeyDown(Key.D2);          // '@' needs the P2000 key UNshifted
        await AwaitForceOffGap();   // the target press is deliberately delayed — see class doc
        t.KeyUp(Key.D2);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[]
        {
            (9, 0, true),   // host Shift pressed
            (9, 0, false),  // forced OFF for '@' — P2000 shift suppressed despite host Shift held
            (6, 7, true),   // '@' position, unshifted (after the force-off gap)
            (6, 7, false),
            (9, 0, true),   // restored to match host Shift, which is still down
            (9, 0, false),  // host Shift finally released
        }, events);
    }

    [Fact]
    public async Task StandardHost_RightShift2_ReleasesTheRightShiftCrosspoint_NotLeft()
    {
        // Regression guard (owner-reported 2026-07-20): typing Shift+2 with the RIGHT Shift key
        // (common when reaching for a left-side key) must release (9,7), not a hardcoded (9,0) —
        // the earlier bug released a position that was never down, leaving (9,7) asserted the
        // whole time, so '@' still read as shifted and produced the umlaut/diaeresis key instead.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.RightShift);
        t.KeyDown(Key.D2);
        await AwaitForceOffGap();
        t.KeyUp(Key.D2);
        t.KeyUp(Key.RightShift);

        Assert.Equal(new[]
        {
            (9, 7, true),   // right Shift pressed
            (9, 7, false),  // forced OFF — releases the crosspoint that's ACTUALLY down
            (6, 7, true),   // '@' position, unshifted (after the force-off gap)
            (6, 7, false),
            (9, 7, true),   // restored
            (9, 7, false),  // right Shift finally released
        }, events);
    }

    /// <summary>Waits longer than <c>HostKeyTranslator</c>'s internal force-off gap (see its
    /// class doc — a real ROM-timing requirement, not an implementation detail to hide) so the
    /// deferred target-key press has landed before the test asserts on it.</summary>
    private static Task AwaitForceOffGap() => Task.Delay(80);

    [Fact]
    public async Task StandardHost_ForceOff_TargetPressIsDeferred_NotImmediate()
    {
        // Confirms the fix itself, not just its eventual outcome: a machine-level diagnostic
        // (owner-reported 2026-07-20) showed the ROM still reads Shift+2 as shifted if the
        // forced-off release and the '@' press land in the same instant — a real field-boundary
        // gap is required. This asserts the press genuinely hasn't fired yet right after
        // KeyDown returns, only after the gap elapses.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.D2);

        Assert.Equal(new[] { (9, 0, true), (9, 0, false) }, events); // shift down, then forced off — no '@' yet

        await AwaitForceOffGap();

        Assert.Equal(new[] { (9, 0, true), (9, 0, false), (6, 7, true) }, events); // '@' has now landed
    }

    [Fact]
    public void StandardHost_PlainEquals_ForcesP2000ShiftOn()
    {
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.OemPlus);   // host '=' key, unshifted — but P2000 needs shift for '='
        t.KeyUp(Key.OemPlus);

        Assert.Equal(new[]
        {
            (9, 0, true),   // forced ON
            (5, 5, true),   // '=' position, shifted
            (5, 5, false),
            (9, 0, false),  // forced shift released
        }, events);
    }

    [Fact]
    public void StandardHost_PlainApostrophe_RedirectsToTheP2000sRealApostrophe()
    {
        // (8,4) unshifted is accent aigu (´), not apostrophe (owner-corrected 2026-07-20) — the
        // P2000's real apostrophe is (0,6) shifted (Shift+7), so Standard-Host must go there,
        // not fall back positionally to (8,4).
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.OemQuotes);
        t.KeyUp(Key.OemQuotes);

        Assert.Equal(new[] { (9, 0, true), (0, 6, true), (0, 6, false), (9, 0, false) }, events);
    }

    [Fact]
    public void StandardHost_Backtick_IsNoOp()
    {
        // Regression guard (2026-07-20): (8,4) does NOT render as accent grave at all — a
        // machine-level test confirmed it renders as ¾ (see P2000.UI/CLAUDE.md §18). The P2000
        // has no way to display a literal backtick anywhere, so Standard-Host is a no-op here,
        // same category as the bracket/arrow finding.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        bool recognized = t.KeyDown(Key.OemTilde);
        t.KeyUp(Key.OemTilde);

        Assert.False(recognized);
        Assert.Empty(events);
    }

    [Fact]
    public void StandardHost_Brackets_AreNoOp()
    {
        // Regression guard (owner-reported 2026-07-20): (7,4) — the position ms.3's photo
        // transcription labeled "] [" — actually renders Left/Right ARROW glyphs on real
        // hardware (confirmed via Saa5050Font.cs), not bracket shapes at all. There is no P2000
        // position that can display a literal '[' or ']', so Standard-Host is a no-op for both,
        // in both shift states. P2000-Authentic mode is unaffected (still sends (7,4)/(6,7),
        // correctly showing arrows — that's genuine, faithful hardware behaviour, not a bug).
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        Assert.False(t.KeyDown(Key.OemOpenBrackets));
        t.KeyUp(Key.OemOpenBrackets);
        Assert.False(t.KeyDown(Key.OemCloseBrackets));
        t.KeyUp(Key.OemCloseBrackets);

        Assert.Empty(events);
    }

    [Fact]
    public void Authentic_Brackets_CorrectlyShowArrows_NotBugged()
    {
        // P2000-Authentic is positional passthrough — sending (6,7)/(7,4) for these host keys
        // is correct even though what's actually displayed is an arrow, not a bracket. This is
        // NOT something to "fix": faithfully reproducing real hardware, including its quirks,
        // is the whole point of Authentic mode.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.OemOpenBrackets);
        t.KeyUp(Key.OemOpenBrackets);
        t.KeyDown(Key.OemCloseBrackets);
        t.KeyUp(Key.OemCloseBrackets);

        Assert.Equal(new[] { (6, 7, true), (6, 7, false), (7, 4, true), (7, 4, false) }, events);
    }

    [Fact]
    public void Authentic_PlainApostropheKey_SendsThePositionalAccentAigu()
    {
        // P2000-Authentic is positional passthrough — it should send whatever the P2000's own
        // key does, which IS the accent aigu, not an apostrophe. Only Standard-Host redirects.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.OemQuotes);
        t.KeyUp(Key.OemQuotes);

        Assert.Equal(new[] { (8, 4, true), (8, 4, false) }, events);
    }

    [Fact]
    public void StandardHost_MissingSymbol_IsNoOp()
    {
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        bool recognized = t.KeyDown(Key.D6);  // Shift+6 = '^', no P2000 equivalent
        t.KeyUp(Key.D6);
        t.KeyUp(Key.LeftShift);

        Assert.False(recognized);
        Assert.Equal(new[] { (9, 0, true), (9, 0, false) }, events);
    }

    [Fact]
    public void StandardHost_UnaffectedKey_FallsBackToPositional()
    {
        // Letters and most punctuation have no override — Standard-Host coincides with Authentic.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);
        t.KeyDown(Key.Q);
        t.KeyUp(Key.Q);
        Assert.Equal(new[] { (0, 3, true), (0, 3, false) }, events);
    }

    [Fact]
    public void KeyDown_Repeat_DoesNotReEmit_ButStillReportsRecognized()
    {
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);
        t.KeyDown(Key.Q);
        events.Clear();
        bool recognized = t.KeyDown(Key.Q); // OS auto-repeat
        Assert.True(recognized);
        Assert.Empty(events);
    }
}

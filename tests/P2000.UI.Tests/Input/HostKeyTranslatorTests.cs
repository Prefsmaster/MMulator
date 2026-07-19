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
    public void StandardHost_ShiftPlus_RedirectsAwayFromOemPlusOwnPosition()
    {
        // OemPlus now positionally sits at (8,4) [¼/¾] (owner-confirmed 2026-07-19), which no
        // longer naturally yields '+' when shifted — must redirect to (8,5) shifted instead.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.OemPlus);
        t.KeyUp(Key.OemPlus);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (8, 5, true), (8, 5, false), (9, 0, false) }, events);
    }

    [Fact]
    public void StandardHost_PlainSemicolon_NeedsNoOverride_PositionalAlreadyCorrect()
    {
        // OemSemicolon now positionally sits at (8,5) [;/+] (owner-confirmed 2026-07-19), whose
        // unshifted value already IS ';' — no override table entry should be needed for this.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.OemSemicolon);
        t.KeyUp(Key.OemSemicolon);

        Assert.Equal(new[] { (8, 5, true), (8, 5, false) }, events);
    }

    [Fact]
    public async Task StandardHost_ShiftSemicolon_RedirectsToColon()
    {
        // (8,7) unshifted = ':' — host holds Shift, P2000 side must not (same force-off pattern
        // as Shift+2/Shift+3, needing the field-boundary gap before the target press).
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.OemSemicolon);
        await AwaitForceOffGap();
        t.KeyUp(Key.OemSemicolon);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (9, 0, false), (8, 7, true), (8, 7, false), (9, 0, true), (9, 0, false) }, events);
    }

    [Fact]
    public void StandardHost_PlainApostrophe_RedirectsToTheP2000sRealApostrophe()
    {
        // OemQuotes (the host "'/\"" key) positionally sits at (8,7) (owner-confirmed via real
        // P2000T hardware, 2026-07-19) — but the P2000's real apostrophe lives at (0,6) shifted
        // (Shift+7), so Standard-Host must redirect there regardless of where OemQuotes sits.
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
    public void Authentic_PlainApostropheKey_SendsThePositionalColonAsterisk()
    {
        // P2000-Authentic is positional passthrough — OemQuotes (the host "'/\"" key, left of
        // Enter) sits over (8,7) [:/*] on a real P2000T (owner-confirmed 2026-07-19), not an
        // apostrophe or accent mark. Only Standard-Host redirects to the literal character.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.OemQuotes);
        t.KeyUp(Key.OemQuotes);

        Assert.Equal(new[] { (8, 7, true), (8, 7, false) }, events);
    }

    [Fact]
    public void Authentic_PlainEqualsKey_SendsThePositionalAccentAigu()
    {
        // OemPlus (the host "=/+" key, left of backspace) sits over (8,4) [¼/¾ — printed as
        // accent aigu/grave on the P2000 keycap] on a real P2000T (owner-confirmed 2026-07-19).
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.OemPlus);
        t.KeyUp(Key.OemPlus);

        Assert.Equal(new[] { (8, 4, true), (8, 4, false) }, events);
    }

    [Fact]
    public void Authentic_Backslash_SendsThePositionalBlockKey()
    {
        // OemPipe (the US "\|" key) has no P2000 position of its own — owner-confirmed
        // (2026-07-19) it's wired to (2,4) [#/block] instead of doing nothing.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.OemPipe);
        t.KeyUp(Key.OemPipe);

        Assert.Equal(new[] { (2, 4, true), (2, 4, false) }, events);
    }

    [Fact]
    public void StandardHost_Backslash_IsNoOp()
    {
        // No P2000 position can display a literal backslash or pipe character — same category
        // as the bracket/tilde findings. OemPipe reaches (2,4) only in P2000-Authentic mode.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        Assert.False(t.KeyDown(Key.OemPipe));
        t.KeyUp(Key.OemPipe);
        t.KeyDown(Key.LeftShift);
        Assert.False(t.KeyDown(Key.OemPipe));
        t.KeyUp(Key.OemPipe);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (9, 0, false) }, events);
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

    // ── PhysicalKey numpad recovery (owner-reported 2026-07-19: Shift+numpad-1 didn't reach
    // ZOEK) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Authentic_ShiftNumpad1ReportedAsEnd_StillReachesZoek()
    {
        // Windows (NumLock ON) reports Key.End instead of Key.NumPad1 when Shift is held while
        // pressing the physical numpad-1 key — PhysicalKey.NumPad1 is scancode-based and reveals
        // the true key regardless, so the translator must still land on ZOEK's position (7,3).
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.End, PhysicalKey.NumPad1);   // what Windows actually reports in this scenario
        t.KeyUp(Key.End, PhysicalKey.NumPad1);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (7, 3, true), (7, 3, false), (9, 0, false) }, events);
    }

    [Fact]
    public void StandardHost_ShiftNumpad1ReportedAsEnd_StillReachesZoek()
    {
        // Same recovery applies in Standard-Host mode — ZOEK has no override (no host ASCII
        // equivalent), so it falls back to positional passthrough same as Authentic.
        var (t, events) = NewTranslator(KeyMappingMode.StandardHost);

        t.KeyDown(Key.LeftShift);
        t.KeyDown(Key.End, PhysicalKey.NumPad1);
        t.KeyUp(Key.End, PhysicalKey.NumPad1);
        t.KeyUp(Key.LeftShift);

        Assert.Equal(new[] { (9, 0, true), (7, 3, true), (7, 3, false), (9, 0, false) }, events);
    }

    [Fact]
    public void Authentic_RealEndKey_UnaffectedByNumpadRecovery()
    {
        // A genuine press of the dedicated End key (PhysicalKey.End, not a numpad scancode) must
        // NOT be swallowed by the numpad-recovery table — it has no P2000 mapping at all.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        bool recognized = t.KeyDown(Key.End, PhysicalKey.End);
        t.KeyUp(Key.End, PhysicalKey.End);

        Assert.False(recognized);
        Assert.Empty(events);
    }

    [Fact]
    public void Authentic_NumpadWithDefaultPhysicalKey_StillWorksPositionally()
    {
        // Soft-keyboard clicks (and any caller that omits PhysicalKey) pass Key.NumPadN directly
        // with no ambiguity to resolve — default(PhysicalKey) must not interfere.
        var (t, events) = NewTranslator(KeyMappingMode.P2000Authentic);

        t.KeyDown(Key.NumPad7);
        t.KeyUp(Key.NumPad7);

        Assert.Equal(new[] { (6, 3, true), (6, 3, false) }, events);
    }
}

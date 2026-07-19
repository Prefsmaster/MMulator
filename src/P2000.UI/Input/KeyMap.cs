using Avalonia.Input;

namespace P2000.UI.Input;

/// <summary>Which convention host keys translate under (project CLAUDE.md §14.3a).
/// P2000-Authentic (default) sends whatever P2000 key sits at the same physical location as
/// the host key — the P2000's own shift-symbol pairing applies. Standard-Host instead
/// reproduces the literal character printed on a standard host keycap, translating to
/// whichever P2000 key/shift-state actually produces that character.</summary>
public enum KeyMappingMode
{
    P2000Authentic,
    StandardHost,
}

/// <summary>
/// Maps Avalonia host keys to P2000T keyboard matrix (row, column) pairs.
/// Row = I/O port 0x00–0x09; column = bit index 0–7 (active-low in the device).
///
/// Source: docs/keyboard/keyboard matrix.md + docs/keyboard/P2000T keyboard.jpg.
/// Layout is Dutch P2000T. Keys with no direct host equivalent (CODE, ZOEK, START,
/// STOP, INL, OPN, numpad-00, etc.) are reachable via the soft-keyboard window (future).
///
/// Mapping mode: POSITIONAL — the host's physical key position maps to the P2000T
/// physical key at the same location. This means the P2000T's SHIFT conventions apply:
/// e.g. Shift+0 = '=', Shift+8 = '('. Symbolic mode (translate characters regardless
/// of shift position) is deferred to a later milestone.
/// </summary>
public static class KeyMap
{
    /// <summary>Returns the (row, col) matrix position for a host key, or null if unmapped.</summary>
    public static (int Row, int Col)? Map(Key key)
        => _map.TryGetValue(key, out var v) ? v : null;

    // ── Matrix: row = port (0x00–0x09), col = bit index (0–7) ──────────────────────
    // Source table column order: bit7 bit6 bit5 bit4 bit3 bit2 bit1 bit0
    //
    // Row 0 (0x00): 4$  7'  5%  3£  Q   ↑/↖  6&   ←/leftTab
    // Row 1 (0x01): F   J   G   D   S   Z    H    Tab
    // Row 2 (0x02): →   ,   ↓/↘ #°  np0 np00 Spc  np,/STOP
    // Row 3 (0x03): V   M   B   C   X   <>   N    ShiftLock
    // Row 4 (0x04): R   U   T   E   W   A    Y    Code
    // Row 5 (0x05): -_  1!  0=  Bsp np_: np+x 9)  np-cntr
    // Row 6 (0x06): @¨  8(  P   Ent np7  np8  O   np9/M
    //   col7 shifted confirmed (2026-07-20) to render as an UP ARROW glyph, not ¨ — see the
    //   row 7 note below; this and row 7/8's findings are the same recurring pattern.
    //   col0 ("np9/M") shifted produces NO visible character at all (confirmed 2026-07-20,
    //   direct machine test) — some silent function, not literally typing 'M'.
    // Row 7 (0x07): 2"  K   /?  ][  np1  np2  .   np3
    //   col4 "] [" — CORRECTION (2026-07-20, owner-reported, confirmed via Saa5050Font.cs):
    //   the P2000 character set reassigns ASCII 0x5B/0x5D to Left-Arrow/Right-Arrow GLYPHS, not
    //   bracket shapes at all. The matrix position is right (this key really is (7,4), and
    //   P2000-Authentic mode correctly shows arrows there — that's genuine hardware behaviour,
    //   not a bug) but there is NO way to display a literal '[' or ']' on this hardware, so
    //   Standard-Host mode now treats both as "no P2000 equivalent" (null) instead of redirecting
    //   here. See P2000.UI/CLAUDE.md §18 for the full font-table cross-reference AND for why an
    //   intermediate "+72 = shifted keycode" theory (derived from BASIC's own keycode-to-ASCII
    //   table at Z80 address 6164) turned out NOT to be universally reliable — e.g. it wrongly
    //   predicted Shift+3 would show '#'; direct machine testing and live confirmation both
    //   show Shift+3 correctly shows '£' (0x23, itself a Saa5050Font.cs remap — British Pound,
    //   not literal '#'). Every correction actually applied in this file was independently
    //   confirmed by a direct machine-level test, never by the table extrapolation alone.
    // Row 8 (0x08): :*  I   ;+  ´`  np4  np5  L   np6
    //   col4 = CONFIRMED (2026-07-20, two independent direct machine-level tests) to render as
    //   ¼ (0x7B) unshifted / ¾ (0x7D) shifted per Saa5050Font.cs, not any accent mark, despite
    //   the keycap being printed accent aigu (´) / accent grave (`). What's still open is WHY,
    //   not WHAT: either (a) the keycap-vs-glyph mismatch is simply real (same category as the
    //   row 7 bracket/arrow finding above), or (b) this is a two-stage dead key whose combined
    //   (accented-letter) output a single isolated keypress can never reveal — every test run
    //   here only ever presses this key alone. The apostrophe redirect two lines below (Standard-
    //   Host `'` → (0,6) shifted) is unaffected either way — that's a different, already-
    //   confirmed position.
    //   col2 ("np5") shifted does not echo a character into the input line, but does trigger
    //   some OTHER screen-level side effect (looks like a redraw touching the top banner row —
    //   confirmed 2026-07-20, not chased further; genuinely unclear what it does, don't guess).
    // Row 9 (0x09): RShift …………………………………………… LShift

    private static readonly Dictionary<Key, (int Row, int Col)> _map = new()
    {
        // ── Row 0 ────────────────────────────────────────────────────────────────────
        { Key.D4,               (0, 7) },   // 4 / $
        { Key.D7,               (0, 6) },   // 7 / '
        { Key.D5,               (0, 5) },   // 5 / %
        { Key.D3,               (0, 4) },   // 3 / £
        { Key.Q,                (0, 3) },   // Q
        { Key.Up,               (0, 2) },   // ↑ cursor up  (also ↖ home when shifted)
        { Key.D6,               (0, 1) },   // 6 / &
        { Key.Left,             (0, 0) },   // ← cursor left  (also left-tab when shifted)

        // ── Row 1 ────────────────────────────────────────────────────────────────────
        { Key.F,                (1, 7) },
        { Key.J,                (1, 6) },
        { Key.G,                (1, 5) },
        { Key.D,                (1, 4) },
        { Key.S,                (1, 3) },
        { Key.Z,                (1, 2) },
        { Key.H,                (1, 1) },
        { Key.Tab,              (1, 0) },

        // ── Row 2 ────────────────────────────────────────────────────────────────────
        { Key.Right,            (2, 7) },   // → cursor right
        { Key.OemComma,         (2, 6) },   // , (also < when shifted on some hosts)
        { Key.Down,             (2, 5) },   // ↓ cursor down  (also ↘ when shifted)
        // col 4 = np 0 / DEF  → NumPad0
        { Key.NumPad0,          (2, 3) },
        // col 3 = np 00 / TB  — no standard host key; reachable via soft keyboard
        { Key.Space,            (2, 1) },
        // col 0 = np , / STOP → NumPad Decimal (numpad .)
        { Key.Decimal,          (2, 0) },

        // ── Row 3 ────────────────────────────────────────────────────────────────────
        { Key.V,                (3, 7) },
        { Key.M,                (3, 6) },
        { Key.B,                (3, 5) },
        { Key.C,                (3, 4) },
        { Key.X,                (3, 3) },
        { Key.OemBackslash,     (3, 2) },   // < / > (ISO extra key left of Z on Dutch layout)
        { Key.N,                (3, 1) },
        { Key.CapsLock,         (3, 0) },   // Shift Lock

        // ── Row 4 ────────────────────────────────────────────────────────────────────
        { Key.R,                (4, 7) },
        { Key.U,                (4, 6) },
        { Key.T,                (4, 5) },
        { Key.E,                (4, 4) },
        { Key.W,                (4, 3) },
        { Key.A,                (4, 2) },
        { Key.Y,                (4, 1) },
        { Key.LeftCtrl,         (4, 0) },   // CODE key

        // ── Row 5 ────────────────────────────────────────────────────────────────────
        { Key.OemMinus,         (5, 7) },   // - / _
        { Key.D1,               (5, 6) },   // 1 / !
        { Key.D0,               (5, 5) },   // 0 / =
        { Key.Back,             (5, 4) },   // Backspace
        { Key.Delete,           (5, 4) },   // also map Delete → Backspace
        // col 3 = np _ / :  → NumPad Subtract (no 1-to-1; map to Subtract)
        { Key.Subtract,         (5, 3) },
        // col 2 = np + / x  → NumPad Add
        { Key.Add,              (5, 2) },
        { Key.D9,               (5, 1) },   // 9 / )
        // col 0 = np centre/tab/envelope — no standard mapping

        // ── Row 6 ────────────────────────────────────────────────────────────────────
        // col 7 = @ / ¨ (umlaut/diaeresis dead key) — on Dutch P2000T the @ key is right of P;
        // on US host OemOpenBrackets ([ key) sits in that position
        { Key.OemOpenBrackets,  (6, 7) },   // @ / ¨ (umlaut)
        { Key.D8,               (6, 6) },   // 8 / (
        { Key.P,                (6, 5) },
        { Key.Return,           (6, 4) },   // Enter
        { Key.NumPad7,          (6, 3) },   // np 7 / tape
        { Key.NumPad8,          (6, 2) },   // np 8 / dsk
        { Key.O,                (6, 1) },
        { Key.NumPad9,          (6, 0) },   // np 9 / M

        // ── Row 7 ────────────────────────────────────────────────────────────────────
        { Key.D2,               (7, 7) },   // 2 / "
        { Key.K,                (7, 6) },
        { Key.OemQuestion,      (7, 5) },   // / / ?
        { Key.OemCloseBrackets, (7, 4) },   // ] / [  (Dutch: ] is right-of-@ area)
        { Key.NumPad1,          (7, 3) },   // np 1 / ZOEK
        { Key.NumPad2,          (7, 2) },   // np 2 / flash
        { Key.OemPeriod,        (7, 1) },   // .
        { Key.NumPad3,          (7, 0) },   // np 3 / START

        // ── Row 8 ────────────────────────────────────────────────────────────────────
        // col 7 = : / *  — on US host : is Shift+; but we treat OemSemicolon as the base key
        // and let the shift state produce : naturally via P2000T shift convention.
        { Key.OemSemicolon,     (8, 7) },   // : / *  (Dutch: these two are the same physical key)
        { Key.I,                (8, 6) },
        // col 5 = ; / +  — on Dutch P2000T ; is a separate key.
        // On US host OemPlus (= key) is nearby; approximate with OemTilde for now.
        { Key.OemPlus,          (8, 5) },   // ; / +  (US = key maps here approximately)
        { Key.OemQuotes,        (8, 4) },   // ´ (accent aigu) / ` (accent grave) — not apostrophe
        { Key.NumPad4,          (8, 3) },   // np 4 / INL
        { Key.NumPad5,          (8, 2) },   // np 5
        { Key.L,                (8, 1) },
        { Key.NumPad6,          (8, 0) },   // np 6 / OPN

        // ── Row 9 (shift keys only) ───────────────────────────────────────────────────
        { Key.RightShift,       (9, 7) },
        { Key.LeftShift,        (9, 0) },
    };

    /// <summary>A P2000 matrix position plus whether P2000 Shift must be held for the target
    /// character to appear (used by Standard-Host mode — see <see cref="MapStandardHost"/>).</summary>
    public readonly record struct MatrixTarget(int Row, int Col, bool NeedsShift);

    // Standard-Host overrides, keyed by (host key, host-Shift-held). Only entries that DIFFER
    // from plain positional passthrough are listed — letters, most digits, and most
    // punctuation already coincide (the P2000 key at that position already produces the same
    // character a standard host keycap would). A `null` value means no P2000 key produces
    // that character at all — Standard-Host mode is a no-op for it rather than guessing a
    // wrong stand-in (owner-decided 2026-07-19).
    // Source: docs/Keyboard/keyboard mappins.md "Standard-Host mode reverse mapping".
    private static readonly Dictionary<(Key Key, bool Shift), MatrixTarget?> _standardHostOverrides = new()
    {
        // ` / ~ — no confirmed P2000 equivalent (see class-level note below on the (8,4) find).
        { (Key.OemTilde, false), null },   // `
        { (Key.OemTilde, true),  null },   // ~

        { (Key.D2, true), new MatrixTarget(6, 7, false) },   // @  (positional gives ¨ instead)
        { (Key.D3, true), new MatrixTarget(2, 4, false) },   // #  (positional gives ° instead)
        { (Key.D6, true), null },                            // ^  — no P2000 equivalent (confirmed:
                                                              // (6,7) shifted renders as an UP ARROW
                                                              // glyph, not '^' — see font-table note)
        { (Key.D7, true), new MatrixTarget(0, 1, true) },    // &  (positional gives ' instead)
        { (Key.D8, true), new MatrixTarget(8, 7, true) },    // *  (positional gives ( instead)
        { (Key.D9, true), new MatrixTarget(6, 6, true) },    // (  (positional gives ) instead)
        { (Key.D0, true), new MatrixTarget(5, 1, true) },    // )  (positional gives = instead)

        { (Key.OemPlus, false), new MatrixTarget(5, 5, true) },   // =  (positional gives ; instead)

        // [ / ] / { / } — NO P2000 equivalent at all (owner-reported 2026-07-20, confirmed via
        // Saa5050Font.cs: the P2000's character set reassigns ASCII 0x5B/0x5D to Left/Right
        // Arrow glyphs, not bracket shapes — (7,4), the position ms.3's photo transcription
        // labeled "] [", genuinely cannot display a literal bracket on real hardware. This is
        // NOT a translator bug: P2000-Authentic mode showing arrows for these keys is correct,
        // faithful emulation. Standard-Host has nothing to redirect to.
        { (Key.OemOpenBrackets, false), null },   // [
        { (Key.OemOpenBrackets, true),  null },   // {
        { (Key.OemCloseBrackets, false), null },  // ]
        { (Key.OemCloseBrackets, true), null },   // }

        { (Key.OemSemicolon, false), new MatrixTarget(8, 5, false) },  // ;  (positional gives : instead)
        { (Key.OemSemicolon, true),  new MatrixTarget(8, 7, false) },  // :  (positional gives * instead)

        { (Key.OemQuotes, true),  new MatrixTarget(7, 7, true) },  // "  (positional (8,4) shifted
                                                                    // renders as ¾, not " — see the
                                                                    // (8,4) open question above)
        // ' — (8,4) unshifted does NOT render as a literal apostrophe (renders as ¼ — see the
        // (8,4) open question above); the P2000's actual apostrophe is (0,6) shifted (Shift+7,
        // part of the original confirmed digit-row table), unaffected by that open question.
        { (Key.OemQuotes, false), new MatrixTarget(0, 6, true) },

        { (Key.OemComma,  true), new MatrixTarget(3, 2, false) },  // <  (positional gives , unshifted twice)
        { (Key.OemPeriod, true), new MatrixTarget(3, 2, true) },   // >  (positional gives . unshifted twice)
    };

    /// <summary>Standard-Host mode: translate a host key + current host-Shift state to the
    /// P2000 matrix position (and required P2000 shift state) that reproduces the SAME literal
    /// character a standard host keyboard shows for that key. Falls back to the positional
    /// mapping for any key with no override (the common case). Returns null if no P2000 key
    /// produces the character at all.</summary>
    public static MatrixTarget? MapStandardHost(Key key, bool shiftHeld)
    {
        if (_standardHostOverrides.TryGetValue((key, shiftHeld), out var overridden))
            return overridden;

        var positional = Map(key);
        return positional is { } p ? new MatrixTarget(p.Row, p.Col, shiftHeld) : null;
    }
}

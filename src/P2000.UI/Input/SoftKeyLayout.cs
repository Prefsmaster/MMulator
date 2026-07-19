using Avalonia.Input;

namespace P2000.UI.Input;

/// <summary>One physical key on the P2000T keyboard, positioned as it appears on the real
/// device (project CLAUDE.md §14.3a soft-keyboard window). Source: <c>docs/Keyboard/P2000T
/// keyboard.jpg</c> (owner-supplied photo, verified via zoomed crops 2026-07-19) cross-checked
/// against <c>docs/Keyboard/keyboard matrix.md</c>.
///
/// <c>Base</c>/<c>Shifted</c> are the P2000-Authentic legend (silk-screened on the real key).
/// <c>HostBase</c>/<c>HostShifted</c> are the legend Standard-Host mode should display instead
/// (the literal symbol a standard host keycap shows) — null means it coincides with
/// <c>Base</c>/<c>Shifted</c>, so no override is needed. <c>HostKey</c> is the Avalonia key this
/// soft-key is equivalent to for translation via <see cref="HostKeyTranslator"/> — null for the
/// P2000 positions with no host-keyboard equivalent at all (np00/TB, the envelope key, and the
/// "#/°" key — none of ms.3's shipped `KeyMap` entries reach these three), which always send
/// their fixed matrix position regardless of mode.
///
/// <c>BaseIcon</c>/<c>ShiftedIcon</c> optionally replace the Base/Shifted TEXT with a small
/// image instead (file name only, no extension — resolved to
/// <c>avares://P2000.UI/Assets/Icons/{name}.png</c>). Icons don't vary by mode: none of the
/// icon-bearing keys have a Standard-Host legend override.
///
/// <c>IsIsoOnly</c> marks a key that exists on the P2000T's own (ISO-style) physical keyboard
/// but not on a standard ANSI host keyboard — hidden in Standard-Host mode so the soft-keyboard
/// can double as a shape-accurate reference for where P2000 functions live on a real host
/// keyboard (owner-reported 2026-07-19: their host has no key there and a wide left Shift
/// instead). <c>StandardHostWidth</c>, when set, overrides <c>Width</c> in Standard-Host mode
/// only — used on the left Shift key to absorb the space the hidden ISO key leaves behind.</summary>
public sealed record SoftKeyDef(
    int Row, int Col,
    string Base, string? Shifted,
    Key? HostKey,
    string? HostBase = null, string? HostShifted = null,
    double Width = 1.0,
    bool IsSticky = false,
    bool IsLock = false,
    string? BaseIcon = null,
    string? ShiftedIcon = null,
    bool IsIsoOnly = false,
    double? StandardHostWidth = null);

/// <summary>The full confirmed P2000T keyboard layout, grouped as visual rows matching the
/// photo (NOT the electrical matrix rows — e.g. the "&lt;&gt;" key is wired to matrix row 3
/// but sits physically on the bottom letter row, exactly as on the real keyboard; the "'`" key
/// is wired to matrix row 8 but sits physically on the top numeric row).</summary>
public static class SoftKeyLayout
{
    public static readonly IReadOnlyList<IReadOnlyList<SoftKeyDef>> Rows = new[]
    {
        // ── Top row ──────────────────────────────────────────────────────────────
        new SoftKeyDef[]
        {
            new(4, 0, "CODE", null, Key.LeftCtrl, Width: 1.5, IsSticky: true),
            new(5, 6, "1", "!", Key.D1),
            new(7, 7, "2", "\"", Key.D2, HostShifted: "@"),
            new(0, 4, "3", "£", Key.D3, HostShifted: "#"),
            new(0, 7, "4", "$", Key.D4),
            new(0, 5, "5", "%", Key.D5),
            new(0, 1, "6", "&", Key.D6, HostShifted: "^"),
            new(0, 6, "7", "'", Key.D7, HostShifted: "&"),
            new(6, 6, "8", "(", Key.D8, HostShifted: "*"),
            new(5, 1, "9", ")", Key.D9, HostShifted: "("),
            new(5, 5, "0", "=", Key.D0, HostShifted: ")"),
            new(5, 7, "-", "_", Key.OemMinus),
            // Legend shows what's PRINTED on the P2000 keycap (accent aigu / accent grave, per
            // the owner's reading of the photo). CONFIRMED (2026-07-20, two independent direct
            // machine-level tests): pressing this key actually renders ¼/¾ on screen, not any
            // accent mark — see the (8,4) note in KeyMap.cs for what's still open (why, not
            // what). Host key CONFIRMED via real P2000T hardware (2026-07-19): this position is
            // reached by the host "=/+" key (left of backspace), not "'/\"" as first mapped.
            new(8, 4, "´", "`", Key.OemPlus, HostBase: "=", HostShifted: "+"),
            new(5, 4, "⌫", null, Key.Back, Width: 1.5),
        },
        // ── Second row ───────────────────────────────────────────────────────────
        new SoftKeyDef[]
        {
            new(1, 0, "TAB", null, Key.Tab, Width: 1.5),
            new(0, 3, "Q", null, Key.Q),
            new(4, 3, "W", null, Key.W),
            new(4, 4, "E", null, Key.E),
            new(4, 7, "R", null, Key.R),
            new(4, 5, "T", null, Key.T),
            new(4, 1, "Y", null, Key.Y),
            new(4, 6, "U", null, Key.U),
            new(8, 6, "I", null, Key.I),
            new(6, 1, "O", null, Key.O),
            new(6, 5, "P", null, Key.P),
            // No HostBase/HostShifted override: Standard-Host has nothing to show for these —
            // the P2000 can't display a literal [ ] { } at all (confirmed 2026-07-20, see
            // KeyMap.cs's Row 7 note) — so both modes show the same P2000-Authentic legend.
            new(6, 7, "@", "¨", Key.OemOpenBrackets),
            new(7, 4, "]", "[", Key.OemCloseBrackets),
            new(6, 4, "⏎", null, Key.Return, Width: 1.5),
        },
        // ── Third row ────────────────────────────────────────────────────────────
        new SoftKeyDef[]
        {
            new(3, 0, "LOCK", null, Key.CapsLock, Width: 1.5, IsLock: true),
            new(4, 2, "A", null, Key.A),
            new(1, 3, "S", null, Key.S),
            new(1, 4, "D", null, Key.D),
            new(1, 7, "F", null, Key.F),
            new(1, 5, "G", null, Key.G),
            new(1, 1, "H", null, Key.H),
            new(1, 6, "J", null, Key.J),
            new(7, 6, "K", null, Key.K),
            new(8, 1, "L", null, Key.L),
            // Host key CONFIRMED via real P2000T hardware (2026-07-19): OemSemicolon itself sits
            // over this position (not OemPlus, as first mapped).
            new(8, 5, ";", "+", Key.OemSemicolon, HostBase: ";", HostShifted: ":"),
            // Host key CONFIRMED via real P2000T hardware (2026-07-19): the host "'/\"" key
            // (left of Enter) sits over this position (not OemSemicolon, as first mapped).
            new(8, 7, ":", "*", Key.OemQuotes, HostBase: "'", HostShifted: "\""),
            // Host key CONFIRMED via real P2000T hardware (2026-07-19): the US "\|" key
            // (OemPipe) has no P2000 position of its own, so it's wired here to give it the
            // #/block function instead of doing nothing.
            new(2, 4, "#", "°", Key.OemPipe, HostBase: "\\", HostShifted: "|"),
        },
        // ── Fourth row ───────────────────────────────────────────────────────────
        new SoftKeyDef[]
        {
            // StandardHostWidth absorbs the "<>" key's Width (1.0) below when it's hidden —
            // 1.75 + 1.0 = 2.75, matching a standard ANSI keyboard's wide left Shift.
            new(9, 0, "SHIFT", null, Key.LeftShift, Width: 1.75, IsSticky: true, StandardHostWidth: 2.75),
            // ISO-only: the P2000T's own keyboard has this key, but a standard ANSI host
            // keyboard doesn't — hidden in Standard-Host mode (owner-reported 2026-07-19).
            new(3, 2, "<", ">", Key.OemBackslash, IsIsoOnly: true),
            new(1, 2, "Z", null, Key.Z),
            new(3, 3, "X", null, Key.X),
            new(3, 4, "C", null, Key.C),
            new(3, 7, "V", null, Key.V),
            new(3, 5, "B", null, Key.B),
            new(3, 1, "N", null, Key.N),
            new(3, 6, "M", null, Key.M),
            new(2, 6, ",", null, Key.OemComma, HostShifted: "<"),
            new(7, 1, ".", null, Key.OemPeriod, HostShifted: ">"),
            new(7, 5, "/", "?", Key.OemQuestion),
            new(9, 7, "SHIFT", null, Key.RightShift, Width: 1.75, IsSticky: true),
        },
        // ── Bottom row ───────────────────────────────────────────────────────────
        new SoftKeyDef[]
        {
            new(0, 0, "⇤", null, Key.Left, Width: 1.5),
            new(0, 2, "↑", null, Key.Up, Width: 1.5, BaseIcon: "home_up"),
            new(2, 1, "SPACE", null, Key.Space, Width: 6.0),
            new(2, 5, "↓", null, Key.Down, Width: 1.5, BaseIcon: "end_down"),
            new(2, 7, "→", null, Key.Right, Width: 1.5),
        },
    };

    /// <summary>The numeric keypad, laid out as its own 3×5 grid (project CLAUDE.md §14.3a —
    /// the keys the milestone originally (and wrongly, see §18 2026-07-19 finding) assumed had
    /// no host equivalent at all; ms.3 already reaches all of these via host NumPad keys except
    /// np00/TB and the envelope key, which exist only here).</summary>
    public static readonly IReadOnlyList<IReadOnlyList<SoftKeyDef>> Numpad = new[]
    {
        new SoftKeyDef[]
        {
            // Confirmed 2026-07-20 via BASIC's own keycode-to-ASCII table (Z80 addr 6164) plus
            // direct machine-level verification: unshifted '-' matches the photo, but shifted
            // is ÷ (divide), not ':' as transcribed — a calculator-style operator pairing with
            // the '+'/'*' key below, not "minus/colon".
            new(5, 3, "-", "÷", Key.Subtract),
            // Same source: shifted is '*' (times), not the letter 'x' — '+'/'*' pairs with '-'/÷
            // above as the numpad's arithmetic-operator row.
            new(5, 2, "+", "*", Key.Add),
            // Owner-corrected via real P2000T hardware (2026-07-19): the envelope icon is this
            // key's SHIFTED function (not base, as first coded) and clears the whole screen (the
            // rectangle gets "x-ed out"); the BASE (unshifted) function is a different symbol —
            // a vertical bar / right-arrow / left-arrow / vertical bar glyph — and clears just
            // the current line, homing the cursor to its leftmost column. "centre-tab" in the
            // raw matrix transcription was this base function, mis-transcribed from the photo.
            // No host key reaches this position at all (still open).
            new(5, 0, "CLR", "CLS", null, BaseIcon: "clear_line", ShiftedIcon: "envelope"),
        },
        new SoftKeyDef[]
        {
            new(6, 3, "7", "WIS", Key.NumPad7, ShiftedIcon: "tape"), // Shift+this key = WIS (clear tape) — owner-confirmed 2026-07-19
            new(6, 2, "8", "dsk", Key.NumPad8, ShiftedIcon: "disk"),
            // "M" is what's printed on the keycap, but pressing Shift+9 produces no visible
            // character at all (confirmed 2026-07-20, direct machine test) — some silent
            // function, not literally typing the letter M. Legend kept as printed; see KeyMap.cs.
            new(6, 0, "9", "M", Key.NumPad9),
        },
        new SoftKeyDef[]
        {
            new(8, 3, "4", "INL", Key.NumPad4),
            new(8, 2, "5", null, Key.NumPad5),
            new(8, 0, "6", "OPN", Key.NumPad6),
        },
        new SoftKeyDef[]
        {
            new(7, 3, "1", "ZOEK", Key.NumPad1),
            new(7, 2, "2", "flash", Key.NumPad2, ShiftedIcon: "flash"),
            new(7, 0, "3", "START", Key.NumPad3),
        },
        new SoftKeyDef[]
        {
            new(2, 3, "0", "DEF", Key.NumPad0),
            new(2, 2, "00", null, null, ShiftedIcon: "flag"), // np00/TB — no host key reaches this at all
            new(2, 0, ",", "STOP", Key.Decimal),
        },
    };
}

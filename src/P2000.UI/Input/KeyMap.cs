using Avalonia.Input;

namespace P2000.UI.Input;

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
    // Row 6 (0x06): @"  8(  P   Ent np7  np8  O   np9/M
    // Row 7 (0x07): 2"  K   /?  ][  np1  np2  .   np3
    // Row 8 (0x08): :*  I   ;+  '`  np4  np5  L   np6
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
        // col 7 = @ / " — on Dutch P2000T the @ key is right of P;
        // on US host OemOpenBrackets ([ key) sits in that position
        { Key.OemOpenBrackets,  (6, 7) },   // @ / "
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
        { Key.OemQuotes,        (8, 4) },   // ' / `
        { Key.NumPad4,          (8, 3) },   // np 4 / INL
        { Key.NumPad5,          (8, 2) },   // np 5
        { Key.L,                (8, 1) },
        { Key.NumPad6,          (8, 0) },   // np 6 / OPN

        // ── Row 9 (shift keys only) ───────────────────────────────────────────────────
        { Key.RightShift,       (9, 7) },
        { Key.LeftShift,        (9, 0) },
    };
}

Keyboard mappings

## Physical P2000T matrix (CONFIRMED — source for `KeyMap.cs`'s positional/P2000-Authentic mode)

| BIT     | 7 | 6 | 5 | 4 | 3 | 2 | 1 | 0 |
| ----------- | ---------- |---------- |---------- |---------- |---------- |---------- |---------- |---------- |
| Port 0x00   | 4 $  |   7 '  |   5 %  |  3 pound | Q   |    up,nw |  6 &  |   lft left-tab |
| Port 0x01   | F    |   J    |   G  |     D   |    S     |  Z   |    H  |     tab |
| Port 0x02   | rt   |   ,    |   dwn se | # degree-symbol |  np 0 def | np 00 tb | space | np , stop |
| Port 0x03   | V    |  M    |   B   |    C  |     X   |   < >  |   N   |    shift-lock |
| Port 0x04   |  R   |    U   |    T  |     E   |    W   |    A    |   Y    |   code   |
| Port 0x05   |  - _ |    1 !  |   0 =  |   backspace |  np -/÷ |    np +/* |   9 )  |   np center-tab envelope |
| Port 0x06   | @ ¨  |  8 (  |   P   |    enter |  np 7 tape/WIS | np 8 dsk | O   |    np 9 (silent) |
| Port 0x07   |  2 " |    K  |     / ?  |   ] [  |   np 1 zoek | np 2 flash | .  |  np 3 start |
| Port 0x08   |  : * |    I  |  ; +  |  ´ ` | np 4 inl | np 5   |   L   |    np 6 opn
| Port 0x09   | right shift key | | | | | | | left shift key |

np = prefix for numpad key. up/nw/lft/rt/dwn/se = arrow directions (up, north-west, left, right, down, south-east).

**Correction (2026-07-19, owner):** Port 0x06 bit 7 shifted is **¨ (umlaut/diaeresis dead key)**,
NOT a double quote. The only key that produces `"` is Port 0x07 bit 7 (the `2 "` key).

**WIS confirmed (2026-07-19, owner):** the WIS function (clear tape — see `P2000T-reference.md`
§5b BASIC↔cassette UI surface) is **Shift + Port 0x06 bit 3** — the numpad `7` key, the one
showing the mini-cassette-tape icon. Same shift-selects-the-function convention as ZOEK/START/
STOP/INL/OPN/DEF elsewhere on the keypad; no new matrix behaviour, just the previously-missing
name for what that key's shifted state does.

---

## Standard-Host mode reverse mapping (derived 2026-07-19, for milestone 3a)

Goal: given a host key produces literal character **X** on a standard US keyboard, find which
P2000 matrix position (and shift state) produces that same **X**, so Standard-Host mode can
translate host input to it — independent of P2000-Authentic/positional mode, which instead
always sends whatever P2000 key sits at the same physical location as the host key.

Letters and the following already need **no remap** — the position ms.3's positional mapping
(`KeyMap.cs`) already uses happens to also be the Symbolic/correct one: `-` `_` `.` `,` `/` `?`
`!` `$` `%` `]` `+`.

### Needs remap (Standard-Host must target a different P2000 position/shift-state than positional)

| Host key | Target char | P2000 position | Shift state | Note |
|---|---|---|---|---|
| `'` | `'` | (0,6) | shifted | (8,4) unshifted does NOT give apostrophe (see below) — the real apostrophe is Shift+7 |
| Shift+2 | `@` | (6,7) | **unshifted** | host holds Shift; P2000 side must NOT |
| Shift+3 | `#` | (2,4) | **unshifted** | host holds Shift; P2000 side must NOT |
| Shift+7 | `&` | (0,1) | shifted | |
| Shift+8 | `*` | (8,7) | shifted | positional gives `(` instead |
| Shift+9 | `(` | (6,6) | shifted | |
| Shift+0 | `)` | (5,1) | shifted | |
| `=` | `=` | (5,5) | shifted (add) | host key unshifted, P2000 side needs shift |
| `;` | `;` | (8,5) | **unshifted** | positional currently gives `:` |
| Shift+; | `:` | (8,7) | **unshifted** | host holds Shift; P2000 side must NOT |
| Shift+, | `<` | (3,2) | **unshifted** | host holds Shift; P2000 side must NOT |
| Shift+. | `>` | (3,2) | shifted | |
| Shift+' | `"` | (7,7) | shifted | CONFIRMED (7,7), not (6,7) — see correction above |

### No P2000 equivalent at all — Standard-Host mode is a no-op for these

`~` (Shift+`` ` ``), `^` (Shift+6), `{` (Shift+[), `}` (Shift+]), `\`, `|` (Shift+\\)
(owner-decided 2026-07-19) — plus, as of 2026-07-20 (see the bracket/arrow finding below):
`` ` `` (plain backtick) and `[` / `]` (plain, unshifted) — the P2000 genuinely cannot display
a literal backtick or bracket character at all, confirmed via the SAA5050 font table, so there
is nothing left to redirect these to.

Pressing any of these host keys in Standard-Host mode enqueues nothing — matches the
milestone's "flag, don't guess" rule rather than picking a wrong stand-in character.

### Ambiguity resolved

Two P2000 positions looked plausible for `"` before the umlaut correction above: (6,7) and
(7,7). (6,7) shifted is actually ¨ (umlaut), not `"` — so (7,7) is the only real candidate,
no ambiguity remains.

### Correction (2026-07-20, owner): accent aigu/grave key

Port 0x08 bit 4 is printed **´ (accent aigu, unshifted) / ` (accent grave, shifted)** on the
real keycap, not apostrophe/backtick as first transcribed — the P2000's real apostrophe is
(0,6) shifted (Shift+7), used in the table above.

**Follow-up (2026-07-20, direct machine-level testing):** pressing (8,4) actually renders **¼
unshifted / ¾ shifted**, not any accent mark at all — see `P2000.UI/CLAUDE.md` §18 for the full
investigation. This means the backtick redirect above (which assumed (8,4) shifted gives a
literal `` ` ``) was ALSO wrong; backtick is now "no P2000 equivalent" (see above), same
category as the bracket/arrow finding below.

### Correction (2026-07-20, owner-reported): brackets are arrows, not brackets

Port 0x07 bit 4 ("] ["), which the Standard-Host table above used to redirect `[` to (shifted),
turns out to render as **Left-Arrow / Right-Arrow glyphs** on real hardware (confirmed via the
SAA5050 font table's own comments) — the P2000 cannot display a literal bracket character at
this position or anywhere else found so far. P2000-Authentic mode showing arrows for `[`/`]` is
correct, faithful emulation, not a bug. Standard-Host now treats both as "no P2000 equivalent."

**Methodology note:** an intermediate theory — that unshifted keycode = row×8+col and shifted
keycode = that + 72, derived from BASIC's own keycode-to-ASCII table at Z80 address 6164 — held
for several cells but is **not universally reliable** (it wrongly predicted Shift+3 would show
`#` instead of the correct `£`). Every correction actually recorded here was confirmed via a
direct machine-level test (real SetKey press against a booted BASIC, VRAM read back), not the
table extrapolation alone.

| BIT     | 7 | 6 | 5 | 4 | 3 | 2 | 1 | 0 |
| ----------- | ---------- |---------- |---------- |---------- |---------- |---------- |---------- |---------- |
| Port 0x00   | 4 $  |   7 '  |   5 %  |  3 pound | Q   |    up,nw |  6 &  |   lft left-tab |
| Port 0x01   | F    |   J    |   G  |     D   |    S     |  Z   |    H  |     tab |
| Port 0x02   | rt   |   ,    |   dwn se | # degree-symbol |  np 0 def | np 00 tb | space | np , stop |
| Port 0x03   | V    |  M    |   B   |    C  |     X   |   < >  |   N   |    shift-lock |
| Port 0x04   |  R   |    U   |    T  |     E   |    W   |    A    |   Y    |   code   |
| Port 0x05   |  - _ |    1 !  |   0 =  |   backspace |  np -/÷ |    np +/* |   9 )  |   np clr-line/envelope(CLS) |
| Port 0x06   | @ ¨  |  8 (  |   P   |    enter |  np 7 tape/WIS | np 8 dsk | O   |    np 9 (silent) |
| Port 0x07   |  2 " |    K  |     / ?  |   ] [  |   np 1 zoek | np 2 flash | .  |  np 3 start |
| Port 0x08   |  : * |    I  |  ; +  |  ´ ` | np 4 inl | np 5   |   L   |    np 6 opn
| Port 0x09   | right shift key | | | | | | | left shift key |


np = prefix for numpad key



up = arrow pointing up

nw = arrow pointing north west

lft = arrow pointing left

rt = arrow pointing right

dwn = arrow pointing down

se = arrow pointing south east

**Correction (2026-07-20, owner):** Port 0x08 bit 4 is **´ (accent aigu, unshifted) / ` (accent
grave, shifted)** — NOT apostrophe/backtick. The P2000's actual apostrophe is Port 0x00 bit 6
shifted (the `7 '` key, Shift+7).

**Corrections (2026-07-20, direct machine-level tests against a real booted BASIC — see
`P2000.UI/CLAUDE.md` §18 for the full investigation, including a cross-check against BASIC's
own keycode-to-ASCII table at Z80 address 6164):**
- Port 0x05 bit 3 ("np _/:") actually shows **`-` unshifted / ÷ (divide) shifted** — pairs with
  bit 2 below as a calculator-style arithmetic-operator row, not "minus/colon".
- Port 0x05 bit 2 ("np +/x") shifted is **`*`** (times), not the letter `x`.
- Port 0x06 bit 0 ("np 9/M") shifted produces **no visible character at all** — a silent
  function, not literally typing `M`.
- Port 0x08 bit 4 (the accent-aigu/grave key above): CONFIRMED (independently, twice) to render
  as **¼ unshifted / ¾ shifted**, not any accent mark. Why it does this (mislabeled key vs. an
  uncombined dead-key placeholder) is still open — see the source-code note.
- Port 0x08 bit 2 ("np 5") shifted also produces no visible character.
- Port 0x02 bit 4 ("# / degree-symbol"): the "#" reading IS correct (confirmed) — the degree
  symbol guess for shifted was never independently verified and the table now shows it renders
  as a solid block glyph instead, though nothing currently depends on that shifted value.
- Port 0x00 bit 4 (3 / £): re-confirmed correct as originally transcribed — an intermediate
  "+72 keycode offset" theory briefly cast doubt on this (predicted '#') but both direct testing
  and live observation confirm Shift+3 genuinely shows £ (a Saa5050 font remap of byte 0x23,
  not literal '#'). That offset theory is NOT universally reliable and should not be trusted
  without independent confirmation for any other cell.

**Real-hardware confirmation (2026-07-19, owner tested a physical P2000T on a monitor):** every
matrix VALUE above at (7,4), (6,7), and (8,4) matches the emulator exactly. What the same test
DID find wrong was which HOST KEY reaches (8,4)/(8,5)/(8,7) on `KeyMap.cs`'s Standard-host-
keyboard side (a wiring bug, not a matrix ground-truth error) — see "keyboard mappins.md"'s
2026-07-19 correction for the fix. It also identified `\`/`|` (`OemPipe`) as a previously-
unmapped host key, now wired to Port 0x02 bit 4 (`#`/block).

**Port 0x05 bit 0 correction (2026-07-19, owner, real hardware):** "np center-tab envelope" is
wrong — this key's SHIFTED function is the envelope icon (rectangle with a cross/X through it),
which clears the whole screen; its UNSHIFTED function is a "|→←|" glyph (vertical bar / right
arrow / left arrow / vertical bar) that clears the current line and homes the cursor to its
leftmost column. "center-tab" was a misread of the unshifted glyph. See "keyboard mappins.md"
for the full note.


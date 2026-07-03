# SAA5050-implementation.md

Implementation guide for the SAA5050 teletext character generator, for the P2000.Machine
**video device (milestone 5)**. Read this when starting that milestone. It distills three
reference implementations and flags where the **P2000T differs from generic teletext**.

Priorities (per project owner): **faithful hardware behaviour AND practical rendering, equally.**

Reference sources (in `docs/` or provided): `MAMESAA5050.c/.h` (MAME, C — clean structural
model), `BeebTeletext.js` (jsbeeb, JS — the rounding + double-buffer approach), `Fn_Saa5050.cs`
+ `FN_SAA5050Data.cs` (the project owner's C# port of the JS, with a hardcoded font). **The C#
port is the closest starting point** — it already renders correctly and is in our language.

---

## 0. The three sources at a glance

| Aspect | MAME (C) | jsbeeb (JS) | Owner's C# port |
|--------|----------|-------------|-----------------|
| Font | external ROM blob (`chargen`) | generated from `teletext_data` | **hardcoded array in `FN_SAA5050Data.cs`** |
| Rounding | **NOT done** (TODO in header) | full diagonal smoothing | full diagonal smoothing (from JS) |
| Glyph storage | reads ROM per pixel | pre-built hi-res glyph tables | pre-built hi-res glyph tables |
| Control codes | set-at (immediate) | set-after (BBC timing) | set-after (from JS) |
| Structure | pin-driven (dew/lose/f1/tr6) | scanline `render()` | scanline `Render()` + `PERender()` |

**Take the C# port's rendering approach** (rounding + pre-built glyphs) **but restructure it
for our needs** (see §6 contention seam, §7 P2000T specifics). MAME is the better reference for
*state semantics* and the pin model; jsbeeb/C# are better for *rounding + palette*.

---

## 1. Geometry & the fundamentals

- Character cell: **6×10** logical pixels, from a 5×9 glyph in a 6×10 box.
- Display: **40 columns × 24 rows** on the P2000T.
- Each logical scanline is **doubled to 2** for interlace → 20 rendered rows/char (the code
  counts 10, renders 20 via the CRS/RA0 odd/even line). → **480 px high** (24 × 20).
- Horizontal: the renderer emits **16 pixel-lanes per character**, NOT a naive 6×2=12. The
  horizontal rounding is computed at sub-pixel resolution: glyph rows are stored **2 bits/pixel**
  packed into a `uint`, and the render loop unrolls the 32-bit `chardef` **16 times** per char
  (both jsbeeb and the C# port agree). → **640 px wide** (40 × 16). **The machine framebuffer is
  therefore 640×480** (CLAUDE.md §3 framebuffer contract — do NOT reduce to 480 wide; that
  discards horizontal smoothing).
- Frame rate 50 Hz PAL (§ reference doc §4a). Flash cadence: ~48-frame counter, on for the
  first 16 (JS/C#) — MAME uses >38 of 50; keep the JS/C# cadence since we adopt that renderer.

---

## 2. Character rounding (diagonal smoothing) — GET THIS RIGHT

This is the most-often-wrong part of teletext emulation, and **MAME does NOT do it** (explicit
TODO). Use the jsbeeb/C# approach.

The SAA5050 doubles vertical resolution and **interpolates diagonals**: when a pixel is empty
but its diagonal neighbours (one horizontal, one vertical) are set, it's filled to smooth
curves. The C# `CombineRows(a,b)` is the exact operation, mirroring the JS:
```
part1 = a | ((a >> 1) & b & ~(b >> 1));
part2 =     ((a << 1) & b & ~(b << 1));
return part1 | part2;
```
- Each output (odd) sub-scanline combines the current row with the **row above or below**
  (`row + (odd ? +1 : -1)`), so diagonals between adjacent character rows round correctly.
- The even sub-scanline is the raw row; the odd one is the smoothed row (selected by CRS/RA0).
- **Rounding also applies to graphics-set characters 64–95** (`@A…Z` etc. that remain
  available in graphics mode) — the C# `MakeHiresGlyphs` handles this via the `(c & 32)` test.
  Do not skip it or those glyphs look wrong in graphics mode.

Build **three pre-computed glyph tables** at init (as C#/JS do): `normalGlyphs`,
`graphicsGlyphs`, `separatedGlyphs`, each `96 × 20` entries of packed 2-bit pixels. This trades
a little memory for a fast per-pixel render.

---

## 3. Control codes (teletext serial attributes)

Codes 0x00–0x1F are **serial attributes** with **set-at vs set-after** semantics — they occupy
a character cell (rendered as space) and change state for *following* cells. The C#/JS use
**set-after** (BBC behaviour): the attribute takes effect from the NEXT cell, so the control
cell itself shows the OLD state. MAME uses set-at (immediate). **CONFIRM which the P2000T's
SAA5050 uses against hardware/ROM output** — teletext spec is set-after, and the P2000T follows
the teletext spec, so set-after (C#/JS) is very likely correct. Flag in findings if boot output
disagrees.

Codes to handle (values are the control byte; see C# `HandleControlCode`):
- **1–7:** alpha (text) + set foreground colour; leaves graphics mode.
- **8/9:** flash / steady.
- **12/13:** normal / double height.
- **17–23:** graphics + set foreground colour; enters graphics mode.
- **24:** conceal (render fg as bg until end of row).
- **25/26:** contiguous / separated graphics.
- **28/29:** black background / new background (bg := current fg).
- **30/31:** hold graphics / release graphics.
- Colour attributes set the **foreground**; bg only changes on 28/29. New-background latches
  the *current* fg as bg (order matters — a colour code then 29 gives that colour as bg).

### Hold-graphics (the fiddly one)
When "hold graphics" is active, a control code cell displays the **last graphics character**
instead of a space (so attribute changes don't leave gaps in graphics). The C# mirrors JS:
`heldChar`/`heldGlyphs` track the last graphics glyph; on a control code, if `holdChar` and the
double-height state is unchanged, the held glyph is shown (with a 0x40–0x5F → space guard).
`holdOff` (release, code 31) clears it after the cell. Reproduce this exactly — it's where
lazy implementations produce gappy graphics.

---

## 4. Colour & palette

- 3-bit RGB fg + 3-bit RGB bg. The C#/JS build a **256-entry palette** indexed by
  `(bg<<5) | (fg<<2) | pixel2bits`, with **gamma-corrected blending** (gamma 1/2.2) over the
  2-bit anti-aliased pixel coverage — this is what makes rounded pixels look smooth rather than
  hard-edged. Keep the gamma blend; it's part of why the rounded output looks authentic.
- Our renderer targets an Avalonia `WriteableBitmap` (BGRA). The C# `Render()` writes
  `uint` BGRA already (`0xFF<<24 | B<<16 | G<<8 | R`); reuse that path, drop the `PixelEngine`
  (`PERender`) variant — it's specific to the owner's old framework.

---

## 5. State reset points (frame / scanline)

Mirror the pin-driven resets (all three agree on the shape):
- **DEW** (= frame sync / VSYNC, the P2000T's 50 Hz): reset scanline counter to 0, clear
  double-height second-half, advance flash counter. (Reference doc §5f: DEW/R2425 is the same
  50 Hz signal exposed on the internal slot — same signal, two roles.)
- **LOSE** (= DISPTMG / scanline): at end of each scanline reset the per-row attribute state
  (fg=7, bg=0, not graphics/separated/flash/dbl, held char = space), increment scanline
  counter, handle the 10-line char-row boundary + double-height carry.
- Per-cell in `Render()`: latch `previousColor`/`currentGlyphs` BEFORE processing the cell's
  control code (set-after), fetch the glyph row, emit pixels.

### Fields, frames, and CRS (the two-pass interlace — REQUIRED for rounding)
The rounded image is built from **two interlaced fields per frame** (this is the P2000T's real
50 Hz interlaced timing, and it's how the smoothing lands — not optional polish):
- **Even field:** sub-scanlines from y=0, `SetCRS(false)` → the raw rows.
- **Odd field:** sub-scanlines from y=1, `SetCRS(true)` → the **smoothed** rows (CRS/RA0 selects
  the rounding scanline). `SetDEW` fires on the even field to start a new frame.
- So a complete 640×480 rounded image exists only after BOTH fields. The fetch/render loop runs
  two interleaved passes per frame.
- **Timing split (see machine CLAUDE.md §3):** the 50 Hz **interrupt + CTC channel-3 clock fire
  per FIELD**; **present to the UI per FIELD into a single PERSISTENT buffer with NO inter-field
  clear** — this reproduces the interlace **comb** artifact in fast horizontal motion (authentic;
  the owner's deliberate choice). Interlaced/comb is the DEFAULT; a progressive (per-frame,
  composited) display option exists for a smooth image. The owner's `P2000Video.cs` exposes
  `FieldComplete` (every field) and `FrameComplete` (odd-field only) to drive both cadences.
- **Height = 480 (24 rows × 20), NOT 500.** The owner's reference `P2000Video.cs` allocated
  640×500 and ran to bitmapY 499/501 — that's **BBC-Micro heritage** (BBC teletext = 25 rows).
  The P2000T is 24 rows. Fix the buffer height, the end-of-field test, and the field/frame
  discriminator together to 480-based values; don't copy the 500 arithmetic.

---

## 6. The CONTENTION SEAM — restructure vs the reference code (IMPORTANT)

All three references **fetch VRAM and render in the same step** (MAME's `screen_update` reads
`m_read_d(addr)` inline; C#/JS `Render` consumes a `dataQueue`). Our machine needs the **VRAM
fetch to be a bus participant** in the master tick loop (reference doc §4, machine CLAUDE.md §3),
so the CPU-vs-video contention can corrupt a fetch.

So **split the reference design into two stages:**
1. **Fetch stage (the SAA5020 role, on the master clock):** issue the VRAM read for the current
   character slot onto the bus. If the CPU collides this slot, the fetched byte is corrupted
   (single-cell, non-persistent — reference doc §4). Feed the (clean or corrupted) byte into the
   generator's `dataQueue`/`FetchData`.
2. **Generate stage (the SAA5050 role):** the code in these references — control codes,
   rounding, palette — consuming the fetched byte. Unchanged in spirit; just driven by the
   fetch stage's output rather than reading VRAM itself.

Do NOT let the SAA5050 read VRAM directly (as `screen_update` does). It receives bytes from the
fetch unit. This is the one structural change from all three references, and it's required for
the headline contention feature.

---

## 7. P2000T-SPECIFIC — things generic teletext code does NOT have

These references are all BBC/generic teletext. The P2000T diverges:

### 7a. The 160–255 inverted-colour trick (MUST implement — Ghosthunt needs it)
Bytes with **bit 7 set (160–255)** display the character for `value − 128` (normal 32–127) but
with **fg/bg swapped**. Generic teletext ignores bit 7. Note:
- **MAME does implement the inversion** in `screen_update`: `if (BIT(code,7)) color ^= 0x07;`
  and it masks `code & 0x7f` before generating. That XOR-swap is the model.
- The C# `PERender` variant also does it (`invert` flag swapping the palette shifts). The plain
  `Render` does NOT — so if you start from `Render`, ADD the bit-7 inversion (swap the fg/bg
  fields of the palette index, per `PERender`'s `invert ? 2:5 / 5:2`).
- Reference doc §5 (memory map) + §4 confirm this is what makes P2000T games colourful.

### 7b. VRAM layout, panning, buffer size
- P2000T VRAM is **0x5000–0x577F**, a **2-screens-wide (80×24)** buffer; the visible 40×24
  viewport **pans** by an upper-left X coordinate 0–40 (reference doc §5). The reference code
  assumes a simple `cols×rows` read — our fetch stage must apply the **pan offset** when
  computing the fetch address.
- The 10×-per-row re-read (each char row fetched for its 10 scanlines) is confirmed (reference
  doc §4a) — the fetch stage re-reads the row's 40 codes each scanline, which is also what
  drives contention density.

### 7c. No hi-res mode on stock T
The stock T has no bitmap/hi-res mode (that's the separate hires overlay board, reference doc
§5a). The SAA5050 teletext output is the whole display.

---

## 8. Character set / font — embed a default, allow overrides (per owner)

Same approach as the monitor ROM: **embed a default font as a compiled-in resource, allow a
config override.**
- **Font provenance — RESOLVED: the P2000T uses the standard SAA5050 (UK/English set), NOT a
  national variant.** Evidence: the SAA505x family has variants SAA5051 (German), 5052
  (Swedish), 5053 (Italian), 5054 (Belgian), 5055 (US ASCII), 5056 (Hebrew), 5057 (Cyrillic) —
  **there is no Dutch part number**, and Philips (a Dutch company) would have made one for their
  own machine had it been needed. Written Dutch also needs no special glyphs (plain Latin; IJ is
  two chars). So the standard 5050 is correct. The MAME `saa5050` ROM (English, `BAD_DUMP`),
  jsbeeb's generated set, and the C# `FN_SAA5050Data.cs` set are all this same UK set.
  - **Cosmetic note (authentic, not a bug):** the UK 5050 has **£ at 0x23** (not #) — the C#
    set shows this (`0x23 'British Pound'`). Only double-check this one glyph if ROM output ever
    shows #/£ swapped.
- **Default font: embed `FN_SAA5050Data.cs`'s `makeChars()` set** (the standard 5050 UK set) as
  a compiled-in resource.
- **On-disk font format = HUMAN-READABLE TEXT** (`.` = 0, `*` = 1). 96 glyphs × 10 rows × 6 cols,
  editable in any text editor, no tool needed — you can see the glyph shapes directly. Optional
  `#`-comment header per glyph (char index/label) for navigation; comments ignored on parse.
  Example:
  ```
  # 0x41 'A'
  ..**..
  .*..*.
  *....*
  *....*
  ******
  *....*
  *....*
  ......
  ......
  ......
  ```
  Parse: read 6-wide `.`/`*` rows, 10 per glyph → the `makeChars()` 0/1 pixel form. This is the
  authoring/save format (load + save).
- **Built-in variants converted to this text format:** the SAA505x dumps (5050 UK default; 5051
  German, 5052 Swedish, 5053 Italian, 5054 Belgian, 5055 US, 5056 Hebrew, 5057 Cyrillic) ship as
  text-format presets. Source dumps are `uint16[960]` (one row/uint16, low 6 bits, **bit 5 =
  leftmost** — matches `getLoResGlyphRow` x0..x5); convert each uint16 to a `.`/`*` row by
  emitting bits 5..0. Standard 5050 (UK) is the embedded default.
- **Fonts loadable from an ARBITRARY FILE:** `MachineConfig` exposes an optional character-set
  file path (null → embedded default), parallel to `MonitorRomPath`. Any conforming text font
  loads — custom/edited fonts are first-class.

---

## 9. What to KEEP vs CHANGE from the C# port

**The C# port is a STARTING POINT, not a spec — improve, optimize, and restructure it freely.**
It was written against the `PixelEngine` framework for a different context and carries some
BBC-Micro heritage; there's real headroom. The golden tests (§10) are the safety net: they let
you refactor aggressively and PROVE you didn't change a pixel. Optimize the *form* freely;
preserve the *output* of the parts marked correct below.

**Preserve the OUTPUT of (these are correct and hard-won — took three implementations to get
right — but you may restructure/optimize their form as long as golden tests still pass):**
- The rounding (`CombineRows`, `MakeHiresGlyphs`), the three pre-built glyph tables, the
  gamma-blended 256-palette, control-code handling incl. hold-graphics, DEW/LOSE/CRS state
  resets, double-height logic. (These are the pixel-exact behaviours; the tests pin them.)

**Actively improve / change:**
- **Split fetch from generate** for the contention seam (§6) — the single biggest change, and a
  chance to restructure cleanly rather than bolt on.
- **Untangle BBC-Micro heritage:** the port carries 6845-CRTC-style pin naming and interlace
  assumptions. Drive the generator from OUR fetch unit and the P2000T's actual timing instead —
  a genuine simplification, not just a port.
- **Add the 160–255 inverted-colour trick** to the main render path (§7a).
- **Apply the pan offset + 80-wide buffer** in the fetch stage (§7b).
- Target **Avalonia `WriteableBitmap` BGRA** via the `Render()` uint path; **drop `PERender`/
  PixelEngine** entirely (dead weight — removes a whole duplicated render path).
- **Optimize the render loops:** the owner's own comments mark "we could unroll this" — unroll,
  precompute, tighten the per-pixel palette lookup. Per root CLAUDE.md, optimize with a
  benchmark, not on spec — but the headroom is real and welcome.
- Make the font an **embedded default + config override** (§8), not a bare static array.
- Implement `IDevice` (`Reset`/`SaveState`/`LoadState`) — serialize the attribute state +
  scanline/flash counters (MAME's `save_item` list is a good checklist of what's stateful).

**Drop:** MAME's pin-level `dew_w/lose_w/f1_w/tr6_w` micro-stepping is more granular than we
need — our fetch/generate split + per-scanline render is sufficient and simpler. Keep MAME only
as the semantics reference.

---

## 10. Validation

- **Golden glyph tests:** render known character codes at a known attribute state; compare the
  packed glyph rows / output pixels to expected (a few letters, a graphics char, a
  rounded-diagonal char, an inverted 160–255 char).
- **Control-code tests:** set-after timing (control cell shows old state), hold-graphics (no gap),
  double-height top/bottom, conceal, new-background latching.
- **Integration:** once the machine boots (milestone 7), the monitor's cassette-wait prompt text
  must render correctly; later a `.cas` like Ghosthunt must show correct colours (validates the
  160–255 trick end-to-end).
- **Contention:** the fetch-stage split must let the §6/reference-doc-§4 stress test produce
  single-cell speckle.

---

## 11. Findings to record (machine CLAUDE.md §17)

Log during milestone 5: whether set-at vs set-after matched P2000T output; the exact
inverted-colour palette handling that matched hardware; any rounding edge cases. (Font is
settled: standard UK SAA5050 set — see §8.) The owner syncs these into the reference doc.

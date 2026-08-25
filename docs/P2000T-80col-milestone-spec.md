# Milestone spec — 80-column board (machine milestone 25 / UI milestone 18)

**Status:** ready to implement. Unblocked 2026-08-04 by the owner-supplied 1986 newsletter scan.
**Numbering:** derived from `P2000T-reference.md`, whose highest references are machine milestone
24 / UI milestone 17 — **confirm before handing to CC.** The accompanying CC prompt
(`cc-starting-prompt-80col.md`) is named descriptively rather than numbered.

**Sources this spec is built on, in precedence order:**

1. [`docs/P2000T-80column-board-1986-newsletter.md`](P2000T-80column-board-1986-newsletter.md) —
   the primary source (P2000 Nieuwsbrief §13.25, Feb 1986), translated. **Authoritative.**
2. `docs/P2000T-reference.md` §5 "80-column mode" — the same facts folded into the project's
   reference, plus this project's decisions. §3a for the config axis, §4/§4a for the timing and
   contention model this must extend.
3. MAME PR #7577 — **structural cross-check only.** It models the two ports but no contention,
   so its timing is not authoritative here (§6).

---

## 1. What this milestone is, and is not

**Is:** an opt-in, T-only *modification device* that, when fitted and enabled via `OUT 0,1`,
doubles the video character-fetch cadence so 80 characters are fetched and displayed per line
instead of 40, over the same unchanged video RAM and the same unchanged raster geometry.

**Is not:**

- Not a new video mode bolted alongside the existing one. It is a **cadence parameter** on the
  existing SAA5020 fetch-timing unit. If the implementation grows an `if (eightyColumn)` branch
  through the render path, it has gone wrong.
- Not a bitmap mode. The SAA5050 stays in circuit; glyphs, colour, semigraphics, double height,
  blinking, the inverted-colour 160–255 trick and the national remaps are all unchanged.
- Not available on the P2000M (no SAA5050 there), and not a stock-T capability.
- Not a VRAM layout change. The buffer is already 80 characters wide.
- Not a fix for anything. `VOLORG.BAS` merely *looks better* in 80 columns; nothing is broken
  today.

## 2. Prerequisite — verify the row stride BEFORE anything else

`P2000T-reference.md` §4's fetch-address pseudocode reads `fetchAddr = videoBase + (charRow *
40) + column`, a **40-byte** row stride. That cannot be right: the buffer is 80 × 24 = 1920 bytes
at `0x5000`–`0x577F`, and the port-`0x30` pan register slides a 40-wide window *sideways* across
an 80-wide row (pan 40 shows "the 2nd screen to the right"). The correct form is:

```
fetchAddr = videoBase + (charRow * 80) + PanX + column     // column = 0..39 in 40-col mode
```

**Check the built code first.** §4's corrupted-cell-overlay note already assumes absolute VRAM
columns beyond the viewport (`viewportCol = vramCol − PanX`), so the implementation is probably
already correct and only the doc snippet is wrong. **Report which it is.** If the code really does
use a 40 stride, that is a pre-existing bug affecting 40-column mode too — stop, report it, and
fix it as its own change with its own tests before starting this milestone. Do not fold a
40-column correctness fix into an 80-column feature.

With the stride correct, 80-column mode needs **no address remapping at all**: it is
`PanX = 0, column = 0..79` over the same buffer.

## 3. Config (§3a) — new T-only "modifications" axis

Add a **modifications axis** to `MachineConfig`, orthogonal to model / RAM / internal-slot board
/ slot population. It composes freely with all of them.

| Setting | Type | Default | Notes |
|---|---|---|---|
| `Modifications.EightyColumnBoard` | bool | **false** | T-only. Reset-to-apply. |
| `Modifications.ShowEightyColumnArtifacts` | bool | **true** | Only meaningful when the board is fitted; see §7. |

Rules:

- **Reset-to-apply**, like every other topology change: queue + cold reset.
- **Unavailable, not merely off, when `Model == P2000M`.** The UI greys out or hides the whole
  axis; a config that sets it on an M is invalid, not silently ignored — surface it the same way
  other invalid topologies are surfaced.
- **Default off means byte-identical behaviour to today.** This is the primary safety net.
- Bump the `.cfg` and `.state` format versions per §3a's versioning rule. A pre-existing config
  without the axis loads as "no board fitted".

## 4. The device

A new device on the common device interface (`Reset`, `SaveState`/`LoadState`), instantiated
**only when the board is fitted**.

**State:** a single latched bit — `EightyColumn` (false = 40, true = 80).

**Port `0x00`, write:** latch bit 0 of the written byte. All other bits ignored. Every write is
effective immediately, mid-frame included — this is real hardware with no synchronisation, so a
mid-frame switch takes effect on the next fetch slot. Do not defer it to a frame boundary.

**Port `0x70`, read:** return `0x01` when in 80-column mode, `0x00` when in 40-column mode.

- The article states `A=INP(&H70)` gives exactly 0 or 1, so the board appears to drive all eight
  bits with the upper seven low. **Flagged as likely-but-not-certain** — BASIC `INP` shorthand
  could be hiding a mask. Implement upper bits as 0; leave a comment naming the uncertainty.

**When the board is NOT fitted:** neither port is claimed. Port `0x00` writes go nowhere; port
`0x70` reads return **open bus `0xFF`** via the existing absent-device convention.

> **This is load-bearing, not a detail.** The source documents a software presence-probe: write,
> read back, repeat, and conclude the board is present only if the write was "taken over". A
> zero-returning stub on port `0x70` would make every probing program conclude "board present,
> currently 40-column". Verify port `0x70` is genuinely unclaimed on an unmodified machine, and
> **check whether anything else in the emulator already claims `0x70`** — if something does,
> stop and report rather than layering.

**Reset:** 40 columns, on **both** cold and warm reset (*"Bij RESET wordt automatisch de 40
karakter-stand gekozen"*). Note this differs from the RAM rule in §5b, where warm reset
deliberately preserves contents — the mode latch is a flip-flop on the board's reset line, not
memory.

**SaveState/LoadState:** the mode bit serialises. A state captured in 80-column mode must restore
in 80-column mode.

## 5. Video — the cadence change

This is the whole substance of the milestone. Per the article: a 24 MHz crystal on the board is
divided to 12 MHz and generates doubled-rate copies of the SAA5020's character-timing outputs
(F6, F1, LOSE, RACK); an LS157 selects board-generated or SAA5020-native. **The SAA5020 itself is
not overclocked** — it stays at 6 MHz generating line and field timing.

**Therefore the raster geometry is completely unchanged in 80-column mode.** Do not touch any of
it: 64 µs / 160 T-state line, 313-line field, active window at char-times 15–54, the 49/240/24
vertical split, the 928 × 626 full-field buffer, the fixed (144, 98) graphics-window crop.

What changes, and only this:

| Quantity | 40-column | 80-column |
|---|---|---|
| Character-fetch rate | 1 MHz | **2 MHz** |
| Fetch slot duration | 1 µs (2.5 T-states) | **0.5 µs (1.25 T-states)** |
| Fetch slots per active line | 40 | **80** |
| Columns fetched | `PanX + 0..39` | **`0..79`** |
| Total fetch-eligible time per active line | 40 µs | **40 µs — unchanged** |
| Rendered pixel lanes per character | 16 | **8** (see below) |
| Full-field buffer | 928 × 626 | **928 × 626 — unchanged** |

Implement as a **cadence parameter on the existing fetch-timing unit**, not a parallel path. The
unit already answers "is this a fetch slot, and at what address" per tick; 80-column mode changes
the slot granularity and the column count, nothing structural.

### 5.1 Sub-T-state slot boundaries — decide deliberately

A 0.5 µs slot is **1.25 T-states**, so 80-column fetch slots do not align to T-state boundaries.
The existing model ticks per T-state. Do not paper over this. Two acceptable approaches:

- **Preferred:** move the fetch-timing unit's internal accounting to the **character/dot clock**
  rather than T-states, and derive T-state alignment from it. §4 already recommends the master
  clock be "the dot/character clock the SAA5020 derives timing from", so this is the model the
  reference doc always intended; 40-column mode happened not to force the issue.
- **Acceptable fallback:** keep T-state ticking and track a fractional slot accumulator, provided
  the *set* of contended CPU accesses it produces is exactly reproducible and documented.

**Whichever you choose, say so explicitly in the findings log with the reasoning.** This is the
one place where an 80-column implementation can quietly change 40-column contention results — the
regression gate in §8 exists mainly to catch that.

### 5.2 Rendered pixel lanes — a decision point, report before changing buffer dimensions

§4a renders **16 pixel lanes per char-time**, giving the 640 px active width (40 × 16). Because
the buffer is a *time-based* raster and the line period is unchanged, **the buffer must stay
928 px wide in 80-column mode** — which means each 80-column character occupies **8 lanes**
(80 × 8 = 640).

Consult `SAA5050-implementation.md` §1/§2 for how 6 dots/char currently map onto 16 lanes, and
check whether that mapping degrades unacceptably at 8 lanes (the character-rounding / smoothing
behaviour is the thing at risk). **If it does:** the fix is to raise the *global* lanes-per-char-
time constant so both modes gain resolution — which changes the buffer width for both modes and
is a UI-visible change well beyond this milestone's scope. **Do not do that unilaterally.**
Report the finding with a recommendation and stop.

### 5.3 Pan register — cleared in hardware

The mode latch drives pin 1 of the 74LS273 scroll register (old-board position 7134, new-board
7222) — the **asynchronous master reset**. So:

- **Entering 80-column mode clears `PanX` to 0.** Not saved, not masked — cleared.
- **While in 80-column mode the register is held cleared:** writes to port `0x30` do not take
  effect.
- **Returning to 40-column mode leaves `PanX` at 0**, until the CPU writes port `0x30` again.

A program that set a pan, switched to 80 and back finds its pan gone. That is a user-visible
behaviour, so implement it exactly — a "save and restore" implementation is wrong and will look
plausible.

> That pin 1 of a 74LS273 is the async clear is a **datasheet inference**, flagged as such in the
> reference doc. The article states the *behaviour* ("the horizontal scroll is switched off")
> outright; the clear-versus-inhibit distinction is the inferred part. It is the reason to prefer
> "cleared" over "ignored", but if real-hardware behaviour ever contradicts it, this is the
> assumption to revisit.

### 5.4 Corrupted-cell overlay — API change

§4's per-field corrupted-cell map is **40 × 24**, indexed `charRow × 40 + viewportCol` where
`viewportCol = vramCol − PanX`. In 80-column mode it becomes **80 × 24**, and since `PanX` is
always 0 there, `viewportCol == vramCol`.

- Expose the viewport width alongside the map rather than letting consumers assume 40. Both the
  UI's "show glitches" overlay and the debugger's VRAM window consume this.
- Keep the existing clear-after-`FieldComplete` semantics unchanged.
- A field during which the mode changed mid-frame is a genuine edge case: pick a rule (simplest:
  the map is sized for the mode in effect at `FieldComplete`, with slots fetched under the other
  cadence mapped to their nearest cell), document it, and test it. Do not leave it undefined.

## 6. Glyph path — unchanged

The SAA5050 stays in circuit and is simply clocked faster. No second font path, no mode-dependent
glyph behaviour. `Saa5050Font.cs`, the inverted-colour 160–255 trick, the national remaps and the
teletext control codes all apply identically in both modes.

## 7. The out-of-spec artifact (`ShowEightyColumnArtifacts`)

The article is candid that at 12 MHz the SAA5050 runs *"far outside its specifications"* and that
*"sometimes one sees, at the position of a switch-over character, a small block or a few dashes
instead of a space."* Its own commissioning procedure calls the block before "PHILIPS CASSETTE
BASIC" **normal**. Owner's decision: reproduce it, behind a config toggle.

- When the toggle is on **and** the machine is in 80-column mode: render teletext **control
  characters as a filled block instead of a space**, deterministically.
- **The rule is NOT sourced.** The article says only "sometimes", gives no root cause, and
  characterises neither which control codes nor under what conditions. **Mark it in code as a
  placeholder awaiting real-hardware capture** — same posture as §4's unresolved corruption-mode
  question, and resolvable the same way (real board, real screen, photograph the control-character
  positions).
- **Do not invent a more elaborate rule** — no pseudo-randomness, no "sometimes" modelled as a
  probability. This project's determinism rule (machine CLAUDE.md §2.2) forbids randomness in
  emulation code, and a deterministic-but-invented rule is worse than a deterministic-and-simple
  one because it looks researched.
- Toggle has no effect with the board absent or in 40-column mode.

## 8. Tests

### 8.1 The regression gate — run this first and last

**Every existing machine and UI test must pass byte-identical with the board absent.** The
current suite is ~627 tests (615 passing, 12 skipped) per the reference doc's disk-investigation
close-out. A machine with `EightyColumnBoard = false` must be indistinguishable from today's,
including timing, contention results, framebuffer output and save-state bytes.

If §5.1's clock-model change perturbs any 40-column timing test, **stop and report** — that is
the signal that the cadence refactor changed behaviour it should not have touched.

### 8.2 New tests

**Ports and presence**

- Board absent: `OUT 0,1` then `IN 0x70` returns **`0xFF`** (open bus), not `0x01` and not
  `0x00`. Explicitly assert the article's presence-probe protocol fails to detect a board.
- Board fitted: `OUT 0,1` → `IN 0x70` == `0x01`; `OUT 0,0` → `IN 0x70` == `0x00`.
- Board fitted: upper bits of `IN 0x70` read 0.
- Cold reset and warm reset both leave the machine in 40-column mode.
- `OUT 0,x` with bits 1–7 set: only bit 0 is honoured.

**Cadence and geometry**

- 80-column mode produces exactly **80 fetch slots per active scanline**; 40-column produces 40.
- **Total fetch-eligible time per active line is identical in both modes** (40 µs) — the single
  best assertion that the cadence change did not disturb the raster.
- Line period, field length, active-window position and the 928 × 626 buffer dimensions are
  identical in both modes.
- Fetch addresses in 80-column mode cover `videoBase + charRow*80 + 0..79` with no gaps or
  repeats.

**Pan register**

- Entering 80-column mode with a non-zero `PanX` clears it to 0.
- Writes to port `0x30` while in 80-column mode leave `PanX` at 0.
- Returning to 40-column mode leaves `PanX` at 0 until the next port-`0x30` write.

**Overlay and state**

- Corrupted-cell map is 80 wide in 80-column mode, 40 wide in 40-column mode; viewport width is
  exposed, not assumed.
- Save/load round-trip preserves the mode bit; a state saved in 80-column restores in 80-column.
- Config with the board fitted on `Model == P2000M` is rejected as invalid.

**Artifact toggle**

- Toggle on + 80-column: control characters render as the block placeholder.
- Toggle on + 40-column: no change from today.
- Toggle off: no change from today in either mode.

## 9. What CC must NOT guess

Report and stop rather than deciding any of these:

1. **The stride finding** (§2) — if the code really uses 40, that is a separate pre-existing bug.
2. **Raising the global lanes-per-char-time constant** (§5.2) — changes buffer dimensions for
   both modes; owner decision.
3. **Any elaboration of the artifact rule** beyond the flat "control character → block" placeholder
   (§7).
4. **Anything that changes 40-column timing results** (§8.1).
5. **A port `0x70` conflict** with an existing claimant (§4).
6. **The exact topology by which doubled RACK/F1 reaches the video address counters.** The article
   does not describe it and tracing it would need the P2000T CPU schematic. It does not matter for
   emulation — the observable result is stated — but do not reverse-engineer a mechanism and
   present it as sourced.
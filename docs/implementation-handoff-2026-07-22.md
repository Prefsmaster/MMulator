# P2000T Emulator — Implementation Handoff (2026-07-22)

This is a punch list for whoever picks up implementation next (Claude Code, or anyone
working from these docs). It doesn't replace the canonical files — everything here is
already written into `src/P2000.Machine/CLAUDE.md`, `src/P2000.UI/CLAUDE.md`, and
`docs/P2000T-reference.md` (owner-supplied working copies attached separately); this is
just a map of where to look and what's actually ready to build, versus what still needs
a judgment call during implementation.

Three areas are ready or near-ready. They're independent of each other — no ordering
dependency between them — but they differ in *kind*: FDC is **net-new milestone work**
(nothing exists yet); Display and RAM-behavior are **corrections to already-implemented
milestones** (5, 6, 10) — going back into working code, not starting from a blank file.

---

## 1. Display — full-field rendering + Full-Field/Graphics-window toggle

**Status: design complete, ready to implement.** Two rounds of owner review already
happened on this one (width correction, and an explicit instruction NOT to touch the
existing dual even/odd field rendering machinery) — both are folded into the docs below,
so there shouldn't be surprises mid-implementation.

**Read first:**
- `P2000.Machine/CLAUDE.md` §3 "The framebuffer" (primary contract — new size, geometry,
  crop rectangle) and §17 findings log, entries dated **2026-07-22** ("machine renders the
  FULL FIELD...") and **2026-07-21** ("Fields vs frames — CORRECTED" + the **WITHDRAWN**
  note under it — read that one carefully, it's specifically there to stop a plausible
  but wrong refactor).
- `P2000.UI/CLAUDE.md` §3.1 "Framebuffer handoff" and §8 "Display / rendering", plus §18
  findings dated 2026-07-22 and 2026-07-21.
- `docs/P2000T-reference.md` §3a (UI-facing spec: two toggles) and §4a "Full raster
  geometry" (the numbers).

**Concrete numbers:**
- Framebuffer: **928 × 626 px** BGRA (was 640×480, active-only).
- Horizontal: 144 px leading blank (9 char-times) + 640 px active (40 char-times) + 144 px
  trailing blank (9 char-times) = 928. Retrace (6 char-times / 96 px, start of each line)
  is **excluded entirely**, not rendered as black — flagged 5-vs-6 ambiguity, not
  independently confirmed.
- Vertical: 98 px pre-roll blank (49 scanlines) + 480 px active (240 scanlines) + 48 px
  post-roll blank (24 scanlines) = 626. Vertical retrace not addressed — horizontal-only
  so far.
- Active "graphics window" crop rectangle: fixed at **(144, 98)**, size 640×480, every
  field (constant, not data-dependent — safe to hardcode).

**What to actually change:**
1. Resize the machine's framebuffer to 928×626; fill blanking regions flat black (no
   fetch/contention there — cheap, no `CombineRows` work needed).
2. Add the UI's second toggle (Full-Field vs Graphics-window), independent of the
   existing 4-way display mode. Default: **Graphics-window** (no visible change for
   existing users). `WriteableBitmap` sizing follows whichever crop is active.
3. Flip the 4-way display-mode **default** from Interlaced/comb to **Odd-only**.
   **Do NOT touch the underlying per-field rendering/compute pattern to do this** — it's
   a preference-default change in `DisplayMode.cs`/`DisplayWindowVm.cs`, nothing else.
4. `CorruptionOverlay` draw path needs a coordinate offset (+144, +98) when Full-Field is
   showing, since overlay indices are relative to the active window. (Implementation
   detail left as Claude Code's call — offset at draw time, or store overlay
   full-buffer-sized — both are fine.)
5. Separately: `P2000.Machine/CLAUDE.md` §17's 2026-07-19 entry flags a **possible bug**
   (not yet verified against source) — `VideoFetchUnit` may be treating field-T-state 0
   as the start of the contention-eligible window instead of offsetting by 7,840
   T-states (49 lines). This is the leading hypothesis for the owner's original Ghosthunt
   top-of-screen glitch report. Worth checking alongside the framebuffer resize since
   both touch the same vertical-timing code, but it's a distinct fix from the resize.

**Downstream sweep still needed (not exhaustive — re-search "640" in both CLAUDE.md files
before starting):** other prose mentions of "640×480" as *the* framebuffer size
(ownership/observer sections, PAL-aspect-ratio math, existing tests asserting exact
buffer dimensions). The primary contract (§3 above) is the new source of truth to
reconcile the rest against.

**Added 2026-07-22 (owner catch, after this handoff was first drafted):** PAL
aspect-ratio correction is **Graphics-window only — a no-op in Full-Field mode.** It
reproduces the active picture's standardized real-world relationship to a 4:3 CRT tube;
the blanking margins have no equivalent standard (real CRTs never show retrace at all,
and hide most of the porch behind bezel/overscan by a set-specific amount, not a
broadcast standard). Disable/grey out the toggle when Full-Field is selected rather than
extending the correction to the whole buffer. See `P2000.UI/CLAUDE.md` §8 and reference
doc §3a/§4a for the full reasoning — an earlier pass of these docs claimed the
correction extends cleanly to the full buffer; that was wrong and has been corrected.

**Explicitly unaffected — no change needed:** the contention model (still only the
active window's fetch slots are contention-eligible, regardless of buffer size).

---

## 2. FDC — Floppy Disk Controller (Milestone 19)

**Status: fully specced, ready to implement as new milestone work.** This predates the
current thread — it's a complete, ROM-disassembly-confirmed design, not something that
needs further design discussion. Read `P2000.Machine/CLAUDE.md` §13, item **19**
("Floppy Disk Controller (µPD765) — standalone chip + minimal board wiring") start to
finish — it's long but self-contained, covering:

- `Upd765 : IDevice` as a standalone chip class (mirrors the `Z80Ctc`/`SAA5050` pattern),
  wired through a thin `InternalExtensionBoard` object.
- Confirmed register interface (`0x8C` MSR, `0x8D` data, `0x90` dual-purpose control/
  semi-DMA-flag register — read carefully, IN and OUT at `0x90` are genuinely different
  registers, not a read-back).
- Confirmed presence-probe sequence (exact byte-for-byte ROM behavior, including the
  `CP 0x80` exact-match detail — not a bare bit-7 test).
- The 3-gate disk-boot condition (`memsize==3` + SLOT1 cartridge present + cartridge
  requests DOS) — already cross-referenced against reference doc §5b.
- CTC wiring (extends M17, doesn't change it), semi-DMA polling model, `TimingPolicy`
  scope (chip-timing only, no ROM trap — unlike cassette).
- `.dsk` host image API, auto-detect geometry (JWSDOS self-describing label), directory
  browsing scope (side 1 only — side 2's on-disk location isn't sourced yet, don't
  guess).
- A full test list (6 groups, (a) through (f)) already written out with exact expected
  byte values for two real fixture images (`Spel1.dsk`, `jwssytem.dsk`).

**One open item, already scoped (not a blocker):** side 2's directory location in a raw
`.dsk` file isn't confirmed (`docs/JWSDOS-format.md` §7 item 2) — browse side 1 only
until that's sourced, per the milestone's own text. Don't invent an offset for side 2.

---

## 3. RAM power-on behavior (non-zero garbage fill)

**Status (2026-07-23): fully decided, ready to implement. No open scope questions
remain** — both decisions that were originally flagged as needing an owner call have
since been resolved.

**Read first:** `P2000.Machine/CLAUDE.md` §17, entry dated **2026-07-21** ("RAM should
power up non-zero, not all-zero") — includes the **REFINED** sub-note covering the seed
mechanism, plus two 2026-07-23 resolution notes (warm-reset, all-RAM scope). Reference
doc §5b has the hardware-fact side (owner's real-hardware observations, boot-sequence
cross-reference).

**What's decided, all of it:**
- RAM should power up with non-zero, unpredictable-looking content (matches the owner's
  real-hardware observation of a brief garbage flash before the monitor ROM clears the
  screen).
- **Applies to ALL RAM, not just VRAM** — *"All memory contains garbage at startup; it
  is all Dynamic ram."* Base RAM, banked window, and VRAM all get the fill.
  **Expansion-card RAM is explicitly carved out as each device's own responsibility**
  (maps onto the existing per-device `IDevice.Reset()` pattern, §4) — build it the same
  way (non-zero fill) unless/until a specific card's hardware says otherwise; this isn't
  a new mechanism, just a reminder that `Reset()` is already per-device.
- **Must respect Locked decision §2.2** ("no `DateTime`/threads/randomness in emulation
  code"): the core itself never calls a nondeterministic API. Shape: `Reset(ulong?
  ramSeed = null)` or equivalent — omitted seed → fixed deterministic default (what
  tests/CI get); explicit seed → reproducible-with-that-seed; `P2000.UI` generates a
  genuine random seed at each real cold boot / app launch and passes it in. Mirrors the
  existing `MonitorRomPath`-style optional-override convention already used elsewhere —
  not a new idiom.
- **Warm reset must NOT clear RAM** — confirmed via a period P2000 newsletter (real warm
  reset leaves RAM untouched) and **already instructed directly to Claude Code by the
  owner** — this isn't waiting on anything either. Bonus fact from the same source,
  worth keeping in mind (not a required feature): P2000 RAM is confirmed **Dynamic
  RAM**, and holding the reset button too long disables refresh and can genuinely
  damage/erase memory. No action needed here beyond awareness; modeling the held-reset
  decay itself would be pure polish, not requested.

---

## Suggested reading order for whoever implements this

Each area is self-contained — no correctness dependency between them, and as of
2026-07-23 all three are fully decided with zero open scope questions. FDC is the
largest single chunk of new code, so it's the easiest to just pick up and build. Display
and RAM-behavior both touch code that already works — for Display, be careful with the
WITHDRAWN note in §1 above (it exists specifically to prevent a plausible-looking
revert); for RAM-behavior, the warm-reset fix has already been actioned directly with
Claude Code outside of this handoff, so double-check its current state before
re-instructing it.
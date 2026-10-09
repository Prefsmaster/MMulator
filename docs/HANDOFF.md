# P2000T Emulator — Project Handoff / Status

Snapshot for resuming in a fresh chat (or a new Claude Code session). The **docs are the source
of truth** — this note is a map + current status, not a replacement for them.

_Last updated: 2026-08-04. (Previous version was 2026-07-05 and had gone badly stale — it still
described `P2000.UI` as "NOT started", when it is now through milestone 21.)_

---

## The documents

| File | Role |
|------|------|
| `docs/P2000T-reference.md` | **THE hardware + design source of truth.** Read on demand, NOT auto-loaded. |
| `CLAUDE.md` (repo root) | Slim solution map: projects, dependency direction, `Z80Tables` rule, global conventions. |
| `src/Z80.Core/CLAUDE.md` | Z80 core contract (DONE, v1.0.0). |
| `src/Z80.Disassembler/CLAUDE.md` | Disassembler contract (DONE). |
| `src/P2000.Machine/CLAUDE.md` | Machine contract + milestone list §13 + **findings log §17**. |
| `src/P2000.UI/CLAUDE.md` | UI contract + milestone list §14 + **findings log §18**. |
| `docs/SAA5050-implementation.md` | Video/teletext device guide. |
| `docs/MDCR-implementation.md` | Cassette device guide. |
| `docs/FDC-implementation.md` | Floppy device guide (µPD765 full command set). |
| `docs/P2000T-disk-formats.md` | JWSDOS + PDOS on-disk formats. |
| `docs/P2000T-80column-board-1986-newsletter.md` | Translated 1986 Nieuwsbrief article — primary source for the 80-column board. |
| `docs/M2200-implementation.md` | M2200 expansion board (mostly deferred). |
| `docs/todo list.md` | **The owner's own running list** — hardware/software/UI wants, plus a Bugs section. Check it before picking work. |
| `docs/Monitor Documented Disassembly/` | Monitor ROM listing. Load-bearing: used to settle real port usage. |

Hierarchical CLAUDE.md loading: root + the nearest project file.

---

## Build status

**DONE + validated:**

- **Z80.Core** — v1.0.0. Full instruction set (all prefixes), interrupts. SingleStepTests (1604
  opcode files) + ZEXALL/ZEXDOC green. Cycle-stepped, bus-exposed, `ulong` pin mask.
- **Z80.Disassembler** — spec-complete, golden + 1604 conformance green.
- **P2000.Machine** — through **milestone 26**. Page table, port dispatch, SAA5050 video
  (full-field 928×626), interrupts, boot (bare + BASIC + disk), keyboard, cassette (authentic
  phase bitstream + CSAVE + turbo trap), contention, CTC, FDC (full 15-command µPD765),
  multi-drive floppy, IMD container, debugger surfaces (snapshot / breakpoints / command queue),
  per-bank RAM access, 80-column board, video control register.
  **707 tests: 695 passed, 12 skipped, 0 failed.**
- **P2000.UI** — through **milestone 21**. Avalonia MVVM; display with 4 display modes +
  Full-Field/Graphics crop, menu/toolbar/status bar, host + soft keyboard, cassette deck, disk
  drives window, full debugger (registers, memory watches, VRAM/pan window, disassembly,
  breakpoints, stepping), audio, `.cfg`/`.state`/`.uistate`, startup config, two-column config
  window. **259 tests, all green.**

**Not started / deferred:** P2000M, hires overlay board, SLOT2 cards, printer, PTC-96K RAM
variant, M2200's own features (RTC, RAM disk, SIO, Centronics, 2nd CTC), external IDE hook.

### ⚠ Milestone numbering drift — read this before starting a milestone

**Take the next milestone number from the FINDINGS LOGS, not from the §13/§14 numbered lists,
and not from the reference doc.** This has produced a wrong number twice.

- `src/P2000.Machine/CLAUDE.md` §13's list stops at **24**, but §17 records **25** (80-column
  board) and **26** (video control register) as implemented. Next free: **27**.
- `src/P2000.UI/CLAUDE.md` §14's list stops at **19**, but §18 records **20** and **21**. Next
  free: **22**.

---

## What landed most recently (2026-08-04)

Nine commits, all pushed. In brief:

- **Machine 25 — 80-column board.** Opt-in T-only modification
  (`MachineConfig.Modifications.EightyColumnBoard`, default off). Port `0x00` bit 0 latches,
  port `0x70` reads back, fetch cadence doubles as a *parameter* on the existing SAA5020
  fetch-timing unit (deliberately not a second render path). Pan cleared in hardware on entering
  80-column mode.
- **Machine 26 — video control register, ports `0x30`–`0x3F`.** Write-only, 16-port partial
  decode. Bit 7 blanks video (active window only; border unchanged), bits 6–0 pan, clamped 0–40
  in the `PanX` setter. This made the pan reachable from software for the first time.
- **UI 20** — the Modifications config axis + viewport-width plumbing (the corrupted-cell
  overlay's stride is no longer a constant; `FrameReady` carries it).
- **UI 21** — config window two-column relayout. **Explicitly interim**; the owner's stated end
  state is a tabbed config window.
- **One real bug fixed:** `EmulationRunner.EnsureRamSeed` silently dropped `Modifications`, so
  ticking the 80-column checkbox did nothing. Now guarded by a reflection test.

---

## Known bugs (from `docs/todo list.md`)

### 1. No keyboard input after warm/cold reset following Ghosthunt — **best next step, see below**
Owner-reported. Investigated 2026-08-04 far enough to eliminate the obvious cause and name a
concrete mechanism — see "Suggested next steps".

### 2. Ghosthunt display glitches "kloppen niet" (don't look right)
The contention glitches don't match real hardware. **Be careful with this one:** reference doc §4
records the exact corruption mode (data bleed vs. bus contention vs. fetch suppression) as an
OPEN question needing a logic-analyzer capture. The emulator's build-against-now default is
"collided slot → blank/black cell". So this may not be fixable from the armchair — expect to hit
the hardware-capture wall. Read §4's "Open: WHICH corruption mode" before investing.

### 3. CSAVE replace/append "gaat nog niet helemaal goed"
Tape write path edge cases. Self-contained; `docs/MDCR-implementation.md` + the ms.9a findings are
the context.

---

## Open hardware questions (all need the owner, or real hardware)

None of these block anything; they are placeholders marked as such in code.

| Question | Where | Status |
|---|---|---|
| Pan values > 40 | §5g | Clamped to 40 as a **deliberate placeholder**. Owner intends to test a real machine; whatever it does replaces `Video.ClampPan`. |
| Does video blanking stop VRAM fetches? | §5g | Logic-analyzer question. **Nearly unobservable** — the Z80 never waits, so timing can't tell, and while blanked a corrupted and a suppressed fetch look identical. Default: fetches continue; decision point marked in `Video.OnColumnFetch`. |
| Which corruption mode on a contended fetch | §4 | Logic analyzer + RGBS capture. Default: blank cell. |
| The 80-column artifact rule | §5 | Fires on control codes 8/13/24 (Flash / Double height / Conceal), narrowed from "every blank control cell" on owner observation. **Unsourced placeholder** — the article says only "sometimes". Needs a photograph of a real 80-column screen. |
| 8 rendered lanes/char at 80 columns | §5 | Measured as glyph-lossless (identical distinct-pattern counts at 8 and 16 lanes for all 20 glyph rows), so the global lane constant was left alone. Owner judgement on a real screen could still override. |

---

## Workflow notes

- **Test commands.** `dotnet test tests/P2000.Machine.Tests` (~7–9 min).
  `dotnet test tests/P2000.UI.Tests --filter "Category!=Integration"` for fast UI iteration;
  full UI run before finalizing. **Only run the whole solution when `Z80.Core`/`Z80.Disassembler`
  are touched.**
- **Findings loop.** Claude Code appends to §17/§18 during a milestone; the owner syncs entries
  into `docs/P2000T-reference.md` and flips `Synced: no` → `yes`. **Do not edit the reference doc
  from a project.** All entries are currently synced.
- **Commit discipline.** Milestone green → conventional commit whose body records what was built
  AND the non-obvious findings. Every commit keeps CI green — which means milestones that
  interleave in the same files get committed together rather than split into states that never
  existed (machine 25+26 and UI 20+21 were each one commit for this reason).

### Traps that have each cost real time — worth knowing before you start

1. **Glyph row 0 is blank padding for nearly every SAA5050 glyph, and a blank cell on a black
   background renders as exactly `Video.BlankedColor`.** Any test comparing cell contents, or
   asserting "the picture changed", passes **vacuously** at row 0. This has bitten three times
   across two milestones. Use a mid-glyph row (4 is good) and add an explicit
   not-uniformly-blank guard. Now also recorded in reference doc §5's SAA5050 quirks.
2. **`MachineConfig` is copied by hand in `EmulationRunner.EnsureRamSeed`.** That list has
   drifted three times. `MachineConfigPreservationTests` now walks the type by reflection, so a
   newly added property fails a test instead of vanishing at runtime — but if you add a property,
   add it there too.
3. **`EmulationRunner.Reconfigure` applies its swap on the emulation thread at a field boundary**,
   so a `Reconfigure` issued while the runner is stopped never lands — it times out silently.
   Tests must `Start()` before `Reconfigure`.
4. **Poking a port in a test is not the same as a guest writing it.** `OUT 48,xx` looked broken
   for a whole investigation; the cause was that Cassette BASIC writes the pan back to 0 on
   returning to its `Ok` prompt (Disk BASIC 24 does not). When a port behaviour is reported
   broken, reproduce it through the real keyboard matrix with the real cartridge.

---

## Suggested next steps

### 1. Fix "no keyboard input after reset" (recommended)

The strongest candidate: it is a real correctness bug, it is recoverable only by restarting the
app, it needs no hardware sourcing, and it is already half-diagnosed.

**What was ruled out (2026-08-04):** the machine-side reset is clean — `KeyboardDevice.Reset()`
clears the matrix and `CPoutLatch.Reset()` zeroes the latch (so KBIEN = 0, scan-off mode).

**The concrete hypothesis to test first:** `HostKeyTranslator` (`src/P2000.UI/Input/`) **has no
reset of any kind** — `_keysDown`, `_activePress`, `_activeForce` and the forced-shift counters
persist across a machine reset, and `DisplayWindowVm.WarmReset`/`ColdReset` only enqueue a machine
command. So the machine's matrix is cleared while the UI still believes keys are held. `KeyDown`
begins `if (!_keysDown.Add(key)) return …` — a stale entry makes every later press of that key
emit **nothing**. Resetting mid-game with keys down (exactly what happens when quitting Ghosthunt)
would strand them.

This is the same bug class as the 2026-08-01 fix in that file (a stale `_keysDown` entry producing
zero matrix events). Likely fix: clear translator state when a reset command is issued. Verify the
hypothesis before building it — reproduce with a key held across a reset.

### 2. Printer device (good first feature, if you'd rather build than fix)

Listed in both deferred lists and the todo. Self-contained: CPOUT bit 7 is printer data, CPRIN
carries PRI/READY/STRAP, and the ROM already has a `Printer.asm` driver to test against. Output to
a text file first; matrix-printer emulation later. No unknowns blocking it.

### 3. Lower-value but easy wins from the todo list

Debugger flag display (currently hard to read — header of flag letters with 1/0 beneath), showing
shortcuts in menus, tape position in the cassette window, read/write byte counters in the disk
tabs.

### What I would *not* start with

- **Ghosthunt display glitches** — likely blocked on a logic-analyzer capture (see Known bugs #2).
- **P2000M** — a large new axis; the T still has open polish and bugs.
- **Tabbed config window** — UI 21's two-column layout is interim but adequate; revisit when the
  next config axis makes it awkward, not before.

# CLAUDE.md — P2000.UI

Project-specific contract for the **Avalonia** front-end. Read this together with the **root
`CLAUDE.md`** (global conventions, dependency direction, `Z80Tables` rule, thread/observer
boundary — NOT repeated here) and **`src/P2000.Machine/CLAUDE.md`** (the machine it observes).
This project is the windowed emulator: display, menus, config, keyboard, debugger, and the
cassette deck.

**Design source of truth:** `docs/P2000T-reference.md` **§3a** (UI architecture) — the window
set, control surface, config axes, display modes, and the full debugger spec. This file
specifies the *software architecture* of the UI; when it says "per the reference doc," open
`docs/P2000T-reference.md` for the exact decision. The reference doc is read on demand (NOT
auto-loaded), so open it explicitly whenever a task needs UI-design detail. Also relevant:
§2 (Avalonia/OpenAL stack decision), §3 (threading/determinism), §5b (cassette runtime
actions), §4/§10 (contention → the "show glitches" toggle + corrupted-cell overlay).

---

## 1. What this project is

`P2000.UI` is a cross-platform (Windows / macOS / Linux) **Avalonia MVVM** application that
presents a running `P2000.Machine` and lets the user drive it. It is a pure **observer** of the
machine: it reads completed framebuffers and state snapshots, and it submits input and commands
through a contract the machine owns. It never advances or mutates the emulation core itself.

**Scope of THIS build: a working windowed T.** Boot a bare machine out of the box, render the
SAA5050 display, take keyboard input, mount/run a `.cas`, save/load `.cfg` and `.state`, and
provide the full debugger (register file, memory watches, the special VRAM/pan window, live
disassembly, breakpoints, stepping). The external IDE/cross-dev interface is **deferred**
(§15) — its transport and protocol are TBD; lessons from this UI build inform it later. But the
contract it will attach to (observer + control, breakpoints, command queue) is the SAME one the
UI consumes here, and that contract lives in `P2000.Machine`, not here.

---

## 2. Locked design decisions (do NOT revisit without being asked)

1. **Avalonia + CommunityToolkit.Mvvm.** Software-rendered display (blit into a
   `WriteableBitmap`, present in an `Image`, nearest-neighbour scale). No GPU path — at
   640×480/50 Hz it buys nothing (reference doc §2). MAUI is rejected (Linux not
   production-grade). MVVM from the start: machine snapshot → ViewModel → binding.
2. **Every window is an OBSERVER.** Windows read machine snapshots / the framebuffer view;
   they NEVER touch the live core. All mutation goes through the machine's command path
   (§3). This is the root/`P2000.Machine` thread-boundary rule, restated as UI law.
3. **The machine runs on its own thread; the UI thread only presents.** Completed frames and
   audio blocks flow across the boundary; the UI consumes them via `Dispatcher.UIThread.Post`.
   The UI never reads a buffer mid-render (§4).
4. **Display is the main window; everything else is a satellite window** (reference doc §3a).
5. **Control surface = menu bar + toolbar + status bar. NOT custom title-bar buttons.** Hijacking
   window chrome fights the OS across Win/mac/Linux (macOS especially) — do not do it (§6).
6. **Bare by default.** First launch = no SLOT1/SLOT2 cartridge, empty cassette, base RAM, no
   disk — the honest baseline that exercises the ROM's presence-probe paths (reference doc §3a).
7. **Topology config is reset-to-apply; cassette mount/eject is the ONE runtime exception**
   (reference doc §5b — CIP is a live transition the ROM polls). Do not generalize the exception.
8. **The observer + control contract, and breakpoint ownership, live in `P2000.Machine`, not
   here.** The UI is its first client; the future IDE hook is its second. The UI defines its
   *requirements* on that contract (§3) but does not own it.

---

## 3. The observer + control contract (the central seam)

This is the heart of the UI project the way the tick loop is the heart of the machine. The UI
does three things through the machine, and nothing else: **it reads frames, it reads state
snapshots, and it submits input + commands.** Keep every window on this side of the seam.

### 3.1 What the machine ALREADY exposes (bind to these — do not reinvent)
- **Framebuffer handoff — SIZE CHANGED (2026-07-22, owner request: the machine now renders the
  FULL FIELD, not just the active picture; width CORRECTED same day to exclude horizontal
  retrace — see machine CLAUDE.md §17 for the two-round owner review).** The machine owns **one
  persistent** buffer, now **928 × 626** `uint[]` BGRA (was 640×480, active-only — see machine
  CLAUDE.md §3 and reference doc §4a "Full raster geometry" for the full derivation; the
  horizontal retrace's 6 char-times are excluded entirely, not rendered). The active 640×480
  "graphics window" sits at a **fixed offset (144, 98)** inside it, every field — a constant
  crop rectangle, not data-dependent. Blanking pixels (the margins outside that rectangle) are
  always flat black; the machine fills them cheaply since no fetch/contention ever happens
  there. Each field writes only its own scanlines (even→even lines, odd→odd) with **no
  inter-field clear**, so the interlace **comb is baked into that single buffer** — there is
  **NO front/back swap chain** (machine CLAUDE.md §3 / reference §4). **Keep this dual even/odd
  per-field write pattern exactly as implemented** — do not collapse it to a single-pass model;
  it's what the 4-way display mode below depends on (machine CLAUDE.md §17, 2026-07-22 WITHDRAWN
  note). At each **field boundary (50 Hz)** the machine hands the UI a **read-only view or a
  fast copy** of the whole buffer; the UI blits either the whole thing or just the
  (144, 98)–(784, 578) sub-rectangle into a `WriteableBitmap`, depending on the new Full-Field
  vs Graphics-window toggle (§8) — and the
  machine keeps writing the next field into the same buffer. Never read mid-render — only take
  the view/copy at the boundary. **Present per field**; the display-mode toggle (§8) chooses
  which field(s)/cadence to present, and the new Full-Field/Graphics-window toggle chooses how
  much of the raster to show — neither changes the machine's timing.
- **Config = topology.** `MachineConfig` (JSON, camelCase properties, enum values in declared
  casing e.g. `"T54"`/`"P2000T"`). Applying a changed topology = build a new machine from the
  config (`new Machine(config)`) — reset-to-apply. `.cfg` load/save is already a machine concern.
- **State capture.** `machine.SaveState` / `LoadState`; `.state` = `"P2ST"` magic + version +
  config-JSON length + config JSON + device stream. Restore = `new Machine(embeddedConfig)`
  then `LoadState`. State is only valid at `AtInstructionBoundary`.
- **Cassette runtime actions.** Mount (`.cas`/`.p2000t`) flips CIP present live; eject flips it
  absent. The host-side `.cas` API (mount/eject/save-as/**create-blank**/directory/write-protect
  — reference doc §5b) is always-fast and independent of `TimingPolicy` (authentic vs turbo).
  "Save as `.cas`" write-back and **create-blank are both now BUILT** (UI milestone 13 — see
  §14.13 and its §18 findings). **Write-protect toggle remains decided-but-unbuilt on both
  layers** — genuinely deferred, see §14.13a. **"Rewind" is NOT a deferred item** (CORRECTED
  2026-07-14): the real MDCR has no rewind button, only Eject — see reference doc §5b, which
  no longer lists rewind as a peer of the other host-API entries.
- **Panning.** `Video.PanX` (0–40) — the special VRAM window's viewport rectangle reads this.
- **Contention overlay hook.** The machine exposes the set of character cells corrupted this
  frame (machine §10). Both the display "show glitches" overlay and the debugger's VRAM window
  consume the same hook.
- **Typed slots.** `machine.Slot1` etc., for the config window to reflect population.

### 3.2 What the machine provides for the debugger (contract additions — now built)
Delivered by **machine milestones 13–15** (`P2000.Machine` CLAUDE.md §3b/§13), now **green** and
living in `P2000.Machine` (locked §2.8), NOT the UI. The UI consumes them for the debugger
milestones (§14); **do not reimplement them in the UI layer.**
- **A read-only state snapshot surface** (machine ms.13): full register file incl. WZ/MEMPTR,
  IFF1/2, IM, flag bits (incl. YF/XF), plus memory reads and the in-frame T-state/cycle
  position. Snapshot-based, taken at a break; never races the core.
- **A Machine-owned breakpoint store** (machine ms.14): execute + memory R/W/X watchpoints +
  I/O-port breakpoints, evaluated inside the tick loop, raising a *break event* the UI observes.
  The UI edits this store; it does not keep its own. (This is what lets the future IDE set the
  same breakpoints.)
- **A command queue drained at `AtInstructionBoundary`** (machine ms.15): run / pause, warm
  reset, cold reset, single-step, step-over, step-out, run-to-scanline, run-to-cycle, set-PC,
  memory write, load-image-to-address ("send code" — for the IDE later), breakpoint CRUD.
  Commands apply at a safe point, symmetric with how host **input** already applies at a frame
  boundary. (Direct memory poke / load-to-RAM mid-run breaks cycle-exact replay for that
  session — same category as turbo cassette; acceptable, flag it, don't forbid it.)
- **A deterministic field-advance surface** the UI's loop drives: `RunField()` (advance one
  50 Hz field, drain the command queue at instruction boundaries, return early on a breakpoint
  hit) + `StepInstruction()`. No wall-clock inside — pacing is the UI's job (§3.2a). The
  early-return + drain behaviours come from ms.14/15; the bare field advance already exists
  (boot/run).

### 3.2a Run-loop host / scheduler — DECIDED: UI-owned now, promotable later
The thread that paces the machine to wall-clock 50 Hz (uncapped for turbo), handles
run/pause/turbo, drains the command queue, and applies queued input at boundaries **lives in
`P2000.UI` (`Runner/`, §12) for this build** — NOT a machine-layer class. It drives the machine's
primitive surface above; `Machine` stays pure (its locked §2.2 forbids wall-clock/threads in
emulation code — satisfied because the loop sits OUTSIDE the core, independent of which project
holds it).
- **Why here now:** the second consumer (the external IDE, §15) is deferred/TBD — don't build a
  shared driver before it exists.
- **Promotion path (recorded — protect it):** when IDE integration becomes current, **lift the
  loop into a machine-layer `MachineRunner` on the identical primitive surface** so UI + IDE
  share one driver — a *move*, not a redesign. Keep `RunField` / `StepInstruction` / `Post` /
  `Snapshot` stable; that stability is what keeps the switch cheap.

### 3.3 The rule
Reads (frames, snapshots) are free and racy-safe (read-only views at boundaries). **Every
mutation is a queued command**, applied at a boundary by the machine/runner. No window ever
calls into the live core directly. If a window needs to *change* something, it enqueues; if it
needs to *show* something, it observes.

---

## 4. Threading model (presentation decoupling)

Per reference doc §3 and machine CLAUDE.md §3:
- **Emulation/runner thread** advances the deterministic machine, produces completed
  framebuffers (swapped at 50 Hz field boundary) and audio sample blocks, drains commands, and
  applies queued input at boundaries.
- **Avalonia UI thread** consumes finished frames and presents at display refresh via
  `Dispatcher.UIThread.Post`. It reads state snapshots for the debugger/watch windows. It never
  blocks the emulation thread and never reads a buffer mid-render.
- **Input** (host key/mouse) queues from the UI thread and is applied by the machine at a frame
  boundary (real input latency, deterministic point).
- The comb glitch and any contention corruption are already in the framebuffer the machine
  hands over — the UI presents, it does not compute them.

---

## 5. Windows

Five windows (reference doc §3a). Each is an MVVM view over a ViewModel fed by a machine
snapshot / the framebuffer view. None mutate the core except by enqueuing commands (§3.3).

1. **Main / display window** — the SAA5050 output as an `Image` over a `WriteableBitmap` (§8).
   Hosts the menu bar + toolbar + status bar (§6). Accepts **drag-and-drop** of `.cas` /
   cartridge (`.bin`/`.rom`) / disk (`.dsk`) images onto the display (Avalonia `DragDrop`),
   complementing the file dialogs. Dropped cassette = **live mount** (runtime); dropped cartridge
   (`.bin`/`.rom`) = **topology change → queued + cold reset**. Dropped disk (`.dsk`) depends on
   the active config (media vs. mechanism, §7): a **runtime insert** if a floppy drive is already
   present (like a cassette swap — no reset), else a topology change that provisions the drive →
   cold reset. Disk mounting is **deferred with the FDC** (§15) — the *rule* is fixed here, not
   the implementation.
2. **Config window** (**non-modal satellite** — `Show(this)`, NOT `ShowDialog`, so the emulator
   display stays interactive while it's open) — the topology axes (§7). Load/save `.cfg`. Changes
   queue and apply on cold reset; the window makes the reset-to-apply nature explicit (an "Apply
   (resets machine)" affordance), except cassette mount which is live.
3. **Keyboard window** — the original P2000 key layout, built from the owner-supplied
   `docs/Keyboard/` photo. Doubles as a **soft keyboard** (click a key → enqueue the matrix
   event, applied at frame boundary like any host key — sticky Shift/CODE for click-based
   modifier holds) and as the **host-key mapping reference**, including a **P2000 Authentic
   (current default, already live) / Standard-Host (new) mode toggle** (§14 milestone 3a — the
   escape hatch for special keys a host keyboard can't reach at all, plus an opt-in alternative
   for anyone who wants literal Windows-keycap symbols instead of the P2000's own shift-pairings;
   row/column matrix positions for CODE and several special keys are still unsourced, see ms.3a).
   Read the layout/labels; the machine models the 10×8 matrix + ghosting.
4. **Debugger window** — full debugger (§10). Purely observer-side.
5. **Cassette "deck" window** — status indicators + the ONE physical control (eject). The MDCR
   is computer-controlled: **NO play/stop/rewind** (the CPU moves the tape via CPOUT). Show:
   **direction** (fwd/rev/stopped from CPOUT FWD/REV), **read/write activity** (RDC toggling =
   reading; WCD/WDA driven = writing — same source as the status-bar activity LED), optional
   **tape position + program directory** (host-side `.cas` API). **Eject** unmounts and flips CIP
   absent; insertion is file-dialog/drag-drop. Authentic/turbo speed is a **config setting**
   (mechanism speed), NOT a deck button. **New (blank) tape** and **Save / Save as `.cas`…**
   (§14 milestone 13) are additional deck actions, host-side container operations like
   mount/eject — neither is a "physical control" in the real-MDCR sense (no such buttons existed
   on the deck); they're the emulator's equivalent of taking a fresh tape out of its shrink-wrap
   and putting a written one back on the shelf.

---

## 6. Control surface + shortcuts

Menu bar + slim toolbar + status bar (reference doc §3a). **No custom title-bar buttons**
(locked §2.5).
- **Toolbar (hottest actions):** Run/Pause, Reset (warm), Reset (cold), Screenshot, Speed/turbo.
- **Status bar:** emulation state (running/paused), **actual vs target speed %**, cassette/disk
  **activity LED** (how the user sees an authentic-mode `.cas` load progressing), current
  **model (T / M)**.
- **Shortcuts:** **F5** run/pause · **F11** reset (warm) · **Shift+F11** reset (cold, clears RAM)
  · **F12** (or PrtScn) screenshot · **F6** toggle turbo/max speed · **F8** single-step (when
  paused; ties to the debugger). **Avoid F1 (Help) and F10 (Windows menu key).**

Every toolbar/shortcut action is a **command enqueued to the machine/runner** (§3.2) — the UI
does not itself pause/step/reset the core.

---

## 7. Config window + axes

Topology changes require a machine reset (reference doc §3a) — queue the new `MachineConfig`,
perform a **cold reset** (`new Machine(config)`), reload any embedded state if applicable. The
window surfaces the axes; the machine owns their meaning.
- **Model selector (top-level axis): P2000T vs P2000M.** Gates the rest (M implies its
  disk/CTC; T offers slot cards). Put it above RAM/slots. (M is deferred in the machine build;
  the selector may present only T until then.)
- **Monitor ROM:** built-in default is embedded/compiled-in (a bare machine boots with zero
  setup — no file dialog, no missing-file failure). Config exposes an optional custom
  `MonitorRomPath` override for patched revisions.
- **RAM configuration (variant):** T/38 16 KB · T/54 32 KB · T/102 80 KB (PTC-96K deferred).
  Driven by the internal-slot board choice below.
- **Internal-slot board (three-way): none / RAM-only / floppy+RAM.** Determines upper memory
  AND whether the FDC/CTC + disk exist ("more RAM" is separable from "disk present").
  - **NEW — board/RAM coupling UI, DECIDED (owner, 2026-07-23), PARTIALLY BUILT as of
    milestone 14 (see `P2000.Machine` CLAUDE.md §17 flag of the same date for the machine-layer
    side):** **floppy+RAM is a single atomic selection, not "board + separate memory dial."**
    Real hardware is one physical card (FDC + CTC + a fixed RAM capacity, all bundled) —
    checking "floppy+RAM" in the UI should just work, immediately implying the FDC, the CTC,
    and the one confirmed capacity (T/102) together, with **no memory-size control shown at
    all** for this board (there's nothing to choose — same reasoning as why there's no
    separate CTC checkbox). **RAM-only, by contrast, IS meant to expose a capacity/bank-count
    control** — it models a homebrew/3rd-party RAM-expansion card (no single official
    product), so a numeric or preset bank-count selector belongs there, not on floppy+RAM.
    **Status update (2026-07-23, after milestone 14):** the Floppy+RAM half is effectively
    already satisfied as a side effect of the milestone-14 board selector — selecting
    Floppy+RAM auto-forces `RamVariant.T102` and disables the RAM selector
    (`ConfigWindowVm.CanEditRamVariant`), so there's already no reachable way to pick a
    different capacity alongside it. **Still open:** RAM-only currently just offers the same
    fixed named tiers (T/38 · T/54 · T/102) via that same disabled/enabled selector, not a
    genuine bank-count dial for an unofficial/homebrew card — the "RAM-only should be a real
    configurable axis, not a picker over three fixed official names" half of this decision is
    not yet built. Practical UI shape once it is: selecting Floppy+RAM should keep hiding/
    disabling any RAM-only-style capacity control entirely (already true); selecting RAM-only
    should show a bank-count control that isn't limited to today's three named tiers.
  - **Drive-config preservation on board removal — DECIDED (owner, 2026-07-23):** switching
    the internal-slot board away from Floppy+RAM (removing the FDC) should **preserve** the
    already-configured floppy drive list (capacity/sides/mounted images), just grey it out —
    not clear it. Switching back to Floppy+RAM restores it exactly as it was. This is a UI/
    `MachineConfig`-retention concern (keep the `FloppyDrives` collection intact in the config
    object even while `Board != FloppyRam`; the machine layer simply doesn't mount any of it
    when the board isn't present), not a machine-layer validation concern. **Not yet verified
    against the milestone-14 implementation** — check whether `ConfigWindowVm`'s board-switch
    handling already keeps `FloppyDriveRows`/`FloppyDriveCount` intact when the board is set
    away from `FloppyRam`, or clears them; the milestone-14 write-up above doesn't say either
    way.
- **Slot population:** SLOT1 (memory-mapped ROM carts — BASIC etc., `.bin`/`.rom`), SLOT2
  (I/O-mapped hardware), internal extension (floppy/CTC). Reflect `machine.Slot1` etc.
- **Disk — drive (mechanism) vs. image (media), split like the cassette:** the **floppy
  drive/controller present?** axis is **topology (reset-to-apply)**; a **disk image in an
  already-present drive is a runtime swap** — insert/eject live, exactly like a `.cas` in the deck
  (the FDC/drive will expose a live disk-change the way the cassette exposes CIP). Mounting a
  `.dsk`/`.img` when the config has **no drive** is therefore a topology change that provisions the
  drive → cold reset (or prompt first — minor UX call). **Deferred with the FDC;** captured now so
  the seam matches the cassette rather than baking in an unconditional reset.
- **Cassette:** `.cas`/`.p2000t` via file dialog / drag-drop. **Live mount (runtime), not
  reset-to-apply.**
- **Display mode + video prefs (§8):** the 4-way mode, integer-scaling, PAL aspect, scanline/CRT
  shader, **show-contention-glitches** toggle, corrupted-cell debug overlay.
- **Audio:** mute + volume.

File extensions (reference doc §3a): ROM/cart = `.bin`/`.rom` (distinguish by config ROLE, not
extension); cassette = `.cas` (primary) / `.p2000t`; config = `.cfg`; state = `.state`; disk =
`.dsk`/`.img` (deferred). Use Avalonia `StorageProvider` for dialogs.

---

## 8. Display / rendering

- **Blit — UPDATED (2026-07-22, width corrected same day, see below):** copy either the
  machine's full framebuffer view (928×626 BGRA) or just its fixed (144, 98)–(784, 578)
  active-window sub-rectangle (640×480), depending on the Full-Field/Graphics-window toggle
  below, into a `WriteableBitmap` sized to match; present in an `Image`; **nearest-neighbour**
  scaling for crisp pixels. Present on the UI thread at display refresh; source is swapped by
  the machine at 50 Hz.
- **Four display modes** (reference doc §3a / machine §3) — a **UI presentation choice over the
  same rendered scanlines**, never a change to machine timing (interrupt/CTC stay per-field).
  **DEFAULT CHANGED (2026-07-21, owner decision):** the P2000TM Field Service manual's
  T-VERSION VIDEO GENERATION section states *"the signal CRS is active during the even
  scanlines of the field. In our system we use only the odd scanlines, so no interlacing is
  used."* Real T hardware has no even/odd field pairing — every field is an independent
  313-line refresh (reference doc §4/§4a). The prior "Interlaced (comb) — DEFAULT" framing was
  BBC-Micro heritage carried over from jsbeeb/MAME (genuinely interlaced machines), not P2000T
  fact. **New default: Odd-only** (mode 4) — it's the one that matches the FSM. This is a
  **P2000.UI-owned setting/preference default**; the machine (`P2000.Machine/CLAUDE.md` §3) only
  needs to expose the raw per-field buffer + `FieldComplete`/`IsOddField` events this depends on
  — it does not own or assert a default itself. Flag for Claude Code: verify/apply this default
  in `DisplayMode.cs` / `DisplayWindowVm.cs` (milestone 6); not yet checked against the actual
  implementation. **IMPORTANT, owner-confirmed 2026-07-22: this is a DEFAULT-VALUE change
  only — do NOT touch the underlying even/odd per-field rendering machinery.** The four modes'
  existing dual-pass computation stays exactly as implemented; Odd-only already produces the
  correct single-field view today. See machine CLAUDE.md §17 (2026-07-22 WITHDRAWN note) for
  the full context — an earlier flag speculating that the per-field write pattern could be
  collapsed to "always a complete image" was retracted specifically to prevent this kind of
  revert.
  1. **Interlaced (comb):** present per field, no inter-field clear → the comb artifact on fast
     motion. No longer authentic-default (no real hardware interlace) — kept as a legitimate
     opt-in extra/nostalgia mode.
  2. **Progressive:** both fields composited per frame, no comb.
  3. **Even-only** / **4. Odd-only — NEW DEFAULT:** single field (odd = the smoothed
     sub-scanlines, matching the FSM's "only the odd scanlines"); field-only defaults to
     **line-doubling** to fill 480. This is now understood to be the AUTHENTIC vertical
     resolution the SAA5050 actually renders (one field's fetched data, line-doubled), not a
     reduced-fidelity fallback.
- **Full-Field vs Graphics-window — NEW (2026-07-22, owner request), a SECOND toggle,
  ORTHOGONAL to the 4-way mode above** (reference doc §3a has the UI-facing spec; machine
  CLAUDE.md §3 / reference doc §4a have the geometry):
  1. **Graphics-window (DEFAULT):** the familiar 640×480 active-picture crop, no visible change
     for existing users.
  2. **Full-Field:** the complete 928×626 raster, including the black leading/trailing
     horizontal margins (9/9 char-times, retrace's 6 char-times excluded entirely — not
     rendered) and black pre-roll/post-roll vertical margins (49/24 scanlines) — what a real
     P2000 + PAL TV also only partially displays as "active video," normally hidden by CRT
     overscan. Authenticity/debug viewing, not the everyday view.
  - Purely a crop choice over whichever buffer the 4-way mode above produced — composes freely
    with all four of those modes, does not interact with or change them.
- **Toggles:** integer-scaling (crisp vs smoothed), **PAL aspect-ratio correction** — **scope
  CORRECTED (2026-07-22, owner catch): applies to Graphics-window only, a no-op in Full-Field
  mode.** Aspect correction reproduces the active picture's standardized real-world relationship
  to a 4:3 CRT tube (at 640×480 pixels are already near-square on 4:3 — close to a straight
  integer scale, not a stretch); the blanking margins have no equivalent standard to correct
  toward (real CRTs never show retrace — beam physically off-screen — and hide most of the
  porch behind bezel/overscan by a set-specific, non-standardized amount). In Full-Field mode,
  disable/grey out this toggle and show the buffer at native pixel geometry instead — see
  reference doc §3a/§4a for the full reasoning (an earlier draft of this doc claimed the
  correction extends cleanly to the full buffer; that was wrong, walked back same day),
  optional **scanline/CRT shader** (the only "scanline gaps" path — do not add a separate gaps
  mode), **show contention glitches**, and a **corrupted-cell debug overlay** (highlights cells
  the machine flagged this frame — the same hook the VRAM window uses; overlay coordinates are
  relative to the active window, so a +144/+98 offset applies when drawing it in Full-Field
  mode — flag for Claude Code, not yet implemented).
- **Screenshot:** serialize the current framebuffer view (whichever crop is currently shown).

---

## 9. Audio

- **OpenAL** via `Silk.NET.OpenAL` or `OpenTK.Audio.OpenAL` (reference doc §2 — Avalonia has no
  audio; avoid NAudio = Windows-only; watch BASS licensing if ManagedBass is considered).
- The machine produces 1-bit beeper square-wave sample **blocks** into a ring across the thread
  boundary; the UI pushes them to the OpenAL source. Mute + volume are UI-side (§7).
- Keep the audio consumer decoupled from frame presentation (its own block cadence).
- **As built (UI milestone 7 / machine milestone 16):** the machine's `SoundDevice` raises
  `SamplesReady(short[])` once per field (882 samples @ 44 100 Hz) with ONE reusable buffer; the
  UI's `AudioEngine` (a 4-buffer OpenAL streaming source with a ~5 ms background refill thread)
  **copies on enqueue** (`Array.Copy` — the machine reuses the buffer immediately) into a
  `ConcurrentQueue`, playing silence on starvation and restarting the source after a stop.
  `Silk.NET.OpenAL` 2.21.0 exposes only unsafe pointer overloads, so the sink uses `fixed`/`&`.

---

## 10. Debugger (full, first implementation)

Reads a machine **state snapshot** each break (§3.2); never races the core. All stepping/
breakpoint edits are **commands** (§3.2). Disassembly uses **`Z80.Disassembler`** over the
shared **`Z80Tables`** (root rule) so the debugger decodes exactly what the core executes.
- **Full register file:** AF/BC/DE/HL + primes, IX/IY, SP, PC, I, R, **WZ/MEMPTR**, IFF1/2, IM,
  flags broken out (incl. YF/XF).
- **Memory watch windows (MULTIPLE, independent):** each an observer over the snapshot with its
  own range; freely spawnable. **Range is explicitly configurable, not fixed at spawn** — a
  "Length" field alongside "Base" (as-built, milestone 12 follow-up, §18 2026-07-14): setting
  either resizes the window to `ceil(length/16)` rows, clamped to `[1, 0x10000]` bytes. Live hex
  + ASCII, refreshed per frame/step; **highlight bytes changed since last refresh** (colour
  flash). Optional **follow a register pair** (HL/SP). **Read-only** for live cell editing (still
  true — this is not a hex editor). **Export/import the whole configured range as a file IS
  supported** (§14 milestone 12: "Save range to file" / "Load file to address" toolbar actions)
  — a bulk file operation over the range, distinct from editing individual cells in place.
  **"Save range to file…" prompts for its own start+length at save time** (as-built; defaults to
  the window's current Base/Length but independently editable), so a one-off export doesn't
  require changing what the window is currently watching.
- **Special VRAM / pan window:** the **80×24** screen buffer (0x5000–0x577F) laid out spatially
  (address = `0x5000 + col + 80*row`), each cell toggleable glyph/hex, with a **rectangle marking
  the visible 40-column viewport** positioned by `Video.PanX`, sliding live as the program pans.
  **Reuse this grid for the contention corrupted-cell overlay** — one window shows what's in
  screen memory, what's visible, and what glitched. Read geometry from the machine **model**
  (T = 80×24), don't hardcode (adapts to M later).
- **Live disassembly around PC** (the spine): PC-relative window that follows execution, PC line
  highlighted a few lines down; auto-scroll on step with a "back to PC" action. **Forward decode
  from PC is exact; backward is a heuristic** (anchor 8–16 bytes back, decode forward, sync to
  PC). Use the monitor-ROM disassembly's named entry points as reliable anchors for ROM. **Show
  raw bytes + mnemonic** (`1234: 21 00 60   LD HL,6000h`). **Symbol resolution** (annotate ports/
  addresses: `OUT (0x10)` → CPOUT, `CALL 0x0038`). **Breakpoint gutter in this same view** (click
  a line to toggle; the disassembly view IS the breakpoint UI). Observer-side only.
- **Breakpoints:** execute + memory R/W/X watchpoints + **I/O-port** breakpoints (the CTC-probe /
  FDC debugging path). Edited here, **stored in the machine** (§3.2).
- **In-frame T-state/cycle counter** (position within the ~50,000-cycle frame — invaluable for
  contention debugging).
- **Stepping:** single-step, step-over, step-out, and — because cycle-exact — **run-to-scanline /
  run-to-cycle N**. All commands drained at instruction boundaries.
- **NOT building:** an in-emulator assembler/editor (scope creep; external cross-assembler +
  load pipeline exists — and is where the deferred IDE hook will plug in).

---

## 11. Save / load wiring

`.cfg` and `.state` are **machine concerns** (machine §11) — the UI is file dialogs + calls, not
serialization logic.
- **`.cfg`:** config window load/save named topologies ("bare T/38", "T/102 + disk"). Loading =
  build a machine (reset-to-apply).
- **`.state`:** save-state feature; save at an instruction boundary; restore = `new Machine`
  from the embedded config header then `LoadState`. Surface version-mismatch rejects/migrates
  from the machine as a user-facing message, don't crash.

---

## 12. Project layout

```
src/P2000.UI/
  App.axaml / App.axaml.cs
  ViewLocator.cs
  Views/            # DisplayWindow, ConfigWindow, KeyboardWindow, DebuggerWindow, CassetteDeckWindow
  ViewModels/       # one VM per window + child VMs (RegisterFileVM, MemoryWatchVM, VramVM, DisasmVM…)
  Rendering/        # framebuffer→WriteableBitmap blit, display-mode present, scaling/aspect/shader
  Audio/            # OpenAL sink, sample-ring consumer
  Input/            # host-key → matrix mapping, enqueue to machine
  Runner/           # owns the emulation loop: paces RunField()/StepInstruction() to 50Hz (uncapped=turbo),
                    # run/pause, command submit, input at boundaries; promotable to a machine-layer runner (§3.2a)
  Assets/           # key-layout data, icons
tests/P2000.UI.Tests/
  ...               # VM logic, blit/mode correctness (headless framebuffer), mapping, snapshot binding
```
Depends on **`P2000.Machine`** (observe + command) and **`Z80.Disassembler`** (debugger decode);
both depend on `Z80.Core`. The UI never references `Z80.Core` directly. One dependency
direction: UI → {Machine, Disassembler} → Core.

---

## 13. Validation gates (not "done" until these pass)

1. **Boot visible:** launch bare → display shows the ROM cassette-wait prompt; status bar shows
   running + model T; activity LED idle.
2. **Mount + run (live CIP):** drag/dialog a real `.cas` (e.g. Ghosthunt) into a running bare
   machine → CIP flips live → ROM auto-loads 'P' → correct colours (validates the 160–255 swap +
   contention + cassette together, per handoff next-step #2). Activity LED tracks the load.
3. **Input:** type into BASIC via host keyboard AND via the soft keyboard (both enqueue at frame
   boundary).
4. **Config reset-to-apply:** change RAM variant / slot → cold reset rebuilds; cassette
   mount/eject stays live (no reset).
5. **Save/restore:** `.state` save then load reproduces identical subsequent frames (machine
   determinism); `.cfg` round-trips.
6. **Debugger fidelity:** disasm at PC matches the core's execution byte-for-byte (shared
   tables); breakpoints (exec/mem/port) fire; VRAM window's viewport rectangle tracks `PanX`;
   corrupted-cell overlay lights under the contention stress routine.
7. **No core races:** windows only ever read snapshots / enqueue commands (assert no direct-core
   mutation path exists).

Gates 6–7 depend on the §3.2 machine-contract additions — now landed (machine ms 13–15 green).

---

## 14. Build order (milestones) — GREEN, THEN COMMIT

Work milestone by milestone. **After each milestone's tests pass green, make a conventional-
commit** whose body summarizes what was built + any non-obvious findings — as the machine/core
builds did. Do not advance while the current milestone is red. Record spec corrections in §18.

1. **App shell + emulation loop + display blit.** Avalonia app, MVVM wiring, the `Runner/`
   emulation loop (§3.2a) driving `Machine.RunField()` on its own thread paced to 50 Hz, and a
   `DisplayWindow` presenting the machine framebuffer view into a `WriteableBitmap`
   (nearest-neighbour) via `Dispatcher.UIThread.Post`. Bare machine boots and renders. → commit.
2. **Control surface.** Menu + toolbar + status bar (state, speed %, activity LED, model) +
   shortcuts (F5/F11/Shift+F11/F12/F6/F8), each as an enqueued command. → commit.
3. **Input.** Host-key → matrix mapping, enqueue at frame boundary; type into BASIC. → commit.
3a. **Virtual keyboard — graphical soft-keyboard window + P2000-authentic mapping mode**
    (fast-follow, same "milestone + a" pattern as ms.9a/13a — closes a gap left open by ms.3,
    not scoped as part of it). Two motivating owner problems (2026-07-14), both rooted in the
    same cause: **ms.3 shipped host-key input, but not the soft-keyboard window §5 item 3 and
    validation gate 3 both already called for** (§5 lists it, gate 3 says "via host keyboard AND
    via the soft keyboard" — no §18 entry exists for ms.3, and nothing built has a keyboard
    window). Consequences:
    - **No way to reach keys with no modern-keyboard equivalent** — the numeric keypad's
      cassette/program-control keys (ZOEK, START, STOP, and others, see photo asset below) have
      no host key to bind to at all, mapping mode aside.
    - **CORRECTED (2026-07-14, owner clarification) — root cause below was wrong, replaced:**
      the owner's original request was misread as "shift+8 currently yields `*`, wrongly." It
      does not. **Owner-confirmed (2026-07-14): the live default ALREADY produces the P2000's
      own shift-row symbols, not the host layout's.** **Full digit-row table, CONFIRMED
      (owner, 2026-07-14, corrected same day — `$` was initially dropped from the list):**

      | Key | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 0 |
      |-----|---|---|---|---|---|---|---|---|---|---|
      | P2000 (shift, live today) | `!` | `"` | `£` | `$` | `%` | `&` | `'` | `(` | `)` | `=` |
      | Windows (shift, US layout) | `!` | `@` | `#` | `$` | `%` | `^` | `&` | `*` | `(` | `)` |

      Consistent with the two anchor points confirmed earlier in the same thread (Shift+2 →
      `"`, Shift+8 → `(`). This is now a confirmed *order*, not just a confirmed *set* — safe to
      use directly as the digit row of the P2000-Authentic mode's (already-live) symbol table
      and as the target table Standard-Host mode must map away from.
    - **So what's actually missing is the reverse of what was first assumed:** the graphical
      soft-keyboard window itself (still not built — see gate/§18 note above) and, as an
      **additive** option alongside the current default, a **"Standard / Host"** mode that
      reproduces the literal Windows-keycap symbol instead — for anyone who wants their typing
      to match what's printed on the keyboard in front of them rather than the P2000's own
      pairing. **The current default is not being replaced or fixed — it already does what the
      owner wants ("act as a P2000") and stays as the default;** "Standard/Host" is new,
      opt-in work, not the other way around as an earlier draft of this milestone had it.
    - **Mechanism behind the current default is UNCONFIRMED — flag, don't assume, and this is
      now the more useful sourcing lead:** per machine ms.8's own findings (machine CLAUDE.md
      §17, 2026-07-04), the `Keyboard` device only accepts **raw (row, column) crosspoint
      presses** — there is no character-level injection path. For shift+8 to already correctly
      produce `(`, ms.3's shipped code must already press SOME matrix coordinate for SHIFT
      together with `8`'s coordinate — meaning **ms.3's existing implementation necessarily
      already encodes a working answer for at least SHIFT's matrix position and the digit row**,
      despite reference doc §5f's "still to confirm" note and despite ms.3 never getting a §18
      findings entry (flagged as unusual when this milestone was first drafted — now explained:
      it shipped a correct, tested, but never-logged mapping table). **Recommended resolution
      path, revised:** ask Claude Code to report ms.3's actual current host-key→matrix table
      (a data-flow check, same pattern as ms.12/13's "flag, don't assume") before designing
      "Standard/Host" mode or the soft-keyboard's special-key positions — it is very likely the
      most direct, already-tested source for SHIFT (and possibly more of the matrix) that exists
      anywhere in this project, more direct than M2000 or fresh disassembly. Fall back to M2000
      (§6: "good behavioural oracle... for keyboard matrix") or disassembly only for whatever
      ms.3's table doesn't already cover (CODE, the special keypad keys, letters/punctuation
      outside the digit row if ms.3 turns out not to handle those positionally too).
    - **CODE key — function unconfirmed, do NOT invent.** Named alongside SHIFT in reference
      doc §5f as a modifier the mapping table must account for, but its actual effect (a second
      shift level? graphics/block-character set, common on similar-era keyboards? something
      else?) is not documented anywhere in this project. Model it as a sticky modifier key with
      an unconfirmed effect; do not assume it produces any specific character set.
      **RESOLVED (2026-07-19, owner, see §18):** CODE's effect is **cartridge/software-dependent,
      not a fixed second shift level** — neither of the two speculated options above. With the
      BASIC cartridge plugged in specifically, it controls **LIST display speed** and is used
      **while editing BASIC program lines**. Confirms modeling it as a bare sticky modifier
      (matrix bit only, no emulator-side character-set logic) was the right call — the ROM/
      cartridge interprets the bit, same as it interprets SHIFT; nothing to build differently.
    - **Photo asset:** `docs/Keyboard/` (owner-supplied photo of a real Philips P2000T
      keyboard) — the visual source for the soft keyboard's key regions, legends, and grouping.
      **My own read of the photo, UNCONFIRMED, needs owner verification before it drives any
      key's built behaviour:** the numeric keypad reads as a 3-column × 5-row grid — row 1:
      `:-` / `x+` / an envelope-icon key; row 2: a cassette-icon key + `7`, a circle-with-dot
      icon key + `8`, `M` + `9`; row 3: `INL` (yellow) + `4`, plain `5`, `OPN` (red-orange) +
      `6`; row 4: `ZOEK` + `1`, a `↔` icon + `2`, `START` + `3`; row 5: `DEF` + `0`, a
      flag/pennant icon + `00`, `STOP`. **Legend transcription only — matrix position AND, for
      several keys, even ROM-level function are separately unsourced** (see above and below).
    - **Several legends have no documented function at all — flag, don't guess:** `INL`, `OPN`,
      `DEF`, and the envelope/cassette/circle-dot/flag icon-only keys appear on the photo but
      are not named or explained anywhere in `P2000T-reference.md`. Only **ZOEK, START, STOP**
      are independently confirmed (§5b "BASIC↔cassette UI surface": "START (run loaded
      program), STOP (halt), ZOEK/search (show cassette index)") — those three legends' ROM-level
      *meaning* is sourced even though their matrix *position* still isn't. **WIS** (the fourth
      member of that same confirmed list — "clear cassette dialog") has not been located on the
      keypad in the photo read above at all; either it's one of the unlabelled icon keys (don't
      guess which) or it sits outside the numeric keypad entirely (e.g. main keyboard block) —
      open item.
      **RESOLVED (2026-07-19, owner, see §18):** WIS = Shift + the numpad `7`/cassette-icon key
      (port 0x06 bit 3) — the same key ms.3 already maps to host `NumPad7`. No new matrix
      behaviour; the key was always reachable, only its shifted meaning was undocumented.
    - **UI:** new soft-keyboard window (§5 item 3, already spec'd: click a key → enqueue the
      matrix event at frame boundary, same as any host key). **Sticky Shift** (click to latch,
      click again or press a regular key to release — matches how a real keyboard's physical
      shift differs from a mouse click, which can't be "held"); **sticky CODE** likewise, pending
      its function being sourced. A mode toggle — **"P2000 Authentic" (current default, already
      live, unchanged by this milestone) / "Standard-Host" (new)** — visible on this window,
      applying to BOTH host-key input and, where meaningful, the soft keyboard's own
      shifted-click behaviour. Special keys with no host equivalent (ZOEK/START/STOP/etc.) are
      soft-keyboard-only regardless of mapping mode — there's no host key for either mode to
      translate from.
    - **Tests:** (a) **regression guard, not a new behaviour:** P2000 Authentic mode (the
      existing default) is unchanged by this milestone — host Shift+8 still enqueues whatever
      matrix event ms.3 already sends and BASIC still echoes `(`; same for Shift+2 → `"`; (b)
      Standard-Host mode: host Shift+8 (US layout) produces the literal Windows-keycap symbol
      (`*`) instead, via whatever P2000 key/combo (if any) produces that character — flag,
      don't guess, if no P2000 key produces a given host symbol at all; (c) soft-keyboard click
      on a QWERTY-block key (confirmed positionally from ms.3) enqueues the identical matrix
      event a host keypress for that key would; (d) sticky Shift latches across a soft-keyboard
      click and releases after exactly one subsequent key; (e) any special key without a sourced
      matrix position is either absent from the built window or clearly marked unavailable —
      never wired to a guessed coordinate. → commit.
4. **Cassette deck + mount/eject.** File dialog + drag-drop mount (live CIP), eject, status
   indicators (direction, R/W activity), directory. RUN a real `.cas` end-to-end. → commit.
5. **Config window + `.cfg`.** Axes, load/save, reset-to-apply (cold reset) with the cassette
   runtime exception. → commit.
6. **Display modes + video prefs.** 4-way mode, integer-scale, PAL aspect, scanline shader,
   show-glitches + corrupted-cell overlay. Headless blit/mode tests. → commit.
7. **Audio.** OpenAL sink consuming the sample ring; mute/volume. → commit.
8. **Save-state UI.** `.state` save/load via dialogs; version-mismatch messaging. → commit.
9. **Debugger — observer core.** Register file, multiple memory watches, VRAM/pan window with
   viewport rectangle + corrupted-cell overlay. *(Depends on the §3.2 snapshot surface.)*
   → commit.
10. **Debugger — disassembly + breakpoints + stepping.** Live disasm around PC (shared tables,
    symbols, byte column), breakpoint gutter, exec/mem/port breakpoints, step/over/out,
    run-to-scanline/cycle. *(Depends on the §3.2 breakpoint store + command queue.)* Tag
    `P2000.UI` T-baseline. → commit.
11. **Symbol tables / ROM labels (debugger).** Load an external symbol file and annotate the
    disassembly + debugger with names (reference doc §3a "Symbol resolution — DESIGN DECISION").
    Builds on ms10's inline symbol hook; post-T-baseline enhancement.
    - **Pluggable parser (`ISymbolFileParser`) → `(name, value, [bank], [type])`.** Ship the
      **z80asm** parser (`label:⇥equ $hex`) first; leave the seam for sjasmplus / z88dk `.map` /
      WLA-DX·no$ / VICE, deferred until a user has that toolchain (don't write them speculatively).
      Detect format by extension + first-line sniff.
    - **Typed, context resolution — NOT a flat address map.** Classify symbols into
      code/data/port/const buckets (format type-hint if present, else address-range + name-prefix
      heuristics; multimap for N-names-per-address; user-overridable). Resolve each disasm operand
      against the bucket matching its KIND (code target / data ref / port / immediate) so
      ports/constants don't mislabel low addresses; constants annotate immediates as trailing
      comments, never labels.
    - **Prerequisite:** confirm `Z80.Disassembler` exposes each operand's value + kind (not just a
      formatted string). If strings only, do Phase 1 and add operand typing before Phase 2.
    - **Phase 1 (core):** code labels on disasm line addresses + jump/call/branch targets (code
      bucket). Per-ROM scoping (monitor `.sym` vs cartridge/CP-M); bank-carrying formats resolve
      against current banking state.
    - **Phase 2 (fast-follow):** port/data/const operand annotation; break-at-symbol, go-to-symbol,
      symbols in the PC + call-stack display.
    - **Tests:** (a) z80asm parser round-trips `MonitorRom.sym` (433 symbols; duplicate addresses
      preserved as a multimap); (b) `OUT (0x88)` resolves to `CTC_CH0` while the disasm line at
      `0x0088` does NOT get the port name; (c) `BIT_MOTON $0002` never labels address 0x0002;
      (d) an unknown-format file is rejected cleanly with a clear message. → commit.
12. **Debugger — memory watch export/import.** Save a memory watch window's configured range to
    a file, and load a file into RAM at an address — the missing piece for pulling machine code
    (e.g. a routine loaded from `.cas`/disk into RAM by BASIC) out of a running session for
    offline disassembly, and pushing it back in. **Motivating case:** the "JWS Systeem Disk"
    writer loads from a `.cas` as a short BASIC wrapper around a machine-code routine — this
    milestone is what lets the owner pull that routine out of RAM into a file.
    - **Data-flow check — confirmed no new machine primitive needed, both directions:**
      - **Export** needs only the memory-read half of the **already-shipped snapshot surface**
        (machine ms.13, §3.2 first bullet — "full register file... plus memory reads"). The UI
        reads the watch window's configured `[start, start+length)` range out of the current
        snapshot and writes it to a file. No machine change.
      - **Import** targets the **already-shipped command queue's `load-image-to-address`**
        (machine ms.15, §3.2 third bullet). That command was scoped in ms.15 for the *future*
        external IDE ("send code") and has had **no real caller until now** — this milestone is
        its first consumer. **Verify, don't assume, its exact signature** (byte payload + target
        address; confirm it accepts an arbitrary length and doesn't require a pre-existing
        image/cartridge shape) before wiring the UI call — if the signature doesn't already fit
        "arbitrary bytes at an arbitrary address," that's a small machine-side gap to flag back
        per §17, not a reason to add a second machine primitive.
      - This confirms the owner's own read of the situation: the data-flow plumbing already
        exists on the machine side: the work here is entirely new UI.
    - **File format:** raw binary, no header — exactly the `length` bytes of the watch window's
      configured range, nothing else. Matches what `load-image-to-address` already expects to
      push back in; do not invent a wrapper format for a one-range dump.
    - **UI surface — extends the existing memory watch window, no new window type:** two toolbar
      actions on each memory watch window (§10):
      - **"Save range to file…"** — dumps the window's current `[start, length)` from the live
        snapshot to a chosen path.
      - **"Load file to address…"** — file picker + an editable target-address field (defaulted
        to the window's own range start, but not required to match it — loading to a different
        address than the window happens to be watching is a legitimate use), then enqueues
        `load-image-to-address` with the file's bytes. Reject/flag files whose length would run
        past the top of addressable RAM rather than silently truncating or wrapping.
    - **Does NOT reopen the §10 "Read-only" decision.** Cell-by-cell live editing of a memory
      watch window is still out of scope — this is a bulk file operation over the whole
      configured range, not a hex editor. Keep the two clearly separate in the UI (distinct
      toolbar actions, no inline-edit affordance added to the grid).
    - **Same non-determinism caveat already on the books for `load-image-to-address`** (§3.2:
      "Direct memory poke / load-to-RAM mid-run breaks cycle-exact replay for that session —
      same category as turbo cassette; acceptable, flag it, don't forbid it"). No new decision
      needed here, just carried forward: importing mid-run is allowed, not hidden from the user,
      and not something `.state` replay needs to reproduce.
    - **Tests:** (a) export a known range, re-import at the same address, verify byte-identical
      round-trip; (b) export while paused vs. while running produces the live-at-that-moment
      snapshot in both cases (no "stale until next break" surprise); (c) import at an address
      outside current RAM size for the configured topology is rejected with a clear message, not
      a silent wrap/crash; (d) importing a file larger than the watch window's own configured
      length is allowed (target address is independent of the window's range) — only the RAM-size
      bound in (c) applies. → commit.
13. **Cassette deck — New (blank) tape + Save/Save-as wiring.** Closes the gap between what
    reference doc §5b already decided for the host-side `.cas` API (create-blank, save-as, among
    others) and what's actually reachable from the UI today (only mount/eject/directory). Two
    new deck actions (§5), same host-side-container-operation category as mount/eject (§3.1) —
    always fast, independent of `TimingPolicy`.
    - **"New (blank) tape."** Mounts a fresh, empty, **unbacked** tape (no file path) live — the
      same CIP-flip-live runtime exception a file-dialog mount already uses (§3.1/§7: cassette
      is the one reset-to-apply exception). If a tape is already mounted, this behaves like
      eject-then-insert-blank as **one** live CIP transition, not two — swapping to a blank tape
      is a legitimate live operation on real hardware (pull one cassette, push in another), not
      a topology change.
    - **Machine-layer check — flag, don't assume (unlike milestone 12's clean two-sided
      answer):** confirm `MdcrDevice`'s current mount entry point before wiring this. The only
      sourced evidence for "blank tape" so far is `MiniTape`'s ms.9a unit test (a blank
      in-memory tape written to via CSAVE, then serialized) — that is NOT the same claim as "a
      host-triggered live CIP-mount from nothing, bypassing `LoadCasImage`, already exists." If
      the current mount entry point requires an actual `.cas` byte stream to parse,
      create-blank needs a small additive machine-layer entry point (e.g.
      `Mount(MiniTape.CreateBlank())` or a `MountBlank()` overload) that skips parsing and
      starts the tape at BOT with zero blocks — same shape as the existing mount, not a new
      subsystem. Per §17 this touches `P2000.Machine`'s canonical contract: **report it back
      rather than adding it to the machine layer from here.**
    - **No format step — confirmed with the owner (2026-07-14).** Unlike disk, the P2000
      cassette has no distinct format/init command. A blank tape is immediately writable: CSAVE
      appends at the head position (BOT on a fresh tape), matching the already-documented
      "append on blank tape" behavior (reference doc §5b "Replace vs append"). Do not build a
      "format tape" affordance — there is nothing for it to do.
    - **"Save" / "Save as `.cas`…"** — write back the currently-mounted tape's content via the
      machine's existing serializer (ms.9a, `MiniTape.Save`/`MdcrDevice.SaveTape`; **no machine
      change needed here** — this half of the gap is UI-only, the mirror image of "New (blank)
      tape"'s uncertain half). **"Save"** reuses the tape's existing backing path if it has one
      (loaded via file dialog/drag-drop, or a prior save-as); behaves like **"Save as…"** only
      when the tape is unbacked (e.g. fresh off "New (blank) tape"). Available any time a tape
      is mounted, live, independent of run/pause state.
    - **Erase tape — confirmed real by the owner, NOT modeled here.** A separate ROM/BASIC-level
      command distinct from host-side create-blank (it wipes/reuses an *already-mounted* tape
      from within a running program, rather than mounting a fresh one from the host side).
      Mechanism (BASIC keyword / ROM entry point) is **not yet sourced** — flagged as an open
      item, needs disassembly or a manual before it can be modeled beyond what "Replace vs
      append" already implies. **Decided: no dedicated UI for it.** It's a program the user runs
      like any other (keyboard/BASIC); the existing activity LED + directory view already
      surface it happening. Do not add an "Erase" button.
    - **Write-protect toggle: still open, decided-but-unbuilt, out of scope here** (§3.1 lists
      it in the API parenthetical; not wired at either layer) — genuinely deferred, not pulled
      into this milestone's deliverable.
    - **Rewind: RECLASSIFIED (2026-07-14), not a peer of write-protect above.** The real MDCR
      has **no rewind button, only Eject** (owner-confirmed; matches §5's "NO play/stop/rewind"
      note) — tape position only resets via ROM-driven REV over CPOUT (software, already
      modeled) or implicitly at the host level, since both mount paths already start the tape
      at BOT (eject-then-reinsert already gets this for free). Unlike write-protect, there is
      no physical control being deferred here — reference doc §5b's host-API list corrected
      to stop listing "rewind" as a peer entry. Not scoped anywhere; if ever wanted, it'd be a
      pure convenience shortcut (skip the ROM's own rewind), not a gap to close.
    - **Tests:** (a) "New (blank) tape" flips CIP present live with no reset, machine keeps
      running; (b) CSAVE a program from BASIC onto a freshly-blanked tape, directory shows it;
      (c) "Save as `.cas`…" on that tape, then "New (blank) tape" again + file-dialog-load the
      saved file + CLOAD reproduces the program byte-identical (end-to-end UI round-trip of the
      ms.9a machine-level test); (d) "Save" (not save-as) on a tape loaded from an existing file
      overwrites that same path without re-prompting; (e) "New (blank) tape" while a different
      tape is already mounted performs exactly one CIP transition, not an observable
      eject-then-insert flicker. → commit.
13a. **Cassette deck — write-protect toggle** (fast-follow, same "milestone + a" pattern as
    ms.9a for the cassette write path — discovered as a near-blocker while exercising
    milestone 13, not scoped as part of it). Reference doc §5b/§5f already frame write-protect
    as a **host-side, physical-tab-style concept** — not derived from the `.cas` file, not a
    property of the data, purely something the owner controls, exactly like snapping the tab
    out of a real cassette. That control was decided but never built on either layer.
    - **Reported symptom (owner, 2026-07-14):** the cassette reads as always write-protected,
      with no UI to change it. **This does NOT match milestone 13's own test evidence** — its
      `MdcrDeviceTests` confirm a freshly-`InsertBlankTape()`'d tape is unprotected (WEN clear)
      and immediately writable. So the symptom is likely specific to the **file-loaded path**
      (`InsertTape()`/`MiniTape.LoadCasImage`) or is purely a missing-control issue (nothing is
      ever protected OR unprotected because nothing can set it either way, and whatever the
      constructor-default happens to be is all anyone ever sees) — **root cause not confirmed
      from this side; Claude Code should check `InsertTape()`'s `IsProtected` handling
      specifically before assuming it matches `InsertBlankTape()`'s (already-correct) default.**
    - **Decision:** `IsProtected` defaults to `false` (writable) on **every** mount path, file-
      loaded or blank alike — matches "a fresh/found cassette is writable until someone
      protects it," the same default `InsertBlankTape()` already has. `MdcrDevice` gets a live
      setter (`SetWriteProtected(bool)` or equivalent) — host-side, always-fast, independent of
      `TimingPolicy`, same category as mount/eject/create-blank/save-as.
    - **Persistence — RESOLVED (owner proposal, 2026-07-14 — full detail in
      `P2000.Machine/CLAUDE.md` §17):** protect state now DOES round-trip through a saved
      `.cas` file, using previously-unspecified padding in the record container (offset `0x50`,
      bit 0 — never the on-tape phase encoding, so no hardware/CRC impact). An unset or absent
      bit reads as writable for any file, old or new, from this emulator or elsewhere — fully
      backward-compatible by construction. UI-side implication: the write-protect toggle's
      state is whatever `IsProtected` reads on the live `MiniTape`, which now survives a
      Save → reload round-trip rather than resetting every mount — no separate UI-layer
      persistence logic needed, it falls out of the machine-layer fix.
    - **UI:** a write-protect toggle on the cassette deck window (§5), reflecting/controlling
      the mounted tape's WEN state live. Meaningless/disabled with no tape mounted (matches the
      existing bare-machine CPRIN default, where WEN is don't-care at CIP-absent).
    - **Tests:** (a) a freshly file-dialog-mounted tape with no protect byte set (any
      pre-existing/foreign `.cas`) defaults writable (the regression check for the reported
      symptom); (b) toggling protect live flips WEN without touching CIP/BET; (c) CSAVE onto a
      protected tape is rejected via the ROM's own already-modeled WEN check — confirms the
      toggle actually gates writes, not just a cosmetic status bit; (d) **protect state now
      correctly persists, not resets:** protect a tape → Save as `.cas` → reload → still
      protected (record offset `0x50` bit 0 round-trips); (e) mounting a genuinely fresh blank
      tape (`InsertBlankTape()`) is unaffected — still defaults writable, since there's no prior
      saved state to read. → commit.

14. **Disk drive UI** (promoted from §15 "Disk / FDC UI"; unlocks with machine-layer M20 —
    `P2000.Machine` CLAUDE.md §13.20 — multi-drive floppy subsystem). Media/mechanism rule
    already fixed (§5/§7): drive (count/capacity/sidedness) = topology, disk image mounted in a
    drive = runtime swap, exactly like the cassette. This milestone is the disk analogue of the
    cassette deck (ms.4/9/13/13a) — same pattern, one drive-count fan-out.
    - **Config window — new "Floppy drives" axis (§7):** drive count selector. **UPDATED
      (2026-07-23, owner-supplied full M2200 manual — `P2000.Machine` CLAUDE.md §13.20, resolving
      most of what this bullet originally left open): cap at 4, not 2.** The M2200 board's own
      34-pin connector is CONFIRMED to carry four drive-select lines (`DRISEL0`-`3`), decoded from
      the FDC chip's native US0/US1 via an external decoder — a real, sourced hardware ceiling,
      not the earlier unconfirmed 2-drive guess. The stock ROM driver still only ever addresses
      unit 1 by default (unaffected by this change — that's a software fact, not a connector
      fact). **RESOLVED (owner, 2026-07-23):** the plain single-purpose Philips floppy+RAM board
      also supports 4 drives — a separate, official Philips-authored P2000 manual confirms it, and
      the earlier "2 drives" figure traced to a poor Field Service Manual scan. No board-specific
      hedge needed: **4 is the confirmed ceiling regardless of which board this UI targets.**
      **Independently re-confirmed (2026-07-23):** the design-doc maintainer has since read the
      referenced manual in full (`raw-conversion.md`) — Ch2 states 4 drives/560k directly. No
      change to the drive-count cap or the per-drive selectors below; see `P2000T-reference.md`
      §5d for the citation. Per
      enabled drive: a **Capacity** selector (35/40/80 tracks) and a **Sides**
      selector (SS/DS) — both reset-to-apply, both act only as the **seed for blank/unlabeled
      media**, since the machine auto-detects real geometry from the on-disk label once an image
      is mounted (M19/M20) — don't let the UI imply the selector overrides a present label.
    - **New "Disk drive(s)" window — RESOLVED (owner, 2026-07-23): one window, DRIVE TABS, one
      tab per configured drive.** Supersedes the "N status rows vs. N separate windows" framing
      below as a genuinely open UX call — it's decided now. Each tab owns everything currently
      described as a "per-drive row": mount/eject, directory browse, live status, write-protect,
      New/Save/Save-as. **This also resolves the main-window `.dsk` drag-drop target ambiguity**
      flagged as blocking in the milestone-14 write-up (`P2000.UI` CLAUDE.md §17, "not built"
      list) — with N drives there was no way to tell which drive a dropped file was meant for;
      with tabs, a drop lands on whichever drive's tab is currently active/focused, exactly like
      dropping a file onto a specific document tab in an editor. No separate target-picker UI
      needed. Tab header should show enough per-drive summary (drive index, mounted filename or
      "empty", dirty-asterisk) that the user can tell tabs apart without opening each one.
      For each configured drive (i.e., within its tab) —
      - **Mount/eject**, file dialog + drag-drop, **runtime** (extends the existing main-window
        `.dsk` drag-drop rule, §5.1 — no reset once the drive itself already exists).
      - **New (blank) disk + Save / Save as `.dsk`…** (owner decision, 2026-07-23 — mirrors
        ms.13's cassette New-blank-tape/Save/Save-as exactly, same host-side-container-operation
        category as mount/eject): "New" creates a genuinely unformatted in-memory image sized to
        the drive's own configured Capacity/Sides (no label, no directory — machine-layer M20)
        without touching a file; "Save"/"Save as" writes the current in-memory image (whether
        mounted-from-file-then-modified, or newly created) out to a host `.dsk` file. Per the
        machine layer's now-resolved buffered write model (M20): **nothing reaches the host file
        until Save is clicked** — ejecting or resetting first silently drops unsaved changes,
        same trade-off the cassette deck already carries. **Warns on eject/replace with unsaved
        changes — see ms.14a below** (owner decision, 2026-07-23, resolves what this bullet
        originally left open).
      - **Directory browse table** — filename, extension, type, blocks used, size — sourced from
        the host-side `DskImage.ReadDirectory()` API (M19) / `docs/JWSDOS-format.md` §4's
        32-byte directory-entry fields. **Side 2 stays unavailable** for a DS-mounted image until
        the machine layer sources side 2's directory offset (same open item M19/M20 carry
        forward, `docs/JWSDOS-format.md` §7 item 2) — show side 1 only, don't guess or leave a
        blank table that looks like an error.
      - **Live status row** — head (0/1), track/cylinder, sector, motor (on/off), read/write
        activity + direction, write-protected/write-enabled — same activity-LED sourcing pattern
        as the cassette deck (§5/§6: derive from device state, not from guessing at command
        intent).
        - **Head and sector when idle vs. active — RESOLVED (owner, 2026-07-23), reopening what
          milestone 14 scoped out as "flagged rather than guessed":** neither is a real
          persistent register on idle hardware, but BOTH are real, recoverable state during an
          active operation — reach into the FDC emulation's own internals for them rather than
          leaving them blank whenever something is actually happening.
          - **Idle (no command in flight): show "–" for both head and sector.** Matches real
            hardware — there's nothing to read when the drive isn't doing anything.
          - **Active (read/write/format/seek — any multi-step operation, not just the
            already-modeled READ/WRITE DATA case):** show the REAL current value, sourced from
            `Upd765`'s own internal transfer-tracking, not guessed from the command bytes alone.
            Head: already available today via `CurrentTransfer.Head` (M14) — just needs
            surfacing for whichever command is active, not only read/write. Sector: extend
            `Upd765`'s transfer-status tracking with a running current-sector value — the chip
            already knows the starting sector (R, from the 9-byte command block) and how many
            bytes have moved through the semi-DMA byte-loop (`0x8D`/`INI`) so far; deriving
            "which sector is this" from bytes-transferred-so-far ÷ bytes-per-sector (wrapping at
            EOT per normal CHS increment rules) is exposing state the chip already implicitly
            tracks, not inventing new state. For a single-sector command this is just R itself
            (satisfies "at least the starting sector is knowable" for the simple case); for a
            multi-sector run it should advance live as the transfer progresses, not stay pinned
            to the starting value.
        - **Motor is a single shared line, not per-drive — CONFIRMED (2026-07-23, M2200
        manual, `P2000.Machine` CLAUDE.md §13.20's per-drive-device-state bullet).** The real
        34-pin connector has exactly one `MOTORON` signal for the whole card, not one per drive.
        **Design implication:** showing an independent "motor on/off" indicator per drive row
        would misrepresent the hardware — either show ONE board-level motor indicator (outside
        the per-drive rows) or, if a per-row indicator is kept for layout-consistency reasons,
        make clear (e.g. via a shared/greyed visual treatment) that all rows reflect the same
        single signal rather than N independent ones. Don't build N independently-wired motor
        indicators as if the hardware supported that.
      - **Write-protect toggle**, per drive, mirrors ms.13a's cassette write-protect UI exactly
        (live setter, defaults writable, disabled with no image mounted). Unlike the cassette,
        this does **not** persist through the image file itself (M20 flag) — surface this as a
        per-session state, or wait for the machine layer's sidecar-file decision before wiring
        persistence UI.
    - **Tests:** `DiskDriveVm`-level tests mirroring `CassetteDeckVmTests`' pattern — mount/eject
      state transitions; directory parse against the `Spel1.dsk`/`jwssytem.dsk` fixtures already
      used at the machine layer (18 real entries, no phantom stale-cluster entries, empty-track
      browses as empty not error); write-protect toggle actually gates a simulated write (not
      just a cosmetic bit); status fields (head/track/sector/activity) update live across a
      scripted read/write sequence; a second drive's head/track/sector/activity status is
      independent of the first's (no shared-VM state bleed between drive rows) — **motor is the
      one exception, correctly shared**: motor-on in one row's VM must reflect as on in every
      other configured drive's row too, since it's the same physical signal (regression guard for
      the shared-motor finding above, not a bug if rows agree); **New creates a blank image at the
      drive's
      configured geometry with an empty directory listing** (regression guard mirroring ms.13's
      blank-tape test); **Save/Save-as round-trips** a modified or newly-created image
      byte-for-byte on reload, matching ms.13's own CSAVE-then-reload test shape. → commit.
14a. **Cassette + disk — unsaved-changes warning on eject/replace** (fast-follow, same
    "milestone + a" pattern as ms.9a/13a — a retrofit onto the **already-shipped** cassette
    deck (ms.4/9/13/13a) as well as the new disk drive window (ms.14); owner decision,
    2026-07-23). Depends on the machine layer exposing a dirty/unsaved-changes signal
    (`P2000.Machine` CLAUDE.md §13.20a) — do not build a UI-only heuristic (e.g. "any write
    happened this session") if that signal exists; wire to it.
    - **Trigger conditions — both windows, same rule:** **Eject** with the current
      cassette/disk dirty; **replacing** a mounted image (file-dialog/drag-drop of a new file, or
      New-blank) over a dirty one — both count as "about to discard unsaved changes." A
      **cold/warm reset** with a dirty cassette/disk mounted is the same hazard in spirit but is
      an existing, already-shipped control (§6) — **flag, don't silently fold reset into this
      milestone's scope**; ask whether reset should also warn, or stays as today, before adding
      it.
    - **UI:** a confirm dialog ("This tape/disk has unsaved changes — eject/replace anyway?"
      Discard / Cancel) blocks the eject/replace only when dirty; a clean cassette/disk
      eject/replaces exactly as it does today, no new friction. Cancel leaves the current
      image mounted and untouched.
    - **Not in scope (flag, don't build):** an auto-save-on-eject shortcut, or a three-way
      "Save / Discard / Cancel" dialog that saves inline — the owner asked for a **warning**,
      not a silent-save; if a save-inline convenience is wanted later, that's a separate,
      explicitly-scoped follow-up.
    - **Tests:** (a) eject/replace with a clean cassette or disk proceeds with no dialog
      (regression guard — this must not add friction to the common case); (b) eject/replace with
      a dirty cassette or disk shows the dialog; (c) Cancel leaves the image mounted, still
      dirty, unchanged; (d) Discard proceeds with the eject/replace exactly as today (post-M20/
      ms.9a semantics — in-memory changes are lost, same as clicking through today's silent
      eject); (e) after an explicit Save/Save-as, eject/replace of the now-clean image shows no
      dialog. → commit.

---

## 15. Deferred (build the seams now, implement later)

- **External IDE / cross-dev interface** — transport + protocol **TBD** (owner decision).
  Attaches to the SAME observer + control + breakpoint contract this UI consumes (§3), which is
  why that contract lives in `P2000.Machine`, not the UI. Candidates noted for later (gdbstub /
  DAP / in-process) but not chosen; lessons from this UI build inform it. Do NOT build it now,
  and do NOT let UI-specific assumptions leak into the shared contract. When it becomes current,
  **promote the UI's emulation loop (`Runner/`, §3.2a) into a machine-layer `MachineRunner`** on
  the same primitive surface so UI + IDE share one driver — a move, not a redesign.
- **P2000M UI differences** (VRAM geometry in the VRAM window reads from model — already
  parameterized; M itself deferred in the machine).
- **80-column display**, **hires overlay** presentation — once the machine supports them.

(**Disk / FDC UI dropped off this list as of milestone 14** — §14.14, now that the machine's FDC
+ multi-drive subsystem has a milestone (M20) to unlock it.)

---

## 16. Coding conventions

Inherit root `CLAUDE.md`. UI-specific: MVVM discipline — no emulation or mutation logic in
code-behind or views; VMs bind to snapshots and enqueue commands only. No wall-clock or core
access on the UI thread beyond reading the handed-over framebuffer view / snapshot. Name every
machine command and port/address symbol (no scattered literals). Keep rendering (blit + mode)
free of Avalonia-control assumptions where it can be headless-tested.

---

## 17. When to ask the human

Ask before: changing a locked decision in §2; choosing the external-IDE transport/protocol
(explicitly deferred, §15); or **finalizing the shape of the §3.2 machine-contract additions**
(the observer snapshot surface, the Machine-owned breakpoint store, the command queue, and the
runner/scheduler) — these change `P2000.Machine` and its CLAUDE.md, so reconcile them with the
machine owner rather than inventing them UI-side or editing the machine's canonical file
unilaterally (handoff "divergence caution"). Ordinary in-project UI choices: proceed, keep CI
green, and log findings in §18.

---

## 18. Findings log (working scratchpad — synced to the reference doc by the human)

Append a dated entry whenever implementation corrects, clarifies, or adds to the spec/reference
doc (see §14). Format: date, milestone, what was assumed → what turned out true, and where it
applies (file/section). Keep entries short and factual. The human periodically syncs these into
`docs/P2000T-reference.md` (§3a) and marks them synced. Do NOT edit the reference doc from this
project.

<!-- Template:
### YYYY-MM-DD — Milestone N: <short title>
- **Assumed:** …
- **Found:** …
- **Applies to:** reference doc §3a / <file>
- **Synced:** yes (YYYY-MM-DD)
-->

**2026-07-24 — trimmed for size.** This log had grown to ~1300 lines. Every entry was
checked against `P2000T-reference.md` — several stale "Synced: no" flags were corrected
(the content was already synced, just never marked), and two small genuine gaps (the umlaut
key correction, the (5,0) key's function pair) were found and synced this same pass. The
full historical log (every entry, unedited) now lives in
`docs/CLAUDE_ui_findings_archive.md` for posterity. What's kept live below: entries still
genuinely open, plus the last few active days, for continuity. Everything fully resolved and
already synced lives only in the archive now — check there before assuming something's
missing.

### 2026-07-24 — Milestone 14a IMPLEMENTED: cassette + disk unsaved-changes warning
- **Machine-layer signal was already built and green (M20a, `P2000.Machine` CLAUDE.md §13.20a)
  — this milestone was purely the UI-layer wiring** it was scoped for: `MdcrDevice.IsDirty`/
  `MarkClean()` and `DskImage.IsDirty`/`MarkClean()` needed no changes.
- **Gate lives in the VM, not the view:** both `CassetteDeckVm` and `DiskDriveVm` gained a
  `ConfirmDiscardRequested` event (`Func<string, Task<bool>>`) and a private
  `ConfirmDiscardAsync(action)` helper that reads the live machine-layer `IsDirty` bit directly
  (not a cached/throttled observable) and short-circuits to "proceed" when clean or when no
  view has subscribed (keeps headless tests dialog-free by default). `EjectAsync`/
  `NewBlankTapeAsync`/`NewBlankDiskAsync` (renamed from their sync `Eject`/`NewBlankTape`/
  `NewBlankDisk` forms — CommunityToolkit's source generator strips the `Async` suffix, so the
  generated `EjectCommand`/`NewBlankTapeCommand`/`NewBlankDiskCommand` names, and therefore
  every existing XAML binding, were unaffected) all await the gate before mutating.
- **Mount (file-dialog + drag-drop) needed a new gated entry point, not a retrofit onto
  `MountBytes`:** `MountBytes` stays the raw, unconditional primitive (existing unit tests call
  it directly and still pass unchanged — it's also still the right tool for a mount that
  shouldn't prompt, e.g. `.state` restore). Added `TryMountBytesAsync` alongside it — runs the
  same discard-confirmation, then calls `MountBytes` — and repointed every user-facing mount
  caller at it: `CassetteDeckVm.MountAsync` (file dialog), `DiskDriveVm.MountAsync` (file
  dialog), `DisplayWindow.OnDrop` (cassette drag-drop), `DiskDriveWindow.OnDrop` (disk
  drag-drop).
- **`DiskDriveWindowVm` relays per-drive `ConfirmDiscardRequested` up to the window**, same
  aggregation pattern already used for `ShowMessageRequested` (one `TabControl`, N drives, one
  dialog owner). `CassetteDeckVm` has no such container — its window binds directly.
- **View-side dialog is a small Discard/Cancel `Window`**, same visual shape as the existing
  error dialog in both `CassetteDeckWindow`/`DiskDriveWindow` code-behind (not extracted to a
  shared helper — the two windows already duplicated the error-dialog code before this
  milestone; kept that existing pattern rather than introducing a new shared-dialog module as
  an unscoped refactor).
- **Confirmed empirically, not just assumed, that the async conversion doesn't break existing
  sync-looking test assertions:** `EjectCommand.Execute(null)` immediately followed by a
  `HasTape`/`HasImage` assertion still works post-conversion because `ConfirmDiscardAsync`
  returns an already-completed `Task<bool>` on the clean-tape/no-subscriber path — the async
  state machine never actually suspends, so it runs to completion synchronously within
  `Execute`. All 130 pre-existing `P2000.UI.Tests` passed unmodified; +19 new tests cover (a)
  clean eject/replace shows no dialog (both cassette and disk), (b) dirty shows it, (c) Cancel
  leaves the image mounted and dirty, (d) Discard proceeds exactly as an unconfirmed
  eject/replace, (e) `MarkClean()` (the Save/Save-as stand-in — file I/O itself is untestable
  headless, same limitation already noted at the top of both test files) silences the dialog on
  a subsequent eject/replace.
- **Reset-with-dirty-media stays explicitly out of scope**, per the milestone's own text — not
  touched.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs`,
  `src/P2000.UI/ViewModels/DiskDriveVm.cs`, `src/P2000.UI/ViewModels/DiskDriveWindowVm.cs`,
  `src/P2000.UI/Views/CassetteDeckWindow.axaml.cs`, `src/P2000.UI/Views/DiskDriveWindow.axaml.cs`,
  `src/P2000.UI/Views/DisplayWindow.axaml.cs`.
- **Synced:** no

### 2026-07-23 — Milestone 14 IMPLEMENTED: Disk drive UI
- **Assumed (per the milestone's own text):** the Config window already had an
  "Internal-slot board" selector (§7 lists it as an existing axis) — the disk drive axis would
  just slot in alongside it.
- **Found (a real, blocking pre-existing gap, not assumed correctly):** `ConfigWindowVm`/
  `ConfigWindow.axaml` had NO board selector at all — `BuildConfig()` never set `Board`, so
  every machine built from the config window was permanently `InternalBoard.None`. Without
  fixing this, a "Floppy drives" axis would have been unreachable (the FDC only exists when
  `Board == FloppyRam`). Added the missing selector (None/RAM-only/Floppy+RAM) as a genuine
  prerequisite, not scope creep — the milestone's own spec assumed it already existed.
- **Found (a second real, latent bug, surfaced by the same gap):** `ConfigWindowVm.Apply()` had
  no try/catch around `_runner.Reconfigure(config)`. `Machine`'s constructor throws
  `ArgumentException` for `FloppyRam` + non-T102 (and, since milestone 20, for an invalid
  `FloppyDrives` shape) — with no board selector, this combination was previously unreachable
  from the UI at all, so the gap was latent. Adding the board selector makes it reachable, so
  fixed it: `Apply()` now catches `ArgumentException` and surfaces it via `StatusMessage`
  instead of crashing the UI thread. Also proactively prevented the specific known-invalid
  combination: selecting `FloppyRam` auto-forces `RamVariant.T102` and disables the RAM
  selector (`CanEditRamVariant`) so a user can't build that combination through normal
  interaction either way — the try/catch is defense-in-depth, not the primary guard.
- **Design choice — config window models drive COUNT, not the machine's more general per-drive
  shape:** `MachineConfig.FloppyDrives` allows arbitrary indices/gaps/per-drive `Enabled`
  flags (machine milestone 20), but the UI only ever needs "how many drives, sequential from
  0." `ConfigWindowVm.FloppyDriveCount` + `ObservableCollection<FloppyDriveRowVm>` (resized to
  match, each row fixed at construction to its `DriveIndex`) is the whole axis — simpler than
  exposing the machine's full generality, and it's still a strict subset (every config this
  window can produce is valid input to `Machine`, just not every config `Machine` accepts is
  reachable from here). `LoadFromCurrentConfig`/`LoadCfgAsync` collapse a loaded config's drive
  list the same way (highest enabled index + 1 = count) — a hand-edited `.cfg` with gaps or a
  disabled middle drive round-trips lossily through this window, which is an accepted
  limitation of the simpler model, not a bug.
- **Machine-layer additions needed (small, additive — the "live status row" the milestone's own
  test (d) requires had no public accessor to read from):** `Upd765` gained `MotorOn` (the
  single shared control-latch bit), `GetCylinder(int drive)` (already-tracked per-drive state,
  just not exposed), and `CurrentTransfer` (a `TransferStatus?` snapshot of drive/head/
  direction during an active semi-DMA transfer, null when idle) — all host-status-only, none
  consulted by the chip's own command dispatch. Confirmed via the "check before adding" rule:
  neither the chip nor `DskImage` already exposed these.
- **Scoped OUT of the live status row, flagged rather than guessed:** "sector" — `Upd765`
  doesn't persist a current-sector value outside an active transfer's own command bytes (which
  aren't retained as separate fields), and adding that would be new state, not just a new
  accessor over existing state. "Head" is shown only during an active transfer (from
  `CurrentTransfer`); there's no persistent per-drive head register to show it from when idle,
  matching real hardware (H is a per-command parameter, not a resting register). Both flagged
  in `DiskDriveVm`'s own doc comments rather than fabricated.
- **NOT built this pass (explicitly out of scope — user asked for milestone 14 only):**
  milestone 14a (unsaved-changes eject/replace warning) — `DskImage.IsDirty`/`MarkClean()` and
  `MdcrDevice.IsDirty`/`MarkClean()` already exist from machine milestone 20a and
  `WriteDiskToFileAsync`/cassette's own save path already call `MarkClean()` on success, so
  14a has its machine-layer signal ready to consume, nothing here blocks it. Also not built:
  drag-drop of `.dsk` onto the main display window (ambiguous which drive should receive it
  with N drives configured, unlike the cassette's single-deck case — needs an owner decision on
  the default target before it can be built without guessing) and any UI-side persistence for
  disk write-protect (machine-layer M20 flagged this as blocked on a still-open "what does a
  saved session persist" question).
- **Tests:** `DiskDriveVmTests` (new, 15) — mount/eject/new-blank/write-protect state
  transitions and `CanExecute` wiring, write-protect actually gating a write, motor state
  shared identically across two drives' rows, per-drive independence (mounting on drive 0
  doesn't touch drive 1). `DiskDriveWindowVmTests` (new, 4) — row collection rebuilds on a
  topology `Reconfigure` (board added/removed, drive count changed), disabled drives get no
  row. `ConfigWindowVmTests` (new, 9 — this VM had NO tests before this pass) — board/RAM
  auto-force interaction, drive-count row resize (grow/shrink preserves earlier rows), config
  round-trip through `LoadFromCurrentConfig`, `Apply`'s try/catch. `Upd765Tests` (+7, machine
  layer) — the three new accessors. Uses `[AvaloniaFact]` + async/`Start()`/`await Task.Delay`
  for any test that needs a real `Reconfigure` swap to land (same requirement already
  documented in `EmulationRunnerStateTests`) — unlike `CassetteDeckVmTests`, which never
  reconfigures the machine's board and could stay fully synchronous. Full `P2000.UI.Tests`:
  124/124 green (was 99); `P2000.Machine.Tests`: 465/465 green (was 459).
- **Verified:** the app launches cleanly with this change (smoke-tested via a background
  launch + window-title check, no crash, main window title "MMulator - P2000T" present) but
  the actual Config→Floppy+RAM→Disk-Drives-window click-through was NOT driven end-to-end from
  this seat (no interactive access to a native Avalonia window) — same limitation already
  logged elsewhere in this file for computer-use against a running dev instance. Owner should
  click through: Config → Board = Floppy+RAM → set drive count → Apply → Disk menu → Open Disk
  Drives window → Mount/New/Save/Eject/write-protect per row.
- **Applies to:** project CLAUDE.md §14 milestone 14 /
  `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`MotorOn`, `GetCylinder`, `CurrentTransfer`,
  `TransferStatus`), `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`Board`, `Boards`,
  `CanEditRamVariant`, `ShowFloppyDrives`, `FloppyDriveCount`, `FloppyDriveRows`,
  `FloppyDriveRowVm`, `Apply` try/catch), `src/P2000.UI/ViewModels/ConfigConverters.cs`
  (`InternalBoardDescConverter`, `DiskSidesDescConverter`), `src/P2000.UI/Views/ConfigWindow.axaml`
  (board selector, floppy-drives section), `src/P2000.UI/ViewModels/DiskDriveVm.cs` (new),
  `src/P2000.UI/ViewModels/DiskDriveWindowVm.cs` (new), `src/P2000.UI/Views/DiskDriveWindow.axaml(.cs)`
  (new), `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`DiskVm`, `OpenDiskDriveWindowRequested`,
  `OpenDiskDrivesCommand`), `src/P2000.UI/Views/DisplayWindow.axaml(.cs)` (Disk menu, window
  wiring), `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs` (new),
  `tests/P2000.UI.Tests/ViewModels/DiskDriveWindowVmTests.cs` (new),
  `tests/P2000.UI.Tests/ViewModels/ConfigWindowVmTests.cs` (new).
- **Synced:** no (implementation-only — no new hardware facts; the scope-out decisions above
  are UX/sequencing calls, not corrections to anything the reference doc claims).

### 2026-07-22 — Flag (not yet implemented): Full-Field vs Graphics-window UI toggle
- **Trigger — owner's request:** the machine should render the complete field (black blanking
  margins included), and the UI should get an option to show "Full-Field" or "Graphics window
  only" — see `src/P2000.Machine/CLAUDE.md` §17 (2026-07-22 entry) and reference doc §3a/§4a
  for the full geometry derivation and design shape.
- **Found (scope confirmation, same pattern as the 2026-07-21 display-mode-default entry
  below):** this is a second, orthogonal UI-owned toggle, not a machine setting — the machine
  produces the full raster unconditionally; the UI decides how much to crop. No machine-layer
  mode needed for this either.
- **Owner review round 1 (before implementation) — two corrections, both resolved before any
  code was touched:**
  1. **Do not revert the dual even/odd field rendering machinery** — see the "IMPORTANT,
     owner-confirmed 2026-07-22" note on the four-display-mode entry above, and machine
     CLAUDE.md §17's WITHDRAWN note. No rendering-code change here, default-value only.
  2. **Full-field width corrected from 1024 to 928 px** — the owner's retrace model (chip
     emits nothing for 6 char-times at the start of each line; trailing blank left intact)
     excludes horizontal retrace from the buffer entirely. Crop rectangle offset is now
     (144, 98), not (240, 98). See machine CLAUDE.md §17 and reference doc §4a for the full
     derivation and the flagged 5-vs-6-char-time ambiguity.
- **Not yet done:** `DisplayMode.cs` / `DisplayControl.cs` / `DisplayWindowVm.cs` need the new
  toggle, the `WriteableBitmap` sizing needs to follow whichever crop is active (928×626 or
  640×480), and the `CorruptionOverlay` draw path needs a coordinate offset when Full-Field is
  active (overlay indices are relative to the 640×480 active window, not the full buffer) —
  this is a flag for Claude Code, not a confirmed implementation.
- **Applies to:** reference doc §3a (Full-Field vs Graphics-window) / `src/P2000.UI/Rendering/
  DisplayMode.cs`, `src/P2000.UI/Rendering/DisplayControl.cs`,
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs`.
- **Synced:** yes (2026-07-22, into P2000T-reference.md §3a) — implementation-side change still
  outstanding.

### 2026-07-22 — IMPLEMENTED: Full-Field/Graphics-window crop toggle + Odd-only default (closes both flags below/above)
- **`DisplayCrop` enum** (new file `Rendering/DisplayCrop.cs`): `GraphicsWindow` (default) /
  `FullField`. `DisplayControl.Crop` reallocates its backing `WriteableBitmap` to the crop's
  pixel size on change; `DisplayWindowVm.Crop` is the bindable VM-side property (default
  `GraphicsWindow`, with `IsCropGraphicsWindow`/`IsCropFullField`/`SetCropCommand` following the
  exact same pattern as the existing 4-way `DisplayMode`).
- **Corruption overlay offset — resolved the handoff's own open implementation choice
  ("offset at draw time, or store overlay full-buffer-sized — both are fine") in favour of
  offset-at-draw-time:** `DrawCorruptionOverlay` computes the active window's own origin as a
  sub-rect of `_destRect`, adding `ActiveOffsetX/Y` (scaled to destRect units) only when
  `Crop == FullField`; zero offset in `GraphicsWindow` since the whole destRect already IS the
  active window. No change to the overlay's own storage shape (stays 40×24, machine-side).
- **PAL aspect — implemented as "always letterbox using the crop's own true aspect ratio when
  Full-Field, regardless of the PalAspect toggle's value," not a silent no-op:** added
  `DisplayWindowVm.CanTogglePalAspect` (`Crop == GraphicsWindow`), bound to the View-menu item's
  `IsEnabled` so the toggle visibly greys out in Full-Field rather than doing nothing invisibly.
  `DisplayControl.ComputeDestRect`'s letterbox branch now fires on `PalAspect || Crop ==
  FullField` — for Full-Field this produces native-pixel-geometry letterboxing (928:626 isn't
  4:3, so this is genuinely different math from the Graphics-window PAL correction, not the same
  branch reused coincidentally).
- **Display-mode default flip (closes the 2026-07-21 flag below) — confirmed TWO separate
  defaults needed changing, not one:** `DisplayControl.Mode` and `DisplayWindowVm._displayMode`
  are independent fields with their own `= DisplayMode.Interlaced` initializers; both flipped to
  `DisplayMode.OddOnly`. Per the owner-confirmed "default-value change only" instruction, no
  per-field rendering code was touched. The View menu's "(default)" label moved from the
  Interlaced entry to the Odd-only entry.
- **Screenshot updated to respect the current crop** (`DisplayWindowVm.Screenshot()`) — it
  previously always serialized the full machine buffer unconditionally; now crops exactly like
  `DisplayControl.CopyToWriteableBitmap` does, using the same offset math.
- **Not done this pass (tooling limitation):** could not get computer-use to attach to an
  ad-hoc `dotnet run`-launched dev window for a live visual check (it only resolves
  Start-Menu-registered/tracked apps). Verified via `P2000.UI.Tests` (97, including 5 new
  `DisplayWindowVmTests`) + full `P2000.Machine.Tests` (401) instead. Flagging so a future pass
  does the actual eyes-on-screen check (see the parallel entry in `src/P2000.Machine/CLAUDE.md`
  §17 for the specific checklist).
- **Applies to:** `src/P2000.UI/Rendering/DisplayCrop.cs` (new),
  `src/P2000.UI/Rendering/DisplayControl.cs`, `src/P2000.UI/ViewModels/DisplayWindowVm.cs`,
  `src/P2000.UI/Views/DisplayWindow.axaml(.cs)`, `src/P2000.UI/Runner/EmulationRunner.cs` (doc
  comments only), `tests/P2000.UI.Tests/ViewModels/DisplayWindowVmTests.cs` (new).
- **Synced:** yes (2026-07-21, implementation-only — confirmed no reference-doc action needed;
  the crop/display-mode design facts were already synced into the reference doc before this
  pass).

### 2026-07-21 — Flag (not yet verified): display-mode default should change to Odd-only
- **Trigger:** owner-supplied P2000TM Field Service manual states, for the T-version: *"the
  signal CRS is active during the even scanlines of the field. In our system we use only the
  odd scanlines, so no interlacing is used."* Confirmed correct by the owner. See
  `src/P2000.Machine/CLAUDE.md` §17 (2026-07-19/21 entries) and `docs/SAA5050-implementation.md`
  §5 for the full hardware-timing correction (real T hardware has no even/odd field pairing;
  every field is an independent 313-line refresh).
- **Found (scope confirmation):** this project's own 2026-07-07 milestone-6 finding below
  already correctly built the four display modes as a pure UI-presentation layer over the
  machine's raw per-field events (`FieldComplete`/`IsOddField`) — no machine changes needed.
  Only the DEFAULT selection needs revisiting.
- **Owner decision, 2026-07-21:** default should move from **Interlaced (comb)** to
  **Odd-only** (mode 4, line-doubled single field) — it's the mode that matches the FSM's "only
  the odd scanlines, no interlacing." Interlaced/comb remains available as a legitimate
  opt-in/nostalgia mode, just no longer presented as authentic-default T behaviour.
- **Not yet done:** the actual default value in `DisplayMode.cs` / `DisplayWindowVm.cs`
  (milestone 6, below) has not been checked or changed in this pass — this is a flag for
  Claude Code, not a confirmed fix.
- **Applies to:** reference doc §3a (display mode) / `src/P2000.UI/Rendering/DisplayMode.cs`,
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs`.
- **Synced:** yes (2026-07-21, into P2000T-reference.md §3a) — implementation-side change still
  outstanding.
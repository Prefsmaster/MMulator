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
- **Disk / FDC UI** (mount `.dsk`, drive indicators) — surfaces once the machine's FDC lands.
  Media/mechanism rule already fixed (§5/§7): drive = topology, disk image = runtime swap like the
  cassette.
- **80-column display**, **hires overlay** presentation — once the machine supports them.

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

### 2026-07-07 — Milestone 4: cassette deck + CLOAD end-to-end
- **Assumed:** the `.cas` tape block structure was MARK + HEADER (32 B) + DATA (1024 B) as three
  separate WriteData frames. See `src/P2000.Machine/CLAUDE.md` §17 (2026-07-07) for the full
  root-cause analysis and Cassette.asm trace.
- **Found (root bug — machine layer):** the correct structure is MARK + ~81 ms gap +
  combined HEADER+DATA in one frame with one CRC. Without the gap, `read_until_timeout` reads
  into the HEADER frame after the MARK → `paddingbytes != 0` → `search_marker_loop` retry →
  eventual 'N'/'M' error. Fixed in `MiniTape.LoadCasImage` and `Save()`.
- **Found (byte order confirmed LSB-first):** the ROM byte assembler is `rr d`
  (Cassette.asm:1140), not `rla` (which is CRC-only). 0xAA is the correct sync byte.
- **Found (CassetteDeckVm — live reference pattern):** `CassetteDeckVm` reads
  `_runner.Machine.Mdcr` / `_runner.Machine.CpOut` on every `FrameReady` tick rather than
  caching the device reference. This automatically stays correct after `Reconfigure()` swaps
  the machine since it dereferences through `_runner.Machine` each time.
- **Applies to:** project CLAUDE.md §14.4 (milestone 4) / `src/P2000.Machine/CLAUDE.md` §17 /
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs`,
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs`.
- **Synced:** yes (2026-07-10 — tape block structure in docs/MDCR-implementation.md §6 + reference §5b; already reflected)

### 2026-07-07 — Milestone 5: config window + .cfg load/save
- **Assumed:** `EmulationRunner.Machine` could remain a get-only property for the lifetime of
  the app; a topology change would require restarting the process or a separate factory.
- **Found (Reconfigure swap pattern):** a volatile `_nextMachine` field + `SemaphoreSlim`
  lets the UI thread build a new machine and block (~20 ms max) while the emulation thread
  acknowledges the swap at the next field boundary. No lock needed: the volatile write is the
  signal; the semaphore is purely for the UI thread to wait for acknowledgement. The old
  machine's `FieldComplete` and `BreakHit` are unsubscribed inside the swap on the emulation
  thread so there is no race between the old event firing and the new machine taking over.
- **Found (BreakHit forwarding):** `DisplayWindowVm` previously subscribed to
  `Runner.Machine.BreakHit` directly (hard reference to the original machine). After
  `Reconfigure` that subscription would silently stop working. Fixed by adding a forwarding
  `Action<BreakEvent> BreakHit` event on `EmulationRunner` that re-routes across swaps;
  `DisplayWindowVm` now subscribes to the runner, not the machine.
- **Found (status bar model text):** `ModelText` was computed as
  `config.Model.ToString().Replace("P2000","")` → always "T" regardless of RAM variant.
  Updated to "T/38", "T/54", "T/102" by appending the `RamVariant` suffix.
- **Found (ConfigWindow as satellite, not modal):** opening as a non-modal `Show(this)`
  satellite (same pattern as `CassetteDeckWindow`) is preferable to `ShowDialog` — the user
  can still interact with the emulator display while the config window is open.
- **Applies to:** project CLAUDE.md §14.5 (milestone 5) /
  `src/P2000.UI/Runner/EmulationRunner.cs` (`Reconfigure`, `BreakHit` forwarding),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`ModelText`, `OpenConfigCommand`),
  `src/P2000.UI/ViewModels/ConfigWindowVm.cs`,
  `src/P2000.UI/Views/ConfigWindow.axaml`.
- **Synced:** yes (2026-07-10 — non-modal-satellite decision in UI §5; Reconfigure/ModelText items implementation-only)

### 2026-07-07 — Milestone 1: app shell + emulation loop + display blit
- **Assumed:** `AppBuilder.WithInterFont()` was a standard Avalonia 11.1 extension.
- **Found:** `WithInterFont()` requires a separate `Avalonia.Fonts.Inter` package not included
  in the base `Avalonia 11.1.0` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` trio. Dropped
  the call — the system font renders fine and the emulator display doesn't depend on it.
- **Found:** `P2000.Machine` is both a namespace AND contains a class named `Machine`.
  Using `using P2000.Machine;` in the runner causes `CS0118: 'Machine' is a namespace but is
  used like a type`. Resolution: alias `using MachineCore = P2000.Machine.Machine;` in files
  that need the class directly. Other files (VM, App) only reference it through the runner
  and are unaffected.
- **Found (AXAML rule):** `xmlns:` declarations must appear on the root element. Moving the
  `xmlns:local` for the `ViewLocator` inline element out to `<Application ...>` fixed
  `AXN0002`.
- **Found (boot screen confirmed):** the "PHILIPS MICROCOMPUTER P2000" splash screen IS the
  ROM's cassette-wait state — the ROM polls CIP in a loop displaying this screen until a
  tape is mounted. Validation gate §13.1 confirmed (bare machine, display rendering, 50 Hz).
- **Applies to:** project CLAUDE.md §14.1 (milestone 1) /
  `src/P2000.UI/Program.cs`, `src/P2000.UI/App.axaml`,
  `src/P2000.UI/Runner/EmulationRunner.cs`,
  `src/P2000.UI/Rendering/DisplayControl.cs`.
- **Synced:** yes (2026-07-10 — implementation-only, no reference change)

### 2026-07-07 — Milestone 6: display modes + video prefs
- **Assumed:** `FrameReady` could remain `Action<uint[]>` for all consumers; mode switching
  was a pure rendering concern inside `DisplayControl`.
- **Found (`fieldWasOdd` timing):** when the runner's `OnFieldComplete` fires, `Video.IsOddField`
  has ALREADY toggled to the next field's parity. The field that just completed = `!IsOddField`.
  This value gates Progressive (present only after odd field = both interlaced fields done),
  EvenOnly (present only after even field), and OddOnly (present only after odd field).
- **Found (corruption overlay must be copied at field boundary):** `Video.CorruptionOverlay` is
  cleared by the machine AFTER `FieldComplete` returns (Video.cs line 152: `Array.Clear` after
  the event). The runner's `OnFieldComplete` runs inside the event, so the overlay is still
  populated when the copy occurs. Must copy it in the runner alongside the framebuffer — not
  deferred to the UI thread callback where it would already be cleared.
- **Found (`FrameReady` signature widened):** changed from `Action<uint[]>` to
  `Action<uint[], bool, bool[]>` (pixels, fieldWasOdd, corruptionSnapshot). Both
  `CassetteDeckVm` and `DisplayWindowVm` updated; the code-behind handler pushes video prefs
  from the VM to `DisplayControl` on every frame (cheap property writes at 50 Hz).
- **Found (`EnumEqualsConverter` needed for AXAML radio-button pattern):** menu `IsChecked`
  for the four display-mode items binds `DisplayMode` property against a `{x:Static}` enum
  value via `EnumEqualsConverter`. Added to `StatusConverters.cs`.
- **Found (`DisplayControl.Background` not supported):** `Control` doesn't expose `Background`
  without explicitly adding an `AvaloniaProperty`. Removed from AXAML; window background
  already `Black` so no visual change.
- **Applies to:** project CLAUDE.md §14.6 (milestone 6) /
  `src/P2000.UI/Rendering/DisplayMode.cs` (new),
  `src/P2000.UI/Rendering/DisplayControl.cs` (modes, scaling, scanlines, overlay),
  `src/P2000.UI/Runner/EmulationRunner.cs` (FrameReady signature, corruption copy),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (video prefs),
  `src/P2000.UI/Views/DisplayWindow.axaml` (View menu),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (FrameReady wiring),
  `src/P2000.UI/Views/StatusConverters.cs` (EnumEqualsConverter).
- **Synced:** yes (2026-07-10 — IsOddField-at-FieldComplete in reference §3a, overlay-clear in §4; converter items implementation-only)

### 2026-07-09 — Milestone 7: audio (OpenAL beeper sink)
- **Assumed:** `Silk.NET.OpenAL` 2.21.0 would expose managed `ref`/`out`/array overloads for
  `GenSources`, `GenBuffers`, `BufferData`, etc. (as some versions do).
- **Found:** 2.21.0 exposes ONLY raw unsafe pointer overloads. All call sites must use `unsafe`
  methods with `fixed` blocks for managed arrays. Stack-local `uint` variables (source ID,
  freed buffer ID, single-buffer queuing) can be addressed with `&local` directly in an
  `unsafe` context without `fixed` (value types on the stack are not GC-moveable).
  Array elements accessed inside a loop must be copied to a stack local first
  (`uint bid = buffers[i]; al.SourceQueueBuffers(source, 1, &bid);`) — nesting a second
  `fixed (&buffers[i])` inside an existing `fixed` block or attempting `fixed` on a loop
  variable triggers CS0213.
- **Found (AudioEngine design):** 4-buffer OpenAL streaming source. Background thread at 5 ms
  poll: dequeues processed buffers, refills with PCM from `ConcurrentQueue<short[]>` (or
  silence on starvation), re-queues. Restarts source on starvation stop. Mute/volume driven by
  lazy `_gainDirty` flag to avoid redundant AL calls.
- **Found (SoundDevice.SamplesReady buffer ownership):** `SoundDevice` reuses its internal
  `short[]` buffer immediately after `SamplesReady` returns. `AudioEngine.EnqueueSamples` must
  copy before enqueuing; it does so with `Array.Copy`.
- **Applies to:** project CLAUDE.md §14.7 (milestone 7) /
  `src/P2000.UI/Audio/AudioEngine.cs` (new),
  `src/P2000.UI/Runner/EmulationRunner.cs` (Audio wiring),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (AudioMute/AudioVolume),
  `src/P2000.UI/Views/DisplayWindow.axaml` (View > Mute audio menu item).
- **Synced:** yes (2026-07-10 — SoundDevice audio seam in reference §5 Sound; OpenAL/pointer items implementation-only)

### 2026-07-07 — Milestone 4 addendum: cassette directory — full header fields
- **Assumed:** the directory only needed the 8-char name (header bytes 06-0D).
- **Found (tape header structure, from `docs/MDCR/Tape Header.md`):** the 32-byte block header
  carries the full 16-char filename split across two 8-byte fields (bytes 06-0D + 17-1E),
  a 3-char extension (bytes 0E-10), a 1-byte creator ID (byte 11), file size as a LE word
  (bytes 04-05), and a block counter (byte 1F). The directory should show all of these.
- **Found (block count from bytes 02-03):** header bytes 02-03 hold the space occupied on tape
  (may be larger than the file if a shorter file was written over a longer one). Divide by 1024
  to get blocks occupied. Header byte 1F ("blocks remaining") is a write-time counter, not used.
- **Found (format — monospaced columns):**
  `{name,-16} {.ext,-4} {creator,-2} {size,8} {blocks,4}` with Dutch-style dot thousands
  separator for file size (e.g. `24.331`). Header row bound via `DirectoryHeader` static
  property on the VM; window widened to 440 px to accommodate the extra columns.
- **Applies to:** project CLAUDE.md §14.4 (milestone 4 addendum) /
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory`, `DirectoryHeader`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml`.
- **Synced:** yes (2026-07-10 — 32-byte header field table in docs/MDCR-implementation.md §6 + §8 directory de-dup)

### 2026-07-09 — Milestone 8: save-state UI
- **Assumed:** `SaveState` / `LoadState` could be called directly from the UI thread at any time.
- **Found (instruction-boundary guarantee):** `MachineStateFile.Save` must be called at an
  instruction boundary. The safe mechanism is a volatile `_pendingSaveStream` field checked
  in `EmulationRunner.OnFieldComplete()` (runs on the emulation thread), identical to the
  existing `Reconfigure` swap pattern. The UI thread sets the stream, then waits on a
  `SemaphoreSlim` (~20 ms max); the emulation thread saves and releases.
- **Found (`ReconfigureWithMachine` added):** `Reconfigure(MachineConfig)` always builds a
  fresh machine. For load-state, `MachineStateFile.Load` already returns a complete
  `Machine`; a `ReconfigureWithMachine(Machine)` overload wires the events and swaps it in
  via the same volatile `_nextMachine` / `_swapDone` mechanism.
- **Found (`AvaloniaHeadlessPlatformOptions` not `AvaloniaHeadlessOptions`):** the correct
  Avalonia 11.1.0 headless options type for `AppBuilder.UseHeadless(...)` in test projects
  is `AvaloniaHeadlessPlatformOptions` (from `Avalonia.Headless`). The `AvaloniaTestApplicationAttribute`
  is in `Avalonia.Headless`; `[AvaloniaFact]` is in `Avalonia.Headless.XUnit`.
- **Applies to:** project CLAUDE.md §14.8 (milestone 8) /
  `src/P2000.UI/Runner/EmulationRunner.cs` (`SaveStateToStream`, `ReconfigureWithMachine`),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`SaveStateCommand`, `LoadStateCommand`),
  `src/P2000.UI/Views/DisplayWindow.axaml` (Machine menu items),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (`ShowErrorDialog`),
  `tests/P2000.UI.Tests/` (new project, 6 tests).
- **Synced:** yes (2026-07-10 — save-at-instruction-boundary already in reference §3a; the .state version-bump gap this exposed was fixed later — see the v1→v2 finding below; reference §3a now RESOLVED)

### 2026-07-10 — Milestone 9: debugger observer core
- **Assumed:** `[ObservableProperty] private string _af` would generate a property named `AF`.
- **Found:** CommunityToolkit.Mvvm source generator capitalises the first letter only (`_af` → `Af`,
  not `AF`). All two-letter register acronyms (AF, BC, DE, HL, IX, IY, SP, PC, WZ, IFF1, IFF2, IM)
  must be written as manual properties with `SetProperty` to keep the public names readable.
- **Found (corruption overlay geometry):** `Video.CorruptionOverlay` is 40×24 (one bool per
  visible viewport column, not per VRAM column). Index = `row × 40 + viewportCol` where
  `viewportCol = vramCol − PanX`. The VRAM grid control maps each absolute VRAM column to a
  viewport column before checking the corruption flag.
- **Found (live memory follow — best-effort):** `Machine.Cpu.Reg.HL` etc. are readable outside
  a snapshot (direct struct access). This is racy mid-instruction but acceptable for the live
  "follow register" display in memory watches. Snapshot-based reads (at break/step) are exact.
- **Found (`VramGridControl` — `AffectsRender` + property replacement):** binding arrays
  (`byte[]`, `bool[]`) to styled properties only triggers `InvalidateVisual` when the array
  reference changes (Avalonia uses reference equality). `VramWindowVm.Update` always allocates
  new arrays, which satisfies this. `AffectsRender<VramGridControl>(...)` wires all four
  properties so any change auto-invalidates without manual override of property changed.
- **Found (VRAM refactored to satellite window — post-ship):** the VRAM grid was inlined
  in `DebuggerWindow`'s right panel, leaving no room for milestone 10 content. Extracted
  to `VramWindow` (same satellite pattern as `MemoryWatchWindow`): `DebuggerWindowVm` fires
  `OpenVramWindowRequested`; code-behind opens/reuses `VramWindow { DataContext = Vram }`.
  `DebuggerWindow` now has registers on the left and a blank right panel ready for
  disassembly/breakpoints/stepping (milestone 10). The `VramWindowVm` and all VRAM update
  paths are unchanged.
- **Found (live register display — post-ship fix):** registers only updated from
  `OnBreakHit`, so the panel showed "–" for all registers while the machine was running.
  Fix: `DebuggerWindowVm.OnFrameReady` now calls `RegisterFile.UpdateLive(m.Cpu.Reg,
  m.Video.FieldTState)` on every frame when not paused. `RegisterFileVm.UpdateLive` reads
  directly from the `Registers` struct and derives flags from the `F` byte via bitmask —
  no snapshot needed. Values are best-effort (sampled at field boundary), exact only at
  break/step.
- **Applies to:** project CLAUDE.md §14.9 (milestone 9) /
  `src/P2000.UI/ViewModels/RegisterFileVm.cs` (manual properties, `UpdateLive`),
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs`, `VramWindowVm.cs`, `DebuggerWindowVm.cs`,
  `src/P2000.UI/Rendering/VramGridControl.cs`,
  `src/P2000.UI/Views/DebuggerWindow.axaml`, `MemoryWatchWindow.axaml`,
  `src/P2000.UI/Views/DisplayWindow.axaml` (Debug menu),
  `tests/P2000.UI.Tests/ViewModels/` (22 new tests).
- **Synced:** yes (2026-07-10 — corruption-overlay viewport-column indexing into reference §4; MVVM/AffectsRender items implementation-only)

### 2026-07-10 — Milestone 10: disassembly + breakpoints + stepping
- **Assumed:** `BreakHit` would already fire for single-step, PauseCommand, and
  run-to-scanline/cycle completions so the UI could take a snapshot after stepping.
- **Found (silent-pause gap):** `SingleStepCommand`, `PauseCommand`, and `_runToFieldTState`
  all set `IsPaused = true` without firing `BreakHit`. The UI subscribed only to `BreakHit`,
  so stepping appeared to do nothing (registers didn't update, disassembly didn't refresh).
  **Fix:** added `BreakpointKind.Step` to the enum (id = -1, no real breakpoint) and wired
  `BreakHit?.Invoke(new BreakEvent(BreakpointKind.Step, Cpu.Reg.PC, -1))` in all three
  silent-pause paths in `Machine.cs`.
- **Found (`DisassemblyVm` in-place update):** `ObservableCollection` fires `CollectionChanged`
  on every `Add`/`Remove`, causing ListView item recycling flicker on every decode. Fix:
  overwrite existing `DisassemblyLineVm` items in-place (mutate properties) and only
  add/remove at the tail to reach the right count.
- **Found (breakpoint management — no IDs needed):** `BreakpointStore` assigns sequential IDs
  but the command queue is fire-and-forget (IDs not returned to caller). UI maintains a
  `HashSet<ushort> _execBpSet`; on toggle: clear all, re-add from the set. The full queue
  drains atomically at one instruction boundary, so the clear+re-add is race-free.
- **Found (disassembly live refresh throttle):** re-decoding on every `FrameReady` (50 Hz) is
  wasteful when PC hasn't moved. `DisassemblyVm.NeedsRefresh(pc)` compares against
  `_lastPc`; only re-decodes when PC has changed.
- **Found (Avalonia 11 visual-tree walk):** `Avalonia.Visual.VisualParent` no longer exists.
  Walking up the tree to find a `DataContext` must use `.Parent as Control` instead.
- **Applies to:** project CLAUDE.md §14.10 (milestone 10) /
  `src/P2000.Machine/Debug/BreakpointKind.cs` (`Step` value),
  `src/P2000.Machine/Machine.cs` (BreakHit in 3 silent-pause paths),
  `src/P2000.UI/P2000.UI.csproj` (Z80.Disassembler reference),
  `src/P2000.UI/ViewModels/DisassemblyLineVm.cs` (new),
  `src/P2000.UI/ViewModels/DisassemblyVm.cs` (new),
  `src/P2000.UI/ViewModels/DebuggerWindowVm.cs` (stepping cmds, breakpoints, disassembly),
  `src/P2000.UI/Views/StatusConverters.cs` (BoolToPcBrushConverter, BoolToBpDotConverter),
  `src/P2000.UI/Views/DebuggerWindow.axaml` (stepping toolbar + disassembly panel),
  `src/P2000.UI/Views/DebuggerWindow.axaml.cs` (OnDisasmTapped breakpoint toggle).
- **Synced:** yes (2026-07-10 — BreakHit-on-all-pause-transitions into reference §3a + machine §3b; disasm/breakpoint-UI items implementation-only)

### 2026-07-10 — .state format version bump: v1 → v2 (retroactive)
- **Assumed (at milestone 8, when Save/Load State shipped):** `MachineStateFile.CurrentVersion`
  would be bumped as each format-changing machine milestone landed. Two changes were explicitly
  flagged as "bumping deferred" but never actually bumped:
  - Milestone 12: `InterruptAggregator.SaveState` grew from 1 bool to 2 (`_intPending` +
    `_nmiPending`).
  - Milestone 16: `SoundDevice` block inserted between `Mdcr` and `Interrupts` in
    `Machine.SaveState/LoadState`.
- **Found (silent mis-load risk):** `CurrentVersion` was still 1; the reader accepted v1 files
  (`version >= 1 && version <= 1`), but the device stream was fatally misaligned —
  `Sound.LoadState` consumed the old single-bool Interrupts payload, then `Interrupts.LoadState`
  read garbage or hit EOF. There was no exception until stream underrun.
- **Fix:** `CurrentVersion = 2`; `MinVersion = 2`; reader rejects v1 files with an
  `InvalidDataException` ("Unsupported .state version 1. This build supports versions 2–2.")
  rather than silently loading corrupt state. `Load_VersionOne_Throws` test added.
- **No migration path:** no external `.state` files were distributed; any saves produced
  during milestones 11–15 testing should be discarded.
- **Applies to:** `src/P2000.Machine/State/MachineStateFile.cs` (`CurrentVersion`, `MinVersion`,
  version-gate check), `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs`
  (`Load_VersionOne_Throws`).
- **Synced:** yes (2026-07-10 — into reference §3a: version-bump note updated PENDING/⚠ → RESOLVED (v2))

### 2026-07-10 — Audio: OpenAL Soft native DLL + queue-cap latency fix
- **Assumed:** `openal32.dll` would be present on the developer's machine (many Windows
  systems have it via games). It was not — `Silk.NET.OpenAL` threw `FileNotFoundException`
  on init and audio was silently absent.
- **Found (native DLL bundling):** Silk.NET.OpenAL is a pure P/Invoke binding with no
  bundled native library. The correct cross-platform solution is to ship the platform DLL
  alongside the app via the .NET `runtimes/<rid>/native/` convention.
  - `tools/get-openal.ps1` downloads OpenAL Soft 1.23.1 Win64 (`soft_oal.dll`) from
    GitHub releases and places it as `runtimes/win-x64/native/openal32.dll`.
  - `P2000.UI.csproj` uses `<Content … Link="openal32.dll">` with `IsOSPlatform` guards
    to copy the right DLL to the output root at build time (`Link=` overrides the default
    `runtimes/` subfolder preservation that `<None>` would give).
  - Linux: `libopenal.so.1` from system packages; macOS: system OpenAL framework.
  - The `runtimes/` folder is committed to git (binary DLL included) so CI/team members
    don't need to run the script.
- **Found (startup latency ~1.3 s):** `alc.OpenDevice("")` on Windows blocks for ~1 s on
  first call (device enumeration / driver init). The emulation thread produces 50 blocks/s
  into `_queue` during this time. No size cap meant ~60 stale silence-blocks queued ahead
  of the first audible beep. Fix: `MaxQueueDepth = 6` in `EnqueueSamples` drops oldest
  blocks when the queue exceeds ~120 ms depth. Combined with 4 OpenAL buffers × 20 ms =
  80 ms, total latency is capped at ~200 ms regardless of init time.
- **Applies to:** project CLAUDE.md §14.7 (milestone 7, audio) /
  `src/P2000.UI/Audio/AudioEngine.cs` (MaxQueueDepth, doc update),
  `src/P2000.UI/P2000.UI.csproj` (native content items),
  `src/P2000.UI/runtimes/win-x64/native/openal32.dll` (bundled binary),
  `tools/get-openal.ps1` (download script).
- **Synced:** yes (2026-07-10 — OpenAL native-DLL bundling + queue-cap latency: deployment/implementation-only, no reference change)

### 2026-07-14 — Milestone 12: debugger memory watch export/import
- **Assumed:** the data-flow check in this milestone's own spec (§14.12) — export via the
  existing snapshot memory-read surface, import via the existing `LoadImageCommand` — would
  hold with no machine-side gap. Confirmed true: `LoadImageCommand(ushort StartAddress, byte[]
  Data)` (`src/P2000.Machine/Debug/MachineCommand.cs`) already writes an arbitrary-length byte
  array at an arbitrary address via a plain `Memory.Write` loop — no cartridge/image-shape
  assumption, no machine change needed.
- **Found (design decision — `MemoryWatchVm` now takes the runner):** `MemoryWatchVm` was
  previously constructible with no arguments (a pure observer fed externally via `Update()`).
  Export/import needs to enqueue a command, so the constructor now takes `EmulationRunner` —
  same pattern as `CassetteDeckVm`/`ConfigWindowVm` (store the runner, dereference
  `_runner.Machine` at call time so a `Reconfigure()` swap is picked up automatically, per the
  milestone-5 finding). `DebuggerWindowVm.AddMemoryWatch()` updated to pass `_runner`; existing
  `MemoryWatchVmTests` updated to construct via `new MemoryWatchVm(new EmulationRunner())`.
- **Found (top-of-RAM guard is a UI-side check, not a machine one):** `PageTable.Write` silently
  discards out-of-range/unpopulated writes (open-bus convention) rather than throwing, so
  "reject a file that would run past 0xFFFF" cannot come from the machine. `LoadFileToAddressAsync`
  checks `address + data.Length > 0x10000` before enqueuing and surfaces a message instead —
  the only bound checked (the file may exceed the watch window's own configured length since
  the target address is independent of the window's range).
- **Found (dialog plumbing reused verbatim):** `SaveFilePickerAsync`/`OpenFilePickerAsync` +
  `TopLevel` lookup + a `ShowMessageRequested` event forwarded to a small dialog in
  code-behind — same shape as `DisplayWindowVm`'s save/load-state commands and
  `CassetteDeckVm`'s mount. No new UI pattern introduced.
- **Applies to:** project CLAUDE.md §14.12 /
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs` (constructor, `SaveRangeToFileAsync`,
  `LoadFileToAddressAsync`, `LoadAddressText`, `ShowMessageRequested`),
  `src/P2000.UI/ViewModels/DebuggerWindowVm.cs` (`AddMemoryWatch`),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml` (toolbar actions),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml.cs` (`ShowErrorDialog`),
  `tests/P2000.UI.Tests/ViewModels/MemoryWatchVmTests.cs` (+6 tests).
- **Synced:** no (implementation-only — no hardware/spec correction to sync; the machine-side
  `LoadImageCommand` contract itself was already synced at machine ms.15).

### 2026-07-14 — Milestone 12 follow-up: configurable watch range (owner feedback)
- **Assumed (first pass above):** a fixed 256-byte window was enough — "export" would just dump
  whatever the window happened to be displaying (`_curr`), and "import" only needed an editable
  target address.
- **Found (owner feedback — not hardware, a scope correction):** a fixed 256-byte range isn't
  actually useful for pulling an arbitrary machine-code routine out of RAM (the milestone's own
  motivating case). Two changes:
  1. **The watch window itself is now range-configurable**, not fixed at 256 bytes. Added a
     "Length" field (hex) next to "Base"; `MemoryWatchVm.SetRange(ushort, int)` sets both
     together, clamps length to `[1, 0x10000]`, and resizes `Rows` (now an
     `ObservableCollection<MemoryWatchRow>`, not a fixed 16-element array) plus the internal
     `_curr`/`_prev` buffers to `ceil(length/16)` whole rows. A length not a multiple of 16
     rounds the display up (the extra trailing bytes are real memory, just past the requested
     length) — only the exact requested length is used for export.
  2. **"Save range to file…" now prompts for its own start+length**, defaulting to the window's
     current `BaseAddress`/`Length` but independently editable at save time — so a one-off
     export doesn't require changing what the window is currently watching. Implemented as a
     small ad-hoc modal in `MemoryWatchWindow.axaml.cs` (`PromptRangeAsync`, same
     inline-`Window`-construction style as the existing `ShowErrorDialog`), then calling
     `MemoryWatchVm.SaveRangeToFileAsync(start, length)` — no longer a `[RelayCommand]` bound
     directly to the button, since the view needs to gather the range first.
- **Found (export now reads live machine memory directly, not `_curr`):** since the save-time
  range can differ from the window's own displayed range, `SaveRangeToFileAsync` reads
  `_runner.Machine.Memory.Read` fresh for the requested range rather than reusing `_curr` (which
  is sized/addressed to the window's own configured range). This is actually simpler than the
  first pass and still satisfies "live-at-that-moment" for both paused and running, since it's a
  direct read at click time either way.
- **Found ("Go" now sets both base and length):** `OnGoClicked` reads both `AddressBox` and the
  new `LengthBox` and calls `vm.SetRange(...)` once. Leaving the length box's parsed value equal
  to `vm.Length` when the box is empty preserves the old "just navigate" behaviour.
- **Found (StorageProvider dialogs remain untested at the unit level):** confirmed no existing
  test in this suite drives `SaveFilePickerAsync`/`OpenFilePickerAsync` end-to-end (checked
  `DisplayWindowVm.SaveStateAsync`/`LoadStateAsync` — also untested for the same reason); a
  headless `[AvaloniaFact]` test has no real desktop `TopLevel`/`MainWindow`, so `GetTopLevel()`
  returns null and the picker call never fires. Kept the same scope here: unit tests cover
  `SetRange`'s resize/clamp behaviour and the underlying `Machine.Memory.Read`/`Write` +
  `LoadImageCommand` paths those commands rely on, not the file-picker plumbing itself.
- **Applies to:** project CLAUDE.md §14.12 /
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs` (`Length`, `SetRange`, `ResizeBuffers`, `Rows` now
  `ObservableCollection<MemoryWatchRow>`, `SaveRangeToFileAsync` signature),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml` (Length field, Save button now `Click`-bound),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml.cs` (`OnGoClicked`, `OnSaveRangeClicked`,
  `PromptRangeAsync`),
  `tests/P2000.UI.Tests/ViewModels/MemoryWatchVmTests.cs` (range tests added; export tests
  reworked around `Machine.Memory.Read`/`Write` instead of the old fixed-buffer helper).
- **Synced:** yes (2026-07-14, this merge pass — folded into §10's memory watch bullet: range is
  now explicitly configurable via a Length field, and "Save range to file…" prompts for its own
  independently-editable start+length rather than always matching the window's live display
  range. Also synced (2026-07-14, follow-up pass) into `docs/P2000T-reference.md` §3a's
  "Memory watch windows" bullet — the canonical home for this per this file's own header — which
  additionally needed its stale "Read-only; never touches the live core" claim CORRECTED: export
  is read-only, but import is a real (queued, boundary-safe) RAM write, not an exemption from the
  "every mutation is a queued command" rule. That bullet had never been synced for milestone 12
  at all until now — this pass covers both the original export/import addition and this
  range-configurable follow-up in one go.).

### 2026-07-14 — Milestone 13: cassette deck — New (blank) tape + Save/Save-as wiring
- **Assumed:** per the milestone's own "verify, don't assume" instruction — that
  `MdcrDevice` might already have a live blank-mount entry point equivalent to `InsertTape()`.
  Confirmed it did not: `InsertTape(byte[] casImage, ...)` is the only mount path and always
  parses a real `.cas` byte stream via `MiniTape.LoadCasImage`.
- **Found (reported back, then authorized and implemented — see `P2000.Machine/CLAUDE.md`
  §17, 2026-07-14 "DECIDED"/"IMPLEMENTED" pair):** `MdcrDevice.InsertBlankTape()` added.
  Turned out to need NO `MiniTape` change at all — its existing parameterless constructor
  already produces exactly the required blank state (BOT, unprotected, zero blocks,
  pseudo-noise-filled). `InsertBlankTape()` is a two-line method that swaps `_tape` directly
  (never through `null`), which is also what gives "one CIP transition, not two" for free —
  no eject-then-insert logic needed.
- **Found (Save-vs-Save-as backing tracked as `IStorageFile?`, not a raw path string):**
  `CassetteDeckVm` now holds `_backingFile` (Avalonia's `IStorageFile`, not a `string` path) —
  the same object returned by `OpenFilePickerAsync`/`SaveFilePickerAsync`/drag-drop, reusable
  directly for `OpenWriteAsync()` on a plain "Save" without re-resolving a path. Null after
  `NewBlankTape()`; set on file-dialog mount, drag-drop mount, and after a successful
  "Save as…". `MountBytes` gained an optional `IStorageFile? backingFile` parameter (default
  null preserves old callers); `DisplayWindow.axaml.cs`'s drag-drop handler now passes the
  dropped `IStorageFile` through instead of just its bytes+name.
- **Found (Save/SaveAs `CanExecute` reuses the `nameof(HasTape)` pattern):** same shape as
  `DebuggerWindowVm`'s `[RelayCommand(CanExecute = nameof(IsPaused))]` — `HasTape` is already
  an `[ObservableProperty]` bool, so `SaveCommand`/`SaveAsCommand` gate on it directly with no
  separate predicate method, and `OnHasTapeChanged` (already existed, extended) keeps both in
  sync alongside the existing `EjectCommand` notify.
- **Found (`SaveTape()` needed no machine change — the milestone's own "clean" half):**
  `MdcrDevice.SaveTape()`/`MiniTape.Save()` already existed from ms.9a and are host-side/
  always-fast/independent of `TimingPolicy`, exactly as the spec assumed — pure UI wiring
  (`WriteTapeToFileAsync`) on top.
- **Found (StorageProvider dialogs remain untested at the unit level — same limitation as
  milestone 12):** `CassetteDeckVmTests` (new) covers state transitions
  (`HasTape`/`TapeLabel`/`IsWriteProtected`/`Programs`), the Save/SaveAs `CanExecute` wiring,
  and — via the real `MdcrDevice` the VM drives — that mounting a blank tape over an
  already-mounted one never observes CIP go absent. The actual file-picker halves of
  `MountAsync`/`SaveAsync`/`SaveAsAsync` are not unit-tested (no real desktop `TopLevel` in a
  headless run); the byte-identical blank→CSAVE→Save→reload round trip is instead tested at
  the machine layer (`MdcrDeviceTests`, new — exercises `WriteBlockAtHead`/`SaveTape`/
  `InsertTape`/`TryReadBlockAtHead` directly), which is where it's actually testable.
- **Not built (per the milestone's own explicit scope):** no "format tape" affordance (blank
  tape is immediately writable, confirmed no format step exists); no dedicated "Erase" UI
  (a running program's own erase is indistinguishable from any other CSAVE, already visible
  via the activity LED + directory); rewind and write-protect toggle remain
  decided-but-unbuilt, untouched here.
- **Applies to:** project CLAUDE.md §14.13 / `P2000.Machine/CLAUDE.md` §17 (the
  `InsertBlankTape` decision + implementation entries) /
  `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs` (`InsertBlankTape`),
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`NewBlankTape`, `SaveAsync`, `SaveAsAsync`,
  `WriteTapeToFileAsync`, `_backingFile`, `MountBytes` signature, `ShowMessageRequested`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml` (New/Save/Save-as buttons),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml.cs` (`ShowErrorDialog`),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (drag-drop passes `IStorageFile` through),
  `tests/P2000.Machine.Tests/Devices/MdcrDeviceTests.cs` (+6 tests),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (new — 8 tests).
- **Synced:** no (implementation-only UI/machine wiring; the one hardware-adjacent fact —
  "no format step, blank tape immediately writable" — was already confirmed with the owner
  and recorded in the milestone spec itself, nothing new to sync).

### 2026-07-14 — Milestone 13a: cassette deck — write-protect toggle
- **Reported symptom (owner):** the cassette always reads as write-protected, no UI to change
  it. **Root cause confirmed by inspection, exactly as milestone 13's own reported-symptom
  matched the machine CLAUDE.md §17 "DECIDED" entry predicted:** `CassetteDeckVm.MountBytes`
  hardcoded `_runner.Machine.Mdcr.InsertTape(casImage, writeProtect: true)` on every
  file-loaded mount — `InsertBlankTape()` was correctly unprotected (as ms.13's own tests
  already showed) because it never took the parameter at all. Nothing was "stuck" by tape
  content; the one caller simply never asked for anything else.
- **Fix (breaking API change, authorized in `P2000.Machine/CLAUDE.md` §17):**
  `MdcrDevice.InsertTape`/`MiniTape.LoadCasImage` no longer take a `writeProtect` parameter —
  protection is now read from the `.cas` file itself (record offset `0x50`, bit 0 — see the
  machine-layer entry for the full container-format decision) and round-trips through
  `Save()`. `CassetteDeckVm.MountBytes` reads `IsWriteProtected` back FROM the machine after
  mount instead of assuming a value.
- **Found (live toggle needed no new plumbing beyond the setter itself):** made
  `CassetteDeckVm.IsWriteProtected`'s existing `[ObservableProperty]` two-way bindable (a
  `CheckBox` in the deck window replacing the old passive 🔒 `TextBlock`) and added
  `OnIsWriteProtectedChanged` pushing to the new `MdcrDevice.SetWriteProtected(bool)` whenever
  `HasTape` — no separate `[RelayCommand]` needed since the property setter IS the command
  here (CommunityToolkit's changed-hook fires on both user interaction and our own internal
  post-mount sync, and re-pushing the same value is harmless/idempotent).
- **Found ("Rewind" reclassified, not a peer of write-protect):** while investigating this
  symptom the owner confirmed the real MDCR has no rewind button at all (only Eject) — a
  correction to §3.1's earlier phrasing, which had listed "rewind" alongside write-protect as
  a peer decided-but-unbuilt item. It isn't a deferred control; there's nothing to build.
- **Tests:** `MdcrDeviceTests`/`MiniTapeTests` (machine layer, see `P2000.Machine/CLAUDE.md`
  §17 for the full list) plus `CassetteDeckVmTests` (+4 at the VM level): the regression check
  (a plain record with no protect byte defaults writable), a protect-byte-set record mounts
  protected, the live toggle flips the real `MdcrDevice`'s WEN without touching CIP, and
  toggling with no tape mounted doesn't throw.
- **Applies to:** project CLAUDE.md §14.13a / `P2000.Machine/CLAUDE.md` §17 (the
  write-protect DECIDED/IMPLEMENTED pair — full container-format detail lives there) /
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`MountBytes`, `OnIsWriteProtectedChanged`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml` (write-protect `CheckBox`),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (+4 tests).
- **Synced:** no (implementation-only; the "rewind has no physical control" correction is a
  UI-doc-only wording fix, not new hardware content — reference doc §5b already only
  describes rewind as a host-API convenience, not a physical button).

### 2026-07-14 — Milestone 13a follow-up: padlock click-to-toggle (owner feedback)
- **Reported:** the `CheckBox` toggle gave mixed signals — a checkmark AND a lock glyph AND
  text, three signals not obviously in sync at a glance.
- **Fix:** replaced the `CheckBox` with a borderless, transparent-background `Button` whose
  content is one bigger padlock glyph (22px, was 11px inline) + a label, both driven by the
  SAME `IsWriteProtected` bool via two new converters — `BoolToPadlockIconConverter` (🔒/🔓)
  and `BoolToWriteProtectLabelConverter` ("Write protected"/"Write enabled") — so there is
  exactly one signal (open vs. closed padlock) reinforced by matching text, no separate
  checkmark to contradict it.
- **Found (no new VM plumbing needed):** added `ToggleWriteProtectCommand`
  (`IsWriteProtected = !IsWriteProtected`) purely so the Button has something to bind
  `Command` to — the actual machine push still happens through the existing
  `OnIsWriteProtectedChanged` hook (§14.13a above), unchanged. Considered `ToggleButton`
  first but a plain `Button` + command avoids fighting the Fluent theme's own checked-state
  chrome (which would have reintroduced a second, competing visual signal).
- **Tests:** `CassetteDeckVmTests` (+2) — command disabled with no tape, enabled once mounted;
  executing it flips both `IsWriteProtected` and the live `MdcrDevice` state back and forth.
- **Applies to:** `src/P2000.UI/Views/StatusConverters.cs` (`BoolToPadlockIconConverter`,
  `BoolToWriteProtectLabelConverter`), `src/P2000.UI/Views/CassetteDeckWindow.axaml` (padlock
  `Button` replacing the `CheckBox`), `src/P2000.UI/ViewModels/CassetteDeckVm.cs`
  (`ToggleWriteProtectCommand`), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`
  (+2 tests).
- **Synced:** no (pure UI/UX polish, no hardware or architectural content).

### 2026-07-14 — CSAVE bug fix confirmed live; directory list didn't refresh after CSAVE
- **Owner-confirmed (live app, after the `P2000.Machine/CLAUDE.md` §17 blank-tape-silence
  fix):** the reported "Cassette fout N" CSAVE failure is resolved. Full round trip tested
  live: CLOAD from a real tape → eject → insert blank → CSAVE → save `.cas` → mount → CLOAD
  again — success. A second CSAVE (different name) onto the tape just loaded from also
  succeeded (search, rewind, save, rewind, search, validate). Replace (same name) and
  tape-full scenarios not yet tested by the owner.
- **Owner-supplied ROM source (`Cassette.asm`, the monitor's cassette driver) reviewed —
  important correction to earlier speculation:** the replace-vs-append decision in
  `cas_block_write` is driven entirely by two `cassette_status` RAM bits (`CST_NOMARK`,
  `CST_WCDON`) carried over from whatever cassette operation ran immediately before
  `cas_Write` — there is **no filename comparison inside `Cassette.asm` at all**. The
  "search for a file starting with the same letter, check it fits in the allocated block
  count" policy the owner described must live in BASIC's own save routine (which positions
  the tape and primes these bits before calling into this low-level ROM driver) — a
  different, higher-level source this project does not have. This means the earlier
  session's `AuthenticCassetteWriteTests` (all constructed as fresh `Machine()`s, so
  `cassette_status` = 0 = `CST_NOMARK` clear) likely exercised the **replace** path on their
  first `cas_Write` call — i.e. probably overwrote the target tape's first found block rather
  than genuinely appending a new file — even though every test only asserted `A==0`
  (success) and never checked *where* the write landed. Flagging honestly rather than
  claiming those tests validated "append" behavior they may not have. Confirmed-good gap
  understanding, not a machine bug: `Cassette.asm` is the low-level tape mechanic only;
  BASIC's file-management policy on top of it is out of scope without that source.
- **Found (owner-reported UI gap, fixed):** the cassette deck's program/directory list never
  refreshed after a live CSAVE (typed in BASIC) — it was only ever built once, from the bytes
  passed to `MountBytes` at mount time. A CSAVE mutates the tape's live bitstream directly
  through `MdcrDevice`/`MiniTape`; the VM had no way to know. Fixed by re-deriving the
  directory on the falling edge of `IsActive` (motor just stopped — covers both CLOAD and
  CSAVE finishing) via `_runner.Machine.Mdcr.SaveTape()` (the same host-side serializer
  "Save"/"Save as…" already use) → `ParseDirectory`, instead of relying on the mount-time
  snapshot.
- **Found (not unit-tested, documented rather than silently skipped):** verifying this
  specific fix needs a genuine "motor on then off" transition on a *live, running* `Machine`.
  A live machine actively runs the embedded ROM, which drives CPOUT itself (keyboard scan,
  its own CIP-triggered auto-load attempt on a freshly-mounted tape) — any CPOUT value a test
  forces gets overwritten by ordinary ROM execution within the same field, independent of
  which thread calls `Tick()`/`RunField()` (this isn't a threading race, it's the ROM's own
  deterministic execution), and `Machine.Tick()` fully no-ops while paused so pausing first
  doesn't help either. No clean way to synthesize this edge without driving a real CSAVE/
  CLOAD through BASIC. Same gap already existed for this class's `HasTape` 5 Hz resync,
  which was never unit-tested either. Verified instead by the owner's live-app test above.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`RefreshDirectoryFromLiveTape`,
  `_wasActive`, `OnFrameReady`), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`
  (documented the untested gap rather than shipping a flaky test).
- **Synced:** yes (2026-07-14 — the UI directory-refresh fix itself is implementation-only, but
  the `Cassette.asm` replace/append finding this entry cites turned out worth syncing after
  all — see `P2000.Machine/CLAUDE.md` §17's matching entry and P2000T-reference.md §5b
  "Replace vs append," now corrected).

### 2026-07-14 — Directory list block count off by one (floor vs ceiling division)
- **Owner-reported (live app, replace-scenario testing):** the "Blk" column undercounted —
  5673 occupied bytes showed 5 blocks (should be 6); 40 occupied bytes showed 0 blocks
  (should be 1).
- **Root cause:** `ParseDirectory`'s `blocks = occupied / 1024` was a floor division. The
  milestone-4 finding that documented this field ("space occupied on tape... divide by 1024
  to get blocks occupied") never established that `occupied` is always exactly
  block-aligned — and per the owner-supplied `Cassette.asm`'s own `get_length_blocks`
  routine, it correctly is NOT: block count is always a ceiling of byte-length/1024 with a
  minimum of one block for any nonzero length (a partial trailing block still counts as a
  whole reserved block). Floor division silently undercounted by one whenever `occupied`
  wasn't an exact multiple of 1024.
- **Fix:** `blocks = (occupied + 1023) / 1024` — ceiling division, matching the ROM's own
  arithmetic. Exact multiples of 1024 are unaffected (verified by a dedicated test) since
  ceiling and floor agree there.
- **Distinct from, and does not change, the already-confirmed "block count doesn't shrink on
  a replace-with-shorter-file" behavior** (milestone-4 finding, re-confirmed live by the
  owner this session) — that's about what value `occupied` legitimately holds (the ORIGINAL
  allocation, preserved across a replace); this fix is purely about correctly converting
  whatever that byte value is into a block count for display.
- **Tests:** `CassetteDeckVmTests` (+2) — the owner's exact reported numbers (5673→6, 40→1)
  via a new `BuildDirectoryEntry` header-construction helper; a block-aligned case (1024→1)
  confirms the fix doesn't regress the already-correct exact-multiple path.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory`),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (+2 tests).
- **Synced:** no (implementation-only UI display fix; the underlying `Cassette.asm`
  `get_length_blocks` ceiling-division fact is machine-adjacent context, already noted in
  the entry above, not new hardware content).

### 2026-07-14 — Block count follow-up: byte 0x1F tried and ruled out empirically
- **Owner instruction:** block count should come straight from the header's own block-counter
  field (offset 0x1F, per the class doc comment's own long-standing "1F: block counter" note)
  rather than be derived arithmetically from the occupied-bytes field.
- **Tried:** switched `ParseDirectory` to `blocks = casImage[hdr + 0x1F]` directly.
- **Reverted — owner empirically confirmed byte 0x1F is unusable:** inspecting a real `.cas`
  file, that byte is always zero. Likely explanation (ties back to the milestone-4 finding
  this session already had on record but under-weighted: "byte 1F ('blocks remaining') is a
  write-time counter, not used"): `Cassette.asm`'s `block_counter` RAM variable counts DOWN
  during a multi-block transfer and is only meaningfully non-zero mid-transfer — whatever
  ends up in the on-tape header's copy of it is a transient snapshot, not a stable per-file
  total, and evidently lands on zero in practice. Reverted to the ceiling-division-on-
  occupied-bytes approach from the entry above (already validated against the owner's exact
  reported numbers), updating the class doc comment to explicitly rule out 0x1F with the
  empirical reason, so this isn't re-attempted without new evidence.
- **Tests:** reverted `CassetteDeckVmTests`' block-count tests back to the occupied-bytes
  `BuildDirectoryEntry` helper (ceiling-division assertions), matching the entry above.
- **Follow-up — mechanism now understood, confirms the revert was correct (owner, same day):**
  the "always zero" file was created by a different tool, not this emulator — files this
  emulator's own CSAVE writes DO show a live decrementing value at byte 0x1F across a
  multi-block file (6, 5, 4, 3, 2, 1 for a 6-block file), because the header transfer is
  literally "32 consecutive bytes from memory" (`des_length`=0x20 from a fixed RAM address,
  Cassette.asm) — whatever `block_counter`'s live value happens to be at that moment rides
  along incidentally, it isn't a deliberate per-file field. Per the owner's own reasoning
  (not yet source-confirmed against BASIC, which this project doesn't have): **reading**
  derives the block count fresh from a size-bearing field via arithmetic and ignores the
  stored byte entirely — i.e. the same shape as the current `ParseDirectory` fix. Different
  tools populate that incidental byte differently (explains the contradiction with the
  externally-created file), so it was correctly ruled out either way. No further code change
  from this follow-up — recorded so the byte-0x1F idea isn't revisited without new evidence.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory` doc comment
  and `blocks` calculation), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`.
- **Synced:** no (implementation-only UI fix; documents a ruled-out approach for future
  reference, no new hardware content beyond what's already flagged above).

### 2026-07-09 — Integer scaling: physical vs logical pixels
- **Assumed:** computing the integer multiplier `n` from `Bounds.Width / Video.Width` (logical
  pixels) would produce exact integer multiples of source pixels on screen.
- **Found:** Avalonia `Bounds` are in logical (device-independent) pixels. At 125% Windows DPI
  (`RenderScaling = 1.25`), `n = floor(logicalWidth / 640) = 1` produces a `Rect` of 640
  logical units, which Avalonia renders as 800 physical pixels — not an integer multiple of the
  640×480 source (800 / 640 = 1.25).
- **Fix:** compute `n` in physical pixel space: `n = floor(logicalWidth × scale / 640)`, then
  convert back: `sw = n × 640 / scale` logical units → exactly `n × 640` physical pixels.
  At 125% DPI with a 640-logical-px panel, `n = 1` → 512 logical = 640 physical px.
- **Applies to:** project CLAUDE.md §14.6 (milestone 6, integer scaling) /
  `src/P2000.UI/Rendering/DisplayControl.cs` (`ComputeDestRect`).
- **Synced:** yes (2026-07-10 — DPI/physical-pixel integer scaling: rendering implementation-only, no reference change)

### 2026-07-19 — Milestone 3a: ms.3's "no host key at all" claim was wrong for the numpad function keys
- **Assumed (§14.3a, as drafted):** the numeric keypad's cassette/program-control keys (ZOEK,
  START, STOP, INL, OPN, DEF, and the tape/dsk/M icon keys) "have no host key to bind to at
  all, mapping mode aside" — implying the soft-keyboard window would be the first thing to
  make them reachable.
- **Found (false — ms.3 already wired all of them):** `src/P2000.UI/Input/KeyMap.cs`, sourced
  from `docs/Keyboard/keyboard matrix.md` (committed 2026-07-07, ms.3's own commit — never
  given a §18 findings entry, same "shipped correct but never logged" pattern already noted
  for SHIFT/digit-row in §14.3a), maps every one of these positions directly to the
  corresponding host NumPad key: NumPad0→DEF (2,3), NumPad1→ZOEK (7,3), NumPad2→flash (7,2),
  NumPad3→START (7,0), NumPad4→INL (8,3), NumPad6→OPN (8,0), NumPad7→tape (6,3),
  NumPad8→dsk (6,2), NumPad9→M (6,0), Decimal→STOP (2,0). CODE (4,0) is already mapped to
  LeftCtrl; Shift-Lock (3,0) to CapsLock. This is the same crosspoint the physical P2000 key
  serves for both plain numeric entry and its icon/function legend — one matrix position, ROM
  context decides the meaning — so ms.3's positional mapping already covers it correctly, no
  machine or KeyMap change needed.
- **Only two matrix positions remain host-unreachable** (known positions, just no host key
  bound): np00/TB (2,2) and np-center/tab/envelope (5,0). The soft-keyboard window can reach
  both directly by matrix coordinate since it enqueues positions, not host keys. **WIS** still
  has no matrix position sourced anywhere (open item, unchanged).
- **Revised scope for the soft-keyboard window (§14.3a):** it is a discoverability/no-numpad
  affordance and the only way to reach np00/TB, the envelope key, and (once sourced) WIS — not
  the first path to ZOEK/START/STOP/INL/OPN/DEF, which already work today from any keyboard
  with a numpad.
- **Applies to:** project CLAUDE.md §14.3a / `src/P2000.UI/Input/KeyMap.cs` /
  `docs/Keyboard/keyboard matrix.md`.
- **Synced:** no (corrects this project's own milestone doc; no reference-doc hardware content
  changes as a result).

### 2026-07-19 — Milestone 3a: soft-keyboard window + Standard-Host mode, built
- **Found (matrix correction, owner-caught):** `docs/Keyboard/keyboard matrix.md` and the new
  `docs/Keyboard/keyboard mappins.md` both mislabeled Port 0x06 bit 7 shifted as `"` — it's
  actually **¨ (umlaut/diaeresis dead key)**. The only key that produces `"` is Port 0x07 bit 7.
  Confirmed by re-photographing the physical key (shows two dots, not a double-quote glyph) and
  by the owner (who'd mistyped it in their own draft table). Fixed in both matrix files and
  `KeyMap.cs`'s comments — the matrix POSITION (`OemOpenBrackets` → (6,7)) was never wrong, only
  the documented character.
- **Found (photo re-verification caught a second transcription slip, mine this time):** my first
  pass at the soft-keyboard's visual layout misplaced the "'`" key (port 8 bit 4) and the "<>" key
  (port 3 bit 2) — I'd assumed "<>" sat on the top numeric row and "'`" didn't exist on the
  keyboard at all. Zoomed crops of `docs/Keyboard/P2000T keyboard.jpg` (via a scratch Python/PIL
  script — no image-crop tool exists in this toolchain) showed "'`" actually sits on the top row
  between `-_` and Backspace, and "<>" is the ISO-style key immediately left of Z (matching
  `KeyMap.cs`'s existing "ISO extra key" comment for `OemBackslash` → (3,2), which turned out to
  already be right — my new layout code was the one with the bug, not the shipped matrix).
- **Built:** `HostKeyTranslator` (`src/P2000.UI/Input/HostKeyTranslator.cs`) — stateful
  press/release bookkeeping shared by the physical-keyboard handler (`DisplayWindow.axaml.cs`)
  and the new soft-keyboard window, so both obey the same `KeyMappingMode`. `KeyMap.cs` gained
  `MapStandardHost` (an explicit override table derived algorithmically from the confirmed
  matrix — see `docs/Keyboard/keyboard mappins.md`'s "Standard-Host mode reverse mapping" — not
  hand-guessed) plus the `KeyMappingMode` enum. New `SoftKeyLayout.cs` (full photo-verified 10×8
  layout data), `KeyboardWindowVm`/`SoftKeyVm` (sticky Shift/CODE, toggle Lock, mode toggle,
  momentary regular-key press), `Views/KeyboardWindow.axaml(.cs)`.
- **Found (`DisplayWindow`'s `e.Handled` needed a real signal, not "always true"):** the
  pre-existing key handler only marked non-matrix keys (F5/F11/…) unhandled so the window's own
  `KeyBindings` still fired. Routing everything through the shared translator meant `KeyDown`/
  `KeyUp` had to start returning whether the key was recognized at all (in the current mode) —
  added that return value rather than hardcoding `Handled = true`.
- **Found (owner decisions, both confirmed live in this session, not guessed):** (1) the
  ambiguous `"` position — (7,7), not (6,7), per the umlaut correction above; (2) missing
  US symbols (`~^{}\|`) are a silent no-op in Standard-Host mode, matching the milestone's own
  "flag, don't guess" instruction rather than picking a wrong stand-in character.
- **Verified live (owner's running app, this session):** soft-keyboard click of "1" types `1`
  into BASIC; sticky Shift + click "8" in P2000-Authentic gives `(`; toggling to Standard-Host
  relabels the digit row (2→@, 3→#, 6→^, 7→&, 8→*, 9→(, 0→)) and Shift+8 now gives `*` instead —
  confirming both the translator's forced-shift bookkeeping and the mode-dependent legend
  refresh work end-to-end, not just at the unit level.
- **Tests (72 total in `P2000.UI.Tests`, all green):** `HostKeyTranslatorTests` — Authentic
  regression (Shift+8/Shift+2 unchanged), Standard-Host literal-character cases including both
  forced-shift-ON and forced-shift-OFF, the missing-symbol no-op, positional fallback, and OS
  auto-repeat suppression. `SoftKeyLayoutTests` — every `HostKey`-bearing position matches
  `KeyMap` exactly, the only `HostKey: null` positions are the three confirmed-unreachable ones
  (np00/TB, envelope, "#/°"), no duplicate matrix positions. `KeyboardWindowVmTests` — soft-click
  parity with the equivalent host key, sticky Shift latch/release (both click-again and
  release-after-one-key), CODE and Shift latch independently, mode toggle updates both the
  translator and displayed legends.
- **Not built (per the milestone's own scope):** CODE's semantic effect (modeled as a sticky
  modifier only, per the milestone's explicit "do NOT invent" instruction). **WIS's matrix
  position was resolved the same day — see the follow-up entry below — so it is NOT in this
  "not built" list**; it was always reachable (host `NumPad7`), only its shifted meaning was
  undocumented at the time this entry was first written.
- **Applies to:** project CLAUDE.md §14.3a / `src/P2000.UI/Input/KeyMap.cs`,
  `src/P2000.UI/Input/HostKeyTranslator.cs` (new), `src/P2000.UI/Input/SoftKeyLayout.cs` (new),
  `src/P2000.UI/ViewModels/KeyboardWindowVm.cs` (new), `src/P2000.UI/Views/KeyboardWindow.axaml(.cs)`
  (new), `src/P2000.UI/Views/DisplayWindow.axaml(.cs)` (menu item, key-handler rewiring),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`KeyTranslator`, `KeyboardVm`),
  `src/P2000.UI/Views/StatusConverters.cs` (+3 converters), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md` (umlaut correction + finished Standard-Host table),
  `tests/P2000.UI.Tests/Input/*` (new), `tests/P2000.UI.Tests/ViewModels/KeyboardWindowVmTests.cs`
  (new).
- **Synced:** no (implementation-only UI wiring; the umlaut correction is worth folding into
  `docs/P2000T-reference.md` §5f on the next human sync pass, since that's real hardware content).

### 2026-07-19 — Milestone 3a follow-up: WIS located (owner)
- **Assumed (per this milestone's own text above, and `docs/P2000T-reference.md` §6 "still to
  confirm"):** WIS's ROM-level function was known ("clear cassette dialog," §5b) but its matrix
  position had never been located anywhere — not in the owner's photo transcription, not
  independently derivable, open item.
- **Found (owner-confirmed):** WIS is **Shift + the numpad `7` key** (port 0x06 bit 3) — the key
  showing the mini-cassette-tape icon, already mapped in ms.3's `KeyMap.cs` to host `NumPad7`.
  Same shift-selects-the-function convention as ZOEK/START/STOP/INL/OPN/DEF elsewhere on the
  keypad (unshifted = the digit, shifted = the function) — no new matrix behaviour and no code
  change needed beyond the label: the key was always reachable and functional via Shift+NumPad7
  through the existing `HostKeyTranslator`, only its shifted MEANING was undocumented.
- **Changed:** `SoftKeyLayout.cs`'s (6,3) entry relabeled from generic "tape" to "WIS" (matching
  the ZOEK/START/STOP convention of showing the named function, not an icon description);
  `docs/Keyboard/keyboard matrix.md` and `docs/Keyboard/keyboard mappins.md` updated
  ("np 7 tape" → "np 7 tape/WIS").
- **Applies to:** project CLAUDE.md §14.3a (WIS paragraph, marked RESOLVED inline) /
  `src/P2000.UI/Input/SoftKeyLayout.cs` / `docs/Keyboard/keyboard matrix.md` /
  `docs/Keyboard/keyboard mappins.md`.
- **Synced:** no (this closes `docs/P2000T-reference.md` §6's "still to confirm" WIS item and
  should fold into that doc's §5b/§6 on the next human sync pass — real hardware content, not
  implementation-only).

### 2026-07-19 — Milestone 3a follow-up: CODE's function confirmed (owner)
- **Assumed (per this milestone's own text and `docs/P2000T-reference.md` §6 "still to
  confirm"):** CODE's matrix position AND its actual function were both fully unsourced;
  speculated candidates were a second shift level or a graphics/block-character set (both
  common on similar-era keyboards) — neither ever confirmed, hence "do NOT invent."
- **Found (owner-confirmed):** CODE's effect is **not a fixed emulator-level behaviour at all —
  it's cartridge/software-dependent**, decided by whatever's plugged into SLOT1, not by the
  keyboard hardware. With the **BASIC cartridge** specifically, CODE is used to control
  **LIST display speed** and while **editing BASIC program lines**. Different cartridge
  software could use the same matrix bit differently.
- **Confirms (no change needed):** modeling CODE as a bare sticky modifier — pressing/releasing
  matrix position (4,0), no character-set or shift-level logic on the emulator side — was
  already the correct design, for the same reason SHIFT needs none: the ROM/cartridge reads the
  bit and decides what it means, exactly like it does for SHIFT. Nothing to build differently in
  `HostKeyTranslator`, `KeyMap`, or the soft-keyboard window.
- **Applies to:** project CLAUDE.md §14.3a (CODE-key paragraph, marked RESOLVED inline).
- **Synced:** no (closes `docs/P2000T-reference.md` §6's "still to confirm" CODE function item —
  real hardware/software-behaviour content, fold in on the next human sync pass).

### 2026-07-20 — Milestone 3a follow-up: (8,4) is accent aigu/grave, not apostrophe/backtick — real bug found
- **Assumed:** `KeyMap.cs`, `SoftKeyLayout.cs`, and both `docs/Keyboard/keyboard matrix.md` /
  `keyboard mappins.md` labeled Port 0x08 bit 4 as `'` (unshifted) / `` ` `` (shifted) —
  apostrophe and backtick.
- **Found (owner-corrected):** it's actually **´ (accent aigu, unshifted) / ` (accent grave,
  shifted)** — a diacritic pair, not a literal apostrophe. The backtick half was coincidentally
  already correct (accent grave IS what ASCII backtick conventionally represents); only the
  unshifted half was wrong.
- **Found (real Standard-Host bug, not just a label fix):** `KeyMap.MapStandardHost` had no
  override for `(Key.OemQuotes, false)`, so it fell back to the positional mapping — pressing
  (8,4) unshifted. With the corrected label, that means a plain host `'` in Standard-Host mode
  was sending the P2000's **accent aigu**, not an apostrophe. The P2000 DOES have a real
  apostrophe — (0,6) shifted (Shift+7, part of the original confirmed digit-row table) — so
  added `{ (Key.OemQuotes, false), new MatrixTarget(0, 6, true) }` to redirect there. Backtick's
  existing override ((8,4) shifted) was unaffected and needed no change.
- **Not a bug in P2000-Authentic mode:** Authentic is positional passthrough by design — sending
  the P2000's own accent-aigu key for that host position is correct there; only Standard-Host
  (which promises the literal host character) needed the fix.
- **Tests (+3, `HostKeyTranslatorTests`):** Standard-Host apostrophe redirects to (0,6) shifted;
  Standard-Host backtick still targets (8,4) shifted (regression guard); Authentic mode still
  sends the positional accent-aigu for the same host key (regression guard, not "fixed" — that
  behavior is correct as-is).
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (comments + new override entry),
  `src/P2000.UI/Input/SoftKeyLayout.cs` ((8,4) entry), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`
  (+3 tests, 75 total in `P2000.UI.Tests`, all green).
- **Synced:** no (real hardware content — the accent-key identity and the P2000's actual
  apostrophe position — fold into `docs/P2000T-reference.md` §5f on the next human sync pass).

### 2026-07-20 — Milestone 3a follow-up: soft-keyboard icons + focus-return fix (owner feedback)
- **Icons added for pictorial keys.** `SoftKeyDef` gained optional `BaseIcon`/`ShiftedIcon`
  (file-name-only, resolved to `avares://P2000.UI/Assets/Icons/{name}.png`); `KeyboardWindow`'s
  `DataTemplate` shows an `Image` instead of the text legend when one is set, via the new
  `IconUriToBitmapConverter`. No new NuGet dependency — plain PNG via Avalonia's built-in
  `AssetLoader`/`Bitmap`, per root CLAUDE.md's minimal-dependency rule (an SVG library would
  have needed one). Wired to the `Assets\Icons\**` `AvaloniaResource` glob in the `.csproj`
  (previously absent — this project had no icon infrastructure at all before this).
- **Icons created (7, via a scratch Python/Pillow script — no image-authoring tool exists in
  this toolchain):** envelope (5,0 base), tape/WIS (6,3 shifted), disk/dsk (6,2 shifted), flash
  (7,2 shifted), flag/00 (2,2 shifted), plus the two keys beside the space bar — `home_up`
  ((0,2) base, combining the real key's ↖ + ↑ glyphs) and `end_down` ((2,5) base, combining
  ↘ + ↓) — scope was owner-specified as "all numpad keys, also the key to the left and right of
  the space bar." Textual numpad keys (INL/OPN/ZOEK/START/STOP/DEF/M and the plain digit/symbol
  pairs) were left as text — the real keycaps show colored TEXT there, not pictograms, so an
  icon would misrepresent them; only genuinely pictorial keys got images.
- **Focus-return fix (owner-reported):** clicking a soft key left the Keyboard window
  OS-focused, so the user couldn't type into the emulator without re-clicking it. Added
  `KeyboardWindowVm.KeyActivated` (raised at the end of every `ActivateAsync` branch — lock,
  sticky, and regular key); `KeyboardWindow.axaml.cs` subscribes and calls `Owner?.Activate()`.
  Verified live: soft-click "1" then a real host "2" keypress (no manual re-click) both landed
  in the emulator's BASIC prompt as "12".
- **Removed:** `StringNotEmptyConverter` (dead code once the shifted-label visibility logic
  moved to `SoftKeyVm.ShowShiftedText`, which also accounts for the icon case).
- **Tests:** existing 75 (`P2000.UI.Tests`) still green — icon rendering and the focus-return
  Activate() call are both AXAML/window-lifecycle behavior not covered by headless VM unit
  tests (same StorageProvider/TopLevel limitation already noted for ms.12/13's dialogs); verified
  live instead, per that established pattern.
- **Applies to:** `src/P2000.UI/Input/SoftKeyLayout.cs` (`BaseIcon`/`ShiftedIcon`, new icon refs),
  `src/P2000.UI/ViewModels/KeyboardWindowVm.cs` (icon computed properties, `KeyActivated` event),
  `src/P2000.UI/Views/KeyboardWindow.axaml(.cs)` (icon template, focus-return handler),
  `src/P2000.UI/Views/StatusConverters.cs` (`IconUriToBitmapConverter` added,
  `StringNotEmptyConverter` removed), `src/P2000.UI/P2000.UI.csproj` (`AvaloniaResource` glob),
  `src/P2000.UI/Assets/Icons/*.png` (new, 7 files).
- **Synced:** no (implementation-only UI polish; no new hardware content).

### 2026-07-20 — Real bug: Standard-Host forced-shift release hardcoded to Left Shift only
- **Owner-reported:** in Standard-Host mode, physical Shift+2 produced an unrelated glyph
  instead of `@`; Shift+3 similarly wrong instead of `#`.
- **Root cause:** `HostKeyTranslator`'s "force P2000 Shift off" path (needed for `@`, `#`, `;`,
  `<` — see the digit-row/Standard-Host table) always released the hardcoded P2000 Left-Shift
  crosspoint (9,0), regardless of which real host Shift key was actually held. Typing Shift+2
  with the **Right** Shift key (mapped to (9,7) — the natural choice when reaching for a
  left-side key with the right hand) meant the "force off" released a crosspoint that was never
  down — a no-op — while the REAL (9,7) press stayed asserted the whole time. So pressing (6,7)
  for `@` still read as shifted on the P2000 side, producing ¨ (the umlaut/diaeresis key) instead.
  Same mechanism for `#`/(2,4) and any other forced-off case.
- **Fix:** track which real Shift key(s) are actually down in a `_realShiftDown` set; "force
  off"/"restore" now release/re-press exactly those crosspoints (`ReleaseRealShifts`/
  `RestoreRealShifts`), never a hardcoded position. The "force ON" path (used when NO real Shift
  is held at all, e.g. plain `=` or `[`) is unaffected — there's nothing real to conflict with,
  so a synthetic (9,0) press/release is still safe there.
- **Not (yet) explained:** the owner also reported `[` showing two arrow characters instead of
  `[`. This isn't reachable from the same bug (the force-ON path for an unshifted key has no
  real-Shift conflict to get wrong) — most likely a keyboard-layout mismatch, where the physical
  key producing `[` on the owner's layout doesn't arrive as Avalonia's `Key.OemOpenBrackets`.
  Flagging rather than guessing; needs the owner to confirm which key/layout is in play.
- **Tests (+1, 76 total):** `StandardHost_RightShift2_ReleasesTheRightShiftCrosspoint_NotLeft` —
  reproduces the exact reported scenario with `Key.RightShift`, asserting (9,7) (not (9,0)) is
  what gets released and restored.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs` (`_realShiftDown`,
  `ReleaseRealShifts`/`RestoreRealShifts`, renamed `ShiftRow`/`ShiftCol` →
  `SyntheticShiftRow`/`SyntheticShiftCol`), `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`.
- **Synced:** no (implementation-only bug fix).

### 2026-07-20 — Real bug #2: force-off release and target press need a genuine field gap
- **Owner-reported (persisted after the left/right-crosspoint fix above):** Standard-Host
  Shift+2 still showed an up-arrow instead of `@`; Shift+3 still showed a block instead of `#`.
  Owner also confirmed via the **soft-keyboard mouse click** (not the physical keyboard) that
  the same wrong output occurs — ruling out a real-hardware/OS key-delivery quirk and ruling
  out the left/right-crosspoint fix as the (sole) cause, since the soft keyboard always drives
  a fixed `Key.LeftShift`, no ambiguity possible.
- **Diagnosed via a machine-level test** (bypassing `P2000.UI` entirely — booted the real
  `assets/BASIC.bin` cartridge, drove `KeyboardDevice.SetKey` directly with the exact sequence
  `HostKeyTranslator` emits, read the echoed byte back from VRAM): a clean, isolated, never-
  shifted press of (6,7) correctly echoes `@`. But releasing an ALREADY-held Shift crosspoint
  and pressing (6,7) in the same synchronous instant (same field) still echoes `^` — the same
  result as if Shift were genuinely still held. A one-field (20 ms) real gap between the
  release and the press was sufficient and necessary in every case tested (`gapFields: 0` →
  wrong, `1/2/3` → correct) — the monitor ROM's keyboard scan apparently needs to observe a
  moment with Shift genuinely released before it will register a subsequent keypress as
  unshifted, even though the emulated matrix state is technically already correct the instant
  both `SetKey` calls land.
- **Force-ON needs no such gap (also empirically confirmed):** the mirror case (no real Shift
  held at all, e.g. plain `=` or `[`) works correctly with ZERO gap between asserting the
  synthetic Shift and pressing the target key — there's no stale "already pressed" state to
  escape; it's exactly how a normal Shift+key combo already looks to the ROM.
- **Fix:** `HostKeyTranslator`'s force-OFF path now defers the target-key press via a
  fire-and-forget `Task.Delay(40ms)` after releasing the real Shift crosspoint(s), instead of
  emitting both in the same synchronous call. The target position is still recorded in
  `_activePress` immediately (so `KeyUp` knows what to release even if the press is still
  pending behind the gap). Force-ON is unchanged — confirmed not to need it.
- **Verified live (owner's build, this session):** Standard-Host Shift+2 → `@`, Shift+3 → `#`,
  both via soft-keyboard click, after the fix.
- **Tests:** `P2000.Machine.Tests/Boot/KeyboardScanTimingTests.cs` (new, permanent) — a
  machine-level regression pair proving the ROM genuinely needs the gap (`...NoGap_
  StillReadsAsShifted`) and that the gap fixes it (`...NeedARealFieldGap_ToRegisterAsUnshifted`),
  independent of `P2000.UI` — protects the underlying ROM-timing assumption itself, not just
  the translator's bookkeeping. `HostKeyTranslatorTests.cs` updated: the two existing force-off
  tests now `await` past the internal gap; added
  `StandardHost_ForceOff_TargetPressIsDeferred_NotImmediate` asserting the press genuinely
  hasn't landed right after `KeyDown` returns. 77/77 green in `P2000.UI.Tests`, 347/347 in
  `P2000.Machine.Tests`.
- **Not chased further (out of scope for this fix):** the same machine-level diagnostic
  surfaced what look like a couple of additional matrix-table transcription discrepancies
  (e.g. Shift+3's POSITIONAL target, port (0,4), read `#` rather than the previously-recorded
  `£` in one sweep) — but that sweep accumulated a long, un-cleared BASIC input line across
  ~140 key presses and is not trusted as clean evidence (probably input-buffer-length
  interference, same category of artifact as the timing bug this entry fixes). Flagging rather
  than silently editing the table on unverified data — if `£` is ever reported wrong live,
  re-verify with a fresh-boot single-key test the way (2,4)="#" and (6,7)="@" were confirmed
  above, not a long accumulated sweep.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs` (`ForceOffGapMilliseconds`,
  `PressAfterForceOffGapAsync`), `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.Machine.Tests/Boot/KeyboardScanTimingTests.cs` (new).
- **Synced:** no (the ROM-timing fact itself — Shift-release-to-keypress needs a real field
  gap to register as unshifted — is real hardware/ROM-behavior content worth folding into
  `docs/P2000T-reference.md` §5f on the next human sync pass).

### 2026-07-20 — Bracket keys are arrows, not brackets; matrix table fully re-verified via ROM data
- **Owner-reported:** Standard-Host `[` still showed two arrows; `]` showed one right-pointing
  arrow. Also: P2000-Authentic `[`/`]` show `@` and a right arrow "instead of `]`" — i.e. the
  owner expected literal brackets from Authentic mode too.
- **Root cause — NOT a translator bug:** `Saa5050Font.cs` (already-shipped, sourced font table)
  reassigns ASCII 0x5B/0x5D/0x5E to Left-Arrow/Right-Arrow/Up-Arrow GLYPHS. (7,4) — the position
  ms.3's photo transcription labeled "] [" — genuinely CANNOT display a literal bracket on real
  hardware; it displays an arrow. **P2000-Authentic mode showing arrows for `[`/`]` is correct,
  faithful emulation** — not a bug to fix. Standard-Host mode, which promises the literal host
  character, has nothing to redirect `[`/`]` to, so both are now "no P2000 equivalent" (null),
  same category as `~`/`^`/`{`/`}`/`\`/`|`.
- **Owner-suggested investigation that unlocked everything else:** the monitor ROM does a
  port-matrix→keycode conversion; the BASIC cartridge separately does a keycode→ASCII
  conversion via a table at Z80 address 6164. Dumping that table from `assets/BASIC.bin`
  (`table[keycode] = asciiByte`, keycode 0–~140ish before the table runs into unrelated Z80
  opcodes) gave ROM-sourced ground truth for the whole keyboard instead of continuing to guess
  from the photo.
- **Found (keycode formula, confirmed against 7+ independently-known facts):** unshifted keycode
  = `row×8+col`; shifted keycode = that **+72**. Also found: **BASIC forces all letters to
  uppercase** regardless of the table's own (mixed-case) entries — explains why every earlier
  letter-row test showed no shift/case distinction.
- **Found (the +72 theory is NOT universally reliable — do not trust it alone):** it wrongly
  predicted Shift+3 would show `#` (keycode 76 → 0x5F). Direct machine-level testing AND the
  owner's live observation both confirm Shift+3 genuinely shows **`£`** (byte 0x23) — which is
  itself a `Saa5050Font.cs` remap (British Pound) that an earlier pass of this investigation
  initially misread as plain '#' by naively casting the byte to ASCII without checking the font
  table for 0x23 specifically (only 0x5B/5C/5D/5E/5F/60/7B/7D/7E/7F had been checked). **Lesson
  recorded for next time:** decode every VRAM byte against the FULL `Saa5050Font.cs` comment
  table, never assume plain ASCII, and never trust a keycode-arithmetic extrapolation over a
  direct machine-level test.
- **Corrections applied, each independently confirmed by a direct SetKey→VRAM test (not the
  table formula alone):**
  - `[` / `]` / `` ` `` (backtick): no P2000 equivalent in Standard-Host (arrows/fractions only
    exist at those positions — see the (8,4) note below for backtick specifically).
  - Numpad "+/x" (5,2) shifted is `*`, not the letter `x`.
  - Numpad "-/:" (5,3) is `-` unshifted / ÷ (divide) shifted, not `_`/`:` — pairs with (5,2) as
    a calculator-style arithmetic-operator row, not "minus/colon".
  - Numpad "9/M" (6,0) shifted produces no visible character (silent function).
  - Numpad "5" (8,2) shifted doesn't echo into the input line either, but DOES trigger some
    other screen-level redraw (looked like it touched the top banner row) — genuinely unclear
    what it does; flagged, not chased further.
  - (8,4) (accent aigu/grave key): now CONFIRMED (independently, twice) to render as ¼/¾, not
    any accent mark — upgraded from "open question" to a settled fact. Backtick's
    Standard-Host redirect (which assumed (8,4) shifted gives literal backtick) is also wrong
    for the same reason — now null, same as the brackets.
  - (2,4) unshifted (`#`) and (0,4) shifted (`£`): re-confirmed CORRECT as originally
    documented — the +72 theory briefly cast doubt on both, wrongly.
- **Tests:** `tests/P2000.Machine.Tests/Boot/MatrixCharacterOutputTests.cs` (new, permanent,
  14 tests) — direct SetKey→VRAM assertions for every position in this entry, both the
  regression guards (positions that were already correct) and the actual corrections, plus the
  two silent-function keys. `HostKeyTranslatorTests.cs`: replaced the now-stale backtick test
  with `StandardHost_Backtick_IsNoOp`; added `StandardHost_Brackets_AreNoOp` and
  `Authentic_Brackets_CorrectlyShowArrows_NotBugged` (the latter explicitly asserting Authentic
  mode's arrow behavior is intentional, not a regression to "fix" later). 79/79 green in
  `P2000.UI.Tests`, 361/361 in `P2000.Machine.Tests`.
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (comments + `OemTilde`/`OemOpenBrackets`/
  `OemCloseBrackets` overrides all now null), `src/P2000.UI/Input/SoftKeyLayout.cs` (labels for
  (5,2)/(5,3)/(6,0)/(8,4), removed misleading Standard-Host legend on the bracket keys),
  `docs/Keyboard/keyboard matrix.md`, `docs/Keyboard/keyboard mappins.md`,
  `tests/P2000.Machine.Tests/Boot/MatrixCharacterOutputTests.cs` (new),
  `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`.
- **Synced:** no (real hardware/ROM content — the whole re-verified matrix table, the SAA5050
  national-character-set remaps, and the letter-auto-uppercase behavior — worth folding into
  `docs/P2000T-reference.md` §5f on the next human sync pass).

### 2026-07-19 — Real physical P2000T hardware test: matrix confirmed, three host-key wiring bugs found

- **Owner tested a real physical P2000T hooked to a monitor** and directly confirmed the (7,4)
  arrow behavior and the (8,4) ¼/¾ behavior from the prior findings entries match the emulator
  exactly — first ground-truth confirmation of this table against actual hardware rather than a
  photo transcription or ROM-table extrapolation.
- **Three NEW bugs found, but a different class from anything above:** the physical MATRIX
  values at (8,4)/(8,5)/(8,7) were already correct — the bug was which HOST KEY `KeyMap.cs`
  wired to which position:
  - Host `=`/`+` key (left of backspace) was wired to (8,5) `;`/`+`; should be (8,4) `¼`/`¾` —
    that's where it sits on a real keyboard.
  - Host `'`/`"` key (left of Enter) was wired to (8,4) `¼`/`¾`; should be (8,7) `:`/`*`.
  - Host `\`/`|` key did nothing (no host key reached this position at all); owner suggested
    giving it the P2000's `#`/block key function at (2,4).
- **Fix:** swapped the three `_map` entries (`OemQuotes`→(8,7), `OemSemicolon`→(8,5) filling the
  gap `OemPlus` vacated, `OemPlus`→(8,4)); added `Key.OemPipe`→(2,4) (confirmed via reflection
  dump of the Avalonia `Key` enum to be the correct, distinct member from `Key.OemBackslash`,
  which is already used at (3,2) for the ISO "<>" key). Updated `_standardHostOverrides`
  accordingly: removed the now-redundant `(OemSemicolon, false)` override (positional already
  gives `;`), added a new `(OemPlus, true)` override targeting `(8,5,true)` for `+` (OemPlus no
  longer naturally reaches it), and added `(OemPipe, false/true)` → null (no P2000 equivalent for
  a literal backslash/pipe character — OemPipe only reaches (2,4) in P2000-Authentic mode).
- **Tests:** `HostKeyTranslatorTests.cs` — updated the apostrophe/accent-key tests for the new
  positions, added `StandardHost_ShiftPlus_RedirectsAwayFromOemPlusOwnPosition`,
  `StandardHost_PlainSemicolon_NeedsNoOverride_PositionalAlreadyCorrect`,
  `StandardHost_ShiftSemicolon_RedirectsToColon`, `Authentic_PlainEqualsKey_...`,
  `Authentic_Backslash_SendsThePositionalBlockKey`, `StandardHost_Backslash_IsNoOp`.
  `SoftKeyLayoutTests.cs`'s `KeysWithNoHostKey_...` set shrank from 3 to 2 positions since (2,4)
  is no longer host-unreachable. 85/85 green in `P2000.UI.Tests`, 361/361 in
  `P2000.Machine.Tests`.
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (`_map` + `_standardHostOverrides`),
  `src/P2000.UI/Input/SoftKeyLayout.cs` (swapped `HostKey`/`HostBase`/`HostShifted` on the three
  affected `SoftKeyDef`s, added `OemPipe` to the (2,4) entry), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.UI.Tests/Input/SoftKeyLayoutTests.cs`.
- **Synced:** no (real-hardware confirmation is worth folding into `docs/P2000T-reference.md`
  §5f alongside the prior ROM-table findings on the next human sync pass).

### 2026-07-19 — Shift+numpad ZOEK bug (Windows nav-key override) + soft-keyboard ANSI reshaping

- **Owner-reported bug:** Shift + physical numpad-1 (NumLock on) didn't activate ZOEK, in either
  mode. **Root cause:** Windows overrides the reported key when Shift is held during a numpad
  press — with NumLock on, Shift+NumPad1 delivers `Key.End`, not `Key.NumPad1` (a documented OS
  behavior for text-selection convenience), indistinguishable from a real End-key press by `Key`
  alone. Avalonia's `KeyEventArgs.PhysicalKey` is scancode-based and unaffected by this override
  (confirmed via reflection dump: `PhysicalKey.NumPad0..9`/`NumPadDecimal` exist as distinct
  values from `PhysicalKey.End`/`Home`/arrows). **Fix:** `HostKeyTranslator.KeyDown`/`KeyUp` now
  take an optional `PhysicalKey` parameter and normalize the effective key through a
  `_physicalNumpadOverride` table before anything else runs — recovering `Key.NumPad1` (etc.)
  regardless of what Windows reported. `DisplayWindow.axaml.cs` passes `e.PhysicalKey` through;
  the soft-keyboard's synthetic presses are unaffected (they pass explicit `Key` values with no
  ambiguity, using the default `PhysicalKey.None`).
- **Tests:** `HostKeyTranslatorTests.cs` — `Authentic_ShiftNumpad1ReportedAsEnd_StillReachesZoek`,
  `StandardHost_ShiftNumpad1ReportedAsEnd_StillReachesZoek` (both simulate the exact Windows
  quirk: `KeyDown(Key.End, PhysicalKey.NumPad1)`), `Authentic_RealEndKey_UnaffectedByNumpadRecovery`
  (a genuine End press must NOT be swallowed), `Authentic_NumpadWithDefaultPhysicalKey_...`
  (soft-keyboard's no-PhysicalKey calls still work). Live end-to-end reproduction of the exact
  Windows quirk via computer-use automation was attempted but not completed this session — that
  input channel proved unreliable (see below) — so this fix is verified at the translator level
  only; owner should confirm on real hardware.
- **Owner-reported layout gap:** the soft-keyboard's shift row always showed the P2000T's own
  ISO-style shape (narrow left Shift + a "&lt;&gt;" key between Shift and Z, wired to
  `Key.OemBackslash`) even in Standard-Host mode, but the owner's real host keyboard is ANSI-shaped
  (wide left Shift, no key there) — asked whether Standard-Host mode should reshape to match
  (owner chose: reshape Standard-Host only, keep P2000-Authentic showing the P2000's real shape).
  **Fix:** `SoftKeyDef` gained `IsIsoOnly` (marks the "&lt;&gt;" key) and `StandardHostWidth`
  (overrides `Width` in Standard-Host mode only, set to 2.75 on the left Shift key — absorbing the
  hidden key's 1.0 width on top of Shift's own 1.75). `SoftKeyVm.IsVisible`/`PixelWidth` become
  mode-aware; `RefreshLabels()` renamed to `RefreshForModeChange()` since it now also refreshes
  shape, not just legends. `KeyboardWindow.axaml`'s button template binds the new `IsVisible`.
  Live-verified via screenshot in both modes: Standard-Host hides `<` and widens Shift; Authentic
  shows both unchanged.
- **Tests:** `KeyboardWindowVmTests.cs` — `Authentic_IsoKeyVisible_ShiftAtOwnWidth`,
  `StandardHost_IsoKeyHidden_ShiftWidened`, `StandardHost_OtherKeys_KeepTheirOwnWidth`. 92/92 green
  in `P2000.UI.Tests`.
- **Methodology note — computer-use as a live-testing channel proved unreliable this session:**
  a `key` tool call for a Shift+letter combo occasionally auto-repeated dozens of times instead of
  a clean press+release (flooding the input line), and a transient Windows shell overlay
  ("ShellHost", likely a notification toast) intermittently stole foreground focus and blocked
  all clicks/keys for several tool calls with no fix available from this side. Pixel-reading the
  soft-keyboard's tiny labels and the Debugger's VRAM hex view was also too unreliable at the
  window's native size to trust (aspect-distorted zoom crops, easy to miscount rows). The
  Debugger's **Memory Watch** (exact address → hex+ASCII text, e.g. VRAM row 7 col 0 = `0x5000 +
  7*80 = 0x5230`) was the one live-verification technique that gave an unambiguous, address-precise
  answer (confirmed Standard-Host `=` writes `0x3D` correctly) — prefer it over pixel-counting for
  any future live VRAM check.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs`, `src/P2000.UI/Views/DisplayWindow.axaml.cs`,
  `src/P2000.UI/Input/SoftKeyLayout.cs`, `src/P2000.UI/ViewModels/KeyboardWindowVm.cs`,
  `src/P2000.UI/Views/KeyboardWindow.axaml`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.UI.Tests/ViewModels/KeyboardWindowVmTests.cs`.
- **Synced:** no (the Windows Shift+NumLock nav-key override is general OS behavior worth a short
  note in `docs/P2000T-reference.md` if the machine-layer docs ever cover host-input quirks).

### 2026-07-19 — (5,0) "envelope/centre-tab" mislabel: envelope is shifted, not base; real function found

- **Owner-reported (real P2000T hardware):** the numpad key ms.3 coded as unshifted-envelope
  ("centre-tab" raw transcription, envelope shifted per the doc note) actually has the envelope
  as its **shifted** function, and it performs **clear screen** (the rectangle-with-cross icon
  visually gets "x-ed out"). The **unshifted** function is a different glyph — a vertical bar /
  right-arrow / left-arrow / vertical bar ("|→←|") — and performs **clear line + home cursor** to
  the leftmost column of the current line. "centre-tab" was a misread of this unshifted glyph.
- **Fix:** `SoftKeyLayout.cs`'s (5,0) entry now sets `BaseIcon: "clear_line"` (new asset,
  generated to match the existing icon set's style — light line-art on transparent 32×32) and
  `ShiftedIcon: "envelope"` (swapped from the other way around); `Base`/`Shifted` text fallbacks
  updated to "CLR"/"CLS". Both icons render stacked on the one key face (matching how a real
  keycap prints both functions at once), confirmed via a live screenshot.
- **Applies to:** `src/P2000.UI/Input/SoftKeyLayout.cs`, `src/P2000.UI/Assets/Icons/clear_line.png`
  (new), `docs/Keyboard/keyboard matrix.md`, `docs/Keyboard/keyboard mappins.md`. No test changes
  needed (this position has no host key at all — `HostKey: null` — so nothing in
  `HostKeyTranslator`/`KeyMap` is affected; existing `SoftKeyLayoutTests.cs` coverage for
  "positions with no host key" still holds).
- **Synced:** no (a real hardware-confirmed correction, same category as the other 2026-07-19
  findings above — worth folding into `docs/P2000T-reference.md` §5f together).
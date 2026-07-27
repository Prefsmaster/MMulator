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
- **Disk — drive (mechanism) vs. image (media): STALE, UPDATE (owner, 2026-07-26 — see reference
  doc §3a "CONFIRMED — .cfg already delivers 'plug everything in and flip the switch'"). This
  bullet predates M20's actual implementation** (`P2000.Machine` CLAUDE.md §17, 2026-07-23
  finding) **and undersells what's already built:** `MachineConfig.FloppyDrives[i].ImagePath` is a
  real, already-implemented config field, and `Machine`'s constructor already mounts every
  configured drive's image AT CONSTRUCTION — so a `.cfg` specifying a drive + its image is not
  "deferred," it's live today and reset-to-apply already carries it through (loading that `.cfg`
  and applying it reproduces "power on with this floppy already in the drive," matching the
  owner's real-hardware comparison). What's still true and unchanged: an already-present drive's
  image can ALSO be swapped live at runtime (insert/eject, ms.4/14, exactly like the cassette) —
  that's additive, not instead of the config-level path. **Two real gaps here, flagged rather
  than assumed either way — Claude Code should check before building anything:**
  1. **Does the Config window (ms.7/ms.14) actually expose a field to SET each drive's
     `ImagePath` as part of building/saving a `.cfg`** — or does it only support the runtime
     mount path (file dialog/drag-drop on an already-built machine, via the Disk Drive window),
     with no UI affordance to author "drive 0 should start with THIS image" into a `.cfg` at all?
     If the field exists on `MachineConfig` but the Config window never surfaces it, that's the
     actual remaining gap for the "preconfigured starting state" story, not a machine one.
  2. **Does saving a `.cfg` from an already-running machine capture what's CURRENTLY live-mounted
     in each drive** (and in SLOT1) back into the saved file, or only whatever was explicitly set
     via the Config window's own fields (which may now be stale if the user mounted something
     live afterward, per gap 1)? Needs checking against `ConfigWindowVm`'s actual save path
     before assuming either answer.
- **Cassette:** `.cas`/`.p2000t` via file dialog / drag-drop. **Live mount stays exactly as-is
  for the runtime case — but RESOLVED (owner, 2026-07-26, reference doc §3a): the asymmetry with
  disk is closed at the machine layer.** `MachineConfig.CassettePath` (nullable) is new
  (`P2000.Machine` CLAUDE.md milestone 20b) — a config can now author "boot with this tape
  already in the deck," mounted at construction, exactly like `Slot1CartridgePath`/
  `FloppyDrives[i].ImagePath`. **Same two UI-side gaps as disk (this section, above) apply here
  too, flagged rather than assumed:** does the Config window expose a field to SET
  `CassettePath` as part of building/saving a `.cfg`, and does saving a `.cfg` from a running
  machine capture the CURRENTLY live-mounted tape back into it? Whatever the answer turns out to
  be for disk's two gaps almost certainly generalizes here — worth fixing both devices in the
  same pass rather than as two separate investigations.
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
  from the machine as a user-facing message, don't crash. **Now self-contained** (reference doc
  §3a, 2026-07-26): mounted cassette/disk content embeds directly in the device blocks, so a
  `.state` file alone is a complete, shareable snapshot — no separate `.cas`/`.dsk` files need to
  travel with it.
- **`.uistate` (NEW, 2026-07-26) — the ONE exception to "not a UI concern":** unlike `.cfg`/
  `.state`, this sidecar IS owned and serialized by `P2000.UI` itself, not the machine (reference
  doc §3a; ms.14b). Written/read alongside a `.state` save/load, never inside it; missing or
  version-mismatched is never a load failure, just a default window layout.

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
        the host-side `DskImage.ReadDirectory()` API (M19) / `docs/P2000T-disk-formats.md` §4's
        32-byte directory-entry fields. **Side 2 stays unavailable** for a DS-mounted image until
        the machine layer sources side 2's directory offset (same open item M19/M20 carry
        forward, `docs/P2000T-disk-formats.md` §7 item 2) — show side 1 only, don't guess or leave a
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
        (live setter, defaults writable, disabled with no image mounted). Still does **not**
        persist through the `.dsk` file itself (M20 flag, unchanged) — but **UNBLOCKED (owner,
        2026-07-26, reference doc §3a + `P2000.Machine` CLAUDE.md §13.20):** it now DOES persist
        through `.state`, the same way cassette write-protect already does — `IsProtected` reads
        off the live `MdcrDevice`/`DskImage`, which now survives a Save-state → reload round-trip
        with no separate UI-layer persistence logic, exactly the pattern ms.13a already
        established for cassette (§13a "no separate UI-layer persistence logic needed, it falls
        out of the machine-layer fix"). No UI-side work needed here beyond what's already built.
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
14b. **Session UI-state persistence — `.uistate` sidecar (NEW, owner decision 2026-07-26,
    reference doc §3a "RESOLVED — UI-layer session state").** Fast-follow to ms.8 (Save-state UI),
    same "milestone + letter" pattern as ms.9a/13a/14a — unblocked now that the owning question is
    resolved (previously this milestone couldn't be scoped at all, since whether it existed, and
    where it would live, were both undecided).
    - **This is entirely a `P2000.UI` concern — no `P2000.Machine` change.** `Machine.SaveState`/
      `LoadState` and the `.state` format are untouched; `.uistate` is a sibling file this project
      alone reads and writes, named to match its `.state` (`mygame.state` + `mygame.uistate`).
    - **Contents:** which satellite windows are currently open (and their positions, if worth
      the fidelity), each memory-watch window's Base/Length/follow-register (§10), the VRAM
      window's glyph/hex toggle, and any other per-window configuration worth restoring — a
      `P2000.UI`-owned JSON shape with its own version field, independent of `.state`'s.
    - **Wiring — extends ms.8's existing Save-state / Load-state actions, not a new menu item:**
      on Save State, after the machine's `.state` write succeeds, also serialize the current
      window/watch layout to the sibling `.uistate` path. On Load State, after the machine's
      `.state` load succeeds, look for a sibling `.uistate`; if present and its version is
      readable, reopen/reposition windows and reconfigure memory watches from it; if absent or
      version-mismatched, proceed with whatever windows are currently open — **never block or
      fail the `.state` load over a missing/bad sidecar.**
    - **Not in scope (flag, don't build):** any attempt to make `.uistate` required, versioned in
      lockstep with `.state`, or embedded — reference doc §3a's resolution specifically rejected
      that shape. Also not in scope: restoring debugger breakpoints (a machine-owned concern,
      §3.2 — if breakpoints should survive a state load, that's the machine's `BreakpointStore`
      persisting via `.state` itself, a separate question from this milestone's UI-layout scope).
    - **Tests:** (a) Save State with several memory-watch windows open produces a `.uistate` that,
      on Load State, reopens the same windows at the same Base/Length; (b) Load State with no
      sibling `.uistate` (or an older/foreign one) succeeds with default window layout, no error
      dialog; (c) a `.uistate` whose version is newer than this build understands is ignored the
      same way, not a crash; (d) `.state` alone (no `.uistate` ever written) still round-trips the
      machine correctly — confirms the two files are truly independent, not silently coupled.
      → commit.

14c. **Startup configuration — remembers your last setup automatically; pinning available**
    (NEW, owner decision 2026-07-26, reference doc §3a "RESOLVED — startup configuration"; depends
    on machine milestone 20c's `CaptureCurrentConfig()`). Also finally closes the §7 investigation's
    confirmed gap — same underlying fix serves both.
    - **New app-level preferences file — a FOURTH file type, distinct from `.cfg`/`.state`/
      `.uistate`:** small JSON (e.g. `AppPreferences.json`) in the platform-appropriate per-user
      app-data folder (NOT the user's documents/save folder) — `StartupCfgPath` (nullable string),
      `StartupCfgIsPinned` (bool, default false).
    - **Auto-remember (default behavior, no toggle to turn it on):** on a clean app quit (and/or
      opportunistically on reconfigure/mount-eject — an implementation robustness choice, not a
      design fork), if NOT pinned, call `Machine.CaptureCurrentConfig()` (machine ms.20c) and
      serialize it via the EXISTING `.cfg` writer to a fixed path in the same app-data folder
      (e.g. `last-session.cfg`) — an ordinary `.cfg` file, no new format. Set `StartupCfgPath` to
      that path if not already.
    - **Startup:** if `StartupCfgPath` is set, try to load and apply it (same reset-to-apply path
      as any `.cfg`) in place of the bare default. **Fail soft on anything wrong** — missing file,
      parse error, version rejection — falls through to today's bare boot, never a startup error
      dialog. A fresh install (no preferences file at all) boots bare exactly as today.
    - **Pinning:** an explicit Config-window action ("Always start with this configuration") sets
      `StartupCfgPath` to a specific, separately-saved, user-named `.cfg` and `StartupCfgIsPinned
      = true` — auto-remember stops overwriting it until explicitly unpinned. This is a DIFFERENT
      file from the auto-managed `last-session.cfg` — pinning points at a real file the user
      manages themselves (browsed/saved through the ordinary Config window flow, itself now
      correctly capturing live media per the fix below).
    - **"New (bare) machine" stays the explicit escape hatch** to the honest baseline (already
      exists in spirit as a config action) — unaffected by any of this, and not gated behind a
      settings toggle; this milestone changes what happens on ordinary relaunch, not what "start
      fresh" means when asked for explicitly.
    - **Also fixes the §7 investigation's confirmed gap, using the SAME deriver:**
      `ConfigWindowVm.SaveCfgAsync`/`BuildConfig()` currently only serializes its own bound
      properties (`FloppyDriveRowVm.ImagePath` hardcoded `null`, no `CassettePath` at all). Change
      it to call `Machine.CaptureCurrentConfig()` when a machine is running, so an explicit
      "Save `.cfg`" now correctly captures whatever's actually mounted — closing the gap in
      general, not just for this feature's own auto-managed file.
    - **Separately, still worth doing (complementary, not a substitute for the fix above):** add
      manual browse/clear fields for each drive's `ImagePath` and for `CassettePath` in the Config
      window, mirroring `Slot1CartridgePath`'s existing pattern — for authoring a `.cfg` BY HAND
      (e.g. a "starter kit" config for someone else) without a machine running to capture from.
    - **Tests:** (a) quit with a disk mounted in drive 0 and a tape in the deck, relaunch — both
      are back, unmounted-then-remounted automatically at startup (via the reconstructed `.cfg`'s
      `FloppyDrives`/`CassettePath`); (b) a fresh install (no preferences file) boots bare,
      unchanged from today; (c) pin a specific `.cfg`, then change live topology and quit — next
      launch still uses the pinned file, not the changed live state; (d) unpinning resumes
      auto-remember on the next quit; (e) a corrupt/missing `StartupCfgPath` target falls through
      to bare with no error dialog; (f) `SaveCfgAsync` after live-mounting a disk/tape now saves
      those paths into the `.cfg` (regression guard for the §7 gap, now closed). → commit.

14d. **Drive-count axis assigns real-hardware indices, not 0-based sequential** (NEW, owner
    decision 2026-07-27, reference doc §5d — the "RESOLVED... closing the 'worth matching if...'
    flag" paragraph just above the two µPD765 usage facts). Triggered by the owner's own real
    boot test: mounting a boot floppy with today's "1 drive" default (internal index 0) silently
    doesn't boot, because the ROM only ever addresses index 1 (already documented, this milestone
    just fixes the UI default instead of leaving it as a trap).
    - **`ConfigWindowVm`'s row-count logic changes from assigning `DriveIndex` 0, 1, 2, … to
      assigning them in the fixed sequence 1, 2, 3, 0 as `FloppyDriveCount` goes 1→4.** "1 drive"
      → `[1]`; "2 drives" → `[1, 2]`; "3 drives" → `[1, 2, 3]`; "4 drives" → `[1, 2, 3, 0]`.
      Display labels stay "Drive 1"/"Drive 2"/"Drive 3"/"Drive 4" in that same left-to-right
      order — this is purely which `MachineConfig.FloppyDrives[i]` slot each row's `ToConfig()`
      targets, nothing about the visible ordering or labeling changes.
    - **`LoadFromCurrentConfig`/`LoadCfgAsync`'s collapse-to-a-count logic must follow the same
      sequence, not raw index magnitude:** walk `[1, 2, 3, 0]` in order, count = the length of the
      enabled prefix (index 1 enabled → at least 1; then 2; then 3; then 0). A config with a gap
      or an out-of-sequence-only drive (e.g. only index 0 set, nothing else — realistic only for
      a hand-edited `.cfg`, never produced by this window itself) collapses lossily to whatever
      count reaches it, same accepted limitation already documented for milestone 14's original
      "highest enabled index + 1" scheme, just restated against the new sequence.
    - **Not in scope:** anything about the machine layer — `Machine`/`MachineConfig`/`Upd765`
      already treat `FloppyDrives` as an arbitrary-index collection (machine milestone 20) and
      need no change; this is purely which indices THIS window's count control chooses to author.
    - **Tests:** (a) "1 drive" → the single row's `DriveIndex == 1`, not 0; (b) "2 drives" →
      `[1, 2]`; (c) "4 drives" → `[1, 2, 3, 0]`; (d) round-trip: build a config via this window at
      each count 1-4, save, reload, count and per-row `ImagePath` match; (e) loading a `.cfg` with
      only index 1 enabled collapses to count 1 (regression guard replacing the old "index 0 alone
      → count 1" case, which must no longer be how count-1 configs are produced or expected); (f)
      loading a `.cfg` with index 0 alone (an irregular hand-edited case) collapses to count 4 with
      drives 2/3 empty, not a crash. → commit.

14e. **Disk mount — geometry-mismatch dialog** (NEW, owner decision 2026-07-27, reference doc
    §5d's "RESOLVED... the label-based auto-detect above is JWSDOS-specific" block; depends on
    machine milestone 20d's mismatch-detection query surface). Triggered by real testing: a PDOS
    boot floppy (no JWSDOS label) and a genuinely short image (32,768 bytes mounted where the
    drive expected 327,680) both mounted with zero feedback today.
    - **`DiskDriveVm.MountBytes`/`MountAsync` (and the `.cfg`-authored construction-time mount
      path, machine-side) now surface whatever mismatch result ms.20d's mount call returns.** No
      mismatch → mounts exactly as today, no dialog, no behavior change for the common case
      (correctly-sized images, JWSDOS-labeled images that validate).
    - **Candidate-mismatch dialog** (file's length matches a DIFFERENT canonical geometry than
      the drive's configured Capacity/Sides — one or two candidates, per the 40-track/DS vs.
      80-track/SS collision): name the match(es) plainly ("this file's size matches 80-track/
      single-sided; the drive is configured for 40-track/double-sided"). **Owner's own requested
      resolution — let the user decide, don't guess:** offer **reconfigure the drive to the
      matching geometry and remount** (one button per candidate when there are two), **continue
      mounting with the current configuration anyway**, or **cancel**.
    - **No-candidate mismatch dialog** (file's length matches no canonical geometry at all — the
      genuinely-short case): state actual vs. expected byte counts plainly. Offer **extend to
      full size** (calls ms.20d's pad operation — in-memory only, nothing touches the host file
      until an explicit Save/Save-as; word the dialog honestly: this fills blank space with the
      same byte real formatting uses, it does NOT recover missing data), **continue mounting
      as-is**, or **cancel**.
    - **Never blocks:** every path above ends in a mounted drive (or a cancelled mount if the
      user explicitly chooses Cancel) — this is strictly better information + optional remedies
      over today's silent mount, not a new gate.
    - **Headless-test limitation, same shape as existing StorageProvider-dependent dialogs**
      (`SaveCfgAsync`'s own documented limitation): the dialog's own display isn't unit-testable
      without a real window; test the VM-level decision logic (which dialog shape a given
      mismatch result should trigger, and what each button does) directly against ms.20d's result
      type instead.
    - **Tests:** (a) a mismatch-free mount shows no dialog (regression guard for the common case);
      (b) a single-candidate mismatch offers exactly one reconfigure option plus continue/cancel;
      (c) a two-candidate mismatch (the 40DS/80SS collision) offers both; (d) reconfigure-and-
      remount actually changes the drive's Capacity/Sides and re-mounts with the new geometry;
      (e) a no-candidate mismatch's "extend to full size" pads the in-memory image and clears the
      mismatch state; (f) "continue as-is" on either dialog shape leaves the image mounted
      unchanged, mismatch state preserved for the session (e.g. shown as a small persistent
      badge/status — exact presentation is an implementation choice, not a design fork here).
      → commit.

14f. **Disk Save / Save As — IMD as the offered target, plain `.dsk` export preserved** (NEW,
    owner decision 2026-07-27, reference doc §3a "RESOLVED — adopt IMD... as the emulator's
    native/preferred disk container"; depends on machine milestone 21's IMD reader/writer).
    - **Plain "Save" never changes format:** saving a `.dsk`-backed drive writes `.dsk`, in
      place, exactly as today; saving an IMD-backed drive writes IMD, in place. No prompt, no
      format decision — matches how Save already behaves for cassette/config elsewhere in this
      UI (`DiskDriveVm`'s own existing Save action, ms.14/20).
    - **"Save As" is the ONLY path that can change format, and it always asks for a name and
      destination — never a silent conversion:** for a `.dsk`-backed drive, offer both
      **"Save as IMD"** (the preferred target — converts) and **"Save as `.dsk`"** (keep the
      legacy format, just a new file/location). For an IMD-backed drive, offer **"Save as
      IMD"** and **"Save as plain `.dsk`"** (the export path — state plainly in the dialog that
      this is lossy: any recorded sector order collapses to plain logical order in the exported
      file). Whichever option is chosen becomes the drive's new backing format AND path going
      forward (same `MountedPath`/format-flag update `DiskDriveVm.SaveAsAsync` already does for
      the path alone, ms.14c's follow-up fixes — extend it to also track format now that there's
      more than one).
    - **Mounting an `.imd` file skips ms.14e's mismatch dialog entirely** — machine milestone 21
      keeps IMD mounting fully deterministic (self-describing, no guessing), so there is nothing
      for that dialog to ever trigger on for an IMD-backed mount. Only raw `.dsk` mounts can hit
      ms.14e's flow.
    - **Write-protect UI is unaffected** — it already lives in this project's own config/`.state`
      layer (ms.14/20/20a), identical regardless of which format backs the drive; nothing here
      adds a per-format write-protect control.
    - **Tests:** (a) plain Save on a `.dsk`-backed drive still writes `.dsk` at the same path, no
      dialog; (b) plain Save on an IMD-backed drive writes IMD at the same path, no dialog;
      (c) Save As on a `.dsk`-backed drive offers both IMD and `.dsk` targets, and choosing IMD
      updates the drive's tracked format; (d) Save As on an IMD-backed drive offers both IMD and
      plain-`.dsk` export, and the exported `.dsk` is a valid raw sector dump machine milestone
      20d could re-mount cleanly (round-trip regression guard, accepting the lossy-order caveat);
      (e) mounting a real IMD file never shows ms.14e's mismatch dialog, even when its geometry
      wouldn't match the drive's current Capacity/Sides config (regression guard that IMD stays
      fully self-describing/deterministic). → commit.

14g. **Config window disk-image picking — unify with the live mount, preview-check the offline
    case, and surface mismatches proactively** (NEW, owner decision 2026-07-27, reference doc
    §3a's "RESOLVED — the Config window's own disk-image picking gets the same geometry-mismatch
    protection..." block — read it in full, it explains the "media mount isn't topology"
    reasoning behind the split below; depends on machine milestone 20e's `DetectMismatch`).
    - **Live case — delegate to the exact same mount action the Disk Drives window uses, don't
      re-implement it:** when this `ConfigWindowVm` is backed by a running machine AND the row's
      `DriveIndex` already exists in the live topology (`Board.Fdc` has a drive there), browsing
      a new image for that row's `ImagePath` must call straight into the SAME mount path
      `DiskDriveVm.MountBytes` uses (same `DskImage.Mount`, same `GeometryMismatchDetected`
      event/dialog) — not a second, parallel implementation. Practically: `ConfigWindowVm`/
      `FloppyDriveRowVm` needs a way to reach the corresponding live `DiskDriveVm` for its row's
      index (via `DisplayWindowVm`'s existing drive collection, ms.14 — don't have the Config
      window reach around it or duplicate machine access). After a successful live mount, the
      row's displayed `ImagePath` reflects what's now actually mounted (read back the same way
      `SaveCfgAsync`'s `CaptureCurrentConfig()` call already does), rather than tracking a
      separately-authored pending value for that row. **Capacity/Sides fields are UNCHANGED** —
      still genuine topology, still require Apply; only `ImagePath` picking gets this treatment.
    - **Offline case (no live machine, or the row's drive isn't live yet) — lightweight,
      non-blocking preview:** call `DskImage.DetectMismatch` (ms.20e) against the row's
      currently-set Capacity/Sides on every new file pick. A mismatch shows an analogous
      dialog — candidate: "update this row's Capacity/Sides to `<candidate>`" / "keep current
      settings anyway" / "choose a different file"; no-candidate: state actual/expected byte
      counts, offer "use anyway" / "choose a different file". **No pad option** — this window
      never touches file bytes, per the owner's explicit decision; real remediation stays with
      the Disk Drives window once the image is actually mounted.
    - **Bidirectional:** changing a row's Capacity/Sides AFTER a path is already set re-runs the
      SAME preview check against the new values — don't let a stale or newly-introduced mismatch
      sit unchecked just because the edit came from the other field.
    - **Proactive surfacing — generalizes beyond this window, closes the loop for real:**
      `Upd765.GetMismatch`'s existing construction-time signal (ms.14e) currently only gets
      raised once `DiskDriveWindowVm` subscribes to a drive — i.e. only if/when that window
      happens to be opened. Fix this at its source rather than just for this feature: whatever
      owns "a Reconfigure just landed" (`ConfigWindowVm.Apply`'s success path) and "the
      startup-config auto-load just landed" (`EmulationRunner`'s startup path, ms.14c) should
      walk every drive's `GetMismatch()` immediately afterward and raise the SAME dialog
      machinery a live mount already uses — regardless of whether the Disk Drives window is
      open. This turns the offline-authored case's preview warning into a guarantee: pick a
      mismatched image with no machine running, save the `.cfg`, and whenever that config is
      next actually applied (by anyone, including a future session's startup auto-load), the
      real mismatch surfaces immediately, not silently.
    - **Tests:** (a) picking a new image for a row backed by a live, already-existing drive
      performs a real live mount and can raise the geometry-mismatch dialog, identically to the
      Disk Drives window's own test for the same scenario; (b) the SAME action for a row with no
      live drive (or no machine running) only ever previews, never mounts; (c) changing
      Capacity/Sides after a path is set re-triggers the preview check (both introducing and
      resolving a mismatch); (d) Apply on a config with an unresolved offline mismatch surfaces
      the dialog immediately after the reconfigure succeeds, with no Disk Drives window open;
      (e) the startup-config auto-load path surfaces the same way for a `.cfg` saved with an
      unresolved mismatch, immediately at launch. → commit.

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

### 2026-07-27 — Housekeeping: `docs/JWSDOS-format.md` renamed to `docs/P2000T-disk-formats.md`
- **Trigger:** owner decision, mechanical follow-through of the rename recorded in
  `P2000.Machine/CLAUDE.md`'s own findings log the same day — this doc grew a substantial PDOS
  section (§6a) alongside its original JWSDOS content, making the old filename misleading.
- **Done:** updated this project's two forward-looking §14 build-order citations (the VRAM
  directory-browse milestone text) and `ViewModels/DiskDriveVm.cs`'s doc-comment citation to the
  new path. No behavior change.
- **Applies to:** `src/P2000.UI/CLAUDE.md` §14, `src/P2000.UI/ViewModels/DiskDriveVm.cs`.
- **Synced:** n/a — no reference-doc content changed, this is a path-reference sweep only.

### 2026-07-27 — Milestone 14g IMPLEMENTED: Config-window disk picking unified + proactive surfacing
- **Depends on machine milestone 20e** (`DskImage.DetectMismatch`/`DskImage.IsImdFile`, same day).
  Reference doc §3a "RESOLVED — the Config window's own disk-image picking gets the same
  geometry-mismatch protection...".
- **Live delegation:** `ConfigWindowVm` now takes a second constructor parameter, the SAME
  `DiskDriveWindowVm` instance `DisplayWindowVm.DiskVm` already owns (not a second instance) — the
  Config window's own `new ConfigWindowVm(_vm!.Runner)` call site became
  `new ConfigWindowVm(_vm!.Runner, _vm!.DiskVm)`. `FloppyDriveRowVm.BrowseImageAsync` now calls a
  new `ConfigWindowVm.PickImageForRowAsync(row, IStorageFile)`: if `_diskDrives.Drives` already has
  a live `DiskDriveVm` for the row's index, it calls straight into that VM's own `MountBytes` — the
  identical mount path (and `GeometryMismatchDetected` event) the Disk Drives window itself uses,
  no second implementation. The row's `ImagePath` is then read back from
  `Fdc.GetDisk(index)?.MountedPath`, mirroring `Machine.CaptureCurrentConfig()`.
- **Offline preview:** no live drive for the row → `DskImage.DetectMismatch` against the row's
  Capacity/Sides, skipping `DskImage.IsImdFile` files (self-describing, never mismatches — machine
  ms.20e's own `DetectMismatch` deliberately doesn't sniff IMD itself, so this project does).
  Raises a new `ConfigWindowVm.OfflineMismatchDetected` event; `ConfigWindow`'s own code-behind
  shows an analogous dialog (candidate: update the row's Capacity/Sides; no-candidate: state byte
  counts) with NO pad option, per the owner's explicit decision — this window never touches file
  bytes.
- **Bidirectional:** `FloppyDriveRowVm`'s `OnCapacityChanged`/`OnSidesChanged` partial hooks
  fire-and-forget an async recheck (`ConfigWindowVm.RecheckOfflineMismatchAsync`) that re-reads the
  row's current `ImagePath` from disk and re-runs the preview. `LoadFloppyDrivesFrom` now sets
  `ImagePath` BEFORE `Capacity`/`Sides` (was: Capacity/Sides then ImagePath) so those hooks recheck
  against the just-loaded path, not a stale previous one.
- **Proactive surfacing — the trickiest part, two genuinely different timing problems, not one:**
  - **Apply case:** `DiskDriveWindowVm.RebuildIfMachineChanged` (previously `private`) is now
    `internal`; `ConfigWindowVm.Apply()` calls it directly right after `EmulationRunner.Reconfigure`
    returns (which already blocks until the swap lands), forcing a synchronous rebuild instead of
    waiting for the next async `FrameReady` tick. Each freshly-built `DiskDriveVm`'s OWN
    construction-time `RaisePendingMismatchIfAny()` (pre-existing ms.14e behavior) is what actually
    raises the dialog here — no new mechanism needed for this half, since `DisplayWindow` is
    subscribed to `DiskVm.GeometryMismatchDetected` for the app's whole lifetime.
  - **Startup case — genuinely needed a new mechanism:** `DiskDriveWindowVm` itself is constructed
    as part of `DisplayWindowVm`'s OWN constructor (`DiskVm = new DiskDriveWindowVm(Runner)`),
    which necessarily runs BEFORE `DisplayWindow`'s code-behind has subscribed to anything (that
    only happens once `OnDataContextChanged` fires, after the VM constructor returns). So a
    startup-config mismatch's construction-time `RaisePendingMismatchIfAny()` fires into a
    dead event — and once fired, `DiskDriveVm.PendingMismatch` is null forever after, so
    re-subscribing later and looking at it again finds nothing. **Found:** `Upd765.GetMismatch()`
    itself is NOT a one-shot/consumed signal (ms.20d/14e: "the mismatch stays on record for the
    session") — only the VM's own `PendingMismatch` field is. Added
    `DiskDriveWindowVm.RaiseAnyPendingMismatches()`, which freshly re-queries
    `Fdc.GetMismatch(driveIndex)` directly for every CURRENT `Drives` entry, bypassing
    `PendingMismatch` entirely. `DisplayWindow.OnDataContextChanged` calls this once, right after
    subscribing to `DiskVm.GeometryMismatchDetected` — mirrors the same "subscribe THEN raise"
    ordering `RebuildIfMachineChanged` already uses per-drive. **Deliberately does NOT also call
    `RebuildIfMachineChanged`** — for the Apply case that would double-fire the same mismatch (once
    via the pending-mechanism during the forced rebuild, once via this fresh loop); the two
    mechanisms are used by exactly one call site each, never together.
  - Both `ConfigWindow` and `DisplayWindow`'s code-behind now carry a `ShowGeometryMismatchDialog`-
    shaped method (`DisplayWindow`'s is a near copy of `DiskDriveWindow`'s own, matching this
    project's existing per-window dialog-duplication convention — e.g. `ShowErrorDialog` already
    exists 3×).
- **Tests:** all 5 of the milestone's own listed cases, in `ConfigWindowVmTests.cs` (a, b, c, d)
  and `StartupConfigurationTests.cs` (e) — a fake `IStorageFile` was added (only `Name`/`Path`/
  `OpenReadAsync` implemented) since no test fake for it existed yet. Full `P2000.UI.Tests`:
  192/192 green (was 187).
- **Applies to:** `ConfigWindowVm.cs` (constructor, `FloppyDriveRowVm`, `PickImageForRowAsync`,
  `RecheckOfflineMismatchAsync`, `PickStorageFileAsync`), `DiskDriveWindowVm.cs`
  (`RebuildIfMachineChanged` visibility, `RaiseAnyPendingMismatches`), `DisplayWindow.axaml.cs`,
  `ConfigWindow.axaml.cs`.
- **Synced:** no (pending human sync into `docs/P2000T-reference.md` §3a).

### 2026-07-27 — Milestone 14f IMPLEMENTED: Save/Save As format choice (IMD as the offered target)
- **Depends on machine milestone 21** (`ImdFormat`/`DskImage.Format`/`GetImdBytes`, this same
  day). Reference doc §3a "RESOLVED — adopt IMD... as the emulator's native/preferred disk
  container."
- **Built (`DiskDriveVm`):** plain `SaveAsync` never changes format — writes back in place via
  whatever `DskImage.Format` currently reports (`GetBytes()` for `.dsk`, `GetImdBytes()` for
  IMD), no prompt, exactly as it behaved before IMD existed. `SaveAsAsync` is the only path that
  can change format: it now first raises a new `SaveAsFormatRequested` event
  (`Func<DiskImageFormat, Task<DiskImageFormat?>>`) carrying the drive's CURRENT format, awaits
  the view's answer, and only then opens the native save-file dialog (extension/file-type-filter
  chosen from the ANSWER, not the current format) — a cancelled format choice aborts before any
  file dialog ever shows. On a successful save, updates `MountedPath` (as before, ms.14c) AND
  `Format` (new) so a later plain Save keeps writing whatever was just chosen. No subscriber
  (headless/tests) keeps the CURRENT format — same "no subscriber, proceed" shape already
  established for `ConfirmDiscardRequested`.
- **`DiskDriveWindowVm`** relays `SaveAsFormatRequested` the same way it already relays
  `GeometryMismatchDetected`/others — one lambda subscription per drive VM in
  `RebuildIfMachineChanged`, falling back to `Task.FromResult(currentFormat)` if nothing above it
  has subscribed either (keeps the "no subscriber, proceed" default intact through BOTH relay
  hops, not just the inner one).
- **`DiskDriveWindow` (view):** new `ShowSaveAsFormatDialog` — a plain-code `Window`, same
  pattern as `ShowGeometryMismatchDialog`/`ShowConfirmDiscardDialog`. Always offers "Save as
  IMD"; the second button's label/tooltip depends on the CURRENT format — "Save as `.dsk`" (keep
  the legacy format, new file/location) for a `.dsk`-backed drive, or "Save as plain `.dsk`" with
  a tooltip stating the export is lossy (recorded sector order collapses to plain logical order)
  for an IMD-backed drive — plus "Cancel". Also: the Mount file dialog's `FileTypeFilter` and the
  window's drag-drop handlers (`OnDrop`/`HasDiskFile`) now accept `.imd` alongside `.dsk`/`.img`
  — needed for an IMD file to actually be mountable through the UI at all (content-based
  detection in `DskImage.Mount` doesn't care about extension, but the file picker/drop filter
  did).
- **Tests:** `DiskDriveVmTests` (+5) — same headless-TopLevel limitation as every other Save/
  Save-As test in this file (noted at the file's own top): the actual file WRITE isn't
  observable without a real desktop `StorageProvider`, but `SaveAsFormatRequested` fires BEFORE
  `GetTopLevel()` is ever called, so the format-choice decision itself is directly testable —
  (1)/(2) `SaveAsCommand` asks with the drive's actual current format (`Dsk` for a fresh blank
  disk, `Imd` for a drive mounted from real IMD bytes built via the public
  `DskImage.CreateBlank(...).GetImdBytes()`); (3) no subscriber doesn't throw and leaves `Format`
  unchanged; (4) a cancelled format choice leaves `Format`/path unchanged; (5) mounting real IMD
  bytes (deliberately a different geometry than the drive's configured Capacity/Sides) never
  raises `GeometryMismatchDetected` — the milestone's own test (e), at the UI layer (machine
  layer already covers this in `ImdFormatTests`). Full `P2000.UI.Tests`: 187/187 green (was 178).
- **Applies to:** `src/P2000.UI/ViewModels/DiskDriveVm.cs` (`SaveAsFormatRequested`,
  `SaveAsync`/`SaveAsAsync`/`WriteDiskToFileAsync`, Mount file-type filter),
  `src/P2000.UI/ViewModels/DiskDriveWindowVm.cs` (relay), `src/P2000.UI/Views/DiskDriveWindow.axaml.cs`
  (`ShowSaveAsFormatDialog`, drag-drop `.imd` acceptance),
  `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs`. Reference doc §3a.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a — new "IMPLEMENTED (UI
  milestone 14f...)" paragraph, including the `.imd` file-dialog/drag-drop filter addition the
  milestone text itself didn't spell out).

**2026-07-24 — trimmed for size.** This log had grown to ~1300 lines. Every entry was
checked against `P2000T-reference.md` — several stale "Synced: no" flags were corrected
(the content was already synced, just never marked), and two small genuine gaps (the umlaut
key correction, the (5,0) key's function pair) were found and synced this same pass. The
full historical log (every entry, unedited) now lives in
`docs/CLAUDE_ui_findings_archive.md` for posterity. What's kept live below: entries still
genuinely open, plus the last few active days, for continuity. Everything fully resolved and
already synced lives only in the archive now — check there before assuming something's
missing.

### 2026-07-27 — Milestone 14e IMPLEMENTED: disk mount geometry-mismatch dialog
- **Built (`DiskDriveVm`):** `MountBytes` now goes through `DskImage.Mount(diskImage, Capacity,
  SidesCount)` (machine ms.20d) instead of the raw `new DskImage(diskImage)` constructor — never
  fails to mount now (the old `try/catch (ArgumentException)` "not a valid disk image" rejection
  is gone; a too-short file mounts using the drive's configured geometry and reports a mismatch
  instead). New `GeometryMismatchDetected` event fires only when `mismatch.Kind != None`.
- **Recovery methods, one per dialog button:** `ReconfigureAndRemount(tracks, sides)` — re-mounts
  the CURRENTLY-mounted image's own bytes under a new geometry (updating `Capacity`/`Sides` going
  forward too, e.g. for a later "New (blank) disk"); re-raises the event if it somehow still
  doesn't match, but a candidate geometry (by construction) always resolves cleanly.
  `ContinueWithCurrentMount()` — a deliberate no-op; the image is already mounted, and the
  mismatch stays on record (`Upd765.GetMismatch` keeps reporting it) rather than being silently
  cleared, so a persistent status indicator could still reflect it. `ExtendMountedDiskToFullSize`
  — calls `DskImage.ExtendTo`, then re-records the mismatch as `None` at the new length.
  `CancelMount()` — the one path that does NOT end in "mounted": ejects the just-mounted image
  (factored `EjectAsync`'s body out into a shared `ReturnToEmptyState()` helper); skips the
  unsaved-changes gate since there's nothing of the user's to lose from a mount made moments ago.
- **Construction-time (`.cfg`-authored) mismatch surfacing:** a mismatch raised synchronously
  inside `DiskDriveVm`'s OWN constructor would fire before `DiskDriveWindowVm` (which subscribes
  to each drive's events right AFTER constructing it, same pattern as its existing
  `ShowMessageRequested`/`ConfirmDiscardRequested` relays) could possibly be listening — so the
  constructor only captures it into a new `PendingMismatch` property, and `DiskDriveWindowVm`
  calls the new `RaisePendingMismatchIfAny()` immediately after subscribing. `DiskDriveWindowVm`
  itself gained a relayed `GeometryMismatchDetected` event carrying `(DiskDriveVm, DiskGeometryMismatch)`
  — the view needs to know WHICH drive to call back into.
- **Two real machine-layer bugs found and fixed while wiring this up** (found via the first real
  test run, not by inspection) — see this same date's entry in `P2000.Machine` CLAUDE.md §17:
  `DskImage.ReadDirectory()` crashed on any short/unpadded image (it assumed `_data` was always
  ≥ `0x2000` bytes — true for every real disk, no longer true once ms.20d made short mounts
  normal); `Machine.CaptureCurrentConfig()`'s `Capacity`/`Sides` never reflected a live
  reconfigure, only ever echoing the stale construction-time config (same staleness class
  `ImagePath` was already fixed for in ms.20c).
- **Actual dialog built in `DiskDriveWindow.axaml.cs`** (not just the VM-level decision logic) —
  two shapes over one plain-code `Window` (same style as this file's existing error/discard
  dialogs, no XAML): Candidate mismatch names the match(es) and offers one "Use `{geometry}` +
  remount" button per candidate; No-candidate mismatch states actual-vs-expected byte counts and
  offers "Extend to full size" (only when `CanPad`, with a tooltip stating plainly it fills blank
  space rather than recovering data) — both always also offer "Continue mounting as-is" and
  "Cancel".
- **Tests:** `DiskDriveVmTests` (+9, plus 1 existing test rewritten): a labeled, correctly-sized
  mount raises no event (regression guard); single- and two-candidate mismatches report the
  right candidate set; `ReconfigureAndRemount` changes the live disk's `Tracks`/`Sides` and
  clears the mismatch; `ExtendMountedDiskToFullSize` pads the image to the expected length and
  clears the mismatch; `ContinueWithCurrentMount` leaves the disk instance and mismatch
  untouched; `CancelMount` ejects; a construction-time `PendingMismatch` only fires after
  `RaisePendingMismatchIfAny()` is called, never eagerly. Rewrote
  `MountBytes_TooShortForLabel_ShowsMessage_DoesNotMount` (behavior fundamentally changed — it
  now mounts and reports a mismatch instead of rejecting) into
  `MountBytes_TooShortForLabel_MountsAnyway_ReportsNoCandidateMismatch`. Full `P2000.UI.Tests`:
  182/182 green (was 174, net of +9 new and the rewritten one).
- **Applies to:** `src/P2000.UI/ViewModels/DiskDriveVm.cs`, `src/P2000.UI/ViewModels/DiskDriveWindowVm.cs`,
  `src/P2000.UI/Views/DiskDriveWindow.axaml.cs`, `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs`.
  Reference doc §5d's "RESOLVED — the label-based auto-detect above is JWSDOS-specific" block;
  machine ms.20d.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §5d — new "IMPLEMENTED (UI
  milestone 14e...)" paragraph confirms this was built exactly as designed, no corrections
  needed).

### 2026-07-27 — FOLLOW-UP FIX 3: Apply was silently re-rolling RamSeed/BankCount, causing false "stale config" mismatches
- **Trigger — owner follow-up:** "When I load a config, apply, then unpin, pin it also asks for
  a save, even though I did not modify the loaded config..." — reported right after follow-up
  fix 2 (below) shipped the live-vs-saved comparison, and correctly suspected it was overzealous.
- **Root cause — a genuine gap in `ConfigWindowVm`, not a flaw in the comparison itself:**
  `BuildConfig()` (what `Apply` feeds to `Reconfigure`) never set `RamSeed` or `BankCount` —
  neither has a bound UI field, so both silently defaulted to `null` on EVERY Apply, regardless of
  what was loaded. `EmulationRunner.Reconfigure`'s own `EnsureRamSeed` treats a `null` seed as "a
  real cold start" and rolls a fresh random one (project CLAUDE.md §17, 2026-07-21/22 finding) —
  correct behavior when authoring a topology from scratch, but wrong when re-applying an
  UNCHANGED loaded config, since a previously-Saved `.cfg` always has a concrete, non-null
  `RamSeed` baked in (`SaveCfgAsync` echoes `Machine.CaptureCurrentConfig()`, which never returns
  null there). Net effect: Load → Apply → Pin ALWAYS saw a mismatch on `RamSeed` alone, even with
  zero field edits, because the live machine's seed got silently re-rolled the moment Apply ran.
- **Fixed:** `ConfigWindowVm` now carries `RamSeed`/`BankCount` through as plain (non-bound)
  private fields — captured in `LoadFromCurrentConfig()` and `LoadCfgAsync()` from whatever
  config was read, included in `BuildConfig()`. A freshly-authored config (never loaded from
  anywhere) still gets `RamSeed = null` → a genuine fresh cold-start seed on Apply, unchanged from
  today; only a config that WAS loaded/synced from somewhere now preserves its exact seed across
  an Apply with no edits — matching `Reconfigure`'s own doc comment, which already described this
  as the intended behavior ("pass a config with RamSeed already set... to keep that value
  instead") but nothing upstream of it ever did so. `BankCount` gets the identical treatment
  pre-emptively (same shape of gap, not yet reported but would hit the same class of bug for
  anyone using a non-default bank count).
- **Tests:** `StartupConfigurationTests` (+1):
  `PinAsStartupConfig_LoadThenApplyWithNoEdits_DoesNotPromptForSave` — reconfigures a runner to a
  concrete `RamSeed`, saves that captured config to a file, opens a `ConfigWindowVm` against the
  already-running machine (`LoadFromCurrentConfig`, the headlessly-testable equivalent of Load
  `.cfg` — the real Load dialog needs a StorageProvider this test run doesn't have), Applies with
  NO field edits, then Pins — asserts no re-save is needed AND that `RamSeed` survived Apply
  unchanged. Full `P2000.UI.Tests`: 174/174 green (was 173).
- **Applies to:** `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`_ramSeed`, `_bankCount`,
  `LoadFromCurrentConfig`, `LoadCfgAsync`, `BuildConfig`),
  `tests/P2000.UI.Tests/State/StartupConfigurationTests.cs`.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a "RESOLVED — startup
  configuration" — folded into the revised Pinning bullet's "always available" wording, which
  covers RamSeed/BankCount staying stable across an unedited Load → Apply as part of why staleness
  detection had to be correct rather than naive).

### 2026-07-27 — FOLLOW-UP FIX 2: pinning now detects a stale saved file, not just "nothing saved yet"
- **Trigger — owner follow-up (asked as a question, confirming it was a real gap):** "user loads
  prior config, tweaks it a bit, clicks apply. When active config doesn't match the saved config,
  clicking always start with this config should also prompt for 'save'. Or is this already
  covered by this last change?" It was NOT covered — the previous fix only handled `LastCfgPath
  is null` (nothing saved/loaded yet in this window session); once ANY `.cfg` had been
  loaded/saved once, `LastCfgPath` stayed non-null forever after, so Pin would silently reuse that
  stale path even after further field edits + Apply diverged the live machine from it.
- **Fixed — `PinAsStartupConfigAsync` now compares live vs. saved before trusting `LastCfgPath`:**
  new `SavedCfgMatchesLiveConfig(path)` reads the file at `path` and compares it, byte-for-byte,
  against `MachineConfigFile.Serialize(Machine.CaptureCurrentConfig())` — the exact same
  serialization `SaveCfgAsync` itself would produce right now. A mismatch (or any read/parse
  failure — a missing/moved file counts as "doesn't match", the safe default) is treated the same
  as "nothing saved yet": prompts the Save `.cfg` dialog before pinning.
- **Found and fixed a second, more serious bug while wiring this up — a fall-through that would
  have pinned the stale file anyway:** the naive re-check after prompting for a save was `if
  (LastCfgPath is null) return;`, copied from the "nothing saved yet" case — but in the MISMATCH
  case, `LastCfgPath` was already non-null (pointing at the STALE file) before the save attempt.
  If the save dialog was cancelled (or, headlessly, has no `TopLevel` to attach to at all),
  `LastCfgPath` stays exactly as it was — non-null — so that check passed and execution fell
  through to pin the stale file regardless, defeating the whole fix. **Caught by the test for this
  exact scenario, before it shipped** (`PinAsStartupConfig_LiveConfigDivergedFromSavedFile_...`
  failed on the first attempt with `IsStartupPinned` unexpectedly `true`). Fixed: re-run
  `SavedCfgMatchesLiveConfig` on whatever `LastCfgPath` is AFTER the save attempt, rather than
  just checking non-null — only a save that actually landed leaves the file matching.
- **Tests:** `StartupConfigurationTests` — renamed and fixed the previous "pins directly" test
  (`PinAsStartupConfig_SavedCfgAlreadyMatchesLiveConfig_PinsDirectly_NoRePrompt`) to save the
  ACTUAL live-captured config to disk rather than a separately-constructed bare `MachineConfig()`
  (the two were never byte-equal in the first place — every `EmulationRunner` gets its own
  randomly-generated `RamSeed`, project CLAUDE.md §17 2026-07-21/22 — so this test would have
  started failing the moment `SavedCfgMatchesLiveConfig` existed, for the RIGHT reason: it
  correctly detected the mismatch). New:
  `PinAsStartupConfig_LiveConfigDivergedFromSavedFile_PromptsReSave_DoesNotPinStaleFile` (+1) —
  a deliberately-mismatched saved file must leave `IsStartupPinned`/`AppPreferences` untouched
  after a (headlessly-unavailable) save attempt fails, the regression guard for the fall-through
  bug above. Full `P2000.UI.Tests`: 173/173 green (was 172).
- **Applies to:** `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`SavedCfgMatchesLiveConfig`,
  `PinAsStartupConfigAsync`'s re-check), `src/P2000.UI/Views/ConfigWindow.axaml` (hint/tooltip
  text), `tests/P2000.UI.Tests/State/StartupConfigurationTests.cs`.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a "RESOLVED — startup
  configuration" — this IS the byte-for-byte staleness check the revised Pinning bullet describes).

### 2026-07-27 — FOLLOW-UP FIX: Pin button redesigned to never be permanently ghosted
- **Trigger — owner follow-up:** the previous fix (below) made the Pin button correctly enable
  after a successful Load/Save `.cfg`, but the owner pointed out a real UX gap: starting from a
  BARE machine, configuring it, then clicking Apply (a reset, not a save) left the button
  ghosted again with no explanation — a user who doesn't already know "you must Save/Load first"
  has no way to discover that. Owner's own framing: "it is better that it becomes available
  after a reset, but then prompts for a save config action."
- **Redesigned, not just re-triggered:** removed `[RelayCommand(CanExecute = nameof(CanPinStartup))]`
  entirely — `PinAsStartupConfigCommand` is now unconditionally enabled (`CanPinStartup` and the
  `OnLastCfgPathChanged`/`NotifyCanExecuteChanged` plumbing from the previous fix are both gone,
  superseded rather than layered on). The command itself (renamed `PinAsStartupConfigAsync`,
  generated command name unchanged since CommunityToolkit strips the `Async` suffix — no XAML
  binding change needed) now checks `LastCfgPath` internally: if null, it calls
  `SaveCfgAsync()` — the exact same Save `.cfg` dialog the button next to it uses — and only
  proceeds to pin if that save succeeded (`LastCfgPath` now set); cancelling the save dialog
  leaves nothing pinned, same as cancelling any other save. If `LastCfgPath` was already set
  (a prior Load/Save in this window session), pins directly with no re-prompt.
- **Tests:** `StartupConfigurationTests` (+2): `PinAsStartupConfigCommand.CanExecute(null)` is
  `true` even with `LastCfgPath` still null (the direct regression guard for the ghosted-button
  report); pinning with `LastCfgPath` already set pins directly (awaited via `ExecuteAsync`
  rather than fire-and-forget `Execute`, since the command is now `async`). The "no
  `LastCfgPath` → prompts and pins" branch itself still needs a real StorageProvider dialog to
  exercise (same headless limitation `SaveCfgAsync` already has) — not separately covered here.
  Full `P2000.UI.Tests`: 172/172 green (was 170).
- **Applies to:** `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`PinAsStartupConfigAsync`),
  `src/P2000.UI/Views/ConfigWindow.axaml` (hint/tooltip text),
  `tests/P2000.UI.Tests/State/StartupConfigurationTests.cs`.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a "RESOLVED — startup
  configuration" — the revised Pinning bullet's "always available... prompts to save first" is
  this redesign).

### 2026-07-27 — FIXED: three bugs in milestone 14c, found via real owner usage (BASIC24k cartridge + boot floppy)
- **Trigger:** owner's first real end-to-end run of the "power on preconfigured" story — loaded
  `Basic24k.bin` into SLOT1, mounted a boot floppy live via the Disk Drives window, booted into
  disk BASIC, saved a `.cfg`. Reported: (1) "Always start with this configuration" stayed
  ghosted; (2) relaunch booted bare/BASIC-empty regardless; (3) the description text under each
  section (Cassette, SLOT1, Monitor ROM, etc.) ran off the right edge of the fixed-width window.
- **Bug 2 (relaunch boots bare/empty) — the ACTUAL root cause, not a cosmetic issue:** `DiskDriveVm.MountBytes`/
  `CassetteDeckVm.MountBytes` (the file-dialog/drag-drop mount path every real user takes) never
  set `DskImage.MountedPath`/`MdcrDevice.MountedPath` at all — milestone 14c only wired that
  property up automatically for the `DskImage(string path)`/`Mdcr.InsertTape(bytes, path)`
  CONSTRUCTION-time paths (machine ms.20b/20c), which a *live* UI mount never goes through (the UI
  reads the file's bytes itself via `OpenReadAsync`, then calls `MountBytes(bytes, filename,
  backingFile)` — the real path was sitting right there in `backingFile.Path.LocalPath` and
  simply never got forwarded). Net effect: `Machine.CaptureCurrentConfig()` always saw
  `MountedPath == null` for anything mounted through the actual UI (as opposed to a `.cfg`'s own
  `ImagePath`/`CassettePath` at construction) — so the owner's floppy never made it into the saved
  `.cfg` at all, and the SLOT1-only config that DID save correctly booted straight into a
  disk-boot-gate wait with no disk → "empty machine." **This is exactly bug 2's cause, not a
  separate issue** — fixing it fixes both.
  - **Fixed:** `DiskDriveVm.MountBytes` now sets `disk.MountedPath = backingFile?.Path.LocalPath;`
    right after mounting; `DiskDriveVm.SaveAsAsync` updates it again to the new file after a
    successful Save-as. `CassetteDeckVm.MountBytes` now passes `backingFile?.Path.LocalPath`
    straight into the (already-existing, from ms.20c) `MdcrDevice.InsertTape(bytes, path)`
    overload; `CassetteDeckVm.SaveAsAsync` updates `Mdcr.MountedPath` after a successful Save-as
    the same way. Eject/New-blank paths needed no fix — they already correctly clear
    `MountedPath` at the machine layer (ms.20c).
- **Bug 1 (Pin button ghosted) — a separate, independent bug, also real:**
  `[RelayCommand(CanExecute = nameof(CanPinStartup))]` does NOT
  automatically re-check `CanExecute` when the backing property (`LastCfgPath`) changes —
  `[NotifyPropertyChangedFor(nameof(CanPinStartup))]` only updates `CanPinStartup`'s OWN bindable
  value; CommunityToolkit requires an explicit `PinAsStartupConfigCommand.NotifyCanExecuteChanged()`
  call, which milestone 14c never added. **Fixed:** a new `partial void
  OnLastCfgPathChanged(string? value)` calls it. This was independent of bug 1/2 above — even
  once `SaveCfgAsync` correctly captured live media, the Pin button would have stayed ghosted
  without this fix too.
- **Bug 3 — text truncation:** none of the description `TextBlock`s under each section header had
  `TextWrapping="Wrap"` (Avalonia's default is `NoWrap`), so anything longer than the fixed
  480px-wide window's content area ran off the right edge instead of wrapping. **Fixed:** added a
  shared `TextBlock.hint` style (`Foreground #777`, `FontSize 11`, `TextWrapping Wrap`) and
  switched every such caption to `Classes="hint"` instead of repeating the same three inline
  setters (also fixes any future caption the same way for free).
- **Not separately unit-tested (documented limitation, not an oversight):** the `MountBytes`
  fix's exact `backingFile.Path.LocalPath` plumbing would need a fake `IStorageFile` to exercise
  headlessly — same StorageProvider-needs-a-real-desktop limitation this suite already accepts
  elsewhere (`MemoryWatchVmTests`' own doc comment). Confidence instead comes from: the identical
  mechanism (`MountedPath` set from a real path) is already covered end-to-end by
  `StartupConfigurationTests` and machine-layer `MachineTests` via the construction-time path,
  and this fix is a one-line delegation to the same property from a call site that already has
  the real path in scope. Full `P2000.UI.Tests`: 170/170 green (unchanged — no test count change,
  since no new automated test was added for this specific gap).
- **Applies to:** `src/P2000.UI/ViewModels/DiskDriveVm.cs` (`MountBytes`, `SaveAsAsync`),
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`MountBytes`, `SaveAsAsync`),
  `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`OnLastCfgPathChanged`),
  `src/P2000.UI/Views/ConfigWindow.axaml` (`TextBlock.hint` style). Milestone 14c (this file,
  above).
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a "RESOLVED — startup
  configuration" — Bug 2, the actual root cause, is the whole subject of the new "IMPLEMENTED,
  then CORRECTED" paragraph there; Bug 1 (Pin ghosting) is covered by the Pinning bullet's
  redesign note; Bug 3 (text wrap) is a pure cosmetic/UI-code fix with no design-doc content).

### 2026-07-26 — Milestone 14c IMPLEMENTED: startup configuration (auto-remember + pin) + the §7 gap finally closed
- **Built (new 4th file type):** `src/P2000.UI/State/AppPreferencesFile.cs` — `AppPreferences`
  (`StartupCfgPath`, `StartupCfgIsPinned`) as small JSON in the platform-appropriate per-user
  app-data folder (`Environment.SpecialFolder.ApplicationData`/`MMulator`), fail-soft `Load()`
  (missing/corrupt file → a fresh unpinned instance, never throws), `Save()`, and a fixed
  `LastSessionCfgPath` (`last-session.cfg`, an ordinary `.cfg`, no new format).
- **Auto-remember:** `DisplayWindowVm.Dispose()` (the existing clean-quit path, wired from
  `App.axaml.cs`'s `desktop.Exit`) gained `SaveStartupConfigIfNotPinned()` — if not pinned, calls
  `Machine.CaptureCurrentConfig()` (machine ms.20c) and writes it to `last-session.cfg`, then
  points `StartupCfgPath` there. Best-effort (swallows all exceptions) — a failed write on quit
  must never block shutdown.
- **Startup:** `EmulationRunner.MakeConfig()` now tries `TryLoadStartupConfig()` FIRST — reads
  `AppPreferences.StartupCfgPath` and loads that `.cfg` via the existing `MachineConfigFile`
  reader, fail-soft (returns `null` on ANY problem: missing prefs, missing target, parse error,
  rejected version) — before falling through to today's unchanged bundled-BASIC-or-bare logic.
  **Found + fixed along the way (a real duplication risk, not new to this milestone):**
  `EmulationRunner.Reconfigure`'s manual `MachineConfig` field-copy (needed because the class has
  no `with` expression) was the SECOND copy of that same field list (RamSeed's own fix, then
  CassettePath, now this) — extracted into one shared `EnsureRamSeed(config)` static helper used
  by both `Reconfigure` and the new startup path, so there's exactly one list to remember instead
  of two that could silently drift apart.
- **Pinning (`ConfigWindowVm`):** `PinAsStartupConfig` pins `LastCfgPath` — the last file THIS
  window explicitly loaded or saved via `LoadCfgAsync`/`SaveCfgAsync` (NOT whatever's currently in
  the fields, which may be unsaved edits) — and sets `StartupCfgIsPinned = true`; `UnpinStartupConfig`
  clears the pin. `IsStartupPinned` is read from `AppPreferencesFile` at VM construction so the
  Config window reflects the real state on open.
- **The §7 investigation's gap, closed exactly as flagged:** `SaveCfgAsync` now serializes
  `_runner.Machine.CaptureCurrentConfig()` instead of `BuildConfig()` — a saved `.cfg` now
  captures whatever's ACTUALLY mounted (live disk swaps, live cassette swaps), not just this
  window's own bound fields (which `FloppyDriveRowVm.ToConfig()` used to hardcode `ImagePath =
  null` for). **`BuildConfig()` itself is UNCHANGED and still used by `Apply`** — Apply's whole
  point is rebuilding from what's authored in the fields, which must stay independent of whatever
  happens to be live-mounted right now; only Save-to-file switches to the live-capture semantics.
- **Complementary hand-authoring fields added** (`ConfigWindowVm`/`ConfigWindow.axaml`):
  `CassettePath` (mirrors `Slot1CartridgePath`'s browse/clear pattern exactly) and
  `FloppyDriveRowVm.ImagePath` (new browse/clear commands per row, reusing
  `ConfigWindowVm.PickFileAsync` — promoted from `private` to `internal static` so the row VM can
  call it without duplicating the dialog plumbing). `LoadFloppyDrivesFrom`/`LoadFromCurrentConfig`/
  `LoadCfgAsync` all populate these now instead of leaving them permanently blank.
- **Found (a real test-isolation hazard, closed before writing any ms.14c tests) — EVERY
  `EmulationRunner` construction now calls through `AppPreferencesFile.Load()` via `MakeConfig()`,
  including every existing test in this suite that does `new EmulationRunner()`.** Left
  unguarded, running `dotnet test` on a developer's own machine would read (and `Dispose()`'s
  auto-remember would WRITE) their REAL `AppPreferences.json`/`last-session.cfg` — a real, wanted regression to
  avoid. Fixed with a test-only seam: `AppPreferencesFile.DirectoryOverride` (internal, gated by
  a new `src/P2000.UI/AssemblyInfo.cs` `[InternalsVisibleTo("P2000.UI.Tests")]`, mirroring
  `P2000.Machine`'s existing precedent for the same need), set ONCE to a throwaway temp directory
  by a `[ModuleInitializer]` in the test project's new `TestEnvironment.cs` — isolates the ENTIRE
  suite automatically, no per-test boilerplate needed for tests that don't care about preferences.
  Tests that DO need to control preferences content use a small `IDisposable` scope that
  redirects further and restores the shared directory on dispose. **Also added
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`** — `DirectoryOverride` is
  a single shared static, and xUnit parallelizes across test classes by default; without this, a
  test in this milestone's own class overriding the directory could race against an unrelated
  test class's `EmulationRunner()` construction in another thread. Suite runtime impact was
  negligible (~7-8s either way, 170 tests).
- **Tests:** `StartupConfigurationTests` (new, 7): fresh-install boots bare (no prefs file);
  mount-then-quit-then-relaunch round-trips both a disk and a cassette automatically; a
  missing/corrupt `StartupCfgPath` target falls through to bare with no exception (both cases);
  pinning survives a live topology change across a simulated quit/relaunch; unpinning resumes
  auto-remember on the next quit; `Machine.CaptureCurrentConfig()` reflects live-mounted paths
  (the direct regression guard for the §7 gap — `ConfigWindowVm.SaveCfgAsync` itself isn't
  unit-tested here, same StorageProvider-needs-a-real-desktop limitation this suite's other
  Save/Load-dialog code already has, per `MemoryWatchVmTests`' own doc comment). Full
  `P2000.UI.Tests`: 170/170 green (was 163), run twice to confirm no flakiness from the
  threading/parallelization changes. `P2000.Machine.Tests`: 501/501, unaffected.
- **Applies to:** `src/P2000.UI/State/AppPreferencesFile.cs` (new), `src/P2000.UI/AssemblyInfo.cs`
  (new), `src/P2000.UI/Runner/EmulationRunner.cs` (`TryLoadStartupConfig`, `EnsureRamSeed`),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`SaveStartupConfigIfNotPinned`),
  `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`CassettePath`, `LastCfgPath`, `IsStartupPinned`,
  pin/unpin commands, `SaveCfgAsync` rewrite, `PickFileAsync` visibility),
  `src/P2000.UI/ViewModels/ConfigConverters.cs` (`BoolToPinnedTextConverter`),
  `src/P2000.UI/Views/ConfigWindow.axaml` (cassette section, per-drive image fields, startup
  section), `tests/P2000.UI.Tests/TestEnvironment.cs` (new),
  `tests/P2000.UI.Tests/State/StartupConfigurationTests.cs` (new). Reference doc §3a's "RESOLVED
  — startup configuration" block; machine ms.20c.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a "RESOLVED — startup
  configuration" — the auto-remember/startup-load/fourth-file-type/hand-authoring-fields shape
  here matched the design exactly and needed no correction; see the new "IMPLEMENTED, then
  CORRECTED" paragraph there for the one real gap this initial pass had, found via subsequent
  real-usage bug reports below).

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
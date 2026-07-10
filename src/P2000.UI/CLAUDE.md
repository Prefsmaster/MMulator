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
- **Framebuffer handoff.** The machine owns **one persistent** 640×480 `uint[]` BGRA buffer.
  Each field writes only its own scanlines (even→even lines, odd→odd) with **no inter-field
  clear**, so the interlace **comb is baked into that single buffer** — there is **NO front/back
  swap chain** (machine CLAUDE.md §3 / reference §4). At each **field boundary (50 Hz)** the
  machine hands the UI a **read-only view or a fast copy** of the whole buffer; the UI blits that
  into a `WriteableBitmap` and the machine keeps writing the next field into the same buffer.
  Never read mid-render — only take the view/copy at the boundary. **Present per field**; the
  display-mode toggle (§8) chooses which field(s)/cadence to present, it does NOT change the
  machine's timing.
- **Config = topology.** `MachineConfig` (JSON, camelCase properties, enum values in declared
  casing e.g. `"T54"`/`"P2000T"`). Applying a changed topology = build a new machine from the
  config (`new Machine(config)`) — reset-to-apply. `.cfg` load/save is already a machine concern.
- **State capture.** `machine.SaveState` / `LoadState`; `.state` = `"P2ST"` magic + version +
  config-JSON length + config JSON + device stream. Restore = `new Machine(embeddedConfig)`
  then `LoadState`. State is only valid at `AtInstructionBoundary`.
- **Cassette runtime actions.** Mount (`.cas`/`.p2000t`) flips CIP present live; eject flips it
  absent. The host-side `.cas` API (mount/eject/save-as/directory/write-protect) is always-fast
  and independent of `TimingPolicy` (authentic vs turbo). "Save as `.cas`" write-back exists
  (machine milestone 9a).
- **Panning.** `Video.PanX` (0–40) — the special VRAM window's viewport rectangle reads this.
- **Contention overlay hook.** The machine exposes the set of character cells corrupted this
  frame (machine §10). Both the display "show glitches" overlay and the debugger's VRAM window
  consume the same hook.
- **Typed slots.** `machine.Slot1` etc., for the config window to reflect population.

### 3.2 What the Machine layer STILL OWES the UI (contract additions)
Planned as **machine milestones 13–15** (`P2000.Machine` CLAUDE.md §3b/§13). They do not exist
in `P2000.Machine` today and belong there (locked §2.8), NOT in the UI. The UI is specified
assuming them; they gate the debugger milestones (§14). **Do not implement them in the UI layer.**
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
2. **Config window** (modal-ish) — the topology axes (§7). Load/save `.cfg`. Changes queue and
   apply on cold reset; the window makes the reset-to-apply nature explicit (an "Apply (resets
   machine)" affordance), except cassette mount which is live.
3. **Keyboard window** — the original P2000 key layout. Doubles as a **soft keyboard** (click a
   key → enqueue the matrix event, applied at frame boundary like any host key) and as the
   **host-key mapping reference**. Read the layout/labels; the machine models the 10×8 matrix +
   ghosting.
4. **Debugger window** — full debugger (§10). Purely observer-side.
5. **Cassette "deck" window** — status indicators + the ONE physical control (eject). The MDCR
   is computer-controlled: **NO play/stop/rewind** (the CPU moves the tape via CPOUT). Show:
   **direction** (fwd/rev/stopped from CPOUT FWD/REV), **read/write activity** (RDC toggling =
   reading; WCD/WDA driven = writing — same source as the status-bar activity LED), optional
   **tape position + program directory** (host-side `.cas` API). **Eject** unmounts and flips CIP
   absent; insertion is file-dialog/drag-drop. Authentic/turbo speed is a **config setting**
   (mechanism speed), NOT a deck button.

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

- **Blit:** copy the machine's framebuffer view (640×480 BGRA) into a `WriteableBitmap`;
  present in an `Image`; **nearest-neighbour** scaling for crisp pixels. Present on the UI
  thread at display refresh; source is swapped by the machine at 50 Hz.
- **Four display modes** (reference doc §3a / machine §3) — a **UI presentation choice over the
  same rendered scanlines**, never a change to machine timing (interrupt/CTC stay per-field):
  1. **Interlaced (comb) — DEFAULT:** present per field, no inter-field clear → authentic comb.
  2. **Progressive:** both fields composited per frame, no comb.
  3. **Even-only** / **4. Odd-only:** single field (odd = the smoothed sub-scanlines); field-only
     defaults to **line-doubling** to fill 480.
- **Toggles:** integer-scaling (crisp vs smoothed), **PAL aspect-ratio correction** (at 640×480
  pixels are near-square on 4:3 — close to a straight integer scale, not a stretch), optional
  **scanline/CRT shader** (the only "scanline gaps" path — do not add a separate gaps mode),
  **show contention glitches**, and a **corrupted-cell debug overlay** (highlights cells the
  machine flagged this frame — the same hook the VRAM window uses).
- **Screenshot:** serialize the current framebuffer view.

---

## 9. Audio

- **OpenAL** via `Silk.NET.OpenAL` or `OpenTK.Audio.OpenAL` (reference doc §2 — Avalonia has no
  audio; avoid NAudio = Windows-only; watch BASS licensing if ManagedBass is considered).
- The machine produces 1-bit beeper square-wave sample **blocks** into a ring across the thread
  boundary; the UI pushes them to the OpenAL source. Mute + volume are UI-side (§7).
- Keep the audio consumer decoupled from frame presentation (its own block cadence).

---

## 10. Debugger (full, first implementation)

Reads a machine **state snapshot** each break (§3.2); never races the core. All stepping/
breakpoint edits are **commands** (§3.2). Disassembly uses **`Z80.Disassembler`** over the
shared **`Z80Tables`** (root rule) so the debugger decodes exactly what the core executes.
- **Full register file:** AF/BC/DE/HL + primes, IX/IY, SP, PC, I, R, **WZ/MEMPTR**, IFF1/2, IM,
  flags broken out (incl. YF/XF).
- **Memory watch windows (MULTIPLE, independent):** each an observer over the snapshot with its
  own range; freely spawnable. Live hex + ASCII, refreshed per frame/step; **highlight bytes
  changed since last refresh** (colour flash). Optional **follow a register pair** (HL/SP).
  Read-only.
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

Gates 6–7 depend on the §3.2 machine-contract additions landing first.

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
- **Synced:** yes (2026-07-10 — save-at-instruction-boundary already in reference §3a; ⚠ .state version-bump NOT recorded — flagged in reference §3a, confirm before sharing .state)

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
- **Synced:** no

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
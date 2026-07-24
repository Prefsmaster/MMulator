# Philips P2000T Emulator — Project Reference

A cycle-exact, multi-platform (.NET) emulator for the Philips P2000T 8-bit
microcomputer. This document collects the architecture decisions, hardware facts,
and source references gathered during research, so the project can be picked up
later without re-deriving any of it.

---

## 1. Project goals

- **Target:** Philips P2000T (Z80-based home computer, ~1980–1981, Dutch market).
- **Platforms:** Windows, macOS, Linux — all first-class.
- **Runtime:** .NET (Core).
- **Fidelity goal:** cycle-exact. Model all chips, devices, and interrupts as
  accurately as possible using an explicit address/data-bus approach, including
  the real machine's *black display glitches* when the CPU accesses video memory
  at the same time as the video circuit.

---

## 2. UI / display technology decision

### Recommended stack
- **Avalonia UI** for the application shell and display surface.
  - The de-facto standard for genuinely cross-platform .NET desktop apps with
    first-class Linux support.
  - Render the frame in software: blit SAA5050 glyphs into a `WriteableBitmap`,
    display in an `Image` control, scale nearest-neighbour for crisp pixels.
  - At this resolution (~480px-class, 40×24 cells, 50 Hz PAL) GPU acceleration
    buys nothing.
  - Big payoff: trivial menus, file dialogs (`.cas` loading), settings, and —
    crucially for this project — memory viewer / disassembler / debugger windows.
- **OpenAL** for audio (via `Silk.NET.OpenAL` or `OpenTK.Audio.OpenAL`).
  - Avalonia has no audio; cross-platform .NET audio is the real pain point.
  - P2000T sound is 1-bit / single-channel: generate a square wave, push samples.
  - Avoid NAudio (Windows-only). ManagedBass works but watch BASS licensing.

### Rejected / alternative options
- **.NET MAUI — rejected.** Linux support is community-only, not production-grade.
  Do not build on it if Linux is a hard requirement.
- **Game/multimedia framework** (Silk.NET SDL/OpenGL, MonoGame, Raylib-cs) —
  viable. Gives window + texture + audio + input + fixed loop in one package.
  Good for a lean single-purpose emulator; costs you hand-rolled dialogs, menus,
  and debug tooling, plus native-library distribution. (Note: the reference C
  emulator M2000 took exactly this route with Allegro 5.)
- **Hybrid** (Avalonia shell + emulator rendered into a control) is common, but
  pure-Avalonia already gives that for the P2000T without the extra dependency.

---

## 3. Threading & determinism (critical architecture note)

**The emulation core must be single-threaded and deterministic.** Do NOT put the
video circuit on its own OS thread and have it "check the bus" against the CPU
thread — that introduces a scheduler-dependent race, so the exact collision cycle
(the very event you're reproducing) becomes non-deterministic and untestable.

Correct model:
- Bus contention is computed **inside one deterministic emulation loop**.
- The address bus, data bus, and Z80 control lines (MREQ, RD, WR, IORQ, M1, RFSH)
  are just **fields in the core's state**. Each tick, contention logic reads them.
  There is no cross-thread "checking" — one thread owns the bus state.
- Threading still belongs in the design, but as **presentation decoupling**:
  - Emulation thread runs the deterministic core, produces completed framebuffers
    and audio sample blocks into a triple-buffer / ring.
  - Avalonia UI thread consumes finished frames, presents at display refresh.
  - Input events queue from the UI thread, applied by the core at a defined point
    (frame boundary, or sampled at the authentic cycle for real input latency).
  - The glitch is already baked into the framebuffer the core hands over.

---

## 3a. UI architecture (Avalonia)

### Guiding principles
- **Bare by default.** On first launch the machine starts with NO slot1/slot2 cartridge,
  empty cassette drive, no expansion RAM, no disk interface. This is deliberate: a bare
  machine exercises the ROM's presence-probe fallback paths (CTC→video tick, disk-absent),
  which is the honest default and the best baseline for correctness testing.
- **Display is the main window;** everything else is a satellite window.
- **Machine on its own thread; every window is an OBSERVER** (extends §3). Windows read
  snapshots of machine state; they never mutate the live core directly. The debugger reads
  a state snapshot; config changes are queued and applied at a safe point (see reset rule).
- **MVVM from the start** (CommunityToolkit.Mvvm). Machine state → ViewModel → binding.
  The debugger and config windows are stateful UI where bindings pay off.
- Frames marshalled to the display via `Dispatcher.UIThread.Post`.

### Windows
1. **Main / display window** — the SAA5050 output as an `Image` (WriteableBitmap, §2).
   Hosts a standard **menu bar** + a slim **toolbar** + a **status bar** (below). Accepts
   **drag-and-drop** of `.cas` / cartridge / disk images onto the display (Avalonia
   `DragDrop`), complementing the file dialogs.
2. **Config window** (modal-ish) — see config axes below.
3. **Keyboard window** — shows the original P2000 key layout; also serves as a soft
   keyboard (click keys) and a reference for the host-key mapping.
4. **Debugger window** — full debugger (spec below).
5. **Cassette "deck" window** — the MDCR is **fully computer-controlled**: the CPU drives the
   tape via CPOUT (FWD/REV/WCD/WDA); there are **NO user transport controls** (no play/stop/
   rewind buttons on the real hardware — the software moves the tape, e.g. the auto-boot
   rewind). The deck window is therefore **status indicators + the one physical control
   (eject)**:
   - **Direction indicator** — forward/reverse/stopped, reflecting CPOUT FWD/REV bits.
   - **Read/write activity indicators** — "reading" when RDC (CPRIN) is toggling, "writing"
     when WCD/WDA (CPOUT) are driven. (Same source as the status-bar activity LED.)
   - Optional: tape position + program directory (from the MDCR directory).
   - **Eject** (the only physical button) → unmount the `.cas`, flip CIP to "no cassette".
     Insertion is done via file dialog / drag-and-drop (mount `.cas` → CIP "present", a
     runtime action, §5b).
   - The **authentic/turbo speed** setting is NOT a transport control — it's a config/settings
     option about mechanism speed, not a deck button.

### Control surface: menu + toolbar + status bar (NOT custom title-bar buttons)
Mature emulators (MAME, Fuse, VICE, ares) converge on menu + status bar, shortcuts layered
on, buttons for the hottest actions. **Do NOT hijack the window chrome with custom
title-bar buttons** — it fights the OS across Win/mac/Linux (macOS especially). Instead a
docked toolbar under a standard menu bar.
- **Toolbar:** Run/Pause, Reset (warm), Reset (cold), Screenshot, Speed/turbo.
- **Status bar:** emulation state (running/paused), **actual vs target speed (%)**,
  cassette/disk **activity LED** (critical — it's how the user sees an authentic-mode
  `.cas` load progressing), and current **model (T / M)**.

### Keyboard shortcuts (borrow familiar conventions)
- **F5** run / pause
- **F11** reset (warm) · **Shift+F11** reset (cold, clears RAM — mirrors the real
  NMI-soft-reset vs full-reset distinction)
- **F12** (or PrtScn) screenshot
- **F6** toggle turbo / max speed
- **F8** single-step (when paused; ties to debugger)
- Avoid F1 (reserve for Help) and F10 (menu key on Windows).

### Config axes (DECIDED: topology changes require a machine reset to apply)
Config changes that alter hardware topology **require a machine reset to take effect**
(simplest, most authentic). Apply by queueing the new config and performing a cold reset.
- **Model selector (top-level axis): P2000T vs P2000M.** Gates everything else — M implies
  its disk/CTC; T offers the slot cards. Put this above RAM/slots.
- **Monitor ROM (base machine, default + override).** The 4 KB monitor at 0x0000 is present on
  every machine from power-on — NOT a cartridge/slot. The emulator loads a **built-in default**
  automatically; config exposes an optional **custom boot-ROM override** (`MonitorRomPath`,
  null → default) for patched/alternate monitor revisions. The default ROM is **embedded as a
  compiled-in resource** so a bare machine boots **out of the box with zero setup** (like a
  real P2000T powering on) — no assets folder, no file dialog, no missing-file failure on the
  default path. The override reads from disk only when deliberately set.
- **RAM configuration = variant selector** (T/38 16 KB · T/54 32 KB · T/102 80 KB ·
  PTC-96K). This expands into which regions are populated and whether port-0x94 banking is
  active at 0xE000–0xFFFF (see §5 memory map). Reset-to-apply. NB: driven by the
  **internal-slot board choice** below — T/38 = bare motherboard (fixed); larger configs come
  from a RAM-only or floppy+RAM extension board.
  - **Board/RAM coupling model — DECIDED (owner, 2026-07-23), NOT YET IMPLEMENTED (Machine.cs
    currently over-constrains this — see machine CLAUDE.md §17 flag of the same date):** the
    two boards below are NOT symmetric with respect to RAM capacity. The **official Philips
    floppy+RAM board is an ATOMIC package** — real hardware, one physical card, FDC + CTC +
    a fixed amount of bank-switched RAM all soldered together. Selecting it in config is a
    single choice: FDC/CTC/RAM all appear together, at the one confirmed real capacity
    (T/102, 80 KB) — **there is no separate memory dial for this board**, because there was
    never a smaller or larger "official" version to choose between. A **RAM-only board**, by
    contrast, models a **homebrew/3rd-party memory-expansion card** — no single fixed real
    product, since these varied (reference doc's own existing note: "homebrew RAM cards
    decode more bits for more banks"). **This is the axis that should be user-configurable**
    (a bank-count control), not the official floppy+RAM board. T/54 is a reasonable default
    for that axis, not a second official product name to hardcode.
- **Internal-slot board (three-way): none / RAM-only / floppy+RAM** (§5). Determines upper
  memory AND whether the FDC/CTC + disk exist. "More RAM" (RAM-only board) is separable from
  "disk present" (floppy+RAM board), but — per the coupling model above — only the RAM-only
  path is meant to be a user-facing capacity dial; floppy+RAM is a single atomic selection.
  **T-scoped model — flag for whenever M support is
  undertaken (currently deferred, §14):** the M's internal slot genuinely daisy-chains (video
  board, then a further extension board behind it — §5c "Daisy-chaining on the M", confirmed
  from the Field Service Manual's §3.8.1/§3.8.11 connector pinouts) rather than being a single
  mutually-exclusive board choice — this three-way T model will need extending, not just
  reusing, when M support starts.
- **Slot population** (three typed slots, §5c): **SLOT1** (external, memory-mapped ROMs:
  BASIC, DB manager, other ROM carts); **SLOT2** (external, I/O-mapped expansion hardware);
  **internal extension** (floppy/CTC card — populated in M, optional on T).
- **Disk interface present?** (internal-slot floppy/CTC card) + mounted disk image(s).
- **Cassette:** `.cas` file selectable via file dialog (also drag-and-drop).
- **Display mode (4-way, over the same rendered scanlines) — DEFAULT CORRECTED (2026-07-21,
  owner decision, per the P2000TM Field Service manual's "no interlacing is used" finding —
  see §4/§4a): default is now odd-only** (line-doubled single field — matches the FSM: real
  T hardware has no even/odd field pairing, every field is an independent 313-line refresh, and
  CRS/RA0 selects the smoothed sub-scanline within that one field's own data). **interlaced
  (comb)** — per-field persistent non-erased buffer → reproduces comb in fast motion; no longer
  presented as "authentic," since real hardware doesn't interlace — kept as a legitimate opt-in
  extra/nostalgia mode, not the default; **progressive** — both fields composited per frame,
  smooth; **even-only** / **odd-only** — present a single field (discard the other), no comb,
  half vertical detail (now understood to be the AUTHENTIC vertical resolution the SAA5050
  actually renders, not a reduced-fidelity fallback). Odd-only is slightly
  smoother (the CRS/RA0 rounding lands on odd sub-scanlines) and is the **new default**;
  field-only defaults to line-doubling.
  (Consumer contract: when `FieldComplete` fires, `Video.IsOddField` has ALREADY toggled to the
  next field, so the field just completed is **`!IsOddField`** — gate even-only/odd-only/progressive
  presentation on that. Confirmed P2000.UI ms6.)
  Plus: integer-scaling toggle (crisp nearest-neighbour vs smoothed), PAL aspect-ratio correction,
  optional scanline/CRT shader, **"show contention glitches"
  toggle**, and a debug overlay highlighting which character cells were corrupted this
  frame (turns the headline feature into something visible/testable).
- **Full-Field vs Graphics-window — NEW (2026-07-22, owner request), a SECOND, ORTHOGONAL
  toggle over the 4-way display mode above** (both axes compose freely — Full-Field/
  Graphics-window controls the CROP, the 4-way mode controls the FIELD SOURCE):
  - **Graphics-window (DEFAULT):** the familiar 640×480 active-picture crop — what's shown
    today, no visible change for existing users.
  - **Full-Field:** shows the machine's complete raw raster — the full 928×626 buffer (§4a
    "Full raster geometry"), including the black leading/trailing horizontal margins (retrace
    itself excluded — see below) and the black pre-roll/post-roll vertical margins. On a real
    P2000 + PAL TV, only part of the transmitted signal is the visible graphics window too —
    the rest is blanking, normally invisible because a real CRT's overscan hides it. This mode
    is for authenticity/curiosity/debugging (e.g. visualizing exactly where the active window
    sits relative to the full field), not the everyday view.
  - **Ownership, same discipline as the 4-way mode:** the **machine** always renders the full
    928×626 buffer (machine CLAUDE.md §3) — it does not know or care which crop the UI shows;
    the **UI** decides whether to blit the whole buffer or just the fixed (144, 98)–(784, 578)
    sub-rectangle. No machine-layer "mode" exists for this either.
  - **PAL aspect-ratio correction does NOT apply to Full-Field — CORRECTED (2026-07-22, owner
    catch, walking back §4a's earlier claim):** PAL aspect correction reproduces how the
    ACTIVE PICTURE fills a real 4:3 CRT tube — a standardized broadcast target. The blanking
    margins have no such standard: real CRTs never display retrace at all (beam physically
    off-screen) and typically hide most of the porch behind the bezel/overscan, with exactly
    how much depending on each individual set's brightness/geometry adjustment — there's no
    "correct" real-world size for the margins to reproduce. So in Full-Field mode, PAL aspect
    correction should be a no-op (disabled/greyed out) — show the buffer at its native pixel
    geometry (integer-scaled like everything else, just without the extra aspect stretch).
    Applying the same correction factor across the whole buffer would be geometrically
    self-consistent (every pixel is still an equal time-slice) but wouldn't correspond to
    anything a real viewer ever actually saw, since no real screen shows the margins at any
    standardized size.
- **Audio:** mute + volume (minor for a 1-bit beeper, but present).

### Debugger (DECIDED: full debugger in the first implementation)
- **Full register file:** AF/BC/DE/HL + primes, IX/IY, SP, PC, I, R, and **WZ/MEMPTR**
  (implemented in the core — expose it), plus IFF1/2, IM, and the flag bits broken out
  (incl. YF/XF).
- **Memory watch windows (MULTIPLE, independent):** each is an observer over the state snapshot
  with its own address range — freely spawnable (stack, sysvars, a data structure, etc.). **Range
  is explicitly configurable** (a Length field alongside the base address; not fixed at spawn —
  CORRECTED 2026-07-14, UI build). Live hex + ASCII, refreshed per frame / per step. **Highlight
  bytes changed since last refresh** (colour flash) — turns a static dump into a view of what the
  program is touching. Optional **"follow" a register pair** (follow HL / SP) so a window tracks
  what the code is working on. **Read-only for interactive cell editing — this is not a hex
  editor — but "never touches the live core" is now CORRECTED (2026-07-14, UI build):** a window
  can **export its configured range to a file** (reads the snapshot, no mutation) and **import a
  file to an address** (a bulk RAM write, queued through the same command-queue/boundary
  mechanism as any other machine mutation — not live-poking, not exempt from the "every mutation
  is a queued command" rule, but a real write nonetheless). The export/import range defaults to
  the window's own Base/Length but is independently editable at save time, so a one-off export
  doesn't require changing what the window is currently watching. Motivating case: pulling a
  machine-code routine out of RAM (e.g. one loaded from a `.cas`/disk by a BASIC wrapper) for
  offline disassembly, and pushing an assembled routine back in.
- **Special VRAM window (the P2000T panning made visible):** shows the **80×24** screen buffer
  (0x5000–0x577F) laid out **spatially as 80×24** (not linear) — matching the hardware addressing
  `0x5000 + col + 80*row`. Each cell shows the char byte, toggleable between **rendered glyph**
  and hex. Overlay a **rectangle marking the visible 40-column viewport**, positioned by the
  **scroll/pan register**, sliding in real time as the program pans — makes the flicker-reduction
  panning trick (§5) watchable. **Reuse this grid for the contention debug overlay** (§10 —
  corrupted-cells highlight): one window then shows what's in screen memory, what's visible, and
  what contention glitched, together. Read the geometry from the machine **model** (T = 80×24; the
  M differs, §5) rather than hardcoding, so it adapts to the M later.
- **Disassembly** view (detailed below).
- **Live disassembly around PC** — the spine of the debugger:
  - **PC-relative window that follows execution**, PC line highlighted and kept a few lines
    down from the top (shows just-executed + upcoming). Auto-scroll on step; allow scroll-away
    + a "back to PC" action.
  - **Forward decode from PC is exact; backward is a heuristic** — Z80 instructions are 1–4
    bytes (more with DD/FD/CB), so there's no fixed alignment and you can't walk back
    unambiguously. Backward context: anchor 8–16 bytes before PC, decode forward, and if
    boundaries land on PC accept it, else back the anchor up and retry ("sync to PC"). The
    line AT PC and everything after must be exact; a mis-decoded leading line after a data
    block is tolerable.
  - **Better anchors for ROM code:** the project's own monitor-ROM disassembly gives
    ground-truth instruction boundaries + named entry points — use them as reliable anchors
    for ROM; fall back to the heuristic for RAM/cartridge.
  - **Show raw bytes + mnemonic:** `1234: 21 00 60   LD HL,6000h` — the encoding matters for a
    hardware debugger (shows prefix/undocumented form; makes mis-decodes visible).
  - **Symbol resolution — labels for known ROM addresses (DESIGN DECISION).** Annotate the
    disassembly from a loaded symbol file (e.g. `CALL 0x0872` → `cas_block_read`, `OUT (0x88),A` →
    `CTC_CH0`). This is a future **P2000.UI debugger** enhancement (own milestone), orthogonal to
    the machine layer. Design:
    - **Resolve by operand CONTEXT, not raw address.** A symbol file is a flat name→value map that
      really mixes four kinds — **code addresses, RAM/data addresses, I/O ports, bit/constant
      values** (e.g. `MonitorRom.sym` carries `BEEP $01ea`, `CTC_CH0 $0088`, `CTC_keyboard $6026`,
      `BIT_MOTON $0002`, all as `equ $x`), and the 0x00–0xFF band is packed with ports/constants
      that alias low ROM addresses and each other (`0x10` = `CPOUT` + `CIP`). A naive address→label
      map mislabels. Instead the disassembler tags each operand's KIND and the debugger looks the
      value up in the **matching bucket only** — so `0x88` is `CTC_CH0` as a *port* operand and a
      code label only if code targets `0x0088`. Constants annotate immediates as a trailing comment
      (`LD A,0x02 ; BIT_MOTON`), never as address labels.
    - **Layering:** `Z80.Disassembler` stays pure (address → mnemonic + **typed operands**); the
      symbol table + resolution live in the UI/debugger as a render-time overlay. **Prerequisite to
      confirm before wiring:** the disassembler must expose each operand's value + kind, not just a
      formatted string. If it returns strings, Phase-1 code labels still work but operand-context
      needs operand typing surfaced first.
    - **Pluggable file format (`ISymbolFileParser`).** Symbol-file syntax is assembler-specific: the
      supplied file is **z80asm** (`label:⇥equ $hex`); others differ — **sjasmplus** (`label: EQU
      addr`), **z88dk `.map`** (`NAME = $addr ; type,scope,file` — carries a type), **WLA-DX / no$**
      (`bank:addr label` — carries a bank), **rasm**, **VICE `.lbl`**. Define a parser seam that
      normalizes any of them to `(name, value, [bank], [type])`; **ship the z80asm parser now, defer
      the rest until a user actually has that toolchain's output** (chip-now / framework-later, like
      the CTC). Detect format by extension + a sniff of the first lines.
    - **Classification into buckets** (code / data / port / const): use the format's own type/scope
      hint when present (z88dk); else heuristics — **address range** (ROM code, VRAM, RAM-var region,
      the 0x00–0xFF port/const band) + **name-prefix conventions** (`BIT_*`/`CST_*` = const,
      `*_CH[0-3]`/`CPOUT` = port). Heuristics are imperfect, so the table is a **multimap** (N names
      → 1 address is common) and buckets are **user-overridable** (it's the owner's own disassembly).
      Constants/ports are excluded from the code-label set outright so they never pollute line labels.
    - **Per-ROM / per-bank scoping:** a symbol set is tied to a ROM image (the monitor `.sym` labels
      0x0000–0x0FFF + its RAM vars; a cartridge / CP-M image brings its own). Bank-carrying formats
      resolve against the current banking state (SLOT1 cartridge, the RAMSW BANK1 window, CP/M @
      0xE000).
    - **Two phases:** Phase 1 = **code labels only** (line labels + jump/call/branch targets) — 80%
      of the value, near-zero risk, readable disassembly. Phase 2 = port/data/const operand
      annotation + **break-at-symbol / go-to-symbol / symbol in the PC & call-stack display**.
  - **Breakpoint gutter in the same view:** click a line to toggle a breakpoint, marker on
    breakpointed lines, distinct PC-line marker. The disassembly view IS the breakpoint UI.
  - **Decode with the SAME opcode/prefix tables as the core** (generate both from one source —
    fits the CLAUDE.md "commit generator + output" note). A disassembler whose tables differ
    from the emulator is a classic source of debugger lies (undocumented DD/FD/CB forms,
    IX/IY halves). Shows precisely what the core will execute.
  - Lives entirely on the **observer side** (§3): reads a memory+PC snapshot, decodes
    functionally, never touches the live core.
- **Breakpoints:** execute AND **memory read/write/execute** watchpoints, plus **I/O port
  breakpoints** (break on access to a port — how you'll debug the CTC probe and FDC).
- **Live in-frame T-state / cycle counter** (position within the 50,000-cycle frame —
  invaluable for contention debugging).
- **Stepping:** single-step, **step-over, step-out**, and — because cycle-exact — **run to
  scanline / run to cycle N**.
- Reads a state snapshot each break; never races the live core.
- NOT building yet: a full in-emulator assembler/editor (scope creep; external
  cross-assembler + load pipeline already exists).

### Observer + control contract — as built (machine milestones 13–15)
The debugger above reads and drives the machine through a contract that **lives in `P2000.Machine`**
(so the future external IDE hook attaches to the same surface, not a UI-private one). Built across
milestones 13–15; the primitive drive surface is `RunField()` / `StepInstruction()` / `Post(cmd)` /
`Snapshot()`, with no wall-clock inside any of them (the pacing loop is the UI's, §3a threading).
- **State snapshot (ms.13):** `TakeSnapshot()` returns a read-only `MachineSnapshot` — full register
  file (incl. WZ/MEMPTR, IFF1/2, IM, flags with YF/XF), a live side-effect-free `ReadMemory(addr)`
  (a delegate over the page table, no array copy), and the **in-frame T-state position**
  (`FieldTState`, forwarded from the video fetch unit's master counter). Never mutates the core.
- **Breakpoint store (ms.14):** machine-owned `BreakpointStore` — execute + memory R/W/X + I/O-port
  breakpoints, edited by clients (never held UI-side). A `Count == 0` fast path means an unbroken
  machine pays nothing. **Semantics that matter:** an **execute** breakpoint fires *before* the
  instruction runs (PC = the about-to-execute address, correct for a debugger display); **memory/IO**
  breakpoints fire mid-instruction and defer the actual break to the **start of the next instruction
  boundary**; **int-ack (M1+IORQ) is excluded** from I/O-port breakpoints (it is not a user I/O
  access). The **`BreakHit` event fires on EVERY pause transition** — real breakpoints AND
  single-step / pause / run-to-scanline / run-to-cycle completions, the latter via a **synthetic
  `BreakpointKind.Step` (id −1)** — so a debugger can refresh off one event source (P2000.UI ms10;
  silent pauses that skipped the event were a bug there).
- **Command queue (ms.15):** all mutation is a queued `MachineCommand` drained at
  `AtInstructionBoundary` — run/pause, warm/cold reset, single-step, step-over, step-out,
  run-to-scanline, run-to-cycle, set-PC, memory write, load-image-to-address, breakpoint CRUD.
  **Ordering rule:** the boundary checks (pause-at-next, run-to-cycle, breakpoints) run **before**
  the drain, and the drain sets state consumed on the **next** boundary — so a single-step queued
  this boundary executes exactly one instruction and pauses at the following one. A mid-run memory
  write / load-to-RAM is flagged **non-replayable** (breaks cycle-exact replay for that session,
  same category as turbo cassette) — allowed, not forbidden.
- **Run-loop host:** the wall-clock pacing / run-pause-turbo thread is **UI-owned** for this build
  (`P2000.UI/Runner/`), driving the surface above; it is promoted into a machine-layer `MachineRunner`
  on the identical surface when external-IDE integration becomes current (a move, not a redesign).

### Save-state / snapshot (DECIDED: bake SaveState/LoadState into every device NOW)
- Every device interface (CPU, RAM, video, cassette, FDC, CTC, slot cards) exposes
  `SaveState` / `LoadState` from day one. Cheap now, miserable to retrofit across devices.
- For a deterministic cycle-exact core, a snapshot = serialize CPU + RAM + every device's
  state. Nearly free, and the single most-loved emulator feature.
- Debugging superpower: reproduce a contention glitch from a saved state deterministically.
- Design the device base interface so `SaveState`/`LoadState` sit alongside `Reset` and
  (where relevant) the `TimingPolicy` hook.

### Configuration vs. State — TWO separate serializable concerns (DECIDED)
These are different things with different lifetimes, sizes, and consumers. Keep them
separate; conflating them is a design smell.

- **Configuration = machine TOPOLOGY, before/independent of running.** Model (T/M),
  internal-slot board (none / RAM-only / floppy+RAM), RAM socket population, SLOT1/SLOT2
  population, mounted `.cas`/disk image paths, display/audio prefs. Small, human-readable
  (JSON), hand-editable, shareable. **Reset-to-apply** (changing it rebuilds the machine).
  Answers "what machine is this?" One serializable **`MachineConfig`** object owned by the
  **machine-assembly layer** (it's the recipe that decides which devices exist) — NOT
  per-device.
- **State capture = dynamic CONTENTS at an instant while running.** CPU registers, all RAM,
  each device's runtime (CPOUT latch byte, CTC counters, FDC phase, cassette position, bank
  index), cycle position in frame. Large, binary, opaque, frequently written. Answers "what
  is it doing right now?" Produced by the distributed **`SaveState`/`LoadState`** walk across
  devices.

**Relationship (the key design point): state depends on config.** A snapshot is only valid
against a compatible topology (an 80K T/102-with-disk state can't restore onto a bare 16K T).
Therefore:
- **Embed the full `MachineConfig` inside every state capture** (as a header). A save-state
  is then self-contained: restoring = (1) rebuild the machine from the embedded config
  [a reset-to-apply], then (2) deserialize device state into the freshly-built devices. This
  reuses the reset-to-apply rule cleanly.
- **Version BOTH formats from day one** (a version field each) — both evolve as devices are
  added (M, CTC, hires); allows reject/migrate instead of crashing on struct mismatch.
- **Derivation is one-way:** you can extract a `MachineConfig` FROM a state capture ("save the
  machine definition this snapshot uses"), never the reverse (a config has no running contents
  to invent).

**Two file types for the user:**
- **`.cfg` / JSON — "machine definitions":** named topologies ("bare T/38", "T/102 + disk",
  "M with two floppies"). Tiny, editable, shareable. The config window loads/saves these.
- **`.state` — "snapshots":** frozen running machine = config header + device blob. The
  save-state feature loads/saves these; restoring rebuilds from the embedded config then
  restores contents.

**Layering:** config serialization lives at the machine-assembly level (one `MachineConfig`
→ whole topology); state serialization is distributed across devices (each serializes its own
runtime) with the config embedded as header. Two layers, two formats, one dependency
direction (state → config).

**IMPLEMENTED format (milestone-11):** `.state` = `"P2ST"` magic (4 B) + version int32 LE +
config-JSON length int32 LE + config JSON (UTF-8) + distributed device-state stream. Restore =
`new Machine(config)` (full reset from the embedded config) then `LoadState`. Config JSON uses
camelCase property names but **enum values keep their declared casing** (`"T54"`, `"P2000T"`) —
construct `JsonStringEnumConverter` WITHOUT a naming policy (camelCase would give unreadable
`"t54"`). **ROM bytes are NOT saved** in `.state` (read-only, embedded, config-determined —
restored from config). State is saved only at Z80 **instruction boundaries** (`AtInstructionBoundary`),
where the CPU's private phase/tstate/prefix are at reset-compatible defaults, so the CPU struct
serializes self-consistently without private fields.

**Version-bump RESOLVED — `.state` is now v2 (2026-07-10).** Two device-stream changes had
accumulated — the interrupt aggregator's second serialized boolean (`_nmiPending`, §5e,
milestone 12) and the **`SoundDevice` block** (inserted between the cassette and interrupt blocks,
§5 Sound, milestone 16). Both were flagged "bump deferred" but the version int32 was left at 1, so
v1 files loaded with a **misaligned device stream** (`Sound.LoadState` consumed the old single-bool
Interrupts payload → later underrun, with no exception until then). Fixed:
`MachineStateFile.CurrentVersion = 2`, `MinVersion = 2`; the reader now **rejects v1 files** with
`InvalidDataException` ("Unsupported .state version 1…") rather than silently mis-loading. No
migration path — no external `.state` files were distributed; discard any saves made during
milestone 11–16 testing.

**`.state` now v3 (2026-07-11, milestone 17):** the CTC channels + Lock state added another device
block, bumped at build time (`CurrentVersion`/`MinVersion = 3`); v2 files are now rejected. This is
the discipline working as designed — every device-block change bumps at build time, never
retroactively.

**OPEN DESIGN QUESTION (owner, 2026-07-23), deliberately not decided yet — whether/how UI-layer
session state gets persisted at all, and everything downstream of that.** The owner is considering
whether a saved session should also capture UI state — which windows are open, a memory-watch
window's configured range, etc. — alongside the machine's own `.state`. Nothing here is decided:
not whether this happens, not whether it lives inside `.state` itself (a new "UI blob" section,
machine-agnostic and owned by `P2000.UI`), in a separate sidecar file next to `.state`, or
somewhere else entirely. **This directly blocks two small, otherwise-ready decisions from
being made independently:** per-drive disk write-protect persistence (machine milestone 20) and
the cassette/disk `IsDirty` dirty-flag persistence (machine milestone 20a) were each flagged as
"pick session-only vs. some persistence mechanism" — but picking a mechanism for either one right
now would mean guessing at an answer to this bigger, still-open question. The owner's reasoning:
**cassette write-protect is already part of `.state`** (`MdcrDevice`'s `Protected` field,
`docs/MDCR-implementation.md` §7) — a `.state` load is supposed to bring the machine back exactly
as it was, so treating disk write-protect and `IsDirty` any differently would be an inconsistency,
not a simplification. Whatever container ends up holding "everything needed to resume exactly
where the user left off" is where these two belong — genuinely undecided until that container is
decided, not because no one has thought about it. **Do not pick a mechanism for either item before
this is resolved** — revisit both the moment the UI-state question is settled.

**A second, related sub-question under the same umbrella (owner, 2026-07-23): should mounted
media CONTENT itself travel inside `.state`, making a save fully self-contained and shareable
("send a state to a friend" without also sending the `.cas`/`.dsk` files separately)?** This is
distinct from the UI-state question above but sits in the same "what does a saved session actually
contain" design space, and it **reopens an already-made decision, not a fresh one:**
- **Cassette — currently does NOT embed content.** The milestone-9 finding (`P2000.Machine`
  CLAUDE.md §17, 2026-07-05) decided `.state` saves tape `Position`+`Side` only, matching the
  ROM-not-saved precedent (§3a above) — the mounted `.cas` must be remounted after `LoadState`.
  `docs/MDCR-implementation.md` §7 still carried the ORIGINAL open framing of this question
  (never updated after the decision was made) — corrected there this pass, and reopened per this
  note.
- **Disk — undecided either way.** Machine milestone 20's per-drive `.state` shape
  ("mounted-image-ref") hasn't picked between "a path, remount required" and "the actual bytes" —
  genuinely open, not defaulting silently to either.
- **If this gets built, the natural shape differs by device, and the pieces already mostly
  exist or are already planned:** disk images are already compact raw sector dumps (140k–560k
  per drive, up to ~2.24 MB for 4 double-sided drives) — cheap to embed directly. The cassette's
  in-memory representation is a ~1 MB/side raw phase-bit array, NOT cheap to embed directly, but
  `docs/MDCR-implementation.md` §8 already calls for a bitstream→`.cas` serializer (currently
  MISSING — needed anyway for the UI's "Save as .cas" feature) whose compact output (tens of KB,
  not ~1 MB) is what should get embedded instead of the raw array — one serializer, two
  consumers, not a redundant second one built just for `.state`.
- **Compression (owner's parenthetical "(the compressed?) state") is an open, low-risk detail,
  not a blocking one** — raw disk sector dumps and `.cas`-format bytes both compress well
  (large runs of unformatted/blank space, repetitive framing), so gzip/deflate over the embedded
  blob(s) is a reasonable default if/when this is built, but the algorithm choice doesn't gate
  the bigger embed-or-not decision above.
- **A nice side effect if this is adopted:** it would resolve the write-protect/`IsDirty`
  persistence deferrals two paragraphs up almost for free — once a drive's/cassette's actual
  content lives inside the same state blob, persisting a couple of extra booleans alongside it
  is a trivial addition, not a separate mechanism to design. Worth deciding the embed-or-not
  question with that in mind.
- **Not yet decided — flagging, not picking.** Revisit whenever `.state`'s device-block shape for
  cassette/disk is next touched (a version bump either way, per the "every device-block change
  bumps at build time" discipline already in place).

### File extensions (DECIDED)
- **Monitor ROM / cartridges: standard `.bin` / `.rom` — NO custom extensions.** These are raw
  binary dumps identical to what MAME/preservation sites distribute; a custom `.p2kr`/`.p2kc`
  would force users to rename dumps for no gain. **Distinguish by config ROLE, not extension**
  (the emulator knows "monitor ROM path" vs "SLOT1 cartridge path"). So existing dumps drop in
  unchanged.
- **Cassette: `.cas`** (established P2000T format; §5b/§6). Note: the newer M2000 ecosystem also
  uses **`.p2000t`** for clean tapes with proper 32-byte block headers — support reading it too
  if convenient, but `.cas` is primary.
- **Fonts: text format** (`.`/`*`, see SAA5050 doc §8).
- **Disk image: raw SECTOR image, extension `.dsk`** (or `.img`). NOT flux; an image. No
  de-facto P2000 disk-image convention exists in public sources (unlike `.cas`), so raw
  sector is the pragmatic default — **CONFIRMED, not just assumed** (real images inspected
  byte-for-byte are plain raw sector dumps, no container header). **Geometry is per-disk, not
  a fixed constant — supersedes the earlier "35-track single-sided" placeholder** (§5d also
  updated). JWSDOS 5.0 itself supports 35/40/80-track, SS/DS as a per-disk format-time
  choice, with a **self-describing on-disk geometry label** the emulator's `.dsk` loader can
  read directly at fixed offsets — keeping this file convention header-free (full offsets +
  fields: `docs/JWSDOS-format.md` §3). **Note:** real JWSDOS itself does NOT read this label
  back to auto-configure its own runtime geometry state (`docs/JWSDOS-format.md` §1) — an
  emulator auto-detecting geometry from the label anyway is a deliberate UX improvement
  beyond replicating real JWSDOS behavior, not "just matching the hardware." Directory
  format + allocation model: `docs/JWSDOS-format.md` (mirrors how `docs/MDCR-implementation.md`
  holds cassette detail rather than duplicating it here). (JWSDOS = a third-party,
  user-group-developed P2000 disk OS; the official Philips disk-BASIC product also existed,
  §5b/§5d — different DOS, different on-disk conventions.)

---

## 4. Bus & contention model

- Use a **tick-based master clock** (performance is a non-issue at 2.5 MHz).
  - Natural base: the dot/character clock the SAA5020 derives timing from.
  - Z80 ticks once per (master ÷ N); video fetch logic ticks on the character clock.

### The Z80 has unconditional priority — the VIDEO is what breaks
This is the corrected, hardware-accurate polarity of the model. Two senses of
"owns the bus" must be kept separate:
- **Arbitration ownership (who wins):** the **Z80, always.** There is NO wait-state
  logic. The CPU takes RAM whenever it wants, never stalls, never pays a penalty.
- **Temporal occupancy (who's using the bus when the CPU isn't asking):** the SAA5020,
  during active display — but only in the slots the Z80 leaves free.

Because the Z80 always wins, the **video fetch is the loser** in a collision. That is
exactly why the *video* shows the glitch and the CPU is unaffected.

**Correct contention rule:**
> In a character fetch slot, **if the Z80 is driving a RAM access, the video fetch for
> that slot is corrupted → bad character cell. The CPU proceeds unaffected.**

The CPU is the aggressor and the always-winner; the glitch is the video's loss.
Heavy CPU RAM activity during the visible frame produces speckle; code that idles or
confines work to vertical blank displays cleanly.

**Implication for "clean bandwidth":** there is no such thing as the CPU being
"locked out" — it never is. Any earlier figure for CPU clean T-states is meaningless
under this (correct) model. The quantity that matters is the **inverse**: what
fraction of video fetch slots the CPU corrupts, i.e. how busy the Z80's RAM accesses
are during the 240 active display lines.

### The contention source is the SAA5020, NOT the SAA5050
- **SAA5050** = character generator. Character code in → glyph pixel rows out from its
  internal font ROM. It does **not** address video RAM. Downstream and "innocent" in
  the collision.
- **SAA5020** = teletext *timing chip*. Officially: a MOS IC that performs the timing
  functions for a teletext system, providing the signals to extract data from memory
  per the European 625-line TV standard. Effectively a binary counter generating the
  VRAM fetch addresses during display.
- Real per-slot sequence: SAA5020 drives a VRAM address → byte returns on data bus →
  fed to SAA5050 → glyph pixels out. The Z80 colliding with that fetch is what
  corrupts the cell.

### Modeling approach: fetch is a real bus transaction, not a side check
Don't bolt a "detect contention" test onto the side. Make the **video fetch a real
bus read in the single tick loop**, like a CPU access. Collision then *happens*; you
don't *detect* it. Three layers, only one timing-critical:
1. **Fetch-timing unit (SAA5020's job — cycle-accurate):** deterministic
   character-clock state machine; each slot knows whether a display fetch is happening
   and its VRAM address. Your contention oracle. Implement as a counter / raster-position
   model (recommended — maps to hardware, easy to probe) or a raster-position formula.
2. **Bus (resolves collisions):** Z80 access present this slot → video fetch corrupted.
   Single owner, deterministic, single-threaded.
3. **Glyph generator (SAA5050's job — contention-irrelevant):** consumes fetched bytes
   → pixels (font ROM, 160–255 inverted-colour trick, control codes, rounding). Never
   drives the bus, so run it however convenient — even an end-of-scanline pass.

Address generation simplifies dramatically given the confirmed 10× re-read (see §5):
within a character row's 10 scanlines the **column sequence is identical every time**;
only the glyph row index advances, and that goes to the SAA5050, not the address:
```
fetchAddr = videoBase + (charRow * 40) + column      // + scroll/pan offset
glyphRow  = scanlineWithinRow                         // 0..9, to SAA5050 only
```
Loop shape:
```
each master tick:
    if videoTiming.IsFetchSlot(cycle):
        addr = videoTiming.FetchAddress(cycle)   // SAA5020 function
        bus.BeginVideoRead(addr)
    cpu.Tick()                                    // may drive a RAM access (always wins)
    bus.Resolve()                                 // CPU RAM access this slot -> corrupt video fetch
    // fetched byte (clean or corrupted) -> row buffer for the glyph pass
```

### Corruption is single-cell and NON-persistent (eyeball-confirmed)
Real-hardware eyeball observation: the glitch appears as a disturbance **within one
character cell**, not a horizontal streak. This implies the corruption is gone by the
next character clock — the CPU grabs RAM mid-slot, that one fetch returns bad data,
the SAA5050 renders one bad character, the following slot is clean again. So model it
as **per-slot, no carry-over state**: mark the single cell fetched during the collided
slot as bad and move on. (Confirm 100% with capture; until then this matches what's
seen.)

### Open: WHICH corruption mode (needs logic-analyzer / video capture)
Eyeball can't resolve what "bad" actually is. Three candidates, visually close at 50 Hz:
- **CPU data bleed** — SAA5050 latches the byte the Z80 was driving on the shared data
  bus → cell shows the character matching the CPU's *data*, not black. Looks like
  speckle of "wrong characters."
- **Bus contention / floating** — both drive the bus, read is electrically invalid →
  garbage code rendering as black, a random glyph, or flicker.
- **Fetch suppression** — video read gated off for that slot, SAA5050 sees null →
  black cell.

The "black-ish glitch in a cell" observation leans toward suppression or
contention-resolving-to-black; "wrong characters" speckle would point to data bleed.
**Capture to run:** trigger the logic analyzer on Z80 **/MREQ asserting during active
display**; record the data bus + the SAA5050's data inputs on that same character
clock. That distinguishes bleed (SAA5050 saw the CPU byte) vs contention (floating/garbage)
vs suppression (null read). A simultaneous RGBS video capture of the same frame
correlates the electrical event to the visible cell.

**Build-against-now default:** collided slot → blank/black cell, no persistence. Swap
in the precise mode once captured.

### Scope of contention — VRAM-ONLY (owner-corrected)
- **Scope = VRAM only: `addr >= 0x5000 && addr < 0x5800`** (0x5000–0x577F is the buffer;
  0x5800–0x5FFF is unused on the T). The video contends only when the CPU touches the **VRAM
  chips the SAA5020 is actually reading** — NOT the whole DRAM range.
- **Why (owner + this doc's own hardware notes):** the video memory is a **separate 2K×8 area**,
  distinct from the 8× 16K×1 chips forming system RAM (§ Memory/video). Since VRAM is its own
  silicon, CPU access to main RAM (0x6000+) uses different chips and cannot collide with a
  display fetch. Behaviourally this matches reality: the glitch is tied to **writing the screen**
  during active scan — ordinary data/stack access to main RAM does NOT glitch the display, which
  a `>= 0x5000` (whole-DRAM) scope would wrongly cause.
- **Implemented (milestone-10 + 2026-07-06 correction).** The over-wide `addr >= 0x5000` check
  was corrected to a per-model VRAM window: `IsVideoRamAddress(addr)` (instance method on the page
  table, using `_videoRamEnd` set at construction from the model):
  - **P2000T: 0x5000–0x57FF** (2 KB VRAM chip) — 0x57FF contends, 0x5800 is clean.
  - **P2000M: 0x5000–0x5FFF** (4 KB VRAM chip) — 0x5FFF contends, 0x6000 is clean.
  Main RAM (0x6000+), expansion RAM, and the banked window are separate DRAM chips the SAA5020
  never addresses, so CPU access there cannot glitch the display.
- **Corrupted-cell overlay (as built):** the machine exposes a per-field **40×24 bool map** — the
  40 is the visible **viewport** width, NOT absolute VRAM columns, so **index = charRow×40 +
  viewportCol** where `viewportCol = vramCol − PanX`; a consumer maps each absolute VRAM column
  through `PanX` before testing the flag. Set whenever a cell's fetch is contended, cleared
  **after** `FieldComplete`
  fires so a consumer can read it from the FieldComplete handler (and cleared on `Reset`). This is
  the hook the UI display "show glitches" overlay and the debugger's VRAM window both consume.
- **Only-if exception:** if the schematic ever shows a SINGLE DRAM controller multiplexing CPU +
  video across the ENTIRE array (shared RAS/CAS over all chips), the wider scope would hold —
  but the separate-2K×8 note argues against it. Confirm from schematic only if the VRAM-only
  behaviour ever looks wrong.
- **Display-start offset — CONFIRMED (2026-07-19, owner-supplied P2000TM Field Service
  manual, "T-VERSION VIDEO GENERATION" section):** the manual states the T-version field
  rate counter counts **313 scanlines**, and *"the displayable area of this is from
  scanline 49 to 289, 240 scanlines to be used to display 24 rows of 10 scanlines
  each."* This resolves the open question directly and asymmetrically:
  **CORRECTED (2026-07-22, owner decision on scanline-counter indexing):** the split below
  changed from an earlier ≈48/25 approximation to an exact **49/24** split — the manual's
  "scanline 49 to 289" reads most naturally as a **0-indexed hardware counter** (0–312 across
  313 total scanlines) with a **half-open active range** (49 ≤ n < 289, i.e. lines 49–288
  inclusive = exactly 240 lines) — the convention counter/comparator hardware typically uses.
  That reading is the one the owner has adopted; it sums exactly (49+240+24=313) whereas the
  1-indexed reading used previously left a rounding gap (48+240+25=313, hedged as "~24-25"
  since the manual text alone doesn't disambiguate indexing). Treat 49/240/24 as the resolved
  figures going forward:
  - **Pre-roll vblank = scanlines 0–48 (0-indexed) → 49 lines** (before the active window starts)
  - **Active window = scanlines 49–288 → 240 lines** (matches the pre-existing derived figure)
  - **Post-roll vblank = scanlines 289–312 → 24 lines** (after the active window ends, up to
    the next field's line 0)
  - In T-states (160/line): pre-roll = **7,840**, active = **38,400**, post-roll =
    **3,840**. Sum = **50,080** vs. the nominal 50,000 (the field-rate figure assumes an
    averaged 312.5 lines/field; the manual's 313 is the exact count for one field — see the
    interlacing note below).
  - **Consumer note — flag for Claude Code:** this means VBLANK does **not** start at
    field-T-state 0. A `VideoFetchUnit` (or equivalent) that treats T-state 0 as the
    start of the fetch/contention-eligible window — instead of offsetting the start of
    eligibility by ~7,840 T-states (49 lines) into the field — will wrongly apply
    contention math during real hardware's pre-roll vertical blank, and the effect
    would concentrate exactly at the **top of the frame** (49/313 ≈ 15.7% of the
    field). This is offered as a **hypothesis to verify against the actual
    `VideoFetchUnit` source**, not a confirmed bug — see the owner's Ghosthunt report
    below. (Contention timing itself is implemented; last-fetch slot LineTState 97, line
    boundary 159 — see machine md milestone-10 finding, which does not address this
    vertical offset at all.)
  - **Owner's diagnostic report (2026-07-19):** *"I did a small test with Ghosthunt and
    saw many display glitches in the top 15% of the screen... Does [contention] take
    into consideration that the video chip is not accessing video memory during the
    full frame, but only in the window IN the frame where the display is?"* — the
    49/313 ≈ 15.7% figure above lines up closely with the reported "top 15%" symptom.
    **Explicit correction from the owner, must not be lost in any fix:** *"assuming
    that all 50000 cycles are used during the 640×480 area is wrong"* — only ~38,400
    of the 50,000 T-states/field are within the active window; the rest (asymmetric
    pre/post blanking above) are outside it and must be contention-free regardless of
    CPU RAM activity during those T-states.
  - **RESOLVED (2026-07-21, owner clarification):** the manual's *"the signal CRS is
    active during the even scanlines of the field. In our system we use only the odd
    scanlines, so no interlacing is used"* is CONFIRMED correct and the owner agrees.
    This was in tension with this doc's/CLAUDE.md's prior "interlaced, frame = two
    fields" framing — that framing is now understood to be **BBC-Micro heritage
    (jsbeeb/MAME are genuinely interlaced machines) carried over into the port, not
    P2000T hardware fact.** See the corrected §4a FIELD note directly below, and the
    fuller correction (with the owner's decision on what the rendering default should
    be) in `SAA5050-implementation.md` §5 and CLAUDE.md §3 "Fields vs frames."
  - **Also resolved:** `docs/SAA5050-implementation.md` (a de facto fourth canonical
    file as of this pass, owner-supplied) and CLAUDE.md §3 have both been updated with
    the same correction — see those files for the rendering-mode implications
    (interlaced/comb should no longer be the default presentation mode; the owner's
    guidance is to default to the FSM-confirmed single-field/odd-scanline mode instead,
    keeping interlaced as an opt-in extra). **Note (2026-07-23): this file does not
    actually exist in the current docs folder** — same gap `docs/MDCR-implementation.md`
    had until the owner supplied it directly; if a copy exists from an earlier session,
    worth re-uploading the same way, since several notes here point to it as holding
    detail not duplicated in this doc.
  - **New flagged hypothesis — possible odd/even field-parity inversion (owner-reported,
    2026-07-23): "the default 'odd field only' shows a different glyph representation
    than the real P2000, and looks like what 'even field only' [of the emulator] should
    show."** Two candidate mechanisms, NOT mutually exclusive, both unverified against
    actual source (no code access from this doc-maintainer seat — flagged for Claude Code
    to check, not a confirmed bug either way):

    **Hypothesis A — absolute-scanline counter re-zeroed at the visible window.** This
    may be the **same root-cause SHAPE as the Ghosthunt contention-offset bug above** — a
    component measuring line parity from a counter that resets to 0 at the start of the
    active/visible window, instead of carrying the true absolute field-scanline number
    through from field-line 0. The arithmetic that makes this plausible: **the pre-roll
    vblank is 49 lines — an ODD count.** Real hardware's odd/even field-parity selection
    (CRS/RA0, §4a below) is defined against the FIELD's own absolute scanline count
    (0–312), and the manual states the display uses only the ODD absolute scanlines.
    Because the offset into the active window is itself odd, the FIRST VISIBLE scanline
    is absolute field-line **49 — itself odd** — so on real hardware it correctly counts
    as one of the "odd" scanlines actually displayed. **If a renderer instead re-zeros
    its own line counter at the start of the visible window** (treating the first
    displayed scanline as local line 0, which is EVEN) **rather than carrying the true
    absolute field-line number through, every parity decision for the rest of the active
    window comes out inverted relative to real hardware** — which would look exactly like
    the reported symptom. Check whether whatever computes "is this scanline odd or even"
    for the CRS/RA0 sub-scanline selection uses the absolute field-scanline number (0–312,
    offset by the 49-line pre-roll) or a window-local index that starts over at the first
    visible line. If it's the latter, the fix is almost certainly to feed the SAME
    absolute field-line counter the contention model already needs (§4's Consumer note
    above) into the SAA5050 rendering path too, rather than maintaining two
    independently-zeroed counters for what should be one shared notion of "where in the
    field are we."

    **Hypothesis B — the new display-mode selector still keyed off the vestigial
    two-field-era `IsOddField`/`FrameComplete` flag, arguably the more likely candidate.**
    `SAA5050-implementation.md` §5 flags this EXACT risk two days before the bug was
    reported (dated 2026-07-21, in the same pass that corrected the two-field-interlace
    assumption): *"The owner's `P2000Video.cs` exposes `FieldComplete` (every field) and
    `FrameComplete` (odd-field only) to drive cadences — re-check which of these the
    single-field default should actually key off, since 'odd-field only' was itself
    premised on the (now-corrected) two-field model."* Under the OLD (now-corrected)
    model, `IsOddField` didn't just track scanline parity — it selected which of TWO
    DIFFERENT fetch/render passes a whole field used (the historical text preserved in
    `SAA5050-implementation.md` §5 shows **even field → `SetCRS(false)` → raw rows only,
    odd field → `SetCRS(true)` → smoothed rows only**, i.e. an entire field was rendered
    as either all-raw or all-smoothed, never both). Under the CORRECTED model, a single
    field contains BOTH raw and smoothed sub-scanlines, selected by CRS/RA0 **within**
    that one field's own data — there's no longer a "the raw field" vs. "the smoothed
    field." **If the new odd-only/even-only display-mode selector (§4 above, config axis)
    was wired directly onto that legacy `IsOddField` toggle** — i.e. it picks between the
    two OLD passes (whole field of raw vs. whole field of smoothed) rather than
    recomputing genuine odd/even scanline selection from the corrected single-field
    model — the label "odd-only" could end up showing what used to be produced by the
    pass the old code called the *even* field, or vice versa, independent of any
    off-by-one in Hypothesis A. This would also explain a "different glyph
    representation" (not just a vertical half-resolution shift): an all-raw field looks
    visibly different from an all-smoothed field (no diagonal rounding at all vs. full
    rounding), which is a bigger, more qualitative difference than a one-line vertical
    parity shift would produce — arguably a closer match to "different glyph
    representation" than Hypothesis A's pure line-parity explanation. Check what the
    odd-only/even-only display-mode branch actually reads: if it still branches on
    `IsOddField`/`FrameComplete` (or an internal equivalent) to pick a whole differently-
    rendered field rather than computing "is THIS scanline one of the odd absolute
    scanlines" from the single-field data both modes now share, that's very likely it —
    see `SAA5050-implementation.md` §12 for the corresponding write-up in that doc.

  - **Owner decision on the investigation (2026-07-23): leave the display-mode options as
    they are for now, do not act on Hypothesis A/B yet.** Claude Code analysed the display
    pipeline per the two hypotheses above; the finding was that staying strictly faithful to
    the corrected single-field FSM model would mean the "extra" options — Interlaced/comb,
    Even-only, Progressive — could no longer exist as genuinely distinct modes (per the
    corrected model, only ONE field-derived image is authentic; the others depend on the old
    two-field-alternation machinery this hypothesis flags as vestigial). **The owner prefers
    to keep all four options available** rather than remove the three non-authentic ones,
    and plans to investigate real hardware output further before deciding whether/how to
    change the default or the option set. **Not a bug fix, not closed — a deliberate "leave
    as-is for now."** Revisit once the owner has more real-hardware reference footage/photos;
    until then this is not to be treated as urgent or as blocking other milestones.

## 4a. Derived timing framework (PAL 625-line)

The SAA5020 datasheet scans are phone-photographed images with no text layer, so the
exact timing tables must be eyeballed from the scan or captured. But the functional
role ("timing chain for the European 625-line standard") plus confirmed geometry makes
the structure fully derivable.

Keyed off the PAL line period and the 2.5 MHz clock:
- **Line:** 64 µs → 64 × 2.5 = **160 Z80 T-states per scanline**
- **FIELD:** 50 Hz → **50,000 T-states** (nominal, averaged); the Field Service manual gives an
  exact **313 lines** for one field (160 × 313 = 50,080, close to the nominal 50,000 — see §4
  above). Interrupt + CTC ch3 fire per field (50 Hz).
  **CORRECTED (2026-07-21, owner-supplied Field Service manual + owner clarification):** the
  P2000T is **NOT interlaced** — *"the signal CRS is active during the even scanlines of the
  field. In our system we use only the odd scanlines, so no interlacing is used."* There is no
  real hardware "frame = two fields = 25 Hz" alternation; **every field is a complete,
  independent 313-line refresh at 50 Hz**, and CRS/RA0 selects raw-vs-smoothed **sub-scanlines
  within one field's already-fetched row data**, not a second field's separately-fetched
  content. The earlier "interlaced, frame = two fields" framing was BBC-Micro heritage (jsbeeb/
  MAME are genuinely interlaced) carried into the reference model, not a P2000T hardware fact —
  see `SAA5050-implementation.md` §5 and CLAUDE.md §3 for the full correction and the owner's
  resulting decision on the default rendering mode (single-field/odd-scanline, not
  interlaced/comb). **See §4's "New flagged hypothesis" note (2026-07-23) if odd-only/even-only
  ever look swapped relative to real hardware** — the CRS/RA0 parity here is defined against the
  field's own absolute scanline count, not a window-local one, and the 49-line (odd) pre-roll
  offset is exactly the kind of thing that inverts parity if a renderer re-zeros its counter at
  the start of the visible picture instead.

**Vertical structure — CONFIRMED (2026-07-19, owner-supplied P2000TM Field Service
manual, "T-VERSION VIDEO GENERATION"), upgraded from derived to sourced, with the
previously-undetermined display-start offset now resolved (see §4 above for full
detail and the Ghosthunt diagnostic that prompted this). Split CORRECTED 2026-07-22 to
the exact 49/24 figures (see §4 for the 0-indexed-counter reasoning; supersedes the
earlier ≈48/25 approximation):**
- Field = **313 scanlines** (exact count, per the manual's field-rate counter — every
  field, since there's no true interlace, §4a FIELD note below)
- Active window = **scanlines 49–288 = 240 active display lines** (24 rows × 10
  scanlines/row — matches the pre-existing derived figure exactly)
- **73 lines of vertical blank total, asymmetrically split**, not evenly around
  the active window:
  - **Pre-roll: scanlines 0–48 → 49 lines (7,840 T-states)** — before the active
    window starts
  - **Post-roll: scanlines 289–312 → 24 lines (3,840 T-states)** — after
    the active window ends
- No fetches occur during either blanking region; CPU RAM access never glitches video
  there — but critically, **only 38,400 of the 50,080 T-states/field (the 240 active
  lines) are inside the fetch-eligible window**. The remaining 11,680 T-states
  are pre/post-roll vblank and must be treated as contention-free regardless of CPU
  activity. (Do not assume the full 50,000-T-state field is "the active area" — see
  the owner's explicit correction in §4.)

**Horizontal structure** (pinned by the confirmed 6 MHz dot clock). **Leading/trailing
split ADDED (2026-07-22, derived from the manual's own char-time markers, mirroring the
vertical breakdown above):**
- 40-col = 6 MHz dot clock, 6 dots/char → **1 MHz char-fetch rate = 1 µs/character** →
  a scanline is **64 char-times** long (64 µs ÷ 1 µs/char-time)
- The manual states *"during character times 15-55 the display of 40 characters on the
  screen is enabled"* — read with the same half-open convention as the vertical split
  (start-inclusive, end-exclusive: char-times 15–54 = 40 char-times), this gives an
  exact, self-consistent leading/active/trailing breakdown:
  - **Leading blank: char-times 0–14 → 15 char-times (15 µs)** — before the active
    window starts (line-sync GLR fires at char-time 6, inside this leading portion)
  - **Active: char-times 15–54 → 40 char-times (40 µs)** — the 40 displayed columns
  - **Trailing blank: char-times 55–63 → 9 char-times (9 µs)** — after the active
    window ends, up to the next line's char-time 0
  - Sum: 15 + 40 + 9 = **64 char-times = 64 µs**, matching the line period exactly
- 80-col mode doubles the dot clock to 12 MHz (still 1 µs/char-pair region; see §5)

**Horizontal retrace — CORRECTED (2026-07-22, owner's model, since no scope trace of the
real video signal is available to confirm directly): the leading blank above is NOT all
renderable border.** The owner's model: the chip cuts off emission immediately after
char-time 64 (end of line) — so the **trailing blank stays as derived (9 char-times,
144 px), nothing changes there** — and the beginning of the NEXT line is genuine
**horizontal retrace** (beam physically returning, chip not emitting anything at all,
not even black) for **char-times 0–5, 6 char-times** (the manual isn't fully explicit
here; "GLR active at character time 6" is read as marking retrace's end/reset-complete,
so retrace spans 0–5 — **flagged as a 5-vs-6-char-time ambiguity**, matching the owner's
own "5 (or 6?)" hedge; 6 is used below since it also produces a tidy symmetric result,
not because it's independently confirmed). Only AFTER retrace does the chip resume
emitting (blanked/porch, still renderable as black border) up to the active window:
- **Retrace (NOT rendered — chip emits nothing): char-times 0–5, 6 char-times.** Not
  part of the buffer at all — there's no signal to represent, not even black.
- **Leading blank (rendered, porch/border): char-times 6–14 → 9 char-times (9 µs)** —
  revised down from the earlier 15 char-times by excluding the retrace portion above.
- **Active: char-times 15–54 → 40 char-times (40 µs)** — unchanged.
- **Trailing blank (rendered, porch/border): char-times 55–63 → 9 char-times (9 µs)** —
  unchanged, left intact per the owner's model (retrace happens at the START of the
  following line, not the end of this one).
- Rendered total: 9 (leading) + 40 (active) + 9 (trailing) = **58 char-times**, plus 6
  char-times of unrendered retrace = 64 char-times total, still matching the line period.

### Full raster geometry — DERIVED (2026-07-22, combines the vertical + horizontal
splits above; owner request to render the complete raster, not just the active window;
horizontal figures CORRECTED same day to exclude retrace, see above)
For a machine that renders the **complete field** (blanking included, not just the
640×480 active viewport) — see machine CLAUDE.md §3 for the framebuffer contract this
feeds and reference doc §3a for the UI's Full-Field vs Graphics-window toggle:
- **Horizontal, at 16 rendered pixel-lanes/char-time** (the existing anti-aliasing
  lane count, §1/§2 of `SAA5050-implementation.md` — unchanged by this, applied
  uniformly across blanking too since it's just a constant-duration timebase): leading
  144 px (9×16) + active 640 px (40×16) + trailing 144 px (9×16) = **928 px wide**
  (retrace's 6 char-times / 96 px are excluded entirely, not rendered).
- **Vertical, at 2 rendered rows/scanline** (the existing CRS-driven line-doubling,
  applied uniformly across blanking too — blanking rows carry no character content to
  smooth, so both sub-rows are simply flat black, no `CombineRows` work needed):
  pre-roll 98 px (49×2) + active 480 px (240×2) + post-roll 48 px (24×2) = **626 px
  tall**. (Vertical retrace is a separate, NOT-YET-ASKED question — the owner's
  retrace-exclusion request so far only covers horizontal; the 49/24-line vertical
  split above is left as pure blanking, not yet split into a retrace/porch sub-breakdown
  the way the horizontal one now is. Flag for a future pass if wanted.)
- **Full-field buffer: 928 × 626 px.** The 640×480 "graphics window" (the picture
  content, what's shown today) sits at a **fixed offset (144, 98)** within it — a
  constant crop rectangle, the same on every field, since the blanking geometry is
  fixed hardware timing, not data-dependent. (Horizontally symmetric: 144 px border on
  both sides of the 640 px active width — a side effect of the 6-char-time retrace
  assumption, not independently confirmed.)
- Both blanking margins are **always contention-free and content-free** (§4: no VRAM
  fetch happens there) — they render as flat black with no CPU interaction possible,
  cheap to produce.
- Every pixel column/row in this buffer represents an equal, constant real-world duration
  (1 char-time horizontally, 1 scanline — halved — vertically) whether blank or active —
  geometrically consistent throughout. **CORRECTED (2026-07-22, owner catch): this does
  NOT mean PAL aspect-ratio correction should extend to the full buffer.** Aspect
  correction targets a standardized real-world relationship (active picture → 4:3 CRT
  tube) that only the active window has — the blanking margins were never displayed at
  any standard size on real hardware (retrace is physically off-screen; porch is mostly
  hidden by bezel/overscan, and by how much varies per set, not standardized). See §3a
  "Full-Field vs Graphics-window" for the resulting UI guidance: aspect correction is a
  no-op in Full-Field mode; the buffer is shown at native pixel geometry instead.

**What this means under the Z80-priority model:** glitches are possible during the
240 active lines (scanlines 49–288, 38,400 T-states) whenever the Z80 drives a RAM
access in a fetch slot. They're impossible during the 49-line pre-roll vblank, the
24-line post-roll vblank, and the 9 µs (or 15 µs leading) horizontal-blank portion of
each active line, because the video isn't fetching then. So a program gets "free" RAM
time in vblank + h-blank automatically; visible glitching scales with how much RAM
work the CPU does inside the active-display fetch windows. A contention model that
doesn't offset the start of its fetch-eligible window by the 7,840-T-state pre-roll
would spuriously glitch the **top of the screen** even when the CPU is doing nothing
unusual — see the sourced offset and Ghosthunt diagnostic in §4 above.

### The one parameter that is NOT derivable
**How much of each 1 µs character slot the video fetch occupies the bus** — full
character period vs. a brief strobe with gaps. This sets how *likely* a given CPU RAM
access lands on top of a live fetch (and thus glitch density). It's in the datasheet's
horizontal-timing / F-signal waveforms (the non-OCR-able part). Get it by reading the
scanned timing diagram by eye, or — better — a logic-analyzer trace of Z80 /MREQ
against RAM /CAS|/RAS during active display.

---

## 5. Hardware facts

### CPU
- Zilog Z80 @ **2.5 MHz**.
- Timing: **50 fps** (PAL), one VBLANK interrupt per frame.
  - Cycles per frame: 2,500,000 ÷ 50 = **50,000**.
- Keyboard is read directly as memory locations and appears interrupt-based; there's
  speculation the low 2.5 MHz clock partly exists due to timing criticality in key
  reading. Watch input timing.
- **NMI**: on real hardware, an added push-button to the Z80 NMI pin (pin 17) for a
  soft reset (reset without clearing memory) — useful during programming deadlocks.

### Memory / video
- System RAM formed by 8 dynamic 16K×1 chips (base). Expansion via a small PCB onto the
  main board (expensive DRAM in period).
- **Video RAM:** 2 KB area on the T-version (mask should be `0x7FF`). Includes
  teletext control characters and cursor info.
- **Screen buffer:** 2 screens in size, 80 chars × 24 rows, address range
  **`0x5000`–`0x577F`**. Pan the viewport via the **scroll register at I/O port `0x30`**
  (CONFIRMED): write **0–40**, 0 = no offset (base screen), 40 = max (shows the 2nd screen to
  the right of the base). Used to reduce flicker, since the Z80 can't refresh the whole screen
  each field. The video device reads port 0x30 when computing fetch addresses.
  - **Values > 40 are UNDEFINED** (owner: to investigate later). For now implement the defined
    0–40 range correctly; clamp or wrap >40 as a placeholder (owner will pin down real behaviour
    later). Don't block on it.
- Monitor ROM: 4 KB, BIOS-style routines only (memory test, cassette + serial
  printer drivers). No UI, no debug monitor. Machine is non-operational without a
  cartridge.
- BASIC: 16K Microsoft BASIC on cartridge; common revisions v1.0 and v1.1 (NL).

### Memory map (CONFIRMED) + variants + bank switching
Fixed 64 KB layout:
| Address        | Region                          |
|----------------|---------------------------------|
| 0x0000–0x0FFF  | Monitor ROM (4 KB, read-only)   |
| 0x1000–0x4FFF  | Cartridge (slots)               |
| 0x5000–0x57FF  | Video RAM (contention-tracked)  |
| 0x5800–0x5FFF  | Unused (open bus) — **T ONLY**  |
| 0x6000–0x9FFF  | Base RAM (16 KB)                |
| 0xA000–0xDFFF  | 16 KB expansion                 |
| 0xE000–0xFFFF  | 8 KB **bank-switched** window   |

**CONFIRMED, independently, by the official Philips "P2000 System T&M Reference Manual"
(2026-07-23, owner-supplied 144-page scan, personally read page-by-page — `raw-conversion.md`
in this docs folder is the full transcription).** Chapter 4's own Figure 2 memory map and the
accompanying §4.2 text lay out the identical structure using the manual's own words: monitor
ROM 4k (fixed, CPU board), ROM key up to 16k (`0x1000`–`0x4FFF`), video memory 4k (`0x5000`–
`0x5FFF`, split "video page 1"/"video page 2" of 2k each), system RAM 16k, and "two extensions,
each of 16k" for applications — matching this table's Base RAM / 16 KB expansion / bank-switched
window exactly, right down to the address boundaries. The manual is explicit that **"if the
system includes flexible disks, these extensions must both be present, since the disk controller
software is stored in here"** — i.e. on the real hardware, a disk-equipped machine could never
be RAM-starved at `0xA000`–`0xFFFF`, an implicit lower bound this project hadn't previously
stated in those terms. One minor, low-stakes internal wrinkle in the manual itself, flagged not
resolved: §4.5.1's Figure/table gives the monitor's own RAM area as `0x6000`–`0x61FF` ("200
hex bytes" = 512 bytes) as a block label, while the manual's OWN detailed byte-by-byte listing
(same section, and §4.5.2's "start of application RAM" note) only actually documents monitor +
disk-driver usage through `0x608F`/`0x6090` (144 bytes, matching this project's own confirmed
figure elsewhere) — the widerFigure-2 block looks like a rounded-up region boundary rather than a
literal usage count; not something this project needs to reconcile since the 144-byte figure is
what's actually backed by the RAM-variable table.

**The 0x5000–0x5FFF block is MODEL-SPECIFIC — the map is NOT a T superset:**
- **P2000T:** 2 KB video RAM (0x5000–0x57FF) + 2 KB unused/open-bus (0x5800–0x5FFF).
- **P2000M:** the **full 4 KB (0x5000–0x5FFF) is video memory** — consistent with the M being
  the 80×24 business-display machine needing the larger character matrix.
- Consequence: build the page table **per model**, not built-for-T-then-patched. The video
  region entry differs by model (2 KB VRAM + 2 KB open-bus on T vs a single 4 KB VRAM page
  on M), AND the video *device* it routes to differs (SAA5050 teletext on T; the M's display
  circuitry driving the larger buffer). "Which model" gates the shape of the video region,
  not just top-of-memory RAM population.
- Contention (§4) is scoped to the **T + SAA5050**. The M has a different display
  architecture and buffer size, so whether it exhibits the same CPU-loses-the-bus glitch is
  a **separate M-phase question** — do NOT assume the M glitches identically or that its
  timing is a rescale of the T's.

**Commercial variants (this IS the top-level RAM config axis; reset-to-apply, §3a):**
| Variant | Total RAM | Populated                                                    |
|---------|-----------|--------------------------------------------------------------|
| **T/38**  | 16 KB   | Base RAM only (0x6000–0x9FFF). Above 0x9FFF = open bus.       |
| **T/54**  | 32 KB   | Base + 16 KB expansion (0xA000–0xDFFF).                       |
| **T/102** | 80 KB   | Base + 16 KB expansion + banked 0xE000–0xFFFF via port 0x94.  |
| **PTC-96K** | 96 KB | PTC-floppyboard variant: 16 KB + 64 KB expansions.           |

**Bank switching — port `0x94`.** The width of this port's decode is **card-specific**, so model
`0x94` as a property of the installed RAM/expansion card, NOT a fixed motherboard feature:

- **Original Philips extension board — 1-bit `RAMSW` flip-flop (CONFIRMED, service manual Fig
  4.12.1).** The board carries up to **two 16 KB RAM banks**:
  - **BANK0 → 0xA000–0xDFFF** (16 KB, linear — SEL8K carries A13, ordinary full-16 KB addressing).
    This is the T/54 expansion.
  - **BANK1 → 0xE000–0xFFFF**: the CPU directly addresses only **8 KB** here, but the bank is a
    full **16 KB**. Port `0x94` on this board is a **1-bit flip-flop (`RAMSW`), bit `D0` ONLY:**
    `D0 = 1` → **upper 8 KB** of BANK1 at 0xE000–0xFFFF, `D0 = 0` (reset) → **lower 8 KB**; other
    bits ignored. A 2-way toggle of one 8 KB window — NOT a multi-bank index. Original board tops
    out at **2 × 16 KB = 32 KB** extension (48 KB addressable); reset = lower half = power-on default.

- **Homebrew / third-party RAM cards — MORE bits of `0x94` → MORE banks (CONFIRMED, owner).** The
  1-bit decode is specific to the Philips board; port `0x94` itself can carry a wider value, and
  homebrew expansion cards decode additional data bits to select **more than two banks**. So the
  "configurable N-bank register" model is real hardware — it just belongs to these cards, not the
  original board.

**Model it per-card:**
- **Original Philips board card:** 1-bit `RAMSW` (D0), BANK1 upper/lower toggle, as above.
- **Homebrew RAM card:** a configurable-width bank register (`bankBits`/`bankCount` from config),
  raw value = bank index, index ≥ populated → open bus. The "emulator as a superset of the
  hardware" path — parameterized so third-party cards are expressible with no special-casing.

**⚠ Reconciles with milestone 2 (not a throw-away).** Milestone 2 already implemented `0x94` as a
configurable N-bank register — that is CORRECT for the homebrew-card path and should stay. The fix
is to add the **original Philips board as a 1-bit `RAMSW` card** and make it the default/authentic
selection, so a stock T/54–T/102 toggles BANK1's two halves rather than indexing 6 banks. Bare-T
and RAM-only (BANK0-only) configs never write `0x94`.

**⚠ RAM-total reconciliation needed.** The *original* board = 48 KB addressable max, yet the
variant table lists **T/102 = 80 KB** and **PTC-96K = 96 KB**. Those can't come from the original
2-bank board as described. (Homebrew wider-`0x94` cards can reach higher totals, but T/102 and
PTC-96K are *Philips* model names — so their actual board is what needs documenting.) Treat T/54
(base + BANK0 = 32 KB) and the `RAMSW` BANK1 toggle as confirmed; flag T/102's 80 KB and PTC-96K
until the board that reaches them is documented.

**Corroborating evidence for T/102's 80 KB, from the M2200 manual (2026-07-23, NOT the same
claim as "the board is confirmed identical"):** the M2200 multi-function board's own bank-switch
is a 3-bit register selecting 6 banks × 8 KB (`docs/M2200-implementation.md` §2.2) sitting behind
a contiguous 32 KB base (0x6000–0xDFFF) — **32 KB + 48 KB banked = 80 KB exactly, matching T/102's
figure.** This is the first source this project has with an exact bit-count/bank-count for an
80-KB-class board, and the arithmetic lands precisely on T/102 — strong corroboration that a
T/102-class bank-switch mechanism looks like "3 bits, 6 banks," without claiming a Philips-branded
T/102 unit and Miniware's M2200 are literally the same hardware (M2200 is explicitly a third-party
board). Treat T/102 as "very probably a 3-bit/6-bank scheme" rather than fully unconfirmed.

### Memory topology = THREE tiers (bare / RAM-only board / floppy+RAM board) — CONFIRMED
The bare motherboard has a **fixed** memory configuration; larger configs come from an
**extension board in the internal slot**, of which there are **two variants**:
| Internal slot | RAM | Disk | Notes |
|---------------|-----|------|-------|
| **None (bare motherboard)** | fixed base (16 KB, T/38-class) | no | true baseline; nothing in internal slot |
| **RAM-only extension board** | expanded (T/54 / T/102 configs) | no | memory upgrade without disk |
| **Floppy+RAM extension board** | expanded (incl. PTC-96K) | yes | RAM config + floppy/CTC interface |

**Board RAM is PER-SOCKET configurable (CONFIRMED).** Chip sockets can be left empty (no
expansion) or populated — RAM was very pricy then, so partial population was real. The
**monitor ROM does a RAM-presence check at startup** and sizes memory to whatever responds.
Presence-probe pattern made literal:
- Config expresses board RAM at **socket granularity** (each socket empty/filled), not as a
  few fixed variants. (Applies to BOTH the RAM-only and floppy+RAM boards.)
- Filled socket → RAM page; **empty socket → open-bus page**. The ROM's startup RAM test
  sizes exactly that layout — **no need to tell the firmware anything.**
- Named variants (T/54, T/102, PTC-96K) are **common socket-population PRESETS**, not distinct
  hardware. Offer presets as one-click choices AND allow arbitrary population, including
  historically-plausible partial fills.
- **Faithful gap behavior:** RESOLVED — the ROM RAM test **stops at the first gap**; memory
  is expected **contiguous**. RAM above a gap is invisible to firmware (and thus to virtually
  all software). So:
  - **Config presents RAM as a single contiguous size** (16K / 32K / 80K / 96K presets) — the
    meaningful configs. Per-socket exposure is optional authenticity; only the contiguous
    prefix matters.
  - **Page table upper-memory = one "top of contiguous RAM" watermark** (RAM pages below,
    open-bus above) — simpler than a per-region map and matches what the ROM sizes to.
  - **Subtlety (keep the model honest):** "stops at gap" is a **firmware convention, NOT a
    bus fact** — physically socketed chips above a gap DO respond on the bus. Keep the page
    table **physical-population-based** (a socketed chip responds, gap or not); let the config
    presets assume contiguous population because that's all the ROM/software uses. Do NOT bake
    "stops at gap" into the memory system — that belongs to the ROM's sizing routine, not the
    bus. With contiguous presets the two views coincide; keeping it physical leaves the door
    open for software that pokes higher RAM directly.

So **"more RAM" and "disk present" stay separable** — the config's internal-slot choice is a
**three-way: none / RAM-only / floppy+RAM**, and RAM size within the board options selects
the board's memory variant. Consequences:
- **T/38 = the no-board baseline** (fixed motherboard RAM). T/54 / T/102 / PTC-96K are
  board-provided; T/54 and T/102 are achievable on the **RAM-only** board (no disk) as well
  as the floppy board — matching selling memory upgrades independently of disk.
- Both board variants drive **RAMS** and (where banking applies) **port 0x94**.
- **Only the floppy+RAM board** adds the FDC/CTC devices and asserts **Lock** (§5c/§5e).
- Machine-assembly: page-table upper-memory population keys off "which board"; the
  interrupt/disk wiring keys off "is it the floppy variant."

### Extension board carries BOTH floppy AND RAM extension (CONFIRMED — field service manual)
The memory expansions and the floppy/CTC interface are **the same board**, not separate
concerns. One internal-extension-slot board carries **both the floppy interface AND the RAM
extension**, and it provides the different memory configurations **including the T/102 port
0x94 bank switching**. Consequences:
- **RAMS (internal-slot pin 26)** is this board's RAM-select — how its memory extension (and
  the banked 0xE000–0xFFFF region) answers memory accesses. (Resolves much of the earlier
  "what does RAMS decode" question.)
- **Port 0x94 banking lives on the extension board**, not the motherboard — the six 8 KB
  banks (or a modern 256-page module) are the board's RAM behind its own decode + bank reg.
- **PTC-96K** (16K + 64K) is a configuration of THIS board — "PTC-floppyboard" = the floppy
  board that also carries RAM.
- **Machine-assembly consequence:** RAM variant and disk interface are related but stay
  **separable** via TWO board variants — see the three-tier topology table above (none /
  RAM-only / floppy+RAM). RESOLVED: the bare motherboard is fixed base RAM; RAM-only and
  floppy+RAM boards provide the larger configs, so "more RAM" doesn't force "disk present."

### Emulation approach — a page table built at machine-assembly time
The machine object builds a **page table** over the 64 KB space when assembled (per variant
+ slot population):
- ROM pages (read-only) 0x0000–0x0FFF.
- Cartridge pages per slot population 0x1000–0x4FFF.
- Video RAM page 0x5000–0x57FF (the contention-tracked one, §4).
- **Open-bus page** for 0x5800–0x5FFF and any region the variant doesn't populate — so the
  monitor ROM's boot-time RAM-sizing probe sizes memory correctly per variant with NO
  special-casing (same presence-probe pattern as disk/CTC; the bare-machine default relies
  on this).
- RAM pages for populated regions.
- For 0xE000–0xFFFF on the floppy/RAM board: an 8 KB window into BANK1 whose half is chosen by
  the **`RAMSW` flip-flop (port `0x94` D0)** — see the CONFIRMED banking note above.
- Port `0x94` = an I/O-dispatch entry whose decode is **card-specific** (§5 banking): the original
  Philips board is a **1-bit `RAMSW` flip-flop** (only D0 — D0=1 upper 8 KB of BANK1, D0=0 lower);
  a homebrew RAM card is a wider bank register. The page table reads it when resolving
  0xE000–0xFFFF. **SaveState serializes the card's `0x94` state (RAMSW bit or bank index) + RAM.**

### Open items to confirm (from disassembly / schematic)
1. **Bank/64 KB arithmetic — SUPERSEDED.** Not a 6-bank scheme; the board is 2 × 16 KB with a
   `RAMSW` toggle on BANK1 (above). The old "+64 KB" figure was part of the wrong inference.
2. **Reset default at 0xE000–0xFFFF — CONFIRMED.** `RAMSW` flip-flop reset state = D0=0 = **lower
   8 KB of BANK1**. Matches the milestone-2 default; mechanism is the toggle, not a bank index.
3. **`0x94` decode width — CARD-SPECIFIC (§5 banking).** The original Philips board is a 1-bit
   `RAMSW` flip-flop (only D0). Homebrew RAM cards decode more bits → more banks (owner-confirmed);
   the configurable N-bank register models those. Model `0x94` per installed card.
4. **PTC-96K addressing — STILL OPEN.** The service manual describes the standard 2-bank (32 KB)
   board; PTC-96K's 16 KB + 64 KB layout is a different/larger board and its scheme is still
   unconfirmed. Also reconcile the T/102 = 80 KB figure (see the RAM-total note above).
5. **Panning/scroll register — RESOLVED: port `0x30`** (owner-confirmed from notes). Write
   **0–40**: 0 = no offset (base screen), 40 = max offset (shows the '2nd screen' to the right
   of the base screen). This pans the 40-wide visible viewport across the 80-wide buffer. The
   video device reads this port value when computing fetch addresses (write-only bank-style
   register in the I/O dispatch). Replaces the interim `Video.PanX` property.

### SAA5050 behaviour (must-implement quirks)
- 40×24 teletext display, 8 colours, semigraphics. **No high-resolution mode** on
  the stock T (a defining limitation; consequently few games).
- Generates 96 alphanumeric + 64 graphics characters + 32 control characters.
  Not reprogrammable.
- **P2000T-unique inverted-colour trick:** a byte value **160–255** displays the
  character for the value **128 lower** (normal 32–127 range) but with **inverted
  (swapped fg/bg) colours**. Clever programming uses this to change colour at more
  locations than the teletext protocol normally allows — **this is what makes games
  like Ghosthunt possible. Skipping it breaks games.**
- Known schematic erratum: in the common hobbyist teletext schematic
  (qsl.net/zl1wtt), SAA5050 **pin 27 (P0) should be tied to GND, not VCC**. Use the
  Philips originals as ground truth.
- **National/teletext character-set remaps — CONFIRMED, some on real hardware (2026-07-19/20,
  found while sourcing the keyboard matrix, UI milestone 3a):** several ASCII code points do
  NOT render as their US-ASCII glyph on the P2000T — already implemented correctly in
  `Saa5050Font.cs` (a sourced, already-shipped font table this predates ms.3a), but not
  previously called out here. Confirmed remaps found so far: **0x5B (`[`) → left-arrow glyph**,
  **0x5D (`]`) → right-arrow glyph**, **0x5E (`^`) → up-arrow glyph**, **0x23 (`#`) → £ (British
  pound) glyph** — the last one independently confirmed twice, including matching real P2000T
  hardware. This means a P2000 key whose UNSHIFTED matrix position produces byte 0x5B, for
  example, will visibly show a left arrow, not a bracket — **correct, faithful emulation**, not
  a bug (a UI-side attempt to "fix" this by rendering a literal bracket would itself be wrong).
  **`Saa5050Font.cs`'s own comment table is the canonical full list — do not re-derive or
  duplicate it here; consult it directly for any byte not listed above**, and always verify
  against that table (or real hardware) rather than assuming plain ASCII when decoding a VRAM
  byte — an earlier pass of the ms.3a investigation briefly misread 0x23 as plain `#` this way.

### 80-column mode (timing-relevant)
- Switching 40→80 columns switches the SAA5050 operating frequency **6 MHz → 12 MHz**.
- Controlled by **bit 0 of port `0x00`** (BASIC: `OUT 0,1` = 80-col, `OUT 0,0` = 40-col).
- Current mode readable on **bit 0 of port `0x70`** (0 = 40, 1 = 80).
- Default after reset: 40 columns.
- Often used with the Word Processor ROM module; required a hardware mod on stock units.

### Sound
- 1-bit, single-channel speaker (square-wave beeper).
- **BEEP drive line — I/O port `0x50`, bit 0 (CONFIRMED).** The 1-bit speaker level is the low
  bit of writes to port `0x50` — a DEDICATED sound-output port, NOT the CPOUT latch (0x10). (An
  earlier pass wrongly assumed CPOUT bit 4; corrected 2026-07-09.) Writing bit 0 sets the speaker
  high/low; square-wave tone comes from the ROM toggling it.
- **Audio-output seam — as built (machine-layer `SoundDevice`, milestone 16):** the device
  **watches writes to port `0x50` and takes bit 0 as the speaker level**, recording
  `(field-T-state, level)` transitions within each field, then at each `FieldComplete` synthesizes
  one **PCM block of 882 samples @ 44 100 Hz** (50 Hz × 882 = 44 100) by walking the recorded
  transitions, and raises a `SamplesReady(short[])` event. It uses ONE reusable buffer — a consumer
  (the UI's OpenAL sink) must copy immediately. This is the machine→UI audio seam, symmetric with
  the framebuffer/observer seams; it serializes in `.state` (adds a device block — see §3a
  versioning). `LoadState` clears any pending transitions (state is always captured at a field
  boundary with none pending; stale ones would corrupt the next block).

### Video output port (RGBS)
- RGBS via DIN-6 (DIN 45322) at the back.
- Sync polarity selectable by solder jumper pads between the **74LS00** and
  **HEF 4022BP** chips: pad A closed = non-inverted sync (from SAA5020); pad B closed
  = inverted (via 74LS00 NAND gate). **Never close both** (shorts different voltages,
  likely damages the 74LS00). For SCART / GBS-Control HDMI conversion, use
  non-inverted sync.

### Storage
- Built-in Mini-Cassette (MDCR) drive, ~42 KB per side, **6000 baud**, **phase encoding**
  (CORRECTED 2026-07-23 — was "FM encoding"; see below), directly-coupled analog circuitry.
  Treated like a floppy from the user's view (CLOAD / CSAVE / directory). **CONFIRMED against
  the owner's BASIC manual (2026-07-14):** stated capacity is **42 blocks per side** — at the
  confirmed 1024-byte data payload per block (§5b), 42 × 1024 = 43,008 bytes ≈ 42 KB, matching
  this figure exactly. Two independent sources (ROM-disassembly block-size math, the printed
  manual's stated capacity) agree — but see §5b "Tape capacity" for a NEWER 40-vs-42 conflict
  from the official T&M manual and the owner's own working implementation, which now weighs
  toward 40; not yet fully resolved.
  - **CORRECTED (2026-07-23, `docs/MDCR-implementation.md` §12, cross-checked against the
    official T&M Reference Manual):** the encoding is **phase encoding**, not FM. The owner's own
    working `MdcrDevice`/`MiniTape` implementation's phase-locked-loop recovery (a bit's value is
    the transition DIRECTION across a fixed 2-phase window, not an FM presence/absence-of-pulse
    scheme) matches the manual's Ch5.1 description verbatim, and both now outweigh whatever
    earlier assumption produced "FM-encoded" here. See §5b below and
    `docs/MDCR-implementation.md` §12 for the full citation trail — this is a correction, not a
    still-open flag, given two independent, converging primary sources.

---

## 5a. Hires overlay board (add-on modification)

A third-party / community add-on board adds bitmap graphics to the stock T. It is an
**overlay (genlock) board, NOT a replacement video mode** — the SAA5050 teletext layer
keeps running exactly as modelled; the board produces a second pixel stream at a higher
dot rate, and a mixing stage composites the two into the final RGB.

Confirmation: the MAME hires work treats it as a slot device and the reviewer's core
instruction was that *multiple layers at different horizontal resolutions* must be
rendered at the **least common multiple** of their resolutions (or via an overlay
primitive like Laserdisc overlays). That phrase — multiple layers, different
resolutions, overlay — is the whole design. Directly analogous to the contemporaneous
TRS-80 / Video Genie hires kits (e.g. 384×192 XOR'd over text).

**Do NOT conflate with the P2000M's card.** The P2000M's separate 80-column /
512×256 graphics card is a different machine's hardware. The T overlay board is the
add-on emulated in MAME PR #7577.

### How it fits the cycle-exact framework
The single-master-clock tick loop absorbs it cleanly — the overlay is just another
consumer of the SAA5020-derived master clock. Three additions:

1. **Bitmap fetch unit, parallel to the teletext fetch.** Each dot/character slot,
   alongside the SAA5020 teletext fetch, the board fetches bitmap bytes into its own
   shift register, locked to the same master timing. Layers stay pixel-aligned by
   construction — this is why the single-clock model matters; you add a second fetch in
   the same slot, not a second timing domain.
2. **Dot-level compositing stage.** After both layers yield a pixel for a dot, combine
   by the board's mixing rule. Render the internal framebuffer at the **LCM of the two
   horizontal resolutions** so neither layer loses pixels, composite there, then scale
   to output. If teletext is 6 dots/char @ 6 MHz and hires runs @ 12 MHz, the LCM raster
   is the 12 MHz grid; each teletext pixel spans two hires dots.
3. **A SEPARATE contention question (its own, distinct from VRAM).** Depends on where
   the bitmap RAM lives:
   - **Dedicated RAM on the card (most likely for an overlay):** CPU writes the bitmap
     via I/O ports or a paged window. No contention with main-RAM teletext fetches.
     BUT the same glitch mechanism reappears on the hires layer — if the board doesn't
     arbitrate, a CPU write to bitmap RAM during a hires fetch slot corrupts that hires
     pixel (overlay-layer speckle, analogous to teletext speckle).
   - **Bitmap mapped into main dynamic RAM:** widens the §4 contention condition — CPU
     accesses now collide with BOTH fetch streams.

### Board constants to extract (NOT guessable — get from MAME slot device / schematic)
- **Bitmap resolution and colour depth** (mono overlay vs per-pixel colour).
- **Register/port interface**: which I/O ports enable the layer, set mode, window the
  bitmap; bitmap RAM size and location.
- **Mixing rule**: priority vs OR vs XOR vs colour-key. XOR-over-text was common for
  these kits but is NOT confirmed for this board.
- **Hires dot clock**, and whether enabling it perturbs teletext timing (the 80-col
  mod already shows the SAA5050 clock switches 6→12 MHz; the board may share that).

### Where the answers live
- **MAME PR #7577** (author "Bekkie", apparently the board designer) is the best
  executable reference — read its slot-device source for exact ports, RAM map, and
  compositing. Since working support exists, this answers the register interface and
  mixing rule more reliably than any datasheet.
- Cross-check: **p2000t/documentation** repo (likely schematic) and the **RetroForum**
  P2000T thread (where the board was discussed).
- Implement as a **slot device** in your own design too (MAME's correct structural
  call), so the stock-T path stays clean and the overlay is opt-in.

---

## 5b. Cassette drive (MDCR) — device design

### Boot sequence & the bare-machine cassette-wait (CONFIRMED from disassembly)
What the monitor ROM does at startup, and why the cassette matters even on a bare machine:
1. **RAM check** (sizes memory via open-bus probing; stops at first gap — §5).
2. **SLOT1 cartridge check.** If a ROM cartridge (BASIC etc.) is present in SLOT1, boot into
   it (the "into BASIC" path). SLOT1-empty is detected via **open bus** in the cartridge
   region — same presence-probe pattern.
3. **If SLOT1 is empty (the default BARE machine):** the ROM shows a **prompt text on
   screen** and enters a **wait-for-cassette loop** — busy-waiting, but polling **CIP**
   (cassette-in-place, CPRIN bit 4).
4. **On cassette insertion** (CIP transitions to present): the ROM **rewinds** the tape (FWD/
   REV drive lines) and tries to **auto-load and run a 'P'-type (Program/executable) file**,
   then starts it.

### RAM power-on content is NOT zero — owner-observed (2026-07-21, real hardware test)
**Owner's report:** *"When starting up, the display shows 'garbage' imagery briefly then it
gets cleared by the monitor ROM. This means that the (video) RAM of a P2000 is not all zero's
at startup, but contains random bytes."*

This is ordinary, well-understood behaviour for the volatile SRAM/DRAM chips of this era: power-on
content is **not guaranteed to be zero or any specific value** — it's whatever charge state the
cells happened to settle into, effectively unpredictable per chip/per power-cycle. There is no
manual figure to source an "official" garbage pattern from — unlike the timing facts elsewhere
in this doc, this is a general hardware-engineering fact plus the owner's direct observation, not
something a datasheet specifies. **What IS confirmed:** the briefly-visible garbage is consistent
with step 3 above — the monitor ROM's own screen write (the cassette-wait prompt text, or
whatever init routine precedes it) is what overwrites/clears it, matching "briefly shown, then
cleared by the monitor ROM" exactly.
- **Scope — DECIDED (2026-07-23, owner): all RAM, not VRAM-only.** *"All memory contains
  garbage at startup; it is all Dynamic ram."* The owner's direct observation was of VRAM (the
  only part that visibly renders), but confirms it's DRAM and applies the fill to every
  populated RAM chip — main RAM (0x6000+), the banked window, VRAM alike.
  - **Expansion-card RAM is explicitly carved out as each device's own responsibility:**
    *"Maybe some memory on expansion cards behaves differently, but then we can make that a
    responsibility of that 'device.'"* The base-machine RAM fill described here is the default;
    a future RAM-bearing expansion device is free to differ if a hardware reason ever surfaces
    (see machine CLAUDE.md §17 for how this maps onto the existing per-device `Reset()`
    pattern).
- **Does NOT apply to:** ROM (fixed content, not volatile) or genuinely open-bus regions
  (already correctly modelled as reading 0xFF — a bus-float value, a different phenomenon from
  RAM cell content — not to be changed by this).
- **Implementation note, REFINED (2026-07-21, owner follow-up) — full detail in machine
  CLAUDE.md §17:** the RAM fill takes an **optional seed** rather than being unconditionally
  fixed. Omitted → a fixed deterministic default (what tests/CI get, keeping the locked "no
  randomness in emulation code" rule, machine CLAUDE.md §2.2, satisfied to the letter — the
  core never calls a nondeterministic API itself). Explicit seed → reproducible-with-that-seed.
  `P2000.UI` supplies a genuinely random seed at each real cold boot / app launch (ordinary
  entropy living in the UI project, outside the core), so the interactive app gets true
  unpredictable garbage every session while the machine stays a deterministic function of
  (config, seed, input events) for testing/replay purposes. `SaveState`/`LoadState` capture the
  concrete resulting bytes regardless, so reproducing a saved session was never actually at
  risk.

  **RESOLVED (2026-07-23, owner-supplied period P2000 newsletter): a WARM reset must NOT clear
  RAM.** Previously flagged as an open, owner's-call question; now CONFIRMED and already
  instructed directly to Claude Code by the owner. Real hardware's warm/soft reset button
  leaves RAM contents exactly as they were — matching the general "Z80 RESET doesn't touch
  memory chips" reasoning this doc already carried, now backed by a period source. **New fact
  from the same source:** holding the reset button too long can **damage/erase memory**,
  because P2000 RAM is confirmed **Dynamic RAM (DRAM)** and holding RESET **disables refresh**
  — DRAM cells leak charge without periodic refresh, so an extended hold causes genuine bit-rot,
  not just a stuck CPU. This also confirms the RAM is DRAM specifically (this section had
  hedged "SRAM/DRAM" — DRAM is now the sourced answer). Modeling the held-reset decay itself is
  optional trivia/polish, not required — flagged for awareness, not scoped as a feature.

### Disk-boot gate — CONFIRMED from disassembly (`Disk.asm`, owner-supplied 2026-07-13)
Runs alongside the SLOT1 checks above when a floppy+RAM extension board (§5, §5c, §5d) is
fitted — disk boot is **opt-in per-cartridge, not a blanket presence check** the way the CTC
probe (§5e) or the cassette CIP-wait are. Three conditions, checked in this order, all
required before the FDC is ever touched:
1. **`memsize == 3`** — banked RAM at `0xE000`–`0xFFFF` populated (i.e. the floppy+RAM
   extension board is fitted, §5). The ROM's own comment: *"mem at 0xE000 is on the
   extension board, so when no mem is found there are also no disk drives"* — firmware
   treats "RAM populated" and "disk exists" as the same fact, checked the same way.
2. **SLOT1 cartridge present** — bit0 of the header byte at `0x1000` (the very start of the
   cartridge region SLOT1 maps into, §5c; `1` reads the same as open-bus, i.e. "no
   cartridge" — consistent with the open-bus SLOT1-empty check above, not a conflicting
   mechanism).
3. **Cartridge requests DOS** — bit1 of the same header byte ("needs DOS"). An ordinary
   cartridge (BASIC etc.) that doesn't set this bit boots into BASIC normally (step 2 above);
   only a disk-BASIC-style cartridge triggers the disk path.

Only when all three hold does the FDC **presence probe** (§5d) run, and only if the probe
succeeds does `getdos` get called to load the 2-track DOS extension into RAM. Any failure at
any gate falls straight through to the normal flow (SLOT1-cartridge-into-BASIC or the
cassette-wait, steps 2–3 above) — on a bare machine, or with an ordinary cartridge, **the FDC
ports are never touched at boot.**

**`getdos`'s own load sequence, CONFIRMED:** FDC reset + presence probe (§5d) → 342 ms settle
(pure CPU busy-loop, no `TimingPolicy` hook needed) → `RETI` → SENSE INTERRUPT STATUS →
SPECIFY (`03 60 34`) → RECALIBRATE (`07 01`, seeks track 0) → motor-on + another 342 ms
settle → for each of 2 tracks: SEEK + READ DATA (`42 01 01 00 01 01 10 0E 00`), servicing the
semi-DMA transfer via the `0x90`-bit0 poll (§5d) → track 1 to `0xE000`–`0xEFFF`, track 2 to
`0xF000`–`0xFFFF` (**8 KB total** — an earlier "16 KB" figure was a typo; the 16 KB SLOT1
cartridge + 8 KB loaded = 24 KB total, matching "24K disk BASIC") → checks the loaded byte
at `0xE000` against `0xF3` ("system disk" signature) → cleanup always runs (CTC ch0 reset,
FDC off, RAM bank restored to 0).

**The `0xF3` signature is the OFFICIAL Philips disk-BASIC signature, CONFIRMED (owner,
2026-07-13) against a real "Disk BASIC 24K" `.IMD` image — it is NOT a general "valid DOS"
marker.** Third-party disk operating systems (e.g. JWSDOS, a user-group-developed DOS) do
not carry it and are not expected to — real JWSDOS disk images have a different byte
(confirmed to be JWSDOS's own first opcode, not a bad dump) at that offset and boot
successfully regardless. See `docs/JWSDOS-format.md` §6/§7 for the full driver-side detail
and the (still open) question of what downstream code actually does with the resulting
`sysdisk_status` value.

**Consequences:**
- The bare machine has a real, demonstrable behavior — not a dead idle loop. It exercises the
  SLOT1 open-bus check, video (the prompt text), and cassette **status polling** (CIP), then
  the drive lines and read path. Good first integration target (needs no cartridge).
- **CIP must be a LIVE transition, not a static bit.** The ROM loops reading CPRIN waiting for
  CIP to flip, so mounting a `.cas` **while the machine runs** must change the CIP bit the
  device returns in real time.
- **Cassette insertion/ejection is a RUNTIME operation — a deliberate exception to
  reset-to-apply.** Real hardware hot-swaps tapes; inserting a `.cas` is live (unlike RAM/slot
  topology, which is reset-to-apply). This is why the cassette-deck UI (§3a) has **insert/
  eject** as runtime actions. (There are NO play/stop/rewind controls — the MDCR is
  computer-controlled; the deck shows direction + read/write status only. See §3a.)
- **The 'P' file-type filter is a real `.cas`-format detail:** the auto-boot path selects on
  the per-file **type byte**. The cassette device + `.cas` parsing must expose file type so
  this ROM path resolves. (Get the type-byte encoding from the disassembly / M2000 format.)

### The key fact that reshapes everything: the MDCR is DIGITAL
The Philips MDCR (Mini Digital Cassette Recorder) is a **digital block device**, not an
analog audio cassette. You do NOT model waveforms / pulse trains the way a C64 or
Spectrum emulator must. The controller deals in bits and blocks; the `.cas` image *is*
the logical tape content. This is why both speed modes come out clean.
- ~42 KB per side, **6000 baud**, **phase-encoded** (CORRECTED 2026-07-23 — was "FM-encoded";
  see the Storage section above and `docs/MDCR-implementation.md` §12 for the full citation:
  the owner's own working PLL-based implementation and the official T&M manual both describe
  transition-direction/phase encoding, not FM), directly-coupled analog circuitry to the
  head (the analog part is below the digital interface you emulate).
- Bidirectional, **floptical-style random-ish access**: treated like a floppy. CLOAD
  searches the tape for a named program; CSAVE finds free space; a directory command
  exists. So your tape model needs a **position** and a **seek**. (This is the user-facing
  BASIC-level experience — the low-level ROM driver's own replace/append mechanism is
  simpler than "search," see below.)
- **A blank/erased tape is SILENCE — no flux transitions — not noise. CONFIRMED (2026-07-14,
  live-app bug + owner's BASIC manual).** BASIC's "Tape init" command preps a tape by writing
  enough silence that the ROM's wait-for-first-marker times out; that's what a real blank
  cassette reads back as. This matters because an earlier emulator design pass filled blank
  tape with deterministic pseudo-noise ("so the ROM's block search finds garbage until real
  data") — that was wrong: `cas_Write` does its own internal forward scan/settle check before
  writing, which real leading silence satisfies almost immediately, but pure noise never does,
  so the scan ran all the way to the physical end of the tape before giving up. Caused a real
  CSAVE failure (`Cassette fout N`) reproduced in the live app; fixed by making blank tape
  genuinely silent (a freshly-allocated all-`false` phase buffer — no seeded fill needed at
  all).

### Two interception levels = your two user-selectable speed modes
A **timing-policy strategy** on one cassette device. The device owns the `.cas` image, a
tape position, and a transport state machine; only the timing policy differs.

1. **Authentic speed — intercept at the I/O ports (bit level).** The MDCR controller
   exposes read-data, write-data, status (ready, tape-present, end-of-tape…), and
   motor/command lines at its I/O ports; the monitor ROM cassette driver bit-bangs them.
   Meter bits in/out at real 6000 baud off the **same deterministic master clock** as
   the core; ROM routines run completely unmodified. Loading takes exactly as long as
   hardware did. **This is the cycle-exact path — deterministic, replay/regression-safe.**
2. **Host speed — trap the monitor ROM cassette entry points. CONFIRMED & BUILT (milestone 18).**
   Traps at the **whole-file level: `cas_Read` (0x0552) / `cas_Write` (0x057A)** — NOT the lower
   per-block routines — because those two own the entire multi-block transfer loop and are entered
   with a clean, fully-set-up RAM contract from the cassette jump-table dispatcher (`do_cas_jump`,
   which pushes `cas_command_return` then `jp (hl)` — functionally a `CALL`). One intercept per file
   instead of per block, and no need to reverse-engineer the block routines' replace-vs-append
   search. The trap performs the whole transfer in C#, writes `cassette_error` (0x6017), and
   simulates the routine's own final `RET` (pops PC off the real stack); it does NOT replicate
   `cas_command_return`'s cleanup (motor-off, key re-enable, register restore) — that runs normally
   afterwards, since the trap only ever fires AT the entry point. Turbo-only; authentic keeps the
   port-level bit engine. **Point tape regression tests at AUTHENTIC mode.**
   - **Confirmed RAM variables (`MonitorRom.sym`):** `transfer` 0x6030, `file_length` 0x6032,
     `record_length` 0x6034, `des1` 0x6068, `des_length` 0x606A, `cassette_error` 0x6017.
   - **Block-count / valid-length math** (from `get_length_blocks` / `get_block_parameters`): block
     count = **ceil(`file_length` / 1024), min 1**; each block's real (non-padding) bytes =
     **min(remaining, 1024)** with `remaining` starting at `record_length` and only the LAST block
     partial; the source/dest pointer advances a full **1024 bytes per block regardless** (a partial
     last block still consumes a full 1024-byte slot).
   - **Replace vs append — CORRECTED (2026-07-14, owner-supplied `Cassette.asm`):** earlier
     phrasing here ("a physical forward tape search for an existing block's marker") overstated
     what the low-level ROM driver does. **`cas_block_write`'s replace-vs-append choice is
     driven entirely by two `cassette_status` RAM bits (`CST_NOMARK`, `CST_WCDON`) carried over
     from whatever cassette operation ran immediately before `cas_Write`** — there is **no
     filename comparison, and no searching for "an existing block's marker," inside
     `Cassette.asm` at all.** It simply writes MARK+DATA at the current head, replace or append
     as those two carried-over bits dictate. The "CLOAD searches for a name / CSAVE finds free
     space" user-facing behavior above is real, but it's **BASIC's own save routine, layered on
     top of this driver** (positioning the tape and priming those bits before calling in) — a
     different, higher-level source this project does not have. The emulator's turbo trap
     already matches the CONFIRMED low-level behavior: it **writes the MARK+DATA pair at the
     current head** — same net effect (overwrite in place when parked over a block, append on
     blank tape) with no search logic, because there was never any search logic to replicate.
   - **`cas_Write` does its own write-then-verify — CONFIRMED (2026-07-14, traced via direct
     ROM-entry-point tests).** After a successful write onto a blank tape, the head ends up
     roughly TWICE one block's on-tape width past where the block was written — `cas_Write`
     reads its own just-written block back (an internal verify) before returning, it doesn't
     just write and stop. Not a bug to emulate around: a same-session `cas_Read` run
     immediately afterward, without a rewind (eject/reinsert or a fresh mount), correctly finds
     nothing further — there genuinely is nothing recorded past the just-verified block.
   - **Byte-identical to authentic:** `MiniTape.TryReadBlockAtHead` / `WriteBlockAtHead` share the
     per-block encode/decode helpers with `Save()`/`LoadCasImage()`, so a turbo-written tape
     re-loads identically through the authentic path (verified both directions).

Generalize the setting to a **speed multiplier**: 1× = real baud + real mechanics,
N× = scaled, ∞ = ROM-trap instant. Nicer UI slider than a 2-way toggle.

### Authentic "slowness" is mostly mechanics, not baud
Model these in authentic mode — they're the bulk of the felt wait, and the dial that
makes it feel real:
- **Seek/search time** proportional to tape distance × speed (CLOAD scanning for a name).
- **Motor spin-up**, inter-block gaps.
- **Save is symmetric**: authentic CSAVE consumes the ROM's 6000-baud bitstream and
  writes blocks to the `.cas`; turbo CSAVE traps the save routine and writes the RAM
  block directly. Both append to the same image.

### Host-side .cas manipulation = a SEPARATE, always-fast API
Do NOT gate this behind the timing policy. Insert/eject, create-blank, write-protect,
directory browse, import/export, reorder/delete programs — host-side container operations,
always instant, independent of authentic/turbo. Keep as a distinct interface so the two
concerns don't tangle.

**"Rewind" — CORRECTED (2026-07-14): not a peer of the other entries above.** The real MDCR
has **no rewind button, only Eject** (confirmed by the owner — matches the "NO play/stop/
rewind controls" note earlier in this section). Tape position only ever resets two ways: (1)
**software-driven**, the ROM commanding REV over CPOUT (e.g. the confirmed on-insertion
rewind-then-auto-load sequence) — already modeled, not a host-side operation at all; or (2)
**implicitly at the host level**, since both `LoadCasImage`-based mount and the blank-tape
mount already start the tape at position 0/BOT — eject-then-reinsert already gets you this
for free, no dedicated action needed. A standalone host-side "Rewind" button, if ever added,
would be a pure **emulator convenience** that skips the ROM's own software rewind (same
category as turbo CSAVE/CLOAD — an authenticity trade-off to flag, not a missing physical
control being restored, since no such physical control ever existed). Unlike write-protect
(which mirrors a real physical tab the owner could snap out), rewind has no hardware analog
to build UI for — don't scope it as an equivalent, equally-deferred item.

### Determinism caveat (restated)
Authentic port-level path: deterministic, fits cycle-exact replay. ROM-trap turbo path:
not replayable in the same sense. Build a deterministic test harness against authentic.

### Constants to source — and the AUTHORITATIVE source for them
The `.cas` byte layout did NOT surface in public web search (it collides with the
unrelated MSX `.cas` and Atari CAS formats — those are DIFFERENT formats; do not use
them). Two authoritative sources, in priority order:
1. **The project's OWN documented monitor-ROM disassembly** (the cassette driver) — the
   block structure, search logic, and ROM entry points / calling convention flow
   directly from it. This is the ground truth for BOTH the port-level bit protocol AND
   the turbo trap points. Location:
   `github.com/p2000t/documentation` → `programming/Monitor Documented Disassembly`.
   *(This disassembly was created by the project author — it is the primary reference.)*
2. **M2000 source** — defines the `.cas` container layout (record/block size, header
   fields: name, length, file type; multi-program layout) and a working MDCR port model.
   Read its cassette loader/saver for the exact `.cas` framing.

Specifically still to pin down (from the two sources above, not invented here):
- `.cas` record/block size + header layout + **byte order — CONFIRMED** (traced from the monitor
  ROM `Cassette.asm`; authoritative detail in `docs/MDCR-implementation.md` §6): bytes are
  **LSB-first** (bit 0 first; sync byte 0xAA). On-tape block layout is **MARK
  (0xAA 0x00 0x00 0xAA) → gap (~81 ms, ≥70 ms) → DATA BLOCK (0xAA · header 32 B · data 1024 B ·
  CRC 2 B · 0xAA)** — header and data are ONE combined frame under ONE CRC. The 32-byte header
  field layout (name split 0x06–0x0D + 0x17–0x1E, ext, creator, size, blocks-occupied 0x02–0x03
  ÷1024) is confirmed — see MDCR guide §6. Multi-program image layout across blocks still to confirm.
- MDCR controller **I/O port addresses** + status/command **bit assignments** — **BOTH
  DIRECTIONS NOW CONFIRMED (see §5f):** output lines on **CPOUT port 0x10** (FWD/REV/WCD/WDA
  + KBIEN + printer), input/status on **CPRIN port 0x20** (RDC/RDA read pair + CIP/BET/WEN
  status + printer PRI/READY/STRAP). Route 0x10 writes and 0x20 reads to the shared
  keyboard/MDCR/printer devices.
- Monitor ROM **cassette entry points + calling convention — CONFIRMED (milestone 18, above):**
  turbo traps `cas_Read` 0x0552 / `cas_Write` 0x057A at the whole-file level.

### Useful confirmations already in hand
- ROM images (from MAME set): monitor `p2000.rom` 4 KB
  (CRC 650784a3 / SHA1 4dbb28adad30587f2ea536ba116898d459faccac); `basic.rom` 16 KB
  (CRC 9d9d38f9 / SHA1 fb5100436c99634a2592a10dff867f85bcff7aec).
- BASIC↔cassette UI surface: START (run loaded program), STOP (halt), ZOEK/search
  (show cassette index), WIS (clear cassette dialog) — these correspond to the ROM
  cassette interactions your traps will intercept.
- Loading note: P2000T BASIC identifies a program by only the **first character** of its
  name (e.g. `cload "h"` matches `"hello world"`). Relevant to your search emulation.

### Tape capacity — CONFIRMED figure (2026-07-14), NOT YET an enforced emulator limit
**(2026-07-23: this 42-blocks-per-side figure now has a genuine, unresolved conflict — see the
"Cross-check against the official T&M Reference Manual" subsection later in this section, and the
follow-up update right after the flagged discrepancy there, for the current state of the
40-vs-42 question.)**
- **CONFIRMED (owner's BASIC manual):** capacity is **42 blocks per side** — cross-checks
  exactly against the ~42 KB/side figure in the Storage section above (42 × 1024-byte block
  data payload = 43,008 bytes ≈ 42 KB). Two independent sources — ROM-disassembly block-size
  math and the printed BASIC manual's stated capacity — now agree.
- **OPEN — whether the emulator enforces this as a hard limit is unconfirmed.** The only
  capacity-adjacent number found in the machine-side build notes is an aside describing
  `MiniTape`'s buffer as "the full 1 MB phase array" (milestone-9 `.state`-serialization
  finding — mentioned only to explain what does/doesn't get saved, not a deliberate capacity
  calculation). Working the phase math from the CONFIRMED block layout above (MARK 4 B → 64
  phases; `MarkDataGap` 970 phases; combined HEADER+DATA frame 1060 B → 16,960 phases =
  **17,994 phases/block**), 42 blocks ≈ 755,748 phases — comfortably inside a 1,000,000-phase
  buffer, so it's at least big enough, but nothing sourced ties `IsAtEnd`/BET's far-end trigger
  to the real 42-block boundary specifically (only BOT, position 0, is confirmed to assert
  BET — milestone 9 finding "tape at BOT on insert"). **Needs a build-side answer:** does
  `MiniTape` stop CSAVE at a real per-side capacity (tape-full / BET-at-42-blocks), or does it
  simply run out whenever its (oversized, ~1 MB) buffer physically ends? If the latter, CSAVE
  can silently exceed real hardware capacity — not period-accurate, and worth a deliberate
  decision (enforce the real limit, or explicitly accept the simplification) rather than
  leaving it as an accident of buffer sizing.

### Cross-check against the official T&M Reference Manual (2026-07-23) — mostly strong
corroboration, TWO flagged discrepancies neither silently resolved
Chapter 5 "MINI-DIGITAL CASSETTE" (personally read page-by-page, `raw-conversion.md` PDF pp.59-77)
gives an independent, primary-source description of the same hardware this section models.

**Strong corroboration — block/record structure matches almost exactly:**
- **Mark bytes match exactly:** §5.2.2 gives the 4-byte mark as `10101010` (preamble) / `00000000`
  / `00000000` / `10101010` (postamble) — byte-for-byte the same as this project's confirmed
  `MARK (0xAA 0x00 0x00 0xAA)`.
- **Combined header+data frame size matches:** the manual's data record = sync byte (1) + header
  (32) + data (1024) + checksum (2) + post-sync (1) = **1060 bytes** — exactly this project's
  confirmed "combined HEADER+DATA frame 1060 B" figure (the manual's OWN block-diagram prose
  elsewhere states "1056 bytes of recorded data" for the same section, which doesn't sum from its
  own five listed parts — a minor internal arithmetic slip in the manual, not a discrepancy with
  this project's figure, which matches the itemized breakdown).
- **32-byte header field layout matches this project's `0x6030`-`604F` cassette file descriptor**
  almost exactly: transfer address (2B) / total length (2B) / valid length (2B) / name (8B) /
  extension (3B) / file type (1B) / data code (1B) / start address (2B) / load address (2B) /
  reserved (8B) / record number (1B) — same field order, same sizes, same RAM offsets (`0x6030`
  onward). **One thing to flag, not a contradiction:** this project's earlier notes describe the
  file name as "split 0x06–0x0D + 0x17–0x1E" (implying a second name-like field at header offset
  23-30), but the manual states header offset 23-30 (i.e. `0x6047`-`0x604E`) is **"Reserved for
  future use."** Likely explanation (not confirmed): the M2000 `.cas` container format — this
  project's OTHER authoritative cassette source, layered on top of the raw ROM record shape — may
  use those reserved bytes for its own extended metadata (e.g. a "creator" field) that has no
  counterpart in the genuine on-tape/ROM-native 32-byte header the manual describes. Whoever next
  touches `.cas` header parsing should check `MiniTape`'s actual field offsets against BOTH
  sources rather than assuming the M2000-format field at that offset is also what real hardware
  wrote to tape.
- **Gap timings match this project's timing figures closely:** start gap ≈515 ms, mark gap ≈85 ms,
  end gap ≈155 ms (§5.1.2 "BLOCK") — consistent with (not previously stated in this level of
  detail in) this project's authentic-mode timing model.
- **Error codes (§5.4) are a useful, previously-unsourced addition** — 14 named error values
  (`0x41` no tape/door open, `0x42` BOT, `0x43` checksum, `0x44` mark checksum, `0x45` EOT-on-
  write, `0x46` tape-full, `0x47` write-protected, `0x49` rewind timeout, `0x4A` tape torn, `0x4B`
  invalid function, `0x4C` EOT-on-read, `0x4D` no mark found, `0x4E` no record found) — worth
  cross-checking against `cassette_error` (`0x6017`)'s actual values in the disassembly next time
  that code path is touched; not yet done as part of this pass.

**Flagged, NOT resolved — capacity: 40 blocks/side (manual) vs. 42 blocks/side (this project's
prior CONFIRMED figure, sourced from the owner's BASIC manual).** §5.2 states plainly: *"Each
track (or side) of a cassette is divided into 40 blocks of information. A file may comprise
between 1 and 40 blocks."* This directly conflicts with the existing "42 blocks per side" figure
above, which was itself cross-checked against an independent ~42 KB/side figure derived from
ROM-disassembly block-size math. Both figures now have primary-source backing from DIFFERENT
manuals (this T&M Reference Manual vs. the owner's BASIC manual) — genuinely unresolved, not a
transcription slip on either side as far as this pass can tell. Possible explanations, none
confirmed: (a) the 40-block figure is a conservative/nominal spec while real tape length allows a
couple more blocks of physical margin; (b) the two manuals describe different tape stock/formats;
(c) one of the two source manuals is simply wrong. **Not silently resolved either direction** —
flag for the owner to arbitrate if the exact per-side block ceiling ever becomes load-bearing
(e.g. an emulator-enforced capacity limit, per the still-open item above).

**UPDATE (2026-07-23) — a THIRD source now weighs in, shifting the balance toward 40, still not
silently resolved.** `docs/MDCR-implementation.md` (the owner's actual working `MdcrDevice`/
`MiniTape` implementation guide, re-added to the docs folder this pass) §6 independently derives
**"~40 data blocks per tape with slack"** from the real block/gap byte-size constants in the
working code — arrived at completely independently of the T&M manual (it's sized from the
implementation's own `WriteData`/gap-timing constants, §12 of that doc has the full arithmetic).
That makes it **40-and-40 (manual + working implementation) vs. 42 (the BASIC manual)** — two
independent sources now agree with each other and against the third. This meaningfully shifts
where the evidence points, but the BASIC manual is a real primary source too (and may describe a
different thing — e.g. a practical recommended limit rather than the physical block ceiling), so
this remains flagged for the owner to arbitrate rather than corrected outright.

**Flagged, NOT resolved — encoding scheme: "phase-encoded" (manual) vs. "FM-encoded" (this
project's prior description).** §5.1 states cassette data is recorded *"in phase-encoded form;
that is, a one bit is represented by the transition between a low value and a high value on the
read/write head line, and a zero bit by a high-low transition"* — this is a description of
**phase encoding (Manchester-style)**, not frequency modulation. This project's existing write-up
above (Storage section) describes the cassette as "**FM-encoded**." The two encoding schemes are
electrically different (FM keys off pulse-presence-per-cell; phase/Manchester encoding keys off
transition DIRECTION at each clock edge) even though both are "self-clocking, one transition per
bit" schemes that can look superficially similar. Note this is the OPPOSITE direction of the
disk-encoding discrepancy above (§5d: manual says disk is FM despite being called
"double-density," which conventionally implies MFM) — here, for the cassette, the manual is
explicit about phase encoding while this project's existing description says FM. Since the
authentic-mode cassette timing model in this project works at the RDC/RDA (self-clocking
read-clock + data level) port abstraction (§ above) rather than modeling flux transitions
directly, this discrepancy is not currently load-bearing for the emulator's behavior — but it
should be corrected in this doc's own prose (the "FM-encoded" phrase) rather than left as a
silent error, since the manual is a substantially more specific and P2000-hardware-specific
source than whatever prior assumption produced "FM-encoded." Flagged for a future editing pass
to fix the word, not resolved by editing it out in the same breath as writing this note.

---

## 5c. Slot / bus-expansion model (build this ONCE)

The P2000T/M is a **bus-expansion machine**. **Build the slot/bus model once and every
add-on drops into it:**
- Devices attach to the bus and claim **I/O port ranges** and/or **memory windows**.
- An empty/absent slot reads **open bus (0xFF)** — this is how the monitor ROM's presence
  probes naturally detect "device absent."
- Same slot-device structure MAME insists on.

### THREE physical slots — NOT interchangeable; typed by bus discipline (CONFIRMED)
The T and M motherboards have **three expansion slots**, of two/three different kinds. The
slot abstraction must distinguish them — "slot" is not one uniform thing.

| Slot | Access | Bus discipline | Purpose |
|------|--------|----------------|---------|
| **SLOT1** | external | **memory-mapped** (full addr/data, MREQ) | ROMs: BASIC, Database manager, etc. Maps into cartridge region **0x1000–0x4FFF** (§5). Devices here appear in the **page table**. |
| **SLOT2** | external | **I/O-mapped** (8 addr + 8 data pins, IORQ, INT, RES) | Expansion hardware. Devices live in **I/O port space**, register into the **port dispatch**, and can raise **INT** (→ interrupt aggregator §5e). |
| **Internal extension** | internal | **full bus** (MREQ + IORQ + INT + addr/data) | Floppy/CTC card. **Populated in the M; empty in the T** but able to take a floppy card to add disk support to a T. Home of FDC (§5d) + CTC (§5e). Likely needs I/O ports AND interrupts AND possibly a memory window (boot ROM / shared buffer) — hence "full bus" and why it's internal. |

**Design consequence — slots are TYPED, at least two interfaces (not one):**
- **Memory-mapped slot** (SLOT1, and the memory side of the internal slot): registers into
  the **page table**; sees MREQ-driven reads/writes over the 16-bit address; occupies an
  address range.
- **I/O-mapped slot** (SLOT2, and the I/O side of the internal slot): registers into the
  **I/O port dispatch**; sees IORQ-driven reads/writes over an 8-bit port address; can
  assert **INT** into the aggregator.
- Both share the **common device interface** (Reset — driven by the slot's RES pin — plus
  SaveState/LoadState), but attach to **different buses**. This maps straight onto the two
  dispatch paths already in the design: the port-dispatch path (SLOT2) and the memory-page
  path (SLOT1) are distinct, and the CPOUT fan-out (§5f) already established multi-listener
  port writes.
- The **internal slot is a hybrid** exposing both disciplines (the floppy/CTC card needs I/O
  ports + interrupts + possibly a memory window) — model it as a full-bus slot combining the
  two interfaces above.

Devices identified for this model: SLOT1 ROMs (BASIC, DB manager); SLOT2 I/O cards
(comms, parallel I/O, EPROM programmer, CP/M?); internal-slot floppy/CTC (§5d/§5e); hires
overlay (§5a) and 80-col card (bus TBD — likely internal/full-bus given video access);
cassette/MDCR (§5b — on-board, NOT a slot).

### PIN LAYOUTS
**SLOT1 / PORT1 — CONFIRMED (memory-mapped ROM slot).** 30-contact PCB edge connector,
pins 1A–15A and 1B–15B (A/B = PCB sides). Active-low: CARS1, CARS2, MRQ, NMI.

| Pin | B side | A side |
|-----|--------|--------|
| 1 | +5V | NMI |
| 2 | D1 | D0 |
| 3 | D3 | D2 |
| 4 | D5 | D4 |
| 5 | D7 | D6 |
| 6 | A1 | A0 |
| 7 | A3 | A2 |
| 8 | A5 | A4 |
| 9 | A7 | A6 |
| 10 | A9 | A8 |
| 11 | A11 | A10 |
| 12 | CARS1 | A12 |
| 13 | — | CARS2 |
| 14 | MRQ | — |
| 15 | Gnd | Gnd |

Reading:
- **13 address lines A0–A12 + 8 data D0–D7 + MRQ** (active-low MREQ) → memory bus, as typed.
- **CARS1/CARS2 = CARtridge Selects (active-low), decoded from A12 & A13** to give the full
  **16 KB in two 8 KB banks**. 16 KB = the whole cartridge region 0x1000–0x4FFF; one SLOT1
  card covers it as two 8 KB halves (two ROM chips, or one 16 KB ROM split by the selects).
- **A13 is NOT on the connector** (only A0–A12). So A13 decoding + region base happen on the
  **motherboard**; the card sees A0–A12 + the two selects. In the emulator: the **page table
  drives CARS1/CARS2** based on which 8 KB half the access falls in; the card responds to the
  active select.
- **No RD/WR pins** → **read-only ROM slot**. SLOT1 devices are **read-only pages** in the
  page table. (Resolves the earlier "pure ROM vs writable" question: pure ROM.)
- **NMI is exposed on SLOT1** (pin 1A, active-low) → a cartridge can pull NMI (same
  non-maskable path as the soft-reset button, §5). **Interrupt aggregator note: NMI has SLOT
  sources too, not only the front-panel button.**
- **CARS1 selects 0x1000–0x2FFF; CARS2 selects 0x3000–0x4FFF** (CONFIRMED). The page table
  drives the matching select for whichever 8 KB half the access falls in; the card responds.

**Implemented (milestone-12) — the typed slot model as built:**
- SLOT1 is now a first-class typed object (`machine.Slot1`, an `IMemorySlot`), not a hidden raw
  array. ROM loading moved OUT of the page table into `Machine`, which constructs a
  `Slot1Cartridge` from `config.Slot1CartridgePath` and passes it to the page table as an
  `IMemorySlot? cartridge` constructor parameter (default null = empty slot).
- **Open-bus is 0xFF, not 0x00.** A cartridge image shorter than its 16 KB region reads **0xFF**
  (unprogrammed-EPROM / open-bus) for every address beyond the image length — `Slot1Cartridge`
  allocates only the image bytes and returns 0xFF above them. (Harmless in practice for BASIC.bin,
  which is exactly 16 KB, but correct for short images.) Consistent with the "read-only ROM slot"
  above; just pins the value.
- **Register-once (no runtime unregister).** Because topology is reset-to-apply (§3a locked
  decision), a slot card's port/memory listeners register at machine-assembly time and live for
  the machine's lifetime — there is no runtime slot-swap. `IIoSlot.RegisterPorts` is the only seam
  needed; no `UnregisterPorts` exists or is required.

**SLOT2 / PORT2 — CONFIRMED (I/O-mapped, full I/O bus).** 30-contact PCB edge connector,
pins 1A–15A / 1B–15B. Active-low: RES, RD, M1, IOREQ, INT, WR.

| Pin | A side | B side |
|-----|--------|--------|
| 1 | 2.5 MHz (CPU clock) | +5V |
| 2 | D0 | D1 |
| 3 | D2 | D3 |
| 4 | D4 | D5 |
| 5 | D6 | D7 |
| 6 | A0 | A1 |
| 7 | A2 | A3 |
| 8 | A4 | A5 |
| 9 | A6 | A7 |
| 10 | RD | RES |
| 11 | IOREQ | M1 |
| 12 | INT | Gnd |
| 13 | BEEP | +12V |
| 14 | −12V | WR |
| 15 | Gnd | Gnd |

Reading (CONFIRMED):
- **Full I/O bus:** D0–D7, **A0–A7** (8-bit port address), RD, WR, **IOREQ**, **M1**, INT,
  RES (all active-low) + **2.5 MHz CPU clock**. I/O-space slot as typed — devices register
  into the **port dispatch** and can assert **INT**.
- **A0–A7 only = pure 8-bit I/O port decode.** SLOT2 cards decode within the 256-port I/O
  space via A0–A7 — exactly the Z80 port-address low byte. No higher address lines.
- **M1 IS PRESENT** → **M1 + IOREQ = interrupt-acknowledge**. A SLOT2 card can
  **participate in the IM2 daisy chain and drive a vector on the data bus during int-ack**
  (§5e). Confirms the daisy chain is NOT M-only / internal-only — a plain SLOT2 expansion
  card can be an IM2 source. Reinforces the general `DaisyChain` module any slot registers into.
- **Separate RD and WR** → SLOT2 cards are **read/write** (unlike SLOT1's read-only ROM).
- **2.5 MHz clock on the slot** → cards can clock synchronously with the Z80 (needed by
  CTC/SIO/counter cards).
- **±12V rails** (13B +12V, 14A −12V) → analog-capable cards (e.g. RS-232 line drivers).
- **BEEP on 13A** → the 1-bit sound line is routed to SLOT2; a card can tap/drive audio.
  Note for the sound device: BEEP is not purely internal. (The CPU drives this line by writing
  **port `0x50` bit 0** — see §5 Sound.)
- **No NMI on SLOT2** (unlike SLOT1). SLOT2's async CPU line is INT only.

**SLOT2 Serial card — CONFIRMED (owner-supplied, 2026-07-21) port map:**

| Port | Register | Direction | Notes |
|------|----------|-----------|-------|
| `0x40` | UART data | R/W | byte in/out |
| `0x41` | UART control | R/W | UART control register |
| `0x61` | Handshake lines | R/W | serial handshake signals |
| `0x62` | DIP switches (×8) | IN | live read of the 8 physical switches — **bus-visible, not just an install-time preference.** A driver could in principle query the configured baud/handshake at runtime, same as any other status port. |

A plain UART, **not** a Z80 SIO — contrast with the M2200's onboard serial interface (§ "Known
real card" below), which uses the more capable SIO chip. Different chip, different register
shape: do not share a device class between the two, even though both are conceptually "a serial
port." Because `0x62` makes the DIP switches bus-readable, their value must be **both** a
`MachineConfig`-level topology setting (a physical switch doesn't move mid-session — reset-to-
apply, same discipline as §3a generally) **and** a live `IIoSlot`-registered read-only register
backing that same value, so the two representations can never drift apart.

**The official Philips manual's own "I/O SLOT" appendix — read (2026-07-23), flagged rather than
reconciled, NOT yet folded into the pinout tables above.** The manual (Ch1.5 "I/O SLOT": *"the
second slot in the top of the main unit, behind the ROM key can hold one of several different
modules to perform I/O"* — this is unmistakably describing SLOT2) has its own **Appendix C — I/O
SLOT PINNING**, which lists pins **1–9 and 20–24 only** (not a contiguous 1–15/30-contact
layout) with **CCITT V.24 circuit-style names** — Transmit data (103), Receive data (104), Request
to send (105), Clear to send (106), Data set ready (107), Signal ground (102), Carrier detect
(109), Select standby (116), Data terminal ready (108/1, 108/2), Calling indicator (125), data
signalling rate selector (111), terminal equipment transmitter signal element timing (113). This
reads as the **RS232-circuit-function view of SLOT2's pins as wired for a serial card**
specifically (i.e. what a modem/serial SLOT2 card sees data/handshake-wise), not a full
generic-connector pinout — it does not list D0–D7, A0–A7, RD/WR/IOREQ/M1/INT/RES, the clocks, or
the power rails that the pin-numbered SLOT2 table above (sourced from the Field Service Manual)
does cover. The two tables use **incompatible pin-numbering schemes** (1–15 A/B-side vs. a flat
1–24) and can't be merged mechanically without a physical connector diagram to correlate them —
flagged as an open cross-reference for whoever eventually needs the exact SLOT2-to-RS232-circuit
pin mapping (e.g. wiring a real SLOT2 serial card's DB25 breakout), not resolved here.

**Internal extension slot — CONFIRMED (full Z80 bus: memory + I/O).** 40-pin connector, 2
rows of 20 (row 1 = odd pins, row 2 = even pins). Pins 27–37 active-low. **Home of the
floppy/CTC card** (populated in M, optional on T).

| Pin | Function | Pin | Function |
|-----|----------|-----|----------|
| 1 | Gnd | 2 | Gnd |
| 3 | D0 | 4 | D1 |
| 5 | D2 | 6 | D3 |
| 7 | D4 | 8 | D5 |
| 9 | D6 | 10 | D7 |
| 11 | A0 | 12 | A1 |
| 13 | A2 | 14 | A3 |
| 15 | A4 | 16 | A5 |
| 17 | A6 | 18 | A7 |
| 19 | A8 | 20 | A9 |
| 21 | A10 | 22 | A11 |
| 23 | A12 | 24 | A13 |
| 25 | A14 | 26 | RAMS (RAM select) |
| 27 | MRQ | 28 | RD |
| 29 | INT | 30 | WR |
| 31 | IOREQ | 32 | WAIT |
| 33 | M1 | 34 | RES |
| 35 | Lock | 36 | RFSM (= RFSH, refresh) |
| 37 | DEW / R2425 | 38 | 2.5 MHz |
| 39 | 5 MHz | 40 | T4M (4 MHz clock) |

Reading (CONFIRMED):
- **Full memory + I/O bus:** D0–D7, **A0–A14** (15 addr, 32K reach), **MRQ, RD, WR, IOREQ,
  M1**. Reaches BOTH memory space (MRQ) and I/O space (IOREQ) — the hybrid/full-bus slot
  §5c predicted. The floppy/CTC card needs I/O ports (FDC regs, CTC channels) AND a memory
  window (boot ROM / buffer); this provides both.
- **★ Pin 37 DEW/R2425 = the 50 Hz keyboard-scan interrupt signal**, exposed on the slot
  (DEW on T, R2425 on M — model-specific name). **This is the physical wire behind the
  CTC-vs-video tick fallback (§5e):** the floppy/CTC card taps the 50 Hz signal as a
  clock/trigger into a CTC channel; once the CTC runs it becomes the system tick. Connects
  §5e's two tick sources on this connector.
- **RAMS (26)** = RAM/card memory chip-select — how the card maps RAM/ROM (boot ROM, shared
  buffer) into memory space. TO CONFIRM: what address range it decodes, and whether card
  memory appears in the normal map or a paged region.
- **WAIT (32)** — the card can **insert CPU wait states** (first slot to expose WAIT). A slow
  FDC/memory card stalls the Z80. The core already plumbs WAIT (CLAUDE.md) — this drives it.
- **M1 + IOREQ** → int-ack → full **IM2 daisy-chain** participation (as expected for the CTC).
- **RFSM (36) = RFSH** (refresh strobe) — a card can distinguish refresh from real memory
  access (DRAM cards / precise bus watchers).
- **Three clocks: 2.5 MHz (38, CPU), 5 MHz (39, master/dot-related), T4M 4 MHz (40).** The
  4 MHz line strongly supports the **µPD765 FDC** identification (§5d) — a dedicated FDC
  data-separator/bit clock available right on the slot.
- **RES (34), INT (29)** as expected. **No NMI** on this connector → internal card is an
  INT/IM2 source, NOT an NMI source.
- **Lock (35)** — NOT a standard Z80 line. **CONFIRMED (T/M field service manual): "Lock =
  disables interrupt generation if extension board is used."** It is a **hardware interlock**
  that mutes the onboard (video / 50 Hz) interrupt when the extension board is present, so
  the board's CTC becomes the INT source instead of both contending. See §5e — this is the
  hardware arbiter behind the CTC-vs-video-tick selection (not merely a firmware probe).

**TO CONFIRM (internal slot):**
- **RAMS (26):** address range it selects; card memory in normal map vs paged region.
- **Lock (35):** does it gate NMI too or only the maskable video INT? Board-driven or
  motherboard-decoded from board presence? (Function itself confirmed — see above.)

**Daisy-chaining on the M — CONFIRMED (Field Service Manual, "INTERCONNECTIONS", §3.8.1/§3.8.11,
owner-supplied page references, 2026-07-23), CORRECTS an earlier mis-scoping this pass:**
- **§3.8.1 "CPU BOARD TO VIDEO OR EXTENSION BOARD"** is this exact connector (its pinout table
  matches the one above pin-for-pin: D0–D7, A0–A14, RAMS/RAMS2, MRQ, RD, INT, WR, IOREQ, WAIT,
  M1, RES, LOCK, RFSH, DEW/R2425, the three clocks). The connector's own name confirms it goes
  to **either** a video board **or** an extension board directly — i.e. on the **T**, a
  floppy/RAM extension card can plug straight into this slot (as already documented above); on
  the **M**, the **video board** occupies it instead.
- **§3.8.11 "VIDEO BOARD TO EXTENSION BOARD"** is a SECOND, separate 40-pin connector — on the
  video board itself — with nearly the same pinout (signals suffixed `U`, e.g. `DU0-DU7`,
  `AU0-AU14`, `RAMSU`, `MREQU`, buffered/passed-through copies of the CPU-side bus) wired to a
  further downstream extension board. **This is a real daisy-chain, not a single-board slot:**
  on the M, the physical order is **CPU board → video board → a further extension board**,
  all sharing the same logical Z80 bus (memory + I/O), the video board acting as a pass-through/
  buffer stage rather than terminating the chain.
- **Known real card that plugs in HERE, not SLOT2 — CORRECTED (owner, 2026-07-23, walking back
  the previous pass's "SLOT2" placement): the M2200 multi-function board.** It has a
  **real-time clock (RTC) with a small battery-backed memory** for time, date, and alarms. On a
  T it would plug directly into this internal slot (§3.8.1); on an M it would plug into the
  video board's downstream connector (§3.8.11) behind the video board. Either way it's this
  internal/daisy-chained slot family, **not SLOT2** (SLOT2 is a separate, external, I/O-only
  connector — see above — SLOT2 cards don't reach memory space, and an RTC's battery-backed
  memory needs a memory-mapped or port-mapped window either way, but the daisy-chain is
  specifically documented for THIS slot, not SLOT2).
  - **RAM-behavior relevance unchanged from the previous (mis-scoped) note:** the RTC's
    battery-backed memory is non-volatile by design — the whole point of the battery is to
    survive power loss with contents (time/date/alarms) intact — so it must NOT get the
    non-zero-garbage-at-cold-boot treatment the rest of RAM gets (machine CLAUDE.md §17,
    2026-07-23 RAM power-on finding; this doc §5b). A future RTC device's `Reset()` should
    preserve/restore it across power cycles instead (analogous to a PC's CMOS RTC). Both the
    internal-slot family and SLOT2 are deferred (§14) — this is captured for when that work
    starts, not an active implementation item now.
  - **Full port map — CONFIRMED (owner-supplied M2200 manual, 2026-07-21).** Full device-level
    detail (register semantics, host-transport design questions, open items) lives in
    **`docs/M2200-implementation.md`** — summary here, following the same split already used for
    the cassette (`docs/MDCR-implementation.md`):

    | Port(s) | Feature | Notes |
    |---------|---------|-------|
    | `0x80`-`0x83` | **CTC2 — a SECOND Z80 CTC (Z8430), CONFIRMED (owner-supplied full M2200 manual, 2026-07-23)** | Not previously documented anywhere in this project. Mostly dedicated to serial baud-rate generation (ch0 = RS232 Rx, ch1 = RS232 Tx, ch2 = optional RS422 baud/free, ch3 chained off ch2). Sits LOWEST in the M2200 daisy chain, behind the SIO. Full detail in `docs/M2200-implementation.md` §3.5/§4. |
    | `0x84`/`0x85` | Serial — RS232 (data/control) | **Zilog Z8440 (Z80-SIO/0) channel A** — chip identity CONFIRMED via parts list. NOT the same chip as the SLOT2 Serial card's plain UART above — do not share a device class between them. Daisy-chain participation CONFIRMED (sits between CTC1 and CTC2). |
    | `0x86`/`0x87` | Serial — RS422 (data/control) | Z80 SIO channel B. |
    | `0x8C`/`0x8D`/`0x90`-`0x93` | FDC | **Same port assignment as the FDC already documented in §5d** — but chip identity is revision-dependent: **the first ~100 M2200 units shipped with a µPD7265** (Sony-compatible format), **later units with the µPD765** (CONFIRMED, M2200 manual) — a µPD7265 unit is explicitly out of scope for now. **Connector CONFIRMED with 4 drive-select lines (`DRISEL0`-`3`)**, decoded from the chip's native US0/US1 via an external 2-to-4 decoder, gated by motor-on — a materially richer connector than the "2 drives" figure above; whether the plain single-purpose board's own connector matches is still open. Full detail (control-register bit table, clock/formatting caveat, drive-timeout watchdog) in `docs/M2200-implementation.md` §2.1. |
    | `0x94` | RAM bank-switch (`RAMSW`) | **Same port as the plain floppy+RAM board's bank-switch** (§5 memory). **Bit-width now CONFIRMED for M2200 specifically: 3 bits, 6 banks × 8 KB (values 0-5, 6/7 alias to bank 0, default bank 0)** — the "homebrew wider decode" case §5 memory already anticipated. Corroborates (does not prove identical to) the `T/102` 80 KB variant's total. Full detail in `docs/M2200-implementation.md` §2.2. |
    | `0x95`/`0x96`/`0x97` | RAM disk (track/sector/data) | Genuinely new — a separate device from the FDC, **not** a media variant of it (owner-confirmed: "RAM disk is port driven"). **Geometry CONFIRMED:** 256 B/sector, max 16 sectors/track, 64 KB or 256 KB total (track register 4 or 6 bits respectively). Contents confirmed to survive a reset-BUTTON press specifically (dedicated refresh-continuity circuit); full power-off persistence not stated. Full detail in `docs/M2200-implementation.md` §3.2. |
    | `0x98`-`0x9B` | Centronics (data/status/strobe-on/strobe-off) | Strobe ports are access-triggered — read or write, same effect, data byte irrelevant. Status bits CONFIRMED: bit4 Error, bit3 Printer On, bit2 Paper Out, bit1 Busy, bit0 ACK. |
    | `0x9C`/`0x9D` | RTC (select/data) | **Chip CONFIRMED: Hitachi HD146818** (MC146818-family — the same RTC used as the IBM PC/AT's CMOS clock), cross-confirmed via both owner statement and the M2200 manual's own parts list. **Full Register A/B/C/D bit layout now CONFIRMED** (update-in-progress, periodic/alarm/update-ended interrupt enables and flags, BCD/binary + 12/24-hour mode, crystal-select code) — see `docs/M2200-implementation.md` §3.1 for the complete table. IRQ wired to **CTC1 channel 2** (cross-confirms the already ROM-disassembly-sourced IM2 vector table, §5e). `Reset()` must preserve the battery-backed contents (see the non-volatility note above). |

    **Design consequence:** M2200 = two features reused (port-compatible, though the FDC's exact
    chip and the bank-switch's bit-width turned out to be board-specific facts of their own, now
    resolved above) from the plain floppy+RAM board (FDC, RAM bank-switch) plus **five** features
    unique to this board (RAM disk, RTC, Serial/SIO, Centronics, and a previously-undiscovered
    second CTC). None of the five new features overlaps SLOT2's address space or the plain
    board's FDC/bank-switch ports. The one open cross-card question is whether M2200's
    Centronics interface shares its register shape with a future SLOT2 Centronics card (SLOT2
    Centronics ports not yet sourced). See `docs/M2200-implementation.md` for the full write-up,
    including the full 10-member IM2 daisy chain (§4) and board-level I/O decode/connector/parts
    detail (§5).

---

## 5d. Floppy disk controller (FDC) — add-on card

### Extension-board I/O map, FDC control port, CTC channels — CONFIRMED (ROM disassembly + service manual)
Port bases and bit layouts are from the **project's monitor-ROM disassembly (authoritative)**; the
field-service manual (Fig 4.12) corroborates the devices and adds the analog/mechanical detail.

**I/O port map** (extension-board I/O select `IOE` = A7 high, ports 0x80–0xFF):

| Port(s) | Device | Notes |
|---------|--------|-------|
| `0x88` | CTC ch0 | timer / disk interrupt (highest priority) |
| `0x89` | CTC ch1 | disk **not-ready** interrupt |
| `0x8A` | CTC ch2 | **communication (serial / I/O) interrupt** |
| `0x8B` | CTC ch3 | keyboard-scan interrupt — 20 ms / 50 Hz **system tick** |
| `0x8C` | FDC status (`DSKIO1`, IN) | µPD765 main status; **bit 7 = RDY** |
| `0x8D` | FDC data/status (`DSKSTAT`, IN/OUT) | **bit 2 = REQ** (data request) |
| `0x90` | FDC control (`DSKCTRL`, IN/OUT) | OUT: control latch — bits below. IN: bit0 = byte-ready (semi-DMA per-byte poll target during a track transfer) |
| `0x94` | `RAMSW` bank flip-flop | §5 memory (card-specific: 1-bit original / wider homebrew) |

The CTC's four channels are **one port each, `0x88`–`0x8B`** (A0/A1 select). This **resolves the
earlier port conflict** — the service-manual "CTC 0x8C–0x8F" text was wrong; `0x88`–`0x8B` is
correct, and the FDC lives at `0x8C`/`0x8D` (status/data) + `0x90` (control), superseding the
manual's "FDC 8D–8F."

**Independently corroborated (2026-07-23) by the official Philips T&M Reference Manual's own
Appendix B ("PORTS")** — a different, primary-source document from the field-service manual
referenced above, personally read page-by-page (`raw-conversion.md`, PDF pp.120-121). Its port
table states: `88`/`89`/`8A`/`8B` = "Counter timer circuit channel 0/1/2/3" (one port per
channel, exactly as this section already has it) and `8C-8D` / `90-93` = "Flexible disk
controller" (grouping status+data and the control/DMA-req range together, consistent with this
project's `0x8C`/`0x8D`/`0x90` — this manual doesn't break the FDC ports down further than that,
so it neither confirms nor contradicts the exact `0x90`-only vs `0x90`-`0x93` control-range
detail already sourced from the ROM disassembly). Also confirms from the same appendix: `0x50`-
`0x5F` = Bell (matches the sound device's port `0x50` bit 0, §5 Sound), `0x30`-`0x3F` = Video
horizontal-scroll (matches the confirmed pan register, port `0x30`, §5 Memory/video), and `0x70`-
`0x7F` = video wait-lock, disabling the video-CPU-disable interrupt during disk/cassette transfer
— an existing but previously uncited mechanism worth flagging for the contention model (§4): real
hardware apparently disables the video-memory-access-lockout interrupt during disk/tape transfers
specifically, which this project's contention model does not currently special-case. Not
acted on here — flagged for whoever next touches the disk/cassette transfer path to check whether
authentic-mode contention should be suppressed during an active disk/cassette I/O burst.

**Extension Board signal listing — CONFIRMED (2026-07-23, manual's Appendix A, PDF pp.117-119),
matches this project's model closely, with signal NAMES now independently sourced from a second
document (previously only from the Field Service Manual):** `FDCS`/`FDCSEL` (select the µPD765,
ports `0x8C`-`0x8F`), `CTCSEL` (select the CTC, ports `0x88`-`0x8B`), `SIOSEL` (ports `0x80`-
`0x83` — on the plain board this is unused/reserved, since only M2200 populates a chip there;
matches `docs/M2200-implementation.md` §3.5's CTC2 occupying that same range on M2200 instead of
an SIO), `USAS` (ports `0x84`-`0x87` — "Select 8215 USART communications control"; almost
certainly a scan/print artifact for the industry-standard **8251** USART, flagged verbatim in the
transcription and not corrected here since no schematic confirms which chip the plain board
actually used, if any — SLOT2's own serial card above is a plain UART, unrelated to this internal-
slot signal), `MOTORON` (motor-on to disk drive — matches `MOTOR` bit3 of the `0x90` control latch
above), `STEP` + `DIRECT` (step-one-track + track direction — matches the stepping-motor model,
§2.1 above), `TRACK00` (matches the confirmed track-00 sensing), `WPROT` (write-protect sense —
matches the confirmed write-protect signal), `INDEX` (sector-hole detector pulse, once per
revolution — matches Ch2.1's LED/photodetector description), `READDATA`/`WRITEDATA` (serial data
lines to/from the drive), `WCK` (4 MHz timing signal — matches the internal-slot connector's `T4M`
4 MHz line already confirmed above), `FNR` ("Floppy not ready; active if, after 'head load', no
index pulses are received from disk" — a hardware not-ready condition distinct from, but likely
the same physical event that feeds, the CTC ch1 "disk not-ready" interrupt above), `FCS`/`FCDK`/
`FCDR`/`FCINT`/`FRES` (floppy-controller select/data-acknowledge/data-request/interrupt/reset —
these look like the internal glue-logic handshake between the FDC chip and the rest of the board,
one level below the CPU-visible `0x8C`-`0x93` ports; not previously named in this project's docs).
No new port-address information beyond what's already confirmed above, but the signal names
independently corroborate the functional model (motor line, step/direction, track-00, write-
protect, index, 4 MHz clock) without requiring any correction.

**CTC control word** (write to a channel port with bit0 = 1):
bit0 `CTRLWRD` (1 = control word) · bit1 `RESET` (1 = reset CTC) · bit2 `TCNEXT` (1 = next byte is
the time constant) · bit3 `CLKSTRT` (1 = start on next clock edge, 0 = immediately) · bit4 `ACTTRG`
(1 = count/trigger on rising edge) · bit5 `PRE256` (1 = prescaler 256, 0 = 16) · bit6 `CNTMD`
(1 = counter, 0 = timer) · bit7 `INTEN` (1 = generate interrupt). A byte with bit0 = 0 written after
a `TCNEXT` control word is the **time constant**.

**FDC control latch `0x90` (`DSKCTRL`) — OUT and IN, per the ROM disassembly:**

OUT (control latch): bit0 `ENABLE` (1 = read/write FDC registers) · bit1 `Count` (terminal
count) · bit2 `RESET` (1 = FDC reset) · bit3 `MOTOR` (1 = on) · bit4 `SELDIS` (1 = select
disabled — **only on the P2C2 disk board**). (The service manual described bit0 as a
data-vs-command transfer select and folded enable+reset into bit2; where the two disagree,
the **ROM disassembly is authoritative** for emulation.)

**bit2 `RESET` only actually takes effect from the chip's Idle phase, CONFIRMED (real-ROM
boot-test diagnosis, 2026-07-22).** Two independent, ordinary (non-buggy) real code sites write
this bit while a command is still active: `read_status_bytes` writes RESET|MOTOR (`0x0C`) while a
SENSE INTERRUPT STATUS result phase is still pending readout, and `read_track` writes
RESET|MOTOR|ENABLE (`0x0D`) to arm the transfer immediately AFTER dispatching READ DATA (chip
already in ExecutionPhase). Neither call resets an in-flight command — two independent real sites
doing this in working firmware is strong evidence the bit has no effect once a command is already
executing, only from Idle. Model `WriteControl`'s RESET handling as a no-op outside `Phase.Idle`.

**IN — CONFIRMED (owner-supplied `Disk.asm`/`getdos` disassembly, 2026-07-13):** reading
`0x90` returns a **different register from the OUT-direction latch above**. During a
track-read/write execute phase, JWSDOS's driver polls `IN A,(0x90)` and tests **bit0** (via
`RRA` into carry) as the semi-DMA byte-ready flag: set → one data byte is available at
`0x8D` (consumed via `INI`); clear → keep polling. This can't be a read-back of the just-
written OUT value — the control latch's live value during a transfer (`0x0D`, `ENABLE`=1)
has bit0 set permanently, which would make the poll never wait. Most likely a separate
physical status line multiplexed onto the same port address, a common pattern on
Z80-peripheral-era I/O — noting the mechanism as inferred, not asserting more than the
disassembly shows. See `docs/JWSDOS-format.md` §6 for the full driver sequence this bit
is used in.

**FDC = µPD765, semi-DMA, software-polled (CONFIRMED):** status/data via A0 (`0x8C` status = MSR,
`RDY`/`REQ` bits; `0x8D` data); command/execute/result phases; MFM; **4 drives** (CORRECTED,
2026-07-23 — see note immediately below); write-protect + track-00 sensing; INT at the result
phase → CTC ch0. During a read/write execute phase the driver polls the request bit and services
each transfer itself (semi-DMA), not autonomous DMA.

**Drive count — CORRECTED to 4 (owner, 2026-07-23).** This doc previously stated "2 drives" for
the plain floppy+RAM board, sourced from a Field Service Manual scan of poor quality — the owner
has since identified that scan as the likely source of the error (a bad transcription of the
connector pinout, not a genuine 2-drive-only board). **A separate, official Philips-authored P2000
manual clearly states the expansion board supports up to 4 drives** — consistent with the M2200
board's own CONFIRMED 4-drive connector (`DRISEL0`-`3`, `docs/M2200-implementation.md` §2.1/§5.2),
and with M2200's own design intent as a drop-in replacement (with extras) for the official Philips
memory/FDC card, per the owner. **Not yet independently reviewed by the maintainer against the new
manual itself** (owner-reported, no document uploaded for this specific correction) — treat as
CONFIRMED-by-owner-statement, same evidentiary weight as other owner-supplied facts elsewhere in
this doc, and upgrade further if/when the manual itself is shared. This resolves the "does the
plain board match M2200's connector" open item carried in `docs/M2200-implementation.md` §7 and
in machine/UI CLAUDE.md's milestone 20/14 — both were flagging exactly this uncertainty, now
closed: **4 drives is the hardware ceiling for both boards.**

**Upgraded from owner-statement to directly-read primary source (2026-07-23) — the official
Philips "P2000 System T&M Reference Manual" (144-page owner-supplied scan, personally read and
transcribed page-by-page, `raw-conversion.md`).** Chapter 2 "FLEXIBLE DISKS" (§2.1 "Disk drive
units") states verbatim: *"Two disk drive units can be supported by all P2000 models, and in some
cases two more may be connected, allowing a total of four disks to be in use at one time, with a
capacity of 560k bytes."* This independently corroborates (does not merely repeat) the
owner-reported correction above — the maintainer has now read the primary source directly rather
than relying on the owner's paraphrase. Also confirmed from the same chapter: **35 tracks, 16
sectors/track, 256 bytes/sector = 140k/disk** (§2.2 "Disk format" — matches the project's own
`getdos`-derived sector geometry exactly), single-sided double-density 5¼" media, 300 RPM,
stepping-motor track positioning (2 steps/track), and Philips-only factory-preformatted disks (the
manual states third-party blank disks aren't usable since the P2000 itself has no format
capability documented in this manual — consistent with disk formatting/blank-disk creation being
an emulator-only convenience feature, not a modeled real capability, per the machine milestone 20
design).

**Flagged, NOT resolved — an apparent encoding-scheme discrepancy in the manual's own words
(2026-07-23).** §2.1 states disk data is recorded *"using frequency modulation; that is, each bit
is recorded together with an associated clock pulse"* — this is textbook **FM encoding**, not
MFM. The manual calls the media **"double-density"** in the same breath, which is the
conventional shorthand for **MFM** in the era's own vocabulary (single-density ⇔ FM, double-
density ⇔ MFM, for both 8″ and 5¼″ formats) — the manual's own text is internally in tension with
its own "double-density" label. This project's existing FDC write-up above states **"MFM"**
(sourced from the µPD765 chip's general capabilities, not from a primary P2000-specific source
until now). Given the manual's explicit, unambiguous FM description directly contradicts the
project's prior MFM assumption, and the manual is the single most authoritative primary source
this project has for the physical disk format, **this is flagged as a real, unresolved
discrepancy rather than silently corrected either way** — the µPD765 chip supports both FM and
MFM via its own mode bit, so which one the real firmware actually programs is a firmware fact,
not a chip limitation; nothing in the disassembly-derived command bytes (§ above) pins down the
FDC's MF (bit 6 of the opcode byte) setting. **Action for whoever next touches FDC command-byte
modeling:** check the actual opcode byte in the confirmed READ DATA/WRITE DATA command
(`0x42`/`0x45`) against the µPD765 datasheet's MF bit position to settle this from the project's
own already-confirmed data, rather than trusting either the manual's prose or this doc's prior
assumption blind.

**Z80-CTC channel roles — CONFIRMED (ROM):**
- **ch0 (`0x88`, highest priority)** = timer / **FDC interrupt**: the µPD765 result-phase INT feeds
  ch0, so the FDC has NO direct CPU INT line — it interrupts *through* ch0, IM2-vectored.
- **ch1 (`0x89`)** = disk **not-ready** interrupt.
- **ch2 (`0x8A`)** = **communication (serial / I/O) interrupt** — the CTC channel a SLOT2 comms
  card's interrupt uses; relevant to the SLOT2 milestone.
- **ch3 (`0x8B`)** = **keyboard-scan / system tick, 20 ms (50 Hz)** — the CTC-path tick that
  replaces the onboard video 50 Hz INT when the board is present (Lock, §5e).
- IM2: on int-ack (M1+IORQ) the interrupting channel supplies its programmed vector.
- **The ROM never *reads* a CTC channel (disassembly-confirmed — no `IN` on `0x88`–`0x8B`).** It
  only writes control words / time constants and reads FDC status from the separate `0x8C`/`0x8D`
  ports. So a channel's **read-back value is don't-care for authentic firmware** — a real chip
  returns the live down-counter, but the emulated chip needn't (returning control flags or 0 is
  harmless). A correct down-counter read-back is only a **debugger nicety**, not required for
  correctness.

**Milestone consequences:** the CTC (milestone 17) must exist before FDC interrupts work (FDC →
ch0); ch2 is the interrupt hook for a SLOT2 comms card; the system tick moves to ch3 when Lock
asserts (§5e). **Modelled as a standalone, board-agnostic `Z80Ctc` chip** (like the SAA5050): the
owning board instantiates one and wires its per-channel CLK/TRG + INT routing; a multi-board
framework is deferred until a second CTC-bearing board is real (machine milestone 17, design note).

**Exact CTC control words for the disk channels — CONFIRMED (`Disk.asm`, owner-supplied
2026-07-13):** ch0 (disk-complete) `0xD5` (INTEN|counter-mode|rising-edge|TC-follows|
CTRLWRD), time constant `0x01`; ch1 (disk-not-ready) `0xC5` — same shape, **falling edge**
instead of rising, TC `0x01`. Both reset via `0x03` when done with them.

**Command bytes actually issued by `getdos`/the disk driver — CONFIRMED exact values
(owner-supplied disassembly, 2026-07-13). Match command dispatch on these bytes, not a
reconstructed MT/MF/SK bit-flag decomposition of the opcode:**

| Command | Opcode | Bytes sent | Result |
|---|---|---|---|
| SPECIFY | `0x03` | `03 60 34` | none (2 param bytes: SRT/HUT, HLT/ND) |
| RECALIBRATE | `0x07` | `07 01` | seeks to track 0; completion via INT |
| SEEK | `0x0F` | `0F 01 01` | drive/head + cylinder byte |
| READ DATA | `0x42` | `42 01 01 00 01 01 10 0E 00` | data phase, semi-DMA (above) |
| WRITE DATA | `0x45` | same shape, opcode `0x45` | data phase, semi-DMA (above) |
| SENSE INTERRUPT STATUS | `0x08` | `08` | 2 result bytes (ST0 + PCN) |

**A seventh command, CONFIRMED separately (2026-07-23, design-doc maintainer's own read of
`docs/jwsdos5.0.asm`) — SENSE DRIVE STATUS, issued by JWSDOS's resident driver, not `getdos`
itself:** `check_write_enable` (the routine JWSDOS's SAVE/format-adjacent paths call before
writing) sends `02 04 <drive>` (length-prefixed: 2 command bytes, opcode `0x04`, drive/head
byte), reads back exactly **one** result byte via `MON_DSK_read_status_bytes`, and tests
**bit 6 for write-protect** (`bit 6,a` / `ret z` = writable, set = protected) — an exact match
to the standard µPD765/8272A ST3 register layout (bit 6 = WP), not a P2000-specific variant.
This is the first real, sourced confirmation that ST3's bit-6 semantics apply unmodified to
this hardware, and the first evidence the plain FDC command set in active use on this platform
is at least 7 commands, not just the 6 `getdos` itself issues. See
`docs/FDC-implementation.md` for the full standardized µPD765/8272A 15-command set (this
project's own command subset above is a proper subset of that, not the whole chip) and the
new machine-layer milestone (project CLAUDE.md §13.19a) building full command-set fidelity.

**Two confirmed µPD765 usage facts from real-ROM boot-test diagnosis (2026-07-22), both
worth knowing beyond just "the FDC has 4 drives" (drive count corrected 2026-07-23, above):**
- **The ROM driver hardcodes unit-select to drive 1, never drive 0.** `Disk.asm`'s
  `disk_constants` gives every "drive #" byte as `0x01` (matching the `01` first param byte in
  the SEEK/READ DATA/WRITE DATA rows above), and `disk_recall_cmd`'s own comment reads "device #
  at disk_recall_device (default 1)" — the ROM never issues a command addressing unit 0. A
  mounted disk image must therefore be attached at drive index 1, not 0, for the ROM to ever see
  it (a mount on drive 0 fails silently: reads return a zero-filled buffer with no error, since
  an unmounted-drive access is a documented no-op rather than a fault).
- **READ DATA/WRITE DATA's own C (cylinder) parameter byte is for ID-field verification only,
  not addressing.** The real µPD765 reads/writes wherever the head physically IS (positioned by a
  prior SEEK), not wherever the command's own C byte says. `Disk.asm`'s `read_track` copies its
  command template to RAM once and never updates the cylinder field between reading two different
  DOS tracks (both copies are byte-identical) — the two reads land on different cylinders only
  because a separate SEEK moved the head between them. Model read/write addressing off the FDC's
  own tracked head position, not the command byte.

Byte positions structurally match the standard µPD765 9-byte READ/WRITE DATA parameter block
(drive/unit, cylinder, head, sector, N, EOT, GPL, DTL) — confident in the values and
positions; not independently re-deriving the datasheet's MT/MF/SK bit-field meaning of the
opcode byte from memory. This is the **full command subset the ROM driver actually issues**,
not the complete **15**-command µPD765/8272A set (corrected 2026-07-23 — earlier text said 16;
MAME's own `upd765.cpp` enum has additional entries beyond 15, but those are enhanced-FDC-
generation-only commands not part of the base chip this hardware uses, per
`docs/FDC-implementation.md` §0) — resolves "Constants to source" item 3 below.

### Fits as a slot device; presence check is free
The FDC is the **internal-extension-slot** device (§5c) — populated in the M, addable to a
T. The T's FDC card was **compatible with the P2000M's internal floppy controller**. The
monitor ROM probes for disk presence, gated behind the disk-boot conditions in §5b, by
reading FDC ports:
- **Card mounted** → FDC responds with valid status → ROM concludes a disk subsystem exists.
- **Slot empty** → open-bus 0xFF → ROM detects nothing.

**CONFIRMED exact sequence (owner-supplied `Disk.asm` disassembly, 2026-07-13):**
`OUT (0x90),0x04` (assert RESET alone — ENABLE/MOTOR/SELDIS all clear) → a fixed
**~256-iteration `DJNZ` delay** (~1.3 ms, **no interrupt wait**) → `IN A,(0x8C)` then
`CP 0x80` (**exact equality** against RQM-set/everything-else-clear, not a bare bit-7 test)
→ match → `CALL getdos` (§5b). Either way `DSKCTRL` is rewritten to `0x00` afterward — "always
switch the FDC off again, just to make sure." Absent card → open-bus `0x8C` reads `0xFF` →
`CP 0x80` fails → `getdos` never called — same "genuine silence" pattern as the CTC probe
(§5e). Model the chip's post-reset MSR as exactly `0x80` (idle, ready-for-command, no drive
busy) so this exact-match check succeeds when a card is present.

So "disk present" = "is an FDC slot device mounted, reset, and idle." No special-casing.

### Emulate the chip, not the magnetism
The card uses a **µPD765-family FDC** (MAME's P2000T support implements it as µPD765, and
PR #7577 carried µPD765 emulation changes; further corroborated by the real command bytes
above structurally matching the standard µPD765 parameter-block shapes). Standard,
well-documented chip — emulate the register interface, not an analog medium:
- Main Status Register + Data Register; command / execution / result phases; 16 commands in
  the datasheet, though the ROM driver only ever issues the subset above; user-programmable
  step-rate / head-load / head-unload; FM + MFM.
- INT line: in **non-DMA mode pulses per byte**, in **DMA mode pulses at command completion**.
  **CONFIRMED non-DMA/semi-DMA, polled:** the driver services each byte itself via the
  `0x90`-bit0 poll + `INI` (above) — resolves "Constants to source" item 3 below.
- **Datasheet reset-interrupt behaviour is real µPD765 behavior, but CORRECTED
  (2026-07-13) — the P2000's own presence probe does NOT rely on it.** The datasheet says:
  if RDY is high during reset, the FDC raises an interrupt within ~1.024 ms, cleared by
  Sense Interrupt Status. The ROM's *actual* presence probe (above) is a fixed ~1.3 ms delay
  then a direct status poll — **no interrupt wait at all**. Model the datasheet reset-INT
  behavior for general chip fidelity if convenient, but it is not load-bearing for this
  probe to succeed; don't gate the probe test on it.

### Two-level speed = chip-timing policy (NO ROM trap needed)
The FDC interface is clean and self-contained, so unlike the cassette it needs no ROM
trapping. Emulate the µPD765 faithfully either way; the same `TimingPolicy` strategy just
decides whether real-time delays are honoured:
- **Authentic:** seek time (step-rate × track distance), motor spin-up, head-load,
  rotational latency, index/sector timing, per-byte transfer rate. JWSDOS / CP/M drivers
  run unmodified, period-correct.
- **Turbo:** complete data phase + seek instantly; same register results, delays zeroed.

### Host-side disk-image API — separate, like the cassette
Mount/eject, create-blank, write-protect, browse, import/export at host speed,
independent of the timing policy.

### Constants to source (NOT invent) + sources
From **MAME PR #7577 FDC slot device** (chip, ports, DMA/INT wiring), the project's **own
monitor-ROM disassembly** (presence probe + driver command sequence), the owner's own
**JWSDOS 5.0 binary disassembly**, and **real JWSDOS/`Disk BASIC` disk images** (test corpus)
— see `docs/JWSDOS-format.md` for everything DOS/disk-format-specific:
1. **Exact FDC chip** — µPD765 per MAME; corroborated (not independently schematic-confirmed)
   by the real command bytes above structurally matching the standard µPD765 parameter-block
   shapes. (A hobbyist building a *custom* P2000T controller once referenced the WD179x
   family, so the schematic cross-check is still worth doing if convenient; µPD765 is the
   strong, now well-corroborated default.)
2. **FDC I/O port addresses** — **RESOLVED**, see the I/O port map above.
3. ~~**DMA vs non-DMA** transfer mode~~ — **RESOLVED**: non-DMA / semi-DMA, software-polled
   (above).
4. ~~**FDC INT line wiring** to the Z80~~ — **RESOLVED**: through CTC ch0, IM2-vectored (§5d
   CTC channel roles above), not a direct maskable INT or NMI line.
5. ~~**Disk geometry / image format**~~ — **RESOLVED for sector size/count; geometry itself
   moved to `docs/JWSDOS-format.md`.** Sector size/count CONFIRMED from `getdos`: 16
   sectors/track, 256 B/sector (4 KB/track). The earlier "single-sided 35-track" figure was
   a placeholder that did not survive contact with real disk images — JWSDOS 5.0 supports
   **multiple geometries** (35/40/80-track, SS/DS) as a per-disk format-time choice, with a
   self-describing on-disk label (byte-exact confirmed against a real 320 KB image: 40-track,
   double-sided). Full detail — directory format, allocation model, the geometry label's
   exact offsets — lives in `docs/JWSDOS-format.md`, not duplicated here. See also §3a for
   the resulting `.dsk` file-convention note.

---

## 5e. Interrupt architecture + Z80 CTC

### Two system-tick sources with auto-fallback (presence-probe pattern, via interrupt)
The monitor ROM probes for a **Z80 CTC** at boot — **CONFIRMED sequence (disassembly):** it sets
**IM 2**, points CTC ch3's IM2 vector at a test handler (`CTC_testcode`, stored in the
`CTC_keyboard` vector slot), programs **ch3 (`0x8B`) as a fast timer** — control word **`0x85`**
(`INTEN` + time-constant-follows + control word; **timer mode, prescaler 16**) then **time constant
`0x01`** (shortest delay) — and enables interrupts. **If a CTC is present**, ch3 fires almost
immediately and the IM2 vector diverts execution to the handler → CTC present, used as the system
tick. **If absent**, no interrupt ever fires and execution falls straight through to `IM 1` + the
keyboard-scan init → the **video 50 Hz VBLANK** becomes the tick. There is **NO timeout loop** —
presence is simply "did the interrupt divert us," which works because the probe timer is set to the
shortest possible delay.

**Emulator consequences:**
- Once ch3 is programmed `0x85` + TC `0x01` with `INTEN`, a present CTC must assert INT within
  ~16 T-states (prescaler 16 × TC 1, **timer mode off the system clock**) and the aggregator must
  deliver ch3's vector by IM2. An absent CTC asserts nothing → the fall-through to `IM 1` is
  automatic (the bare-T path, unchanged) — this is the exact regression to protect.
- **Two modes on ch3, both now CONFIRMED:**
  - **Probe:** control `0x85` (INTEN + TC-follows + control; **timer mode**, prescaler 16) + TC
    `0x01` — a fast one-shot off the system clock, for presence detection.
  - **Normal keyboard / system tick (`CTC_enable`):** control `0xD5` (INTEN + **counter mode** +
    **rising-edge trigger** + TC-follows) + TC `0x01`. In counter mode ch3 counts its **CLK/TRG
    input = the video vertical-retrace pulse (one per field, 50 Hz)**, so TC 1 → **one interrupt
    every field = 20 ms**. `CTC_testcode` disables the CTC, restores the `keyscan` handler, then
    calls `CTC_enable` to arm this mode.
  So the CTC must support **both** timer mode (system clock, for the probe) and counter mode fed by
  the **field / retrace pulse** (for the tick). That is the same 50 Hz that drives the bare-T video
  tick: with the CTC present it feeds ch3's counter and the tick becomes IM2-vectored; without it,
  the 50 Hz drives INT directly (IM1). `enable_interrupts` = `EI` + `RETI` (the `RETI` also lets the
  CTC daisy chain see end-of-service — the emulated daisy chain must snoop it).
- **IM2 vector base = `0x6020` (CONFIRMED).** `CTC_setup_for_IM2` sets `IM 2`, writes the base low
  byte `0x20` to **CTC ch0** (`0x88`) — the Z80-CTC way to set the vector base for all four channels
  — and loads `0x60` into `I`. Table entries: ch0 `0x6020`, ch1 `0x6022`, ch2 `0x6024`, **ch3
  (`CTC_keyboard`) `0x6026`**. The probe stores the test handler at the ch3 slot; `CTC_testcode`
  later restores `keyscan` there.
  - **Independently confirmed a THIRD time (2026-07-23), directly in the official manual's own
    body text, not just an appendix** — this is the strongest possible corroboration this project
    has for any single fact: the ROM disassembly, the Field Service Manual's appendix, AND now the
    T&M Reference Manual's §3.2.2 "Keyboard initialise routine" state the identical table verbatim:
    *"channel 0 — disk operation ready or timer CTC interrupt — 6020 hex; channel 1 — disk not
    ready interrupt — 6022 hex; channel 2 — communication interrupt — 6024 hex; channel 3 —
    keyboard timer interrupt (this is external to the CTC) — 6026 hex."* Word-for-word the same
    channel-role assignment already documented here (ch0 disk/timer, ch1 disk-not-ready, ch2
    comms, ch3 keyboard) — three independent sources now agree exactly, nothing left open on this
    point.
- **Detection diverts the boot flow — it does not "return true":** on the CTC interrupt,
  `CTC_testcode` **pops (discards) the interrupt return address** so control never falls back to the
  `IM 1` line; a present CTC simply takes over into keyboard-scan init. This is why absence needs no
  timeout — only presence changes the control flow.
- **`KBIEN` = CPOUT **bit 6** (`0x40`, port `0x10`, §5f):** the probe writes `0x00` to quiet keyscan
  before testing; keyscan init writes `0x40` to enable it. (Same presence-probe idea as the disk
  check §5d, but the test is "does an interrupt fire.")

**Physical mechanism CONFIRMED (internal-slot pin 37, §5c):** the 50 Hz keyboard-scan
interrupt signal (**DEW** on T / **R2425** on M) is exposed on the internal extension slot.
The floppy/CTC card taps this 50 Hz line as a clock/trigger into a CTC channel; once the
CTC is programmed and running it supplies the system tick (IM2 vectored), otherwise the raw
50 Hz drives the video-tick path (IM1). So both tick sources meet physically on pin 37.

**The Lock interlock (internal-slot pin 35) is the hardware arbiter.** Field service manual:
*"Lock = disables interrupt generation if extension board is used."* So the selection is NOT
merely a firmware probe — inserting the extension board asserts Lock, which **mutes the
onboard/video 50 Hz interrupt** so only the board's CTC drives INT. The firmware probe then
detects which source is live. Emulator model:
- Lock is an **input to the interrupt aggregator**, asserted when an active extension board
  occupies the internal slot.
- Lock asserted → aggregator **suppresses the onboard video 50 Hz INT**; the CTC path drives
  INT (IM2).
- Lock deasserted (bare T) → video 50 Hz tick reaches INT normally (IM1).
- So it's a gate in the aggregator, ensuring only one tick source is electrically active;
  the probe tells firmware which. **RESOLVED (milestone 17, documented default): Lock gates only
  the maskable video INT, NOT NMI** — the front-panel reset button and SLOT1 NMI have no logical
  tie to the internal-slot board. Revisit only if a schematic/service-manual capture shows Lock
  also gating NMI.

**Implementation notes (milestone 17, as built):**
- **The daisy chain registers individual CTC *channels* (ch0…ch3), not the chip as a whole** —
  each channel is its own IEI→IEO link, matching how the real chip chains its channels internally.
  A future SLOT2 card registers as a single chip-level link *behind* all four CTC channels.
- **Int-ack `Acknowledge()` must fire ONCE per ack M-cycle, on its first T-state** — not per
  T-state. Z80.Core's int-ack M-cycle holds M1+IORQ across 3 T-states; a stateless IM1 pull-up
  didn't care (idempotent 0xFF), but a **stateful daisy-chain `Acknowledge()` has side effects**
  (clears pending, sets in-service), so calling it 3× drove the wrong vector (the 2nd/3rd call saw
  the channel self-blocked and fell through to the 0xFE pull-up). Edge-detect the first T-state and
  cache/re-drive the same byte for the rest of the cycle. (Also a caution for any UI/debugger code
  that reads bus state mid-M-cycle.)
- **`EI` before `RETI` is load-bearing, not stylistic** (§5e's `enable_interrupts = EI + RETI`):
  accepting a maskable INT clears BOTH IFF1 and IFF2, so a bare `RETI` after a non-nested interrupt
  leaves interrupts disabled and the next field's tick pends forever.

**Consequence — the stock T needs NO CTC:** model an absent CTC exactly like an empty
slot (open-bus reads, INT never asserted) and the ROM auto-selects the video 50 Hz tick.
No ROM stubbing, no special-casing. **CTC is purely additive for the M phase.** For the
T-first build, implement only the video VBLANK interrupt source; defer the CTC.
- CRITICAL for the fallback to trigger: "absent device" must mean genuine silence —
  open-bus reads AND INT never driven. A stray live-looking status read or an accidental
  latched interrupt would make the probe misfire.

### NMI sources (not only the soft-reset button)
NMI (active-low) is driven by the front-panel **soft-reset button** AND is **exposed on
SLOT1** (pin 1A, §5c) — a cartridge can pull NMI. If SLOT2 / the internal slot also carry
NMI (pin-outs TBD), they're sources too. The aggregator should **wired-OR all NMI sources**
onto the core NMI pin, same as it does for INT.

**Implemented (milestone-12):** the interrupt aggregator now holds a **separate `_nmiPending`
latch** alongside its INT-pending latch, and both serialize in `SaveState`. Adding the second
boolean **changed the `.state` device-stream format** — this change (together with the
milestone-16 `SoundDevice` block) is why `.state` was later bumped to **v2** (see §3a versioning
— RESOLVED 2026-07-10; the reader now rejects v1 files rather than mis-loading). (Test note: on a default T38 machine SP=0x0000, so an NMI pushes into the empty
banked window and the writes are discarded; the CPU still vectors to 0x0066 correctly — the
corrupt stack only matters on `RETN`.)

### Interrupt aggregator (build once, like the slot model)
All interrupt sources feed the SAME Z80 INT line, **wired-OR** (real Z80 peripheral INT
pins are open-drain, tied together, daisy-chained for priority). Machine layer needs an
**interrupt aggregator**:
- Each mounted source can assert/release INT: **video (always present)**; CTC, SIO/DART,
  PIO, FDC (when present).
- Machine ORs all active sources onto the core's INT pin.
- For **IM2**, a **daisy-chain controller** resolves which source supplies the vector
  during the int-ack cycle, and tracks in-service / RETI to re-enable lower priority.
- **IM2 daisy chain is an OPTIONAL, config-driven module — NOT M-only.** It is required
  whenever ANY Z80-family peripheral is mounted: the **floppy interface, serial/CTC/SIO,
  PIO, or other expansion cards** — which can happen on the **T** as well as the M. So it
  is optional *per configuration*, not deferred to a machine. Build it as an opt-in
  `DaisyChain` component that mounted peripherals register into; a bare T with no such
  card never instantiates it and runs purely on the IM1 video tick.
- Confirmed mode split: **T video tick = IM1** (INT → RST `0x0038`). **CTC / Z80-family
  peripherals = IM2** vectored, via the optional daisy chain above.

### How the CTC ties to the Z80 core (affects the core build NOW)
The CTC is a Z80-family peripheral on the IM2 daisy chain, which imposes two core
requirements (both fall out of the bus-exposed cycle-stepped design — a good
confirmation it was the right call):
- **Int-ack cycle:** CPU asserts **M1 + IORQ together**; the highest-priority requesting
  channel with IEI active places its **vector on the data bus**. The core must model an
  int-ack M-cycle that reads the vector FROM THE BUS (peripheral supplies it), not from an
  internal register. (CLAUDE.md §6 already says "IM2 uses I register + bus vector" — keep
  that explicit.)
- **RETI snoop:** peripherals watch the instruction stream for `ED 4D` (RETI) to clear
  their in-service latch. The core does nothing special, but **every opcode fetch must be
  snoopable on the bus** (cycle-stepped core exposes this already). RETI detection +
  vector supply are the PERIPHERAL's job, out of scope for the core build.

### CTC emulation model (standard Zilog Z8430 — high confidence)
- **4 independent channels**, each: control register, 8-bit time-constant register, 8-bit
  down-counter, prescaler (timer mode only).
- **Timer mode:** prescale system clock by **16 or 256**, then the time constant divides
  further; down-counter decrements per prescaled tick; on zero → reload, pulse ZC/TO,
  optionally interrupt. Can be gated/started by the CLK/TRG input.
- **Counter mode:** decrement on each active edge of CLK/TRG; on zero → reload, pulse
  ZC/TO (if present), optionally interrupt.
- **Time constant 0 = 256.**
- **Channel 3 has NO output pin** → the conventional pure timekeeping/interrupt channel.
- **CTC channel 3 CLK/TRG is wired to the video 50 Hz (per field)** — CONFIRMED from the owner's
  working video code (`P2000Video.cs` calls `Ctc.ClkTrg(3)` once per field). This is the concrete
  wiring behind the CTC-as-system-tick mechanism (§5e): the video field signal clocks CTC ch3,
  so when a CTC is present ch3 counts fields and can generate the system tick; absent a CTC, the
  same 50 Hz drives the IM1 video tick directly. (Fires per FIELD = 50 Hz, not per frame.)
- **Cascading:** ZC/TO of channel n → CLK/TRG of channel n+1 for longer periods
  (e.g. ch2→ch3 for a 16-bit timer / slow tick).
- **Control word** (bit0=1): bit7 int-enable, bit6 mode (0=timer/1=counter), bit5
  prescaler (0=÷16/1=÷256), bit4 trigger edge, bit3 trigger mode, bit2 "time constant
  follows", bit1 reset. Writing bit0=0 to channel 0 sets the **interrupt vector base**;
  CTC supplies vector = (base & 0xF8) | (channel << 1).
- **Cycle-exact integration:** timer-mode channels tick on the prescaled master clock
  (deterministic, advance with everything else); counter-mode channels tick on edge
  events you deliver (e.g. a cascaded output or an external signal). Per channel hold:
  control, time constant, counter, prescaler phase, int pending, int in-service.

### Partial data from the monitor disassembly (`Symbols.asm`)
CTC-related RAM variables name the channel PURPOSES (not the I/O ports, but confirms usage):
`CTC_timer_disk` (0x6020), `CTC_disk_not_ready` / `CTC_communication` (0x6022), `CTC_keyboard`
(0x6026) — so CTC channels serve disk timing, a comms/disk-ready function, and a keyboard-related
timer. **Disk (FDC) I/O ports CONFIRMED:** `DSKIO1` = 0x8C (FDC status IN), `DSKSTAT` = 0x8D
(IN/OUT), `DSKCTRL` = 0x90 — real port data for the deferred FDC work (§5d). Also `type_T_M`
(0x6013) = the T-vs-M model flag the ROM keeps.

### Still to source from you / the schematic / the disassembly
1. **Scope:** M is a target (after the T). T-first; CTC deferred to M phase.
2. **Channel assignments — CONFIRMED (§5d):** ch0 timer/disk, ch1 disk-not-ready, ch2
   communication (serial/I/O), ch3 keyboard / 50 Hz system tick.
3. **CTC ports + IM2 vector base — CONFIRMED:** channels `0x88`–`0x8B` (one each); vector base
   `0x6020` (I = 0x60, base low byte written to ch0) — see the probe sequence above.
4. **Clock source per channel** — **ch3 CONFIRMED:** timer mode = system clock ÷16 (probe),
   counter mode = the vertical-retrace pulse on CLK/TRG (normal 20 ms tick). Other channels'
   clock/trigger sources (ch0 FDC INT, ch1 not-ready, ch2 comms) still to confirm.
5. **Daisy-chain order/priority** (CTC vs FDC vs SIO/PIO).
6. **Floppy board:** own CTC or the M's? FDC INT routed through the CTC or independent?
7. **IM mode of each tick path** (IM1 for video? IM2 for CTC?) — from the monitor disassembly.

---

## 5f. Keyboard (input device) — CONFIRMED from monitor disassembly

### The keyboard is an I/O DEVICE — same shape as the cassette (two faces)
The keyboard is an ordinary I/O device, not a special input path. Like the cassette, it has
**two faces**, and it's important not to confuse them:
- **Bus face (identical discipline to the cassette):** the CPU does an `IN` from a keyboard
  port; the device puts row bits on the data bus. Plain I/O port dispatch — no different in
  kind from the cassette answering a CPRIN read. Keyboard answers ports 0x00–0x09; cassette
  answers CPRIN (0x20) and consumes CPOUT (0x10) writes.
- **Host face (also shared with the cassette):** the matrix state is fed from host key events,
  exactly as the cassette's tape content is fed from a mounted `.cas`. Both are external feeds
  the emulation thread samples at a safe point (**frame boundary**), so input can't race the
  50 Hz scan.

So "keyboard" spans a **device** (emulation-thread bus peripheral, behind the common device
interface) and a **window** (UI-thread observer: layout, click-to-press, reflects keys down) —
but the device itself is a peer of the cassette, not a different category. **They even share
port 0x10 (CPOUT):** KBIEN (keyboard scan enable) lives in the same latch as the cassette
FWD/REV/WCD/WDA lines, so keyboard and cassette register on the port dispatch the same way and
the `CPoutLatch` fans a 0x10 write out to both. Do NOT model the keyboard as a special
non-I/O input path.

Note on data direction: earlier framing called the keyboard "reversed-flow" vs display/debug.
That refers only to the HOST face (host input feeding in), NOT the bus face — and the cassette
has the same host-fed face (its `.cas`). On the bus both are ordinary I/O devices. Don't let
"input device" imply a different bus discipline.

### Scan protocol (CONFIRMED — active-LOW, pressed = 0)
Scanning is driven by **KBIEN = bit 6 (0x40) of port 0x10 (CPOUT)**:
- **KBIEN = 1 (scan ON):** only **port 0** is meaningful — returns the **AND of all 10
  rows**. `0xFF` = no key down anywhere; non-`0xFF` = at least one key down. The ISR's cheap
  poll to decide whether to bother parsing.
- **KBIEN = 0 (scan OFF):** **ports 0–9 each return their own row** (8 columns, active-low),
  and the ISR parses all ten to find which keys.
- Matrix: **10 rows (ports 0x00–0x09) × 8 columns**.
- Scanning itself is invoked from the **IM1 / RST 38h (video 50 Hz) interrupt**.

**Independently confirmed (2026-07-23), official Philips T&M Reference Manual, Ch3 "KEYBOARD":**
§3.1 states the physical matrix is *"ten x-lines and 8 y-lines"* — matches the confirmed 10×8
model exactly (the manual's own §3.2.3 "Reading keyboard" text sloppily says "the **nine**
x-lines... An input is made from ports 0 to 9" in the very next paragraph — internally
inconsistent with its own §3.1 figure, since "ports 0 to 9" is ten ports; read as a manual
typo/rounding, not a real 9-vs-10 hardware discrepancy, given ports 0-9 inclusive is what's
actually described both here and in this project's own disassembly-sourced model). Also confirms
the **20 ms / 50 Hz scan interval** verbatim (§3.2.3: *"Every 20 ms, an interrupt is generated...
The monitor then scans the keyboard"*) and the **74-key** physical layout (§1.4, §3), plus the
**12-entry keyboard queue** and **repeat-key** behavior (§3.2.1/§3.3.2) already implicit in this
project's model. Appendix E's key-code table (per-key alone/shifted decimal values, full physical
layout) is transcribed in `raw-conversion.md` PDF p.132 for whoever eventually builds the
host-key-to-P2000-key mapping table — not reproduced here since it's a large lookup table, not a
architectural fact.

### CPOUT (port 0x10) is a SHARED output latch — not keyboard-only
Writes to 0x10 update the whole latch; KBIEN is one bit among cassette + printer lines.
The MDCR drive lines (§5b) live here too — the same latch the authentic-mode cassette path
writes. Bit map (from the disassembly):
| Bit | Name | Function |
|-----|------|----------|
| 7 (0x80) | PRD  | Printer data out (printer port pin 3) |
| 6 (0x40) | KBIEN| Keyboard interrupt/scan enable (1=on, 0=off) |
| 5,4 | — | unused |
| 3 (0x08) | FWD | Cassette forward |
| 2 (0x04) | REV | Cassette backward |
| 1 (0x02) | WCD | Write Command (1=write, 0=no action) |
| 0 (0x01) | WDA | Write Data (data bit to cassette) |

→ The emulator must treat a write to 0x10 as updating ALL of these together (keyboard
enable + printer + the four MDCR lines), routed to the respective devices — not as a
keyboard register.

### CPOUT latch object (write-only, shadow byte, edge detection)
CONFIRMED behaviour: the ROM keeps a **shadow copy** of CPOUT and always writes the **whole
byte** — the disassembly ORs KBIEN into every MDCR command so tape operations don't disable
keyboard scanning. Classic write-only-latch idiom (the port can't be read back). Design:
- A **`CPoutLatch` object holds the last full byte written = single source of truth.** Each
  device reads its bits from it; no device owns a sub-register.
- **Edge vs level differs per bit — this matters:**
  - **KBIEN (6)**, **PRD (7)**, **FWD (3)**, **REV (2)** are **levels** — read current state.
  - **WDA (0) / WCD (1)** carry the cassette bitstream. Because the firmware rewrites the
    whole byte per command, the MDCR authentic-mode encoder must detect **transitions** of
    these bits, not levels — each rewrite flipping the data/strobe clocks a bit to tape.
- So the latch computes edges ONCE (exposes prev+new byte or per-bit change events); the
  MDCR consumes them. SaveState serializes just the shadow byte.

```
class CPoutLatch:                 # port 0x10, write-only
    byte current                  # shadow byte = entire latch state
    write(value):
        byte prev = current
        current = value
        keyboard.KBIEN = (value & 0x40) != 0      # level
        printer.Data   = (value & 0x80) != 0      # level
        mdcr.OnCPout(prev, value)                 # MDCR inspects edges: WDA/WCD (+FWD/REV level)
```
- `mdcr.OnCPout(prev, value)` = authentic-mode write-data edge detection; **turbo mode
  ignores it** (traps the ROM routine instead, §5b).
- **TO CONFIRM from disassembly:** is **WCD the strobe/clock and WDA the data level** (ROM
  sets WDA, pulses WCD per bit → encoder keys off WCD edges, samples WDA)? The "Write
  Command / Write Data" naming suggests so. Determines the authentic encoder's clock.

### CPRIN (port 0x20) — SHARED INPUT port (cassette + printer), CONFIRMED
The read-side counterpart to CPOUT. Symmetry holds: shared *input* port as CPOUT is a
shared *output* latch. Bit map (from the disassembly); `(N)` = **active-LOW**:
| Bit | Name | Function |
|-----|------|----------|
| 0 (0x01) | PRI   | Printer data in (printer port pin 2) |
| 1 (0x02) | READY | Printer ready (pin 20) |
| 2 (0x04) | STRAP | (N) Printer type (Daisy/Matrix) |
| 3 (0x08) | WEN   | (N) Write enable: 0 = can write, 1 = write-protected |
| 4 (0x10) | CIP   | (N) Cassette in place: 0 = cassette present, 1 = none |
| 5 (0x20) | BET   | (N) Begin/End of tape: 0 = at an end, 1 = tape OK |
| 6 (0x40) | RDC   | Read Clock — **toggles** (H→L or L→H) when a data bit is ready |
| 7 (0x80) | RDA   | Read Data — the data bit from cassette |

**RDC + RDA = self-clocking read pair** (mirrors WCD/WDA on write). RDC is a **level that
flips per bit**, not a one-direction edge: the ROM samples RDA on each RDC transition.
Authentic-mode read path: the MDCR device streaming a `.cas` **toggles RDC and presents the
bit on RDA each bit-period (6000 baud)**; the ROM polls CPRIN, watches RDC change, samples
RDA. Emulator generates RDC edges at the bit rate; ROM clocks off them.

**Status bits drive directly from device state (mind the active-LOW sense):**
- **CIP (4):** `0` = `.cas` mounted, `1` = empty drive. The bare-machine default (§3a) →
  CIP reads 1; mounting a `.cas` flips it to 0.
- **BET (5):** `1` = tape OK, `0` = at a physical end. Asserted (0) when the emulated tape
  position hits either extremity — the mechanical boundary the transport model (§5b) needs
  for rewind/search limits.
- **WEN (3):** `0` = writable, `1` = protected. **CONFIRMED from monitor disassembly**
  (`Symbols.asm` WEN equ 0x08; `Cassette.asm` "bit 3 = WEN (1=protected, 0=can write)"; the
  cas_status decode reads CIP|WEN: 00=loaded+writable, 01=loaded+protected, 11=no cassette,
  10=invalid). Driven by the host-side `.cas`
  write-protect toggle.
- **Encode each bit's polarity EXPLICITLY in the device** — the `(N)` bits are asserted-low;
  mixing one up yields a tape that reads as perpetually-ending or never-present.
- PRI/READY/STRAP belong to the future **printer device** (shared input, like PRD on CPOUT).

**TO CONFIRM from disassembly:** does the ROM sample RDA on **both** RDC edges (every toggle
= one bit) or only one direction? Sets how many RDC toggles/byte the encoder generates.

Read handler sketch (active-low applied per bit):
```
read(0x20):                        # CPRIN
    byte v = 0
    if !cassettePresent: v |= 0x10     # CIP=1 when no cassette
    if !tapeAtEnd:       v |= 0x20     # BET=1 when tape OK (0 at end)
    if writeProtected:   v |= 0x08     # WEN bit set = PROTECTED (confirmed: 1=protected, 0=writable)
    if RDC_level:        v |= 0x40
    if RDA_bit:          v |= 0x80
    # PRI/READY/STRAP from printer device
    return v
```
(The WEN/`(N)` lines above are illustrative — implement each bit's asserted level from the
table, not from this sketch's shorthand.)

### Device read/write handler (fully determined)
```
read(port):                        # ports 0x00..0x09
    if (CPOUT & 0x40) != 0:         # KBIEN set -> scan ON
        return (port == 0) ? AndOfAllRows() : 0xFF   # only port 0 meaningful
    else:                          # scan OFF
        return RowColumns(port)                        # per-row, active-low (pressed=0)

write(0x10, value):                # CPOUT shared latch
    CPOUT = value
    # bit6 KBIEN -> keyboard scan mode
    # bit7 PRD   -> printer device
    # bits3-0 FWD/REV/WCD/WDA -> MDCR drive lines (§5b)
```
`AndOfAllRows()` and `RowColumns()` are BOTH computed from the **real intersection matrix**
(model rows×columns as actual crosspoints, NOT a flat pressed-set) so **ghosting/rollover
emerges naturally** — some software depends on matrix quirks; hard to add later.
- **Ghosting mechanism (CONFIRMED, milestone-8):** the P2000T keyboard is **diode-less**, so
  pressing 3 corners of a matrix rectangle — (R,C0), (R2,C0), (R2,C1) — lets current loop
  R→C0→R2→C1 and pulls C1 low when scanning row R, registering a **phantom 4th key** at (R,C1)
  that isn't pressed. Authentic; some software depends on it. Implementable as an O(R×C)
  3-corner search per column (no circuit sim needed) — same result at 10×8.
- `0xFF` chosen for the non-meaningful ports 1–9 while scanning (consistent "no key",
  active-low). Confirm against hardware if they float differently.

### Debounce/repeat handled by the ROM — emulator does NOT
The ISR does its own debounce + auto-repeat at 50 Hz. The device just presents a **stable
matrix** each frame. No debounce logic in the emulator.

### Host-key mapping (the real work — two modes, BOTH now built, UI milestone 3a)
- **Positional ("P2000 Authentic"):** host physical key → same physical P2000 key. The
  **live default** — correct P2000 shift-pairings (e.g. Shift+8 → `(`, Shift+2 → `"`) fall out
  for free since the P2000's own ROM/hardware decides the shifted character.
- **Symbolic ("Standard-Host"):** host key producing character X → the P2000 key/combo that
  also produces X, where one exists. New, opt-in (UI milestone 3a). Characters with **no P2000
  equivalent at all** (`~^{}\|` among others — see below) are a deliberate silent no-op, not a
  best-guess substitute.
- Both modes now built, plus a graphical **soft-keyboard window** (click-to-press, sticky
  Shift/CODE) as the escape hatch for keys with no host equivalent at all (ZOEK/START/STOP/
  INL/OPN/DEF and other numeric-keypad functions, reachable from a host numpad in EITHER mode
  since they're plain matrix crosspoints — see below — but also clickable for anyone without a
  numpad).
- **Full, current 10×8 key/legend table lives in `docs/Keyboard/keyboard matrix.md` and
  `docs/Keyboard/keyboard mappins.md`** (UI project) — kept as the single canonical source
  through several correction passes during ms.3a's implementation and a real-hardware
  verification session; **do not duplicate the full table here**, only the hardware-level facts
  below that are worth having independent of the UI implementation.

### Confirmed keyboard facts (2026-07-19/20, from UI milestone 3a's implementation + a real
physical P2000T hardware verification session — supersedes the "still to confirm" note below)
- **SHIFT — CONFIRMED positions:** Left Shift = matrix **(9,0)**; Right Shift = matrix **(9,7)**
  (two independent physical shift keys, both real crosspoints, not a single shared one).
- **CODE — CONFIRMED position (4,0); function is cartridge/software-dependent, NOT a fixed
  second shift level or graphics set (both had been speculated, both wrong).** With the
  **BASIC cartridge** specifically, CODE controls **LIST display speed** and is used while
  **editing BASIC program lines**. The ROM/cartridge reads the matrix bit and decides its
  meaning, exactly like SHIFT — the keyboard device itself needs no CODE-specific logic.
  Different cartridge software could use the bit differently; this is a BASIC-cartridge fact,
  not a universal one.
- **WIS — CONFIRMED position: Shift + the numeric-keypad "7"/cassette-icon key (port 0x06 bit
  3, i.e. matrix (6,3)).** Closes the last unlocated item from §5b's BASIC↔cassette UI surface
  list (WIS/ZOEK/START/STOP).
- **The numeric keypad's special functions are ALL the shifted meaning of a plain digit key,
  same convention throughout** (unshifted = digit, Shift = function) — ZOEK/START/STOP/INL/
  OPN/DEF/WIS/M/etc. are not separate physical keys from the numpad digits, they're what the
  same crosspoints mean when read with SHIFT asserted. Since the `Keyboard` device already
  accepted raw crosspoints with no character-level path (machine CLAUDE.md §17 ms.8), and
  these are ordinary Shift+digit combinations like any other, **no machine-layer change was
  needed to reach any of them** — confirms the original ms.8 assessment that the device was
  already "ready to accept key presses at any crosspoint."
- **Keyboard scan timing quirk — CONFIRMED (machine-level test, `assets/BASIC.bin` booted
  against real ROM entry points):** releasing an already-held SHIFT crosspoint and pressing a
  different key in the **same field** still reads as shifted on the P2000 side — the monitor
  ROM's keyboard scan needs to observe **at least one real field (20 ms) with SHIFT genuinely
  released** before a subsequent press registers as unshifted, even though the emulated matrix
  state is technically already correct the instant both changes land. Matters for anything that
  programmatically "releases SHIFT and presses a new key" in the same host input event (as
  Standard-Host mode's redirect logic must for characters like `@`/`#`/`;`/`<` that sit on an
  unshifted P2000 crosspoint). No such gap is needed the other direction (asserting SHIFT fresh,
  with none previously held, then pressing a key — behaves like an ordinary Shift+key combo).
- **BASIC forces all letters to uppercase**, regardless of what the ROM's own keycode→ASCII
  table (dumped from `assets/BASIC.bin` at Z80 address 6164 during this investigation) contains
  for the lowercase entries — confirmed by direct testing, not inferred from the table alone.
- **Keycode arithmetic exists but is NOT reliable on its own — CONFIRMED, use with caution:**
  unshifted keycode = `row×8+col`, shifted keycode = `+72`, holds for most positions but
  **wrongly predicted Shift+3** (predicted `#`, actual — confirmed twice, including on real
  hardware — is `£`). Treat the formula as a starting guess only; verify each position against
  a direct machine-level `SetKey`→VRAM read (or real hardware) before trusting it, the same
  lesson that caught the original mis-transcription of the (8,4) key as an accent mark when it
  actually renders **¼ (unshifted) / ¾ (shifted)** — a fraction glyph pair, not any accent.

### Still to confirm (minor)
- Whether ports 1–9 float or read 0xFF while scanning is ON (assumed 0xFF above).
- The exact matrix position/legend for a small number of keys not yet independently confirmed
  outside the UI project's own working table (`docs/Keyboard/keyboard matrix.md`) — e.g. the
  numpad "5" key's shifted function, which doesn't echo a character and triggers what looked
  like a screen-level redraw effect the owner's session flagged as "genuinely unclear," not
  chased further.

---

## 6. Reference implementations — what they do and don't cover

### M2000 — the canonical P2000 emulator
- Hosted under the `p2000t` GitHub org (P2000T Preservation Project). C, GPL-3.0,
  actively maintained (commits as recent as mid-2025).
- Originally by Marcel de Kogel (1996–1997); now maintained by Ivo Filot,
  Martijn Koch, Dion Olsthoorn. Allegro 5 port by Stefano Bodrato (2013).
- **Good behavioural oracle** for: ROM behaviour, cassette timing, keyboard matrix,
  SAA5050 character set, character rounding.
- **Does NOT model bus contention.** Descends from a 1996 instruction-level emulator;
  renders per-frame from VRAM. For the hardest part of this project it has no answer.
- License note: GPL-3.0. Read it freely to *understand* behaviour, but write your own
  implementation if you want a different license for the .NET project.

### MAME — better architectural reference
- Driver reorganised into `src/mame/philips/` (old `src/mame/drivers/p2000t.cpp`
  paths now 404).
- **Confirmed: MAME does NOT model the CPU/video contention or the black glitch.**
  It renders per-scanline from a clean `videoram` array; its SAA5050 device reads
  VRAM with no collision penalty; it doesn't tick a 5020-style address counter
  against the Z80. The whole driver review was about layer resolution (render at
  lowest common multiple, hires as slot device) and device structure — zero mention
  of contention artifacts.
- MAME *does* model the 80-col clock switch (port 0x00 bit 0 / port 0x70 bit 0).
- Good for: how a cycle-accurate driver is *structured*, the SAA5050 device, the
  scheduler model. Verify specifics against current source.

### higan / ares (byuu / near)
- Gold-standard reference for how a deterministic cycle-exact core is organised
  (cooperative scheduling). You likely don't need cooperative threading for a machine
  this simple — tick-based will do — but the determinism discipline is worth studying.

### Net-net
**No existing emulator reproduces the black-glitch artifact.** The cycle-exact bus
model is therefore the novel contribution of this project, and must be validated
against real-hardware capture, not against another emulator.

---

## 7. The Z80 core is the real constraint

- Instruction-stepped Z80 cores (execute a whole opcode, report "took N cycles") are
  **useless here** — intra-instruction bus activity (when MREQ drops, when the refresh
  address appears during M1) is exactly where contention lives.
- You need a **T-state-accurate core** exposing bus state at sub-instruction
  granularity: step one T-state at a time, or issue bus-access callbacks at the
  correct intra-instruction cycle.
- Most .NET Z80 cores (e.g. Konamiman's Z80dotNet) are instruction-level with
  memory-access *events* but not full per-T-state bus modeling. Budget for heavily
  adapting one or writing your own from documented Z80 timing tables.
- **This is the single biggest piece of work in the project** — more than the
  SAA5050, more than the cassette.

### DECISION: write our own cycle-stepped core in C#
Chosen path. Rationale: in a cycle-stepped design the **pin/bus state exposed each tick
maps one-to-one onto the bus fields the contention logic reads** (§4), so a purpose-built
managed core is the cleanest fit AND keeps the project single-language and debuggable.
The core advances **one T-state per step** and exposes, every tick: address bus, data
bus, and control lines (MREQ, IORQ, RD, WR, M1, RFSH). The video fetch unit and
`bus.Resolve()` consume that state directly.

### The distinction that disqualifies most cores
Two architectures hide under "cycle-accurate":
- **Instruction-stepped + T-state counter:** runs a whole opcode, then reports N
  T-states (+ optional memory/IO events). Count is right; **intra-instruction bus timing
  is invisible** (which T-state MREQ drops on; refresh address during M1). Useless for
  contention.
- **Cycle-stepped (tick-based):** one T-state per call, full pin/bus state every tick.
  Required here.

### Reference cores (study / oracle — NOT the engine)
- **floooh `chips` z80.h (C, MIT/zlib)** — gold-standard cycle-stepped design; tick
  function exchanges a 64-bit **pin mask per clock cycle** = exactly the control lines +
  address/data our model needs. Model our C# tick/pin design on this. Write-up:
  floooh.github.io "A new cycle-stepped Z80 emulator" — THE architectural reference.
- **Dotneteer / spectrum-dotnet-engine (SpectNetIDE / Klive lineage, Istvan Novak)** —
  closest existing C# match: genuinely T-state-stepped (increments Tacts per T-state,
  lets other components act within each clock cycle; mem ≥3 / IO ≥4 T-states), already
  contention-aware (Spectrum). Use as the **C# reference for how per-T-state stepping +
  contention hooks look**. Caveat: Spectrum contention is WAIT-STATE based (CPU stalls)
  — OPPOSITE polarity from our video-loses model; reuse the hooks, not the logic.
- **Konamiman/Z80dotNet** — mature, thorough documented+undocumented behaviour, but
  instruction-stepped (reports TStatesElapsedSinceStart; events at access point). Keep as
  an **instruction-behaviour oracle** for cross-checking, not the core.
- **neilhewitt/Zem80** (MIT, .NET 10, redesigned core Dec 2025) — readable idiomatic
  C#; bus sub-instruction granularity UNVERIFIED; author flags IM0 untested / IM2 buggy.
  Readable starting reference only.
- **sklivvz/z80** (BSD) — clean IMemory/IBus/ULA separation worth reading for interface
  design; whole-frame Tick(N) API suggests batch exec, per-tick bus exposure unconfirmed.

### Validation (REQUIRED — both)
- **SingleStepTests "z80" suite** (JSON; formerly raddad772/jsmoo) — per-instruction,
  **cycle-by-cycle bus activity**: every memory/IO access with address, value, pin state
  per T-state. This is exactly the assertion a bus-exposing core needs; nothing else
  tests intra-instruction timing as directly. **Primary harness for the cycle-stepped core.**
- **ZEXALL / ZEXDOC** (Frank Cringle's exerciser, via J.G. Harston) — instruction/flag
  correctness incl. undocumented behaviour. Layer on top.
- A core passing BOTH is trustworthy enough to build contention on. Run SingleStepTests
  in CI per-commit; ZEXALL as a slower full-suite gate.

### Build notes
- Drive at real cycle budget: **50,000 T-states/frame** (2.5 MHz ÷ 50 Hz), one VBLANK
  interrupt/frame (§4a).
- Model WAIT and BUSRQ/BUSAK pins even if unused at first — cheap now, painful to retrofit.
- Signal priority at instruction boundary: RESET, NMI (soft-reset button), INT.
- Keep the core free of P2000T specifics — it only sees the bus. The machine (RAM, video
  fetch, slot devices) lives outside and reacts to pin state. This is what lets the same
  core run the contention model deterministically.

---

## 8. Source links

### Preservation project (primary hub)
- Org: https://github.com/p2000t
- M2000 emulator: https://github.com/p2000t/M2000  (releases: /releases)
- Software (cartridge/cassette/disk dumps, test material):
  https://github.com/p2000t/software
- Documentation (scanned manuals, schematics, Nat.Lab. material):
  https://github.com/p2000t/documentation
  - **Monitor Documented Disassembly** (cassette driver = primary source for MDCR
    ports, `.cas` block protocol, ROM cassette entry points):
    `programming/Monitor Documented Disassembly` — authored by the project author.
- ROM checksums (MAME set): `p2000.rom` 4 KB CRC 650784a3 /
  SHA1 4dbb28adad30587f2ea536ba116898d459faccac; `basic.rom` 16 KB CRC 9d9d38f9 /
  SHA1 fb5100436c99634a2592a10dff867f85bcff7aec. BASIC revs: v1.0, v1.1 NL, v1.1A2.
- Browser build: https://github.com/p2000t/p2000t.github.io  /  https://p2000t.github.io/

### Curated maintainer site (Ivo Filot)
- https://philips-p2000t.nl/  (hardware section, Z80 asm + C cross-dev, tools)
- Video port specifics: https://philips-p2000t.nl/hardware/videoport.html

### Primary Philips technical description (architecture / memory map)
- http://home.iae.nl/users/pb0aia/cm/p2ktech.html  (blocks automated fetch — open in browser)
- Mirror: https://oldskool.silicium.org/divers/p2000t.htm  (also blocks automated fetch)

### Chip-level references
- **SAA5020 datasheet PDFs (located — but ALL are image-only scans, no text layer):**
  - https://martin-jones.com/wp-content/uploads/2017/12/saa5020.pdf  (1981 Mullard,
    phone-scanned by Chris; also at martinjonestechnology.files.wordpress.com/2017/12/saa5020.pdf)
  - https://www.fabian.com.mt/viewer/41850/pdf.pdf  (via the Fabian parts listing)
  - Landing pages: datasheetcatalog.com (S/A/A/5/SAA5020), alldatasheet.com, datasheetarchive.com
  - Because they're scans, the horizontal/vertical **timing-diagram figures must be read
    by eye** — they cannot be extracted as text. This is where the non-derivable fetch
    bus-occupancy parameter lives (see §4a).
  - Functional summary (manufacturer): timing chain for the European 625-line standard;
    generates the signals to extract data from memory; pairs with SAA5030 (VIP),
    SAA5050 (TROM/char-gen), SAA5040 (TAC), SAA5045 (GALA). DIL-24, 5 V, ~50 mA.
- SAA5050 die-shot reverse engineering (gate-level glyph behaviour, teletext corner cases):
  https://github.com/lanceewing/saa5050
  and thread: https://stardot.org.uk/forums/viewtopic.php?t=21608
- SAA5050/5020 discussion (incl. pin-27 erratum, lazy 64-byte row alignment):
  https://stardot.org.uk/forums/viewtopic.php?t=14267  ("Playing with the SAA5050")
- MAME hires/80-col PR (driver render approach, mode switching, NMI):
  https://github.com/mamedev/mame/pull/7577

### Z80 core — references, validation, design
- floooh `chips` z80.h (cycle-stepped, MIT/zlib): https://github.com/floooh/chips
  - Design write-up (THE reference): https://floooh.github.io/2021/12/17/cycle-stepped-z80.html
- Dotneteer spectrum-dotnet-engine (C# T-state-stepped, contention-aware reference):
  https://github.com/Dotneteer/spectrum-dotnet-engine  (also SpectNetIDE / Klive)
- Konamiman/Z80dotNet (instruction-behaviour oracle): https://github.com/Konamiman/Z80dotNet
- neilhewitt/Zem80 (MIT, .NET 10, readable): https://github.com/neilhewitt/Zem80
- sklivvz/z80 (BSD, clean IBus/IMemory/ULA): https://github.com/sklivvz/z80
- redcode/Z80 (ANSI C, LGPL, configurable granularity): https://github.com/redcode/Z80
- **Validation — SingleStepTests z80** (per-cycle bus activity, JSON):
  https://github.com/SingleStepTests/z80
- **Validation — ZEXALL/ZEXDOC** (Cringle exerciser; Harston build/conversions).

### Community
- RetroForum P2000T thread (Dutch community, helpful for questions).
- libretro M2000 core docs: https://docs.libretro.com/library/m2000/

### Recommended test software (from p2000t/software)
- Arcade conversions good for testing: Brick-Wall (Breakout), Doolhof (3D Maze),
  Fraxxon (Phoenix), **Ghosthunt (Pac-Man — exercises the inverted-colour trick)**,
  Lazy Bug (Lady Bug).

---

## 9. Suggested next steps / open items

1. **Z80 cycle-stepped core (DECIDED: write our own in C#)** — biggest task. One
   T-state per step, full bus/pin state exposed each tick (address, data, MREQ, IORQ, RD,
   WR, M1, RFSH). Model the tick/pin design on floooh's z80.h; use Dotneteer's engine as
   the C# per-T-state/contention reference; Konamiman as instruction-behaviour oracle.
   **Validate against SingleStepTests z80 (per-cycle bus activity, run in CI) + ZEXALL/
   ZEXDOC (instruction/flag correctness).** Model WAIT + BUSRQ/BUSAK early. Keep the core
   P2000T-agnostic — it only sees the bus.
2. **SAA5020 datasheet** — already located (§8), but image-only; read the horizontal/
   vertical timing diagrams by eye to get the fetch bus-occupancy and display-start offset.
3. **Pull board schematics** from the p2000t/documentation repo (use Philips originals,
   not hobbyist redraws — remember the pin-27 erratum). Resolve **contention scope**:
   VRAM-only vs. whole dynamic-RAM bus, and whether CPU RAS/CAS is gated during display.
4. **Real-hardware captures** (the two unknowns no document answers):
   - **Corruption mode:** trigger logic analyzer on Z80 /MREQ during active display;
     record data bus + SAA5050 data inputs that character clock → distinguishes data
     bleed / contention-garbage / fetch-suppression. Add simultaneous RGBS video capture.
   - **Fetch bus occupancy:** /MREQ vs RAM /CAS|/RAS during active display → how much of
     each 1 µs slot the video owns → glitch density.
5. **Build-against-now defaults** (swap in captured truth later):
   - Z80 unconditional priority; collided slot → blank/black cell; per-slot, no persistence.
   - Contention only during active-display fetch slots (none in v-blank / h-blank).
6. Implement the inverted-colour (160–255) behaviour early — needed for Ghosthunt etc.
7. Implement 80-col clock switch (port 0x00 bit 0 / port 0x70 bit 0) once 40-col is solid.
8. **Hires overlay board (defer until stock T is solid):** implement as an opt-in slot
   device — parallel bitmap fetch on the master clock, LCM-resolution compositing with
   the teletext layer, separate bitmap-RAM contention. Extract the board constants
   (resolution, ports, RAM map, mixing rule, dot clock) from the MAME PR #7577 slot
   device before writing it; do not guess them.
9. **Cassette (MDCR) device:** build as one device with a timing-policy strategy —
   authentic bit-level port path (cycle-exact, deterministic) + turbo ROM-trap block
   path (instant). Separate always-fast host-side `.cas` manipulation API. Source the
   `.cas` layout + MDCR ports + ROM cassette entry points from the project's own
   Monitor Documented Disassembly and M2000 — NOT from MSX/Atari `.cas` formats. Point
   tape regression tests at authentic mode.
10. **Slot / bus-expansion model:** build ONCE — but slots are **TYPED by bus discipline**
    (§5c): SLOT1 memory-mapped (page table), SLOT2 I/O-mapped (port dispatch + INT),
    internal extension = full bus (floppy/CTC). At least two slot interfaces sharing the
    common device interface; absent slot reads open-bus 0xFF (makes ROM presence-probes
    work). Pin layouts to be added when supplied.
11. **FDC card (slot device):** emulate a µPD765-family chip (register interface, not
    magnetism); presence falls out of the slot model; same TimingPolicy (authentic delays
    vs instant) — no ROM trap needed. Separate host-side disk-image API. Confirm chip,
    ports, DMA/non-DMA, INT wiring, and 35-track single-sided geometry from MAME PR #7577,
    the own disassembly (presence probe), and JWSDOS images.
12. **Interrupt aggregator (build once):** wired-OR all sources onto the core INT pin;
    video (always) + CTC/SIO/PIO/FDC (when present). **T-first: implement ONLY the video
    50 Hz VBLANK source** (IM1 → RST `0x0038`) — the ROM's CTC probe times out and falls
    back automatically IF absent = open-bus + no INT.
12a. **IM2 daisy chain (OPTIONAL, config-driven — not M-only):** an opt-in `DaisyChain`
    module that mounted Z80-family peripherals register into (IEI/IEO priority, vector
    supply at int-ack, RETI/RETN in-service tracking). Required whenever the floppy
    interface / serial / CTC / SIO / PIO / other expansion card is present — on T or M.
    A bare T never instantiates it. Core already supports it (see CLAUDE addendum):
    int-ack reads vector from bus, opcode fetches stay snoopable.
13. **CTC (Z8430) device — defer to M phase:** 4 channels, timer(÷16/÷256)/counter modes,
    TC=0 means 256, ch3 has no output, cascading ch_n→ch_(n+1). Timer channels tick on the
    prescaled master clock; counter channels on delivered edges. Wire into the daisy chain.
    Source channel assignments, port base, vector base, clock source, and priority from the
    P2000M schematic / disassembly.
14. **Common device interface (define EARLY):** every device (CPU, RAM, video, cassette,
    FDC, CTC, slot cards) implements `Reset`, `SaveState`/`LoadState`, and where relevant a
    `TimingPolicy` hook. SaveState on every device from day one — retrofitting cross-device
    serialization later is miserable.
15. **UI (Avalonia, MVVM):** display = main window; config / keyboard / debugger / cassette
    deck as satellite observer windows. Menu + toolbar + status bar (activity LED, speed %,
    model) — NOT custom title-bar buttons. Shortcuts F5/F11/Shift+F11/F12/F6/F8.
    Drag-and-drop images. Bare-by-default machine.
16. **Config = reset-to-apply:** topology changes (model T/M, RAM, slots, disk interface)
    queue + cold-reset. Model selector is the top-level axis gating the rest.
17. **Full debugger (first implementation):** full register file incl. WZ, mem view,
    disasm, exec + mem R/W/X + I/O-port breakpoints, in-frame cycle counter, step
    over/out, run-to-scanline / run-to-cycle. Snapshot-based; never races the core.
18. **Save-state:** serialize CPU + RAM + all device state; deterministic core makes it
    near-free; use it to reproduce contention glitches from a frozen state.
19. **Config vs. state = two serializable concerns (§3a):** `MachineConfig` (topology, JSON,
    hand-editable, reset-to-apply, machine-assembly level) as `.cfg`; state capture (device
    blob) as `.state` with the full `MachineConfig` embedded as header. Restore = rebuild
    from embedded config then deserialize device state. Version both formats. Config is
    derivable FROM a state, never the reverse.

### Known-good test cases for the contention model
- **Ghosthunt** — exercises the 160–255 inverted-colour trick.
- A deliberate stress ROM: tight loop hammering VRAM during active display should
  produce heavy single-cell speckle; the same loop confined to v-blank should display
  cleanly. This is your regression test for the glitch model once captured.
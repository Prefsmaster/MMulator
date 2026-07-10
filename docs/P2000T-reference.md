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
- **Internal-slot board (three-way): none / RAM-only / floppy+RAM** (§5). Determines upper
  memory AND whether the FDC/CTC + disk exist. "More RAM" (RAM-only board) is separable from
  "disk present" (floppy+RAM board).
- **Slot population** (three typed slots, §5c): **SLOT1** (external, memory-mapped ROMs:
  BASIC, DB manager, other ROM carts); **SLOT2** (external, I/O-mapped expansion hardware);
  **internal extension** (floppy/CTC card — populated in M, optional on T).
- **Disk interface present?** (internal-slot floppy/CTC card) + mounted disk image(s).
- **Cassette:** `.cas` file selectable via file dialog (also drag-and-drop).
- **Display mode (4-way, over the same rendered scanlines):** **interlaced (comb)** — authentic
  default (per-field persistent non-erased buffer → reproduces comb in fast motion);
  **progressive** — both fields composited per frame, smooth; **even-only** / **odd-only** —
  present a single field (discard the other), no comb, half vertical detail. Odd-only is slightly
  smoother (the CRS/RA0 rounding lands on odd sub-scanlines); field-only defaults to line-doubling.
  (Consumer contract: when `FieldComplete` fires, `Video.IsOddField` has ALREADY toggled to the
  next field, so the field just completed is **`!IsOddField`** — gate even-only/odd-only/progressive
  presentation on that. Confirmed P2000.UI ms6.)
  Plus: integer-scaling toggle (crisp nearest-neighbour vs smoothed), PAL aspect-ratio correction,
  optional scanline/CRT shader, **"show contention glitches"
  toggle**, and a debug overlay highlighting which character cells were corrupted this
  frame (turns the headline feature into something visible/testable).
- **Audio:** mute + volume (minor for a 1-bit beeper, but present).

### Debugger (DECIDED: full debugger in the first implementation)
- **Full register file:** AF/BC/DE/HL + primes, IX/IY, SP, PC, I, R, and **WZ/MEMPTR**
  (implemented in the core — expose it), plus IFF1/2, IM, and the flag bits broken out
  (incl. YF/XF).
- **Memory watch windows (MULTIPLE, independent):** each is an observer over the state snapshot
  with its own address range — freely spawnable (stack, sysvars, a data structure, etc.). Live
  hex + ASCII, refreshed per frame / per step. **Highlight bytes changed since last refresh**
  (colour flash) — turns a static dump into a view of what the program is touching. Optional
  **"follow" a register pair** (follow HL / SP) so a window tracks what the code is working on.
  Read-only; never touches the live core.
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
  - **Symbol resolution:** annotate addresses/ports with names from the existing disassembly
    (e.g. `OUT (0x10)` → CPOUT, `CALL 0x0038`). Turns hex into readable code; where the
    disassembly work pays off directly.
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
- **Disk image (deferred with the FDC): raw SECTOR image, geometry ASSUMED** — 35-track
  single-sided (confirmed hardware, §5d), extension **`.dsk`** (or `.img`). NOT flux; an image.
  **No de-facto P2000 disk-image convention exists in public sources** (unlike `.cas`), so raw
  sector is the pragmatic default. **CONFIRM what JWSDOS dumps actually use** from the
  p2000t/documentation repo / RetroForum when the FDC milestone is undertaken — adopt their
  layout if one is established. (JWSDOS = the P2000 disk OS; CP/M also existed.)

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
- **Display-start offset:** where in the ~312 lines the 240 active lines begin fixes
  **when VBLANK fires relative to the display window** — which the panning tricks
  depend on. (Contention timing itself is implemented; last-fetch slot LineTState 97, line
  boundary 159 — see machine md milestone-10 finding.)

---

## 4a. Derived timing framework (PAL 625-line)

The SAA5020 datasheet scans are phone-photographed images with no text layer, so the
exact timing tables must be eyeballed from the scan or captured. But the functional
role ("timing chain for the European 625-line standard") plus confirmed geometry makes
the structure fully derivable.

Keyed off the PAL line period and the 2.5 MHz clock:
- **Line:** 64 µs → 64 × 2.5 = **160 Z80 T-states per scanline**
- **FIELD:** 50 Hz → **50,000 T-states**; 160 × 312.5 = 50,000 → **~312–313 lines/field**.
  NB: the 50 Hz cycle is a **FIELD, not a frame** — the P2000T is interlaced, so a complete
  **frame = two fields = 25 Hz** (even + odd). The 50,000-T-state / 240-active-line cycle is one
  field. Interrupt + CTC ch3 fire per field (50 Hz); the display composes a frame from two
  (§3a display modes). (Terminology corrected per milestone-5 implementation finding.)

**Vertical structure:**
- 24 rows × 10 scanlines = **240 active display lines**
- → **~72 lines of vertical blank** (no fetches; CPU RAM access never glitches video here)

**Horizontal structure** (pinned by the confirmed 6 MHz dot clock):
- 40-col = 6 MHz dot clock, 6 dots/char → **1 MHz char-fetch rate = 1 µs/character**
- 40 columns → **40 µs active fetch per scanline**, leaving **~24 µs horizontal blank**
  (sync + porches + borders), where video isn't fetching
- 80-col mode doubles the dot clock to 12 MHz (still 1 µs/char-pair region; see §5)

**What this means under the Z80-priority model:** glitches are possible during the
240 active lines whenever the Z80 drives a RAM access in a fetch slot. They're
impossible during the ~72 vblank lines and the ~24 µs horizontal-blank portion of each
active line, because the video isn't fetching then. So a program gets "free" RAM time
in vblank + h-blank automatically; visible glitching scales with how much RAM work the
CPU does inside the active-display fetch windows.

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

**Bank switching (T/102):** the 8 KB window at **0xE000–0xFFFF** shows one of **6 banks**
(8 KB each) selected by writing **0–5 to I/O port 0x94** (write-only bank register). 6 × 8
KB = 48 KB banked; combined with the 16 KB expansion that's the "+64 KB over base" figure
(confirm arithmetic, below).

**0x94 has NO hardware range restriction — make bank count CONFIGURABLE.** The original
firmware only ever writes 0–5, but the port itself doesn't clamp; a **modern
bank-switching module could expose up to 256 pages** (2 MB in that window) by honoring any
byte on 0x94. So parameterize:
- Page-table bank pointer indexes an array of 8 KB pages whose **count comes from config**,
  not a constant. `bankCount = 6` for a faithful T/102; up to 256 for a homebrew module.
- Port 0x94 stores the **raw written byte** as the bank index — no masking.
- **Index ≥ populated bank count → reads open bus** (same convention as any unpopulated
  region; keeps a partially-populated custom module, e.g. 16 banks, sane). For a faithful
  T/102 this never triggers (firmware stays 0–5).
- This is the "emulator as a superset of the hardware" pattern: model the original
  faithfully, parameterize the limit so expansion hardware is expressible with no
  special-casing. Costs an `int bankCount` in config instead of a hardcoded 6.

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
- For 0xE000–0xFFFF on T/102: a **switchable page pointer** driven by port 0x94.
- Port 0x94 = another I/O-dispatch entry: write-only bank register the page table reads when
  resolving 0xE000–0xFFFF. **SaveState serializes the current bank index + RAM contents.**

### Open items to confirm (from disassembly / schematic)
1. **Bank/64 KB arithmetic:** does "+64 KB over base" = the six 0x94 banks (48 KB) PLUS the
   16 KB expansion? (Assumed yes.)
2. **Reset default at 0xE000–0xFFFF** before any write to 0x94 — implemented as **bank 0** by
   default (milestone-2 finding; least-surprising, harmless since firmware writes 0-5 before
   relying on banking). Confirm against disassembly/schematic if it matters.
3. **Out-of-range 0x94 writes** — RESOLVED: no hardware restriction. Bank count is
   configurable (6 for T/102, up to 256 for a modern module); index ≥ populated count reads
   open bus. See bank-switching note above.
4. **PTC-96K addressing:** how 16 KB + 64 KB combine — is the 64 KB also behind 0x94
   (more/wider banks) or mapped differently? Scheme unclear from the base description.
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
- Built-in Mini-Cassette (MDCR) drive, ~42 KB per side, **6000 baud**, FM encoding,
  directly-coupled analog circuitry. Treated like a floppy from the user's view
  (CLOAD / CSAVE / directory).

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
- ~42 KB per side, **6000 baud**, FM-encoded, directly-coupled analog circuitry to the
  head (the analog part is below the digital interface you emulate).
- Bidirectional, **floptical-style random-ish access**: treated like a floppy. CLOAD
  searches the tape for a named program; CSAVE finds free space; a directory command
  exists. So your tape model needs a **position** and a **seek**.

### Two interception levels = your two user-selectable speed modes
A **timing-policy strategy** on one cassette device. The device owns the `.cas` image, a
tape position, and a transport state machine; only the timing policy differs.

1. **Authentic speed — intercept at the I/O ports (bit level).** The MDCR controller
   exposes read-data, write-data, status (ready, tape-present, end-of-tape…), and
   motor/command lines at its I/O ports; the monitor ROM cassette driver bit-bangs them.
   Meter bits in/out at real 6000 baud off the **same deterministic master clock** as
   the core; ROM routines run completely unmodified. Loading takes exactly as long as
   hardware did. **This is the cycle-exact path — deterministic, replay/regression-safe.**
2. **Host speed — trap the monitor ROM cassette entry points (block level).** Detect the
   Z80 calling the ROM load/save/search routines; service the whole block transfer
   directly (copy between `.cas` image and RAM); set up the register/flag state the ROM
   would have returned; `RET`. Bit timing bypassed; transfer instant. **This is "turbo".
   It is a deliberate side-channel that breaks cycle-exactness for the transfer's
   duration — fine, but point tape regression tests at AUTHENTIC mode.**

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
Do NOT gate this behind the timing policy. Insert/eject, rewind, create-blank,
write-protect, directory browse, import/export, reorder/delete programs — host-side
container operations, always instant, independent of authentic/turbo. Keep as a distinct
interface so the two concerns don't tangle.

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
- Monitor ROM **cassette entry points** (load / search / save) and their register/flag
  **calling convention** (turbo trap).

### Useful confirmations already in hand
- ROM images (from MAME set): monitor `p2000.rom` 4 KB
  (CRC 650784a3 / SHA1 4dbb28adad30587f2ea536ba116898d459faccac); `basic.rom` 16 KB
  (CRC 9d9d38f9 / SHA1 fb5100436c99634a2592a10dff867f85bcff7aec).
- BASIC↔cassette UI surface: START (run loaded program), STOP (halt), ZOEK/search
  (show cassette index), WIS (clear cassette dialog) — these correspond to the ROM
  cassette interactions your traps will intercept.
- Loading note: P2000T BASIC identifies a program by only the **first character** of its
  name (e.g. `cload "h"` matches `"hello world"`). Relevant to your search emulation.

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
- **No NMI on SLOT2** (unlike SLOT1). SLOT2's async CPU line is INT only.**Internal extension slot — CONFIRMED (full Z80 bus: memory + I/O).** 40-pin connector, 2
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

---

## 5d. Floppy disk controller (FDC) — add-on card

### Fits as a slot device; presence check is free
The FDC is the **internal-extension-slot** device (§5c) — populated in the M, addable to a
T. The T's FDC card was **compatible with the P2000M's internal floppy controller**. The
monitor ROM probes for disk presence by
reading an FDC port:
- **Card mounted** → FDC responds with valid status → ROM concludes a disk subsystem exists.
- **Slot empty** → open-bus 0xFF → ROM detects nothing.

So "disk present" = "is an FDC slot device mounted." No special-casing. **The exact probe
routine (which port, which status bits, expected response) is in the project's OWN monitor
ROM disassembly** — authoritative; look there first.

### Emulate the chip, not the magnetism
The card uses a **µPD765-family FDC** (MAME's P2000T support implements it as µPD765, and
PR #7577 carried µPD765 emulation changes). Standard, well-documented chip — emulate the
register interface, not an analog medium:
- Main Status Register + Data Register; command / execution / result phases; 16 commands;
  user-programmable step-rate / head-load / head-unload; FM + MFM.
- INT line: in **non-DMA mode pulses per byte**, in **DMA mode pulses at command completion**.
- **Reset-interrupt behaviour matters for the presence probe:** if RDY is high during
  reset the FDC raises an interrupt within ~1.024 ms, cleared by Sense Interrupt Status.
  Model it faithfully or ROM detection may misfire.

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
monitor-ROM disassembly** (presence probe + driver command sequence), and **JWSDOS disk
images** from `p2000t/software` (test corpus):
1. **Exact FDC chip** — µPD765 per MAME; confirm against the card schematic. (A hobbyist
   building a *custom* P2000T controller once referenced the WD179x family, so pin down
   the standard club-card chip; µPD765 is the strong default.)
2. **FDC I/O port addresses** + any drive-select / motor / side latch on the card.
3. **DMA vs non-DMA** transfer mode (a Z80 add-on most likely runs the µPD765 **non-DMA /
   polled-interrupt**, where the driver moves each byte via the data register — changes
   the transfer loop and INT cadence). Confirm.
4. **FDC INT line wiring** to the Z80 (maskable INT / NMI / polled status). Note the NMI
   line is already used for the soft-reset button, so FDC INT is presumably elsewhere.
5. **Disk geometry / image format**: P2000M/T drives were **single-sided 35-track**;
   confirm sector size + sectors/track and what JWSDOS images contain.

---

## 5e. Interrupt architecture + Z80 CTC

### Two system-tick sources with auto-fallback (presence-probe pattern, via interrupt)
The monitor ROM probes for a **Z80 CTC** at boot: it configures the CTC and waits for a
CTC interrupt. If one arrives → CTC present, used as the system tick. If it **times out**
→ falls back to the **video circuit's 50 Hz VBLANK interrupt** as the tick. Same
presence-probe idea as the disk check (§5d), but the probe is "does an interrupt fire."

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
  the probe tells firmware which. (TO CONFIRM: does Lock gate NMI too, or only maskable INT?)

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
boolean **changed the `.state` device-stream format** — so the `.state` **version field must be
bumped** before any persisted `.state` files are released (see §3a versioning). No external
`.state` files exist yet in the T-first build, so the bump is deferred to when the UI save-state
path lands. (Test note: on a default T38 machine SP=0x0000, so an NMI pushes into the empty
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
2. **Channel assignments** — partially confirmed above (disk/comms/keyboard); exact channel
   numbers + baud/system-tick mapping still to pin down.
3. **CTC I/O port base** (4 channel addresses) + **IM2 vector base** the firmware programs.
4. **Clock source per channel** (system clock prescaled vs external crystal on CLK/TRG).
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

### Host-key mapping (the real work — borrow two modes, default symbolic)
- **Positional:** host physical key → same physical P2000 key. Good for games/muscle memory,
  wrong characters across layouts.
- **Symbolic:** host key producing 'A' → P2000 'A'. Good for typing; must handle P2000 shift
  states + characters with no modern equivalent (and vice versa).
- Offer both, default symbolic. The Dutch layout + special keys (CODE, ZOEK/START/STOP,
  cursor/function keys) won't all map → the **soft keyboard window is the escape hatch**
  (click to press unmappable keys).

### Still to confirm (minor)
- Where **SHIFT / CODE** and function keys sit in the 10×8 matrix (for the mapping table).
- Whether ports 1–9 float or read 0xFF while scanning is ON (assumed 0xFF above).

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
  
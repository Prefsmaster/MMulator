# CLAUDE.md — P2000.Machine

Project-specific contract for the machine layer. Read this together with the **root
`CLAUDE.md`** (global conventions, dependency direction, `Z80Tables` rule, thread/observer
boundary — NOT repeated here). This project wires the finished `Z80.Core` into a running
Philips **P2000T** (P2000M is a later phase).

**Hardware source of truth:** `docs/P2000T-reference.md` (relative to repo root). It has the
confirmed memory map, I/O ports, slot pin-outs, interrupt architecture, contention model, and
device details. This file specifies the *software architecture*; when it says "per the
reference doc," open `docs/P2000T-reference.md` for the exact hardware numbers rather than
guessing. (It is read on demand — it is NOT auto-loaded like CLAUDE.md — so open it explicitly
whenever a task needs hardware detail.)

---

## 1. What this project is

`P2000.Machine` assembles a `Z80.Core` CPU plus memory and devices into a cycle-exact,
bus-accurate P2000T. It owns the deterministic emulation loop, the memory page table, the I/O
port dispatch, the devices, and the interrupt wiring. It produces completed video frames +
audio + machine-state snapshots for a future UI/debugger to observe.

**Scope of THIS build: a working T.** Boot the real monitor ROM + BASIC, render the SAA5050
display, accept keyboard input, load/run `.cas` software, and model the CPU-vs-video
contention. The P2000M, the CTC, the FDC/floppy, the hires overlay, the SLOT2/expansion cards,
and the IM2 daisy chain are **deferred** (§16) — but their seams are built now so they drop in
without rework.

---

## 2. Locked design decisions (do NOT revisit without being asked)

1. **Bare by default.** A new machine has NO SLOT1/SLOT2 cartridge, empty cassette, no
   extension board (fixed base RAM), no disk. A bare machine exercises the ROM's
   presence-probe fallbacks (RAM sizing, CTC→video-tick, disk-absent) — the honest baseline.
2. **Single-threaded, deterministic emulation loop.** The whole machine advances in ONE tick
   loop on ONE thread. Bus contention is computed inside that loop, never across threads (see
   §5, §10, and reference doc §4). No `DateTime`/threads/randomness in emulation code.
3. **Reset-to-apply topology.** Configuration changes (model, board/RAM, slots, disk) rebuild
   the machine via a cold reset. The running machine's topology is fixed.
4. **One common device interface** (`IDevice`, §4). Every device — CPU wrapper, RAM/pages,
   video, keyboard, CPOUT latch, CPRIN, cassette, and later CTC/FDC/slot cards — implements
   it. `Reset` + `SaveState`/`LoadState` (+ `TimingPolicy` where relevant) from day one.
5. **Config and state are SEPARATE serializable concerns** (§11). `MachineConfig` = topology;
   state capture = running contents with the config embedded as header.
6. **The machine only ever sees the CPU's bus.** `Z80.Core` stays pure; the machine reacts to
   pins each T-state. No machine logic leaks into the core.

---

## 3. The central emulation loop (this is the heart of the project)

Drive `Z80.Core.Step()` one T-state at a time and service the bus, exactly as the core's
harness does (root/core CLAUDE.md §4), but against real memory + devices:

```
each T-state:
    // 1. Advance the video fetch unit (SAA5020 role) — it may issue a VRAM fetch this slot.
    videoTiming.Tick();                       // knows if a display fetch is active + its addr

    // 2. Advance the CPU one T-state.
    pins = cpu.Step(pins);

    // 3. Service the CPU's bus request against the PAGE TABLE (memory) or PORT DISPATCH (I/O).
    if (MREQ && RD)  pins = SetData(pins, pageTable.Read(addr));
    if (MREQ && WR)  pageTable.Write(addr, data);
    if (IORQ && RD)  pins = SetData(pins, ports.Read(portAddr, pins));   // M1+IORQ = int-ack
    if (IORQ && WR)  ports.Write(portAddr, data);

    // 4. Resolve contention: if the CPU drove a RAM access this slot, the video fetch loses.
    bus.Resolve();                            // see §10 — Z80 wins, video cell corrupted

    // 5. Advance other devices that tick on the master clock (cassette bit engine, later CTC).
```

Rules:
- **One machine tick == one CPU T-state.** Everything is slaved to the same master clock; this
  determinism is what makes contention reproducible.
- The video fetch is a **real bus participant**, not a side-check (reference doc §4).
- INT-ack is a normal bus service: when the CPU asserts M1+IORQ, the port/interrupt layer
  supplies the vector on the data bus (core already handles the ack timing).

### The framebuffer (the machine→UI output seam — a first-class surface)
The SAA5050 generate stage writes pixels into a **framebuffer the MACHINE owns**, not an
ad-hoc array inside the video device. This is the single output surface the video path writes
and any consumer (Avalonia display, a test, a screenshot writer) reads. Define it explicitly:
- **Format & size:** a `uint[]` of BGRA pixels (matches the SAA5050 render path). The SAA5050
  renderer emits **16 pixel-lanes per character** (NOT a naive 6×2=12) — the horizontal rounding
  is computed at sub-pixel resolution, which is why the glyph tables pack 2 bits/pixel and both
  jsbeeb and the owner's C# port unroll a 32-bit `chardef` 16× per character. So the framebuffer
  is **640 × 480** for 40×24 (40 chars × 16 lanes = 640 wide; 24 rows × 20 rendered scanlines =
  480 high, the 20 being 10 logical lines doubled for interlace). Do NOT "simplify" the width to
  480 — that discards the horizontal smoothing information. (See `SAA5050-implementation.md` §1/§2;
  this contract was corrected from an earlier mistaken 480×480 that under-counted the lanes.)
  The machine emits these pixels as-is; PAL aspect correction + integer scaling are the **UI's**
  job — and at 640×480 the pixels are already near-square on 4:3, so the UI scale is close to a
  straight integer scale, not a stretch.
- **Ownership:** the **machine owns the buffer(s)** and hands the video device a target to
  render into. The video device stays a pure pixel producer; the machine owns the frame
  lifecycle (render → complete → swap → expose).
- **Double/triple buffer across the thread boundary:** the emulation thread renders into a
  back buffer; on **frame completion (50 Hz DEW/VBLANK)** it swaps to a front buffer the UI
  reads. This is reference doc §3's "completed frames into a ring/triple-buffer" made concrete
  — the framebuffer is what flows through it. The UI/observer NEVER reads a buffer mid-render.
- **Consumers are interchangeable:** Avalonia copies the front buffer into a `WriteableBitmap`;
  a test asserts on its contents (this is how milestone 5/7 video tests work — headless, no
  window); a screenshot writer serializes it. The framebuffer is the contract.
- The machine stays **headless**: it fills and exposes framebuffers; it never opens a window.
  Windowing is the separate `P2000.UI` phase (§16 / root map).

---

## 4. The common device interface

```csharp
public interface IDevice
{
    void Reset();                         // cold reset behaviour
    void SaveState(IStateWriter w);       // serialize this device's runtime state
    void LoadState(IStateReader r);       // restore it
}
```
- Devices that model authentic-vs-turbo timing (cassette, later FDC) additionally take a
  `TimingPolicy` (authentic real-time delays vs instant).
- Memory-mapped devices register an **address range** with the page table; I/O devices
  register a **port range** with the port dispatch; some (later: internal-slot floppy/CTC) do
  both. See §6/§7 and reference doc §5c (slots are TYPED by bus discipline).
- Keep the interface small and stable; it is the seam every current and future device shares.

---

## 5. Memory — the per-model page table

Build a **page table** over the 64 KB space at machine-assembly time from the config
(reference doc §5). Key rules:
- **Per model, not T-then-patched.** The 0x5000–0x5FFF block differs: T = 2 KB VRAM
  (0x5000–0x57FF) + 2 KB open-bus; M = full 4 KB VRAM. Build the video region per model.
- **Open-bus (0xFF) for unpopulated regions.** This is what makes the monitor ROM's boot-time
  RAM-sizing probe work with no special-casing — same presence-probe pattern used everywhere.
- **Contiguous-RAM watermark:** the ROM RAM test stops at the first gap (memory expected
  contiguous). Keep the page table **physical-population-based** (a socketed chip responds);
  the config presets are contiguous, so the two coincide. Do NOT bake "stop at gap" into the
  page table — that's a firmware convention, not a bus fact.
- ROM pages read-only; SLOT1 cartridge region 0x1000–0x4FFF with CARS1 (0x1000–0x2FFF) /
  CARS2 (0x3000–0x4FFF); banked 0xE000–0xFFFF via port 0x94 with a **configurable bank
  count** (6 for T/102, up to 256 for a homebrew module; index ≥ populated → open bus).
- **Monitor ROM is the BASE machine, not a cartridge.** Fixed 4 KB at 0x0000–0x0FFF, present
  from power-on on every machine. The emulator loads a **built-in default monitor ROM**
  automatically; `MachineConfig` exposes an OPTIONAL `MonitorRomPath` override (null → default)
  for custom/patched monitor revisions. Do NOT model it as a slot/cartridge.
  - **The default monitor ROM is EMBEDDED as a compiled-in resource, not a loose file.** A bare
    machine must boot **out of the box with zero setup** (like flipping on a real P2000T) — no
    assets folder required, no file dialog, no missing-file failure mode on the default path.
    The `MonitorRomPath` override reads from disk only when deliberately set; the default path
    can never fail.
- **BASIC is a SLOT1 CARTRIDGE image, not a boot ROM.** It populates SLOT1 (0x1000–0x4FFF) via
  the normal slot config (image path). Empty SLOT1 → cassette-wait boot; populated → into
  BASIC (§5b). Keep monitor ROM and cartridge images as distinct config concepts.
- Fixed base RAM 0x6000–0x9FFF; 16 KB expansion 0xA000–0xDFFF (board-provided).

---

## 6. I/O port dispatch (with fan-out)

Route IORQ reads/writes by port address to registered devices. **A single port address may
have multiple listeners** — port **0x10 (CPOUT)** is a shared write latch (keyboard KBIEN +
printer + cassette FWD/REV/WCD/WDA), and **0x20 (CPRIN)** is a shared input (cassette
RDC/RDA/CIP/BET/WEN + printer). So the write path must **fan out** to all listeners of an
address, and the read path must **combine** contributing bits. Confirmed ports for the T
(reference doc §5f):
- **0x00–0x09:** keyboard matrix rows (read). Active-low. With KBIEN set, only port 0 is
  meaningful (AND of all rows); with KBIEN clear, ports 0–9 return their rows.
- **0x10 CPOUT** (write latch): bit6 KBIEN, bit7 printer data, bits3-0 FWD/REV/WCD/WDA.
  Implement as a `CPoutLatch` holding the shadow byte; it computes per-bit edges (WDA/WCD are
  edges the cassette encoder consumes; KBIEN/FWD/REV are levels).
- **0x20 CPRIN** (read): RDC/RDA (self-clocking cassette read pair) + CIP/BET/WEN status
  (active-low; drive from device state) + printer PRI/READY/STRAP.
- **0x94:** bank-select register (write-only) for the 0xE000–0xFFFF window.

---

## 7. Devices for the T-first build

Per the reference doc; implement to boot + run:
- **Video (SAA5050 + fetch timing):** char generator over VRAM (0x5000–0x57FF), 40×24, the
  160–255 inverted-colour trick (needed for Ghosthunt), the panning viewport, 50 Hz PAL
  frame. **Writes pixels into the machine-owned framebuffer** (§3 framebuffer contract — the
  device is a pure pixel producer; the machine owns the buffer + swap). See
  `docs/SAA5050-implementation.md` for the full device guide (rounding, control codes, palette,
  the fetch/generate split).
- **Keyboard (I/O device — same shape as the cassette):** an ordinary I/O device with two
  faces, exactly like the cassette:
  - **Bus face:** plain port reads on 0x00–0x09 (the CPU does `IN`; the device puts row bits
    on the data bus), per the KBIEN protocol. No different in kind from the cassette answering
    CPRIN reads — both are just port dispatch.
  - **Host face:** the matrix is fed from host key events (as the cassette's tape is fed from a
    mounted `.cas`); host events queue on the UI side and apply at a **frame boundary** on the
    emulation thread (observer rule, root CLAUDE.md).
  - Model the 10×8 matrix as real row/column intersections (so ghosting emerges). Debounce/
    repeat is the ROM's job — present a stable matrix only.
  - **Keyboard and cassette are PEERS that share port 0x10 (CPOUT):** KBIEN (the keyboard scan
    enable) lives in the same latch as the cassette FWD/REV/WCD/WDA lines. They register on the
    port dispatch the same way; the `CPoutLatch` fans a 0x10 write out to both. Do NOT model
    the keyboard as a special non-I/O input path — on the bus it is an I/O device like any
    other.
- **CPoutLatch (0x10):** shared write latch (§6), edge detection for cassette.
- **CPRIN reader (0x20):** shared input (§6), active-low status bits from device state.
- **Cassette / MDCR:** digital block device (not analog). Two-level `TimingPolicy` — authentic
  bit-level at 6000 baud (RDC/RDA self-clocking, drive off master clock, deterministic) OR
  turbo ROM-trap block transfer. Separate always-fast host-side `.cas` manipulation API. CIP
  reflects whether a `.cas` is mounted (bare = no cassette). **CIP is a LIVE transition:** the
  bare-machine ROM busy-waits polling CIP, so mounting a `.cas` at RUNTIME must flip CIP while
  the machine runs — cassette insert/eject is a **runtime operation, an exception to
  reset-to-apply** (real hardware hot-swaps tapes). On insertion the ROM rewinds and auto-loads
  a **'P'-type file**, so `.cas` parsing must expose the per-file **type byte** (ref doc §5b).
  See `docs/MDCR-implementation.md` for the full device guide (phase-bitstream model, the
  phase-locked bit recovery, the authoritative `.cas` format + checksum, and the open items:
  WEN active-sense reconcile, toggleable reverse-direction mapping, seeded blank-tape fill).
- **Sound (1-bit beeper):** square-wave from the beeper line; push samples to audio out.

---

## 8. Interrupt aggregator

All INT sources wired-OR onto the core INT pin; the machine ORs them (reference doc §5e).
- **T-first: implement ONLY the video 50 Hz VBLANK source → IM1 → RST 0x0038.** The ROM's CTC
  probe times out (no CTC present → open-bus, no INT) and falls back to this automatically.
- NMI sources wired-OR too: the front-panel soft-reset button and SLOT1 (pin 1A). SLOT2 has
  no NMI.
- **Build the aggregator so the optional IM2 `DaisyChain` + Lock interlock + CTC can register
  later** (deferred, §16) — but don't implement them now. The core already supplies everything
  the daisy chain needs (int-ack vector-from-bus, snoopable fetches).

---

## 9. Project layout

```
src/P2000.Machine/
  IDevice.cs
  Machine.cs              # assembly object: reads MachineConfig, builds page table + devices,
                          # owns the tick loop; Reset/SaveState/LoadState at machine level
  MachineConfig.cs        # topology (model, board/RAM, slots, mounts, prefs) — serializable
  Memory/PageTable.cs     # per-model 64K page map, open-bus, banking
  Io/PortDispatch.cs      # port routing with fan-out/combine
  Io/CPoutLatch.cs
  Io/CprinReader.cs
  Devices/Video.cs        # SAA5050 + fetch timing + framebuffer
  Devices/Keyboard.cs
  Devices/Cassette.cs     # MDCR, TimingPolicy
  Devices/Sound.cs
  Interrupts/Aggregator.cs
  Contention/VideoFetch.cs
  State/*.cs              # config + state serializers (versioned)
tests/P2000.Machine.Tests/
  ...                     # per-device unit tests + integration (boot, run .cas) + contention
assets/                   # monitor ROM, BASIC cartridge, test .cas (see reference doc §8 links)
```
Depends on `Z80.Core` only (UI/debugger and `Z80.Disassembler` are separate, higher layers).

---

## 10. Contention model (the headline feature)

Per reference doc §4 — get the polarity right:
- **The Z80 has unconditional priority; the VIDEO loses.** No wait-states on the CPU. When the
  CPU drives a RAM access in a character fetch slot, that video fetch is corrupted → a bad
  character cell. The CPU proceeds unaffected.
- Model the video fetch as a real bus read in the tick loop; contention is resolved there
  (§3 step 4), not detected by a side-check.
- **Single-cell, non-persistent** corruption (eyeball-confirmed): mark the one cell fetched in
  the collided slot as bad; no carry-over.
- Exact corruption mode (blank/data-bleed/suppression) and the fetch bus-occupancy are
  **unconfirmed pending a logic-analyzer/RGBS capture** — build the DEFAULT (collided slot →
  blank/black cell, no persistence) and leave the mode swappable.
- Provide a **debug overlay** hook highlighting corrupted cells this frame (turns the feature
  visible/testable).

---

## 11. Config vs. state serialization (two concerns, one dependency)

Per reference doc §3a:
- **`MachineConfig`** = topology (model, board/RAM socket population, SLOT1/SLOT2, mounts,
  display/audio prefs). JSON, human-editable, small, shareable → **`.cfg`** files. Owned by
  `Machine` assembly; loading one builds a machine (reset-to-apply).
- **State capture** = running contents (CPU regs, all RAM, each device's runtime, cycle
  position) via the distributed `SaveState`/`LoadState` walk → **`.state`** files, with the
  full `MachineConfig` **embedded as a header**.
- **Restore = rebuild from embedded config (reset-to-apply), THEN deserialize device state.**
- **Version both formats** (a version field each) — devices will be added; reject/migrate old
  files rather than crash. Config is derivable FROM a state, never the reverse.

---

## 12. Validation gates (the project is not "done" until these pass)

Unlike the core (SingleStepTests/ZEXALL), the machine's gold standard is **it behaves like a
real P2000T**:
1. **Per-device unit tests** — page table (open-bus, banking, per-model video region),
   port dispatch fan-out/combine, CPoutLatch edges, keyboard matrix + ghosting + KBIEN
   protocol, cassette bit engine, interrupt aggregator (video tick → RST38).
2. **Integration — BOOT:** load the real monitor ROM + BASIC cartridge (SLOT1) and reach the
   BASIC prompt; the ROM's RAM-sizing probe must size the configured variant correctly via
   open-bus. This is the big gate.
3. **Integration — RUN:** load a real `.cas` (e.g. Ghosthunt — exercises the inverted-colour
   trick) and run it; frame output matches expectation.
4. **Contention stress test:** a routine hammering VRAM during active display produces
   single-cell speckle; the same routine confined to v-blank displays clean. (Full fidelity
   pends the hardware capture, §10 — assert the *pattern*, not exact pixels, until then.)
5. **Save/restore round-trip:** `.state` save then load reproduces identical subsequent frames
   (determinism makes this exact).

---

## 13. Build order (milestones) — GREEN, THEN COMMIT

Work milestone by milestone. **After each milestone's tests pass green, make a git commit**
(conventional-commit message) whose body summarizes what was implemented AND any non-obvious
findings or hardware quirks discovered — exactly as was done for the `Z80.Core` build. This
commit log becomes the project's decision record. **Do not move to the next milestone while
the current milestone's tests are red.**

### Record corrections/updates in THIS file (§17) during each milestone
Implementation always turns up things the spec/reference doc got wrong, vague, or missing —
a hardware detail that differs from what was assumed, an interface that needed reshaping, a
"to confirm" item now confirmed, a quirk discovered while making a test pass. **When that
happens, append a dated entry to §17 (Findings log) in this CLAUDE.md** as part of the same
milestone, before committing. Keep it short: what was assumed, what turned out true, and where
(file/port/section). Do NOT edit the reference doc (`docs/P2000T-reference.md`) yourself — the
human syncs §17 into the reference doc separately once a milestone (or the project) is done.
This file is the working scratchpad; the reference doc is the clean source of truth.

1. Solution project + `IDevice` + `MachineConfig` skeleton + a `Machine` that instantiates a
   `Z80.Core` and runs the empty tick loop. → commit.
2. Page table: per-model map, ROM load, RAM pages, open-bus, banking (port 0x94). Unit tests
   for reads/writes/open-bus/banking. → commit.
3. Tick loop wiring: drive `Step()`, service memory via the page table. CPU executes ROM code
   in a test. → commit.
4. Port dispatch + fan-out/combine; CPoutLatch (0x10) + CPRIN (0x20) with unit tests. → commit.
5. Video device: SAA5050 char gen, VRAM, framebuffer, 50 Hz frame, inverted-colour trick.
   Unit tests render known VRAM to expected pixels. **See `docs/SAA5050-implementation.md`.**
   → commit.
6. Interrupt aggregator: video 50 Hz → IM1 RST 0x0038. Test the tick fires and vectors. →
   commit.
7. **BOOT milestone (two outcomes).** The monitor ROM is part of the base machine — it's
   present at 0x0000 from power-on (loaded automatically, §5), NOT a per-test fixture. Verify
   the two boot outcomes that depend on SLOT1 population (ref doc §5b boot sequence):
   (a) **Bare machine (no SLOT1):** RAM check sizes the variant via open-bus → on-screen
   cassette-wait prompt, ROM polling CIP. The fundamental default; needs no cartridge.
   (b) **SLOT1 populated (BASIC cartridge image):** boots into BASIC → prompt.
   Integration tests for both. → commit.
8. Keyboard device: matrix + ghosting + KBIEN protocol; apply host input at frame boundary.
   Test typing into BASIC. → commit.
9. Cassette (MDCR): authentic bit engine + turbo ROM-trap `TimingPolicy`; host-side `.cas` API;
   CIP/BET/WEN. **See `docs/MDCR-implementation.md`.** **RUN milestone:** load + run a real
   `.cas`. → commit.
10. Contention model: video fetch as bus participant, Z80-priority single-cell corruption,
    debug overlay hook. Stress test (speckle vs clean). → commit.
11. Config + state serialization: `.cfg` load/save, `.state` with embedded config header,
    versioned; round-trip test. → commit.
12. Slot model formalized (SLOT1/SLOT2/internal typed interfaces, even if only SLOT1 populated
    now) so expansion drops in later. Tag `P2000.Machine` T-baseline. → commit.

---

## 14. Deferred (build the seams now, implement later)

Do NOT implement these in this build, but keep the interfaces ready (they're specced in the
reference doc): **P2000M** (different video-memory sharing, 4 KB VRAM); **CTC** (Z8430, the
system-tick source when present) + **IM2 daisy chain** + **Lock interlock**; **FDC/floppy**
(internal-slot µPD765 card) + disk images; **hires overlay board**; **SLOT2 expansion cards**;
**80-column mode**; **printer**. The aggregator (§8), slot model (§12.12), and `TimingPolicy`
(§7) are the seams these plug into.

---

## 15. Coding conventions

Inherit root `CLAUDE.md`. Machine-specific: keep emulation deterministic (no wall-clock in the
loop); keep the page table and port dispatch behind clean methods (no scattered address
literals — name the regions/ports); every device implements `IDevice`; no `Z80.Core` changes
from this project.

---

## 16. When to ask the human

Ask before: changing a locked decision in §2; implementing any deferred item in §14 without
being asked; deviating from the confirmed hardware in the reference doc; or relaxing a
validation gate in §12. For the hardware details still marked "to confirm" in the reference
doc (exact contention corruption mode, WCD/WDA clock, SHIFT/CODE matrix positions), ask rather
than guess. Ordinary in-project choices: proceed and keep CI green.

---

## 17. Findings log (working scratchpad — synced to the reference doc by the human)

Append a dated entry here whenever implementation corrects, clarifies, or adds to the
spec/reference doc (see §13). Format: date, milestone, what was assumed → what turned out true,
and where it applies (file/port/section of the reference doc). Keep entries short and factual.
The human periodically syncs these into the P2000T reference document, then may prune entries
marked synced. Do NOT edit the reference doc from this project.

<!-- Template:
### YYYY-MM-DD — Milestone N: <short title>
- **Assumed:** …
- **Found:** …
- **Applies to:** reference doc §… / <file/port>
- **Synced:** no
-->

### 2026-07-02 — Milestone 2: page table
- **Assumed:** nothing to confirm on the ROM/RAM/expansion/open-bus shape — those are all
  CONFIRMED hardware (reference doc §5) and implemented as documented.
- **Found (documented default for an unconfirmed item):** the 0x94 bank register's
  power-on/reset value is reference doc open item #2 ("which bank is the normal top-of-RAM
  that non-banking software sees"), left unconfirmed there. Implemented as bank 0 by
  construction (`PageTable._bankIndex` defaults to 0) — the least-surprising default, and
  harmless for faithful T/102 behaviour since the real firmware always writes 0-5 itself
  before depending on banking. Revisit if a disassembly/schematic confirms otherwise.
- **Found (scope decision, not a hardware finding):** `RamVariant` implements only T38/T54/
  T102. PTC-96K is deliberately NOT modelled — reference doc open item #4 (how its 16 KB +
  64 KB expansions combine, and whether the extra 64 KB rides port 0x94 or a separate
  scheme) is unconfirmed, and PTC-96K is a floppyboard-only variant, so it has no confirmed
  shape to build against while floppy support is deferred (project CLAUDE.md §14). Add it
  once floppy support is undertaken and the addressing scheme is confirmed.
- **Applies to:** reference doc §5 (memory map, RAM variants, bank switching, open items
  #2 and #4) / `src/P2000.Machine/Memory/PageTable.cs`, `src/P2000.Machine/MachineConfig.cs`.
- **Synced:** no

### 2026-07-03 — Milestone 4: port dispatch, CPoutLatch, CprinReader
- **Assumed:** the CPOUT/CPRIN bit maps and shared-port fan-out/combine model (reference doc
  §5f) were CONFIRMED hardware and implemented as documented.
- **Found (design decision, not a hardware finding):** `PortDispatch` combines multiple read
  sources on one port by bitwise OR, on the assumption each source only ever sets the bits it
  owns and leaves the rest 0. This works cleanly for CPRIN (cassette bits vs. future printer
  bits are disjoint) but would silently corrupt a port where two sources legitimately
  disagree on the same bit — there is no such port today, but a future shared port must keep
  its sources bit-disjoint or this combine strategy needs revisiting.
- **Found (scope decision, not a hardware finding):** `CprinReader` currently owns ALL of
  CIP/BET/WEN/RDC/RDA directly (settable properties) rather than combining a separate
  cassette-device read source, because the cassette device is milestone 9. PRI/READY/STRAP
  read as 0 (inactive) since the printer is deferred entirely (§14) and has no confirmed
  hardware shape yet. When the cassette device lands, decide whether it registers its own
  `PortDispatch` read source for 0x20 (letting `CprinReader` shrink to printer-only bits) or
  keeps feeding these same properties — either is compatible with the fan-out/combine model.
- **Found (doc self-correction applied):** the reference doc's illustrative CPRIN read
  sketch (§5f) has a confusing/self-contradictory comment on the WEN bit ("WEN=0 writable ->
  so set when NOT protected?"). Implemented literally per the doc's own bit TABLE instead:
  bit 3 = 1 means write-protected, bit 3 = 0 means writable (`CprinReader.WriteProtected`
  sets the bit when `true`). The doc itself flags the sketch as illustrative, not
  authoritative — no correction needed there, just noting which reading was implemented.
- **Applies to:** reference doc §5f (CPOUT/CPRIN bit maps, shared-port fan-out/combine) /
  `src/P2000.Machine/Io/PortDispatch.cs`, `src/P2000.Machine/Io/CPoutLatch.cs`,
  `src/P2000.Machine/Io/CprinReader.cs`, `src/P2000.Machine/Machine.cs`.
- **Synced:** no

### 2026-07-03 — Milestone 5: video device (SAA5050 + fetch timing)
- **Assumed:** the framebuffer would be 480×480 (12 px/char-column) per this file's §3 as
  first written.
- **Found (spec correction, confirmed by tracing the reference renderers bit-for-bit):** the
  hard-won C#/jsbeeb renderer blends adjacent glyph columns into **16 output pixel lanes per
  character**, not a plain 6×2=12 doubling - verified by decoding the `MakeHiresGlyphs`
  multiplier constants bit-by-bit (each of the 12 raw column bits spreads across 3-4 output
  bits with deliberate overlap between neighbours, the anti-aliasing mechanism itself). This
  makes the real framebuffer **640×480**, not 480×480. §3 was corrected before implementation
  started (both by me and, in parallel, by the human editing the same section) rather than
  silently re-deriving the smoothing math to fit 12 lanes.
- **Found (hardware confirmation, resolves an implementation-doc ambiguity):** the reference
  doc §5 explicitly confirms the 160-255 trick as "inverted (**swapped** fg/bg) colours", not
  a per-channel complement. This matches the C# port's `PERender` variant (swap the palette
  shift positions, `invert ? 2:5 / 5:2`) rather than MAME's `color ^= 0x07` (a channel
  complement, mathematically different unless fg/bg happen to be exact complements). Built
  the swap model; MAME's own comment header flags it disagrees with jsbeeb's rounding anyway,
  so it was already the weaker semantics reference for this quirk.
- **Found (untested-in-the-wild quirk, ported deliberately):** `PERender`'s `previousLineData`
  cache — double-height's bottom half re-shows the TOP row's byte at each column instead of
  whatever is actually in that column's own VRAM row — was carried into `BeginCell`. This is
  easy to miss (the non-PE `Render()` path in the same reference file does NOT have it) and
  would silently break any double-height text whose authoring tool doesn't duplicate the top
  row's bytes into the row below.
- **Found (unconfirmed CPU-facing control, scoped decision):** the reference doc confirms the
  panning MECHANISM (screen buffer is 80 cols × 24 rows, viewport pans by an upper-left X
  0-40) but not which port/register the CPU writes to set it. Exposed as a plain settable
  `Video.PanX` property for now, same pattern as `CprinReader`'s properties ahead of the
  cassette device (milestone 4 finding) - wire it to the real control once found.
- **Found (fetch-slot timing, acknowledged approximation):** the SAA5020's real per-slot bus
  occupancy is confirmed-unconfirmed (reference doc §4a). `VideoFetchUnit` schedules the 40
  column fetches at `floor(column × 2.5)` T-states within the 100 T-state active window -
  evenly spaced integer slots, the best available approximation until a logic-analyzer
  capture pins the real waveform. Swappable without touching `Video`/`Saa5050Generator`.
- **Found (scope decision, not a hardware finding):** framebuffer pixel contents are NOT
  included in `Video.SaveState` - only the fetch-unit counters and generator attribute state
  are, which is sufficient for validation gate §12.5 ("subsequent frames" must match; a
  mid-render buffer snapshot is not required for that guarantee) and keeps state files small.
- **Applies to:** reference doc §5/§5f (VRAM layout, panning, 160-255 trick), §4a (fetch
  timing) / `docs/SAA5050-implementation.md` (whole device guide) /
  `src/P2000.Machine/Devices/Video.cs`, `src/P2000.Machine/Devices/Saa5050/*.cs`,
  `src/P2000.Machine/Contention/VideoFetchUnit.cs`.
- **Synced:** no

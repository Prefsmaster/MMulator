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
  480 high, the 20 being 10 logical lines doubled). Do NOT "simplify" the width to 480 — that
  discards horizontal smoothing. **NB: NOT 500 high** — the owner's reference video code used
  640×500 (25 rows), which is **BBC-Micro heritage** (BBC teletext is 25 rows); the P2000T is
  **24 rows = 480**. Fix the three coupled constants together (buffer height, end-of-field test,
  field/frame discriminator) — don't copy the 500-based arithmetic.
- **Fields vs frames (interlaced — model BOTH distinctly):** the P2000T runs **50 fields/sec**,
  interlaced: an **even field** (sub-scanlines from y=0, `CRS=false`) and an **odd field** (from
  y=1, `CRS=true`) interleave into the display. The odd field is where the diagonal smoothing
  lands (CRS/RA0 selects it). Consequences:
  - **Interrupt + CTC trigger fire per FIELD (50 Hz):** the video 50 Hz VBLANK (→ IM1 RST 0x0038,
    §8) and the CTC channel-3 clock (reference doc §5e; when a CTC is present) tick once per
    field. DEW resets on the even field.
  - **Present to the UI per FIELD (50 Hz), into a SINGLE PERSISTENT buffer — do NOT clear/erase
    between fields.** Each field writes ONLY its own scanlines (even field → even lines, odd →
    odd) into one persistent 640×480 buffer, leaving the other field's lines (from ~20 ms ago)
    untouched. Presenting every field with no inter-field clear **reproduces the real interlace
    "comb" artifact**: fast horizontal motion serrates because adjacent scanlines are 1/50 s out
    of sync — an authentic CRT behaviour, deliberately preserved. (This REPLACES the earlier
    per-frame back/front swap idea — it's a single persistent buffer, not a swap chain.)
  - **Thread boundary:** the persistent buffer is owned by the emulation thread; at each **field
    boundary** hand the UI a read-only view or a fast copy of the whole buffer, then continue
    writing the next field into the SAME buffer (no clear). Snapshot-at-field-boundary avoids
    tearing while keeping the comb.
  - **`FieldComplete` ordering contract (consumer-facing, confirmed P2000.UI ms6):** when
    `FieldComplete` fires, `Video.IsOddField` has ALREADY toggled to the NEXT field's parity —
    so the field that just completed is **`!IsOddField`**. Consumers gating even-only / odd-only
    / progressive presentation must use `!IsOddField`. The per-field **`CorruptionOverlay` is
    `Array.Clear`ed AFTER the `FieldComplete` event returns**, so a consumer must **copy it inside
    the handler** (still populated there), not defer to a later UI-thread callback.
  - **No inter-field erase = maximum comb (simplest + authentic).** Do NOT model phosphor decay
    /field dimming unless asked — leaving the previous field's lines as-is is both simplest and
    the strongest, most faithful effect.
  - **Display mode (owner decision) — four options over the SAME rendered scanlines; default
    interlaced/comb:**
    1. **Interlaced (comb) — DEFAULT:** both fields, single persistent buffer, present per field,
       no inter-field clear → authentic comb on fast horizontal motion (as above).
    2. **Progressive:** both fields composited per frame, no comb, full vertical detail.
    3. **Even-only:** present only the even field (raw sub-scanlines), discard odd.
    4. **Odd-only:** present only the odd field (the SMOOTHED sub-scanlines — CRS/RA0 rounding
       lands here), discard even.
    - Even-only vs odd-only are NOT identical: odd-only looks slightly smoother (it's the rounded
      scanlines), even-only slightly harder-edged. Both eliminate comb (single temporal field) at
      the cost of half the vertical info.
    - **Field-only default = line-double** (draw each field line twice to fill 480, gap-free,
      chunky). A scanline-gaps look is achievable via the existing scanline/CRT shader option — do
      NOT add a separate gaps mode.
    - All four read the same rendered scanlines; only present-cadence + clear + which-lines differ.
  - Do NOT collapse field==frame in the emulation timing — the interrupt/CTC are per-field
    regardless of the display toggle. The toggle only affects UI presentation.
  - PAL aspect correction + integer scaling are the **UI's** job; at 640×480 pixels are already
    near-square on 4:3, so the UI scale is close to a straight integer scale, not a stretch.
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

## 3b. The observer + control seam (machine → debugger / UI / IDE)

§3's framebuffer is the machine's **output** seam. This is its **observe-and-command** seam: how
the debugger, `P2000.UI` (its §3.2), and the future external IDE hook (deferred, §14) read
machine state and drive it. Same discipline as everywhere else — one owner, snapshots at safe
points, mutation ONLY via queued commands drained at instruction boundaries. **It lives here, in
the machine, so every client shares ONE contract** (the UI is the first client, the IDE the
second); do NOT let it migrate into `P2000.UI`.

Three surfaces (milestones 13–15):

1. **Read-only state snapshot.** A cheap, allocation-light view an observer reads at a break /
   per step: full register file incl. **WZ/MEMPTR**, IFF1/2, IM, flags broken out (incl. YF/XF),
   plus memory reads and the **in-frame T-state/cycle position**. Derived from the deterministic
   core; never mutates it. Register/flag reads are consistent at `AtInstructionBoundary` — reuse
   the same safe-point discipline `SaveState` already relies on (§11). The core already exposes
   WZ and `AtInstructionBoundary`; expose the rest read-only.
2. **Breakpoint store (machine-owned).** Execute + memory **R/W/X** watchpoints + **I/O-port**
   breakpoints, held in the machine's debug state and evaluated **inside the tick loop** (the
   loop that already resolves contention, §3 step 4). A hit pauses at the next instruction
   boundary and raises a **break event** observers see. **The break event ALSO fires on every
   non-breakpoint pause — single-step / pause / run-to-scanline/cycle — via a synthetic
   `BreakpointKind.Step` (id −1), so observers refresh off one event (P2000.UI ms10).** Clients
   EDIT this store (via commands);
   they never keep their own — this is what lets the UI debugger and the IDE set the SAME
   breakpoints. Guard the hot loop with an "any breakpoint armed?" fast path so an unbroken
   machine pays nothing.
3. **Command queue (drained at `AtInstructionBoundary`).** Every mutation from a client is a
   **queued command applied at a safe boundary** — symmetric with host input, which already
   applies at a frame boundary (§7 keyboard). Commands: run / pause, warm reset, cold reset,
   single-step, step-over, step-out, run-to-scanline, run-to-cycle, set-PC, memory write,
   **load-image-to-address** ("send code," for the IDE later), breakpoint CRUD. **Determinism
   caveat:** a mid-run memory write / load-to-RAM breaks cycle-exact replay for that session —
   same category as turbo cassette; allowed, documented per-command, NOT forbidden.

These surfaces sit on a **primitive drive surface** the machine exposes: `RunField()` (advance
one 50 Hz field; drain the command queue at instruction boundaries; return early on a breakpoint
hit), `StepInstruction()`, `Post(command)`, `Snapshot()` — **no wall-clock inside any of them**.
The bare field advance already exists (milestones 5–7); the drain + early-return behaviours are
delivered by milestones 14–15.

**The run-loop host / scheduler — DECIDED (was §16 open): UI-owned for this build, promotable.**
Something must pace `RunField()` to wall-clock 50 Hz (uncapped for turbo), handle
run/pause/turbo, and apply queued input at boundaries. Locked decision §2.2 forbids
wall-clock/threads inside the emulation core, so this host sits OUTSIDE it — and for now it
lives in **`P2000.UI` (`Runner/`)**, driving the primitive surface above; **there is NO
machine-layer runner class in this build.** When external-IDE integration becomes current (§14),
**promote that loop into a machine-layer `MachineRunner` on the identical surface** so UI + IDE
share one driver — a move, not a redesign. Keep `RunField`/`StepInstruction`/`Post`/`Snapshot`
stable to keep that promotion cheap. The three surfaces above are **runner-agnostic** and are
milestoned (13–15) regardless.

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
  CARS2 (0x3000–0x4FFF); 0xE000–0xFFFF banking on port 0x94 is **card-specific** (reference doc
  §5): the original Philips board is a **1-bit `RAMSW` flip-flop** (D0 → BANK1 upper/lower 8 KB),
  homebrew RAM cards decode more bits for more banks. **⚠ milestone 2's configurable N-bank model
  STAYS (that's the homebrew path); ADD the original board as the 1-bit RAMSW default card.**
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
- **0x50 sound-out** (write): **bit 0 = the 1-bit speaker level** — a DEDICATED sound port,
  NOT part of the CPOUT latch. `SoundDevice` registers here; the ROM toggles bit 0 for tone.
- **0x94:** bank-select, **card-specific** (reference doc §5) — original Philips board = 1-bit `RAMSW` flip-flop (D0, BANK1 upper/lower 8 KB); homebrew cards = wider bank register (configurable width).

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
- **Cassette WRITE / SAVE path (both timing modes — currently thin in the guide, specify it):**
  - **CSAVE updates the internal bitstream in BOTH modes.** Realtime/authentic: WCD/WDA writes
    capture the ROM's bitstream phase-by-phase into the in-memory `MiniTape` (as read is the
    reverse). Turbo: ROM-trap the save routine, write whole blocks into the tape image directly.
    Either way the mounted tape's in-memory state is updated live.
  - **Bitstream → `.cas` serializer (the reverse of `LoadCasImage` — MISSING, must be built):**
    the MDCR guide documents `.cas` → bitstream only. Add the inverse: decode the phase stream
    back into 1280-byte `.cas` records (find block framing / `0xAA` markers, recover bytes via
    the same PLL logic, strip framing + checksum). Needed so a tape written by CSAVE can be
    persisted.
  - **UI "Save as .cas":** a host-side action (always available, not gated by timing policy) that
    runs the serializer and writes the current tape to a `.cas` file — symmetric with load. Also
    a plain "save tape" that writes back to the loaded file. (Host-side `.cas` API, §7.)
  - So the round trip is: `.cas` → bitstream (load) → CSAVE mutates bitstream → bitstream →
    `.cas` (save). Blank-tape CSAVE (no file loaded) → new tape in memory → Save as .cas.
- **Sound (1-bit beeper — `SoundDevice`, milestone 16):** watches writes to **port `0x50`, bit 0**
  (CONFIRMED — dedicated sound-out port, NOT CPOUT; reference doc §5 Sound), records level
  transitions per field, and at each
  `FieldComplete` emits one **882-sample @ 44 100 Hz** PCM block via `SamplesReady(short[])` (one
  reusable buffer; the consumer copies immediately). This is the machine→UI audio seam.

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
9a. **Cassette WRITE / CSAVE path** (distinct from read — do NOT consider milestone 9 done
    without it): (a) realtime write — WCD/WDA capture the ROM's bitstream into the in-memory
    tape; (b) turbo — ROM-trap the write routine (`cas_Write`/`write_block`, MDCR guide §5) for
    instant block save; (c) **bitstream → `.cas` serializer** (the inverse of `LoadCasImage`,
    currently MISSING — recover blocks + headers + checksum from the phase stream); (d) UI "Save
    as .cas" + write-back. **Tests:** CSAVE a known program → read it back via the authentic path
    → bytes + checksum match; blank-tape CSAVE → Save as `.cas` → reload → identical. → commit.
10. Contention model: video fetch as bus participant, Z80-priority single-cell corruption,
    debug overlay hook. Stress test (speckle vs clean). → commit.
11. Config + state serialization: `.cfg` load/save, `.state` with embedded config header,
    versioned; round-trip test. → commit.
12. Slot model formalized (SLOT1/SLOT2/internal typed interfaces, even if only SLOT1 populated
    now) so expansion drops in later. Tag `P2000.Machine` T-baseline. → commit.

**Post-T-baseline — the observer + control contract (§3b) the debugger + external IDE consume
(`P2000.UI` §3.2). Runner-agnostic: the run-loop host is UI-owned for this build (§3b), so there
is NO machine-layer runner milestone here — it's promoted in with the external IDE (§14).**

13. **Observer state-snapshot surface** (§3b.1). Read-only snapshot: full register file (incl.
    WZ/MEMPTR, IFF1/2, IM, flags incl. YF/XF), a memory-read view, in-frame T-state/cycle
    position; taken at a safe point, never mutating the core. **Tests:** snapshot registers/flags
    match the core at a known break; re-reading without stepping is identical; stepping advances
    PC + cycle position as expected. → commit.
14. **Machine-owned breakpoint store** (§3b.2). Exec + mem R/W/X + I/O-port breakpoints evaluated
    in the tick loop behind an "armed?" fast path; a hit pauses at the next instruction boundary
    and raises a break event. **Tests:** each type fires on the correct access and only then; a
    machine with nothing armed is behaviour- AND performance-unchanged; the break lands on an
    instruction boundary. → commit.
15. **Command queue** (§3b.3). Queue drained at `AtInstructionBoundary`: run/pause, warm/cold
    reset, single-step, step-over, step-out, run-to-scanline, run-to-cycle, set-PC, memory write,
    load-image-to-address, breakpoint CRUD — symmetric with frame-boundary input. **Tests:** each
    command applies at a boundary with the expected transition; step-over/step-out land correctly
    across CALL/RET; run-to-cycle N stops exactly at N; a mid-run poke is flagged non-replayable.
    → commit.
16. **Audio output (1-bit beeper — `SoundDevice`).** The machine's audio-output seam. (The device
    landed early during P2000.UI ms7 — findings 2026-07-09 — so this milestone formalizes it as a
    first-class machine device and adds the machine-level output test it was missing.)
    `SoundDevice : IDevice` watches writes to **port `0x50`, bit 0** (CONFIRMED — dedicated
    sound-out port, NOT CPOUT; §5/§7 + reference doc §5 Sound), records `(FieldTState, level)`
    transitions per field, and at
    each `FieldComplete` synthesizes one **882-sample @ 44 100 Hz** PCM block, raising
    `SamplesReady(short[])` (ONE reusable buffer; the consumer copies immediately). Serializes in
    `.state` as a device block between cassette + interrupts (feeds the pending version bump,
    reference doc §3a). **Test — confirms the Machine can drive audio OUT to a consumer:** attach a
    fake sink to `SamplesReady`; drive port-`0x50` bit-0 writes (or boot the ROM to emit the power-on
    beep) for several fields; assert (a) exactly one block per field arrives, (b) block length +
    rate are 882 @ 44 100 Hz, (c) content is a non-constant square wave while the beeper toggles
    AND flat silence when it doesn't. This proves the machine emits a consumable sample stream —
    the UI/OpenAL sink is just another `SamplesReady` subscriber. → commit.
17. **Z80 CTC (Z8430) + IM2 daisy chain + Lock interlock** (reference doc §5e; the
    interrupt-architecture foundation the FDC INT and SLOT2 vectored INT build on — promoted
    from §14).
    - **Design — standalone chip (DECISION):** model the CTC as a **board-agnostic `Z80Ctc` chip**
      (its own class + unit tests), NOT logic inlined in a board. Its interface is fully pinned
      (ports / control word / timer+counter / IM2 / RETI — §5d/§5e) and coincides with the real
      chip boundary, so this is honest modelling, not a speculative abstraction. The **owning board
      wires it**: the extension board instantiates one `Z80Ctc`, feeds **ch3 CLK/TRG ← the
      vertical-retrace pulse**, **ch0 ← the µPD765 INT**, and registers the chip into the
      aggregator's IM2 daisy chain. The chip stays board-agnostic; wiring is the board's job.
      **Defer the multi-board *framework*** — confirmed P2000 hardware has exactly one CTC (ch2
      comms is the same chip), so a second (homebrew) CTC just instantiates + wires its own, no
      framework needed until one is real. (Mirrors the `SAA5050` standalone-chip decision.)
    - **CTC device** (`Ctc : IDevice`): 4 channels, each with a control register (timer/counter
      mode, prescaler 16/256, edge/trigger, int-enable, time-constant-follows), a down-counter,
      and a ZC/TO output (channel 3 has no pin output). Programming = write control then time
      constant. **Ports + roles CONFIRMED (ROM disassembly, reference §5d)** — one port per
      channel: **ch0 `0x88`** (highest) = timer / FDC interrupt (the µPD765 INT feeds ch0 — the
      FDC has no direct CPU INT line); **ch1 `0x89`** = disk not-ready; **ch2 `0x8A`** =
      communication (serial / I/O) interrupt — the SLOT2 comms hook; **ch3 `0x8B`** = the
      keyboard-scan / system tick every 20 ms (50 Hz), the CTC-path replacement for the video
      50 Hz tick when Lock asserts. Control-word bit layout confirmed (reference §5d).
    - **IM2 daisy chain** (`DaisyChain`): an ordered source chain plugged into the aggregator's
      existing IM2 seam (§8 — int-ack vector-from-bus, snoopable fetches). On int-ack (M1+IORQ)
      the highest-priority pending source drives its **vector** onto the bus; the core vectors via
      `(I<<8) | vector`. CTC channels register in priority order (ch0 > … > ch3); SLOT2 cards
      (later) register behind them. The chain must **snoop `RETI` (ED 4D)** to clear each
      source's in-service latch (the ROM's `enable_interrupts` is `EI` + `RETI`) — the §8 seam's
      snoopable fetches cover this.
    - **Lock interlock** (§5e, internal-slot pin 35): an input to the aggregator, asserted when an
      active **floppy+RAM extension board** occupies the internal slot. Lock asserted → aggregator
      **suppresses the onboard video 50 Hz INT**; the CTC (fed by the 50 Hz line) drives the tick
      via IM2. Lock deasserted (bare T, current behaviour) → video 50 Hz → IM1 / RST 0x0038,
      unchanged. A GATE ensuring exactly one tick source is electrically live.
    - **Absent CTC = genuine silence:** with no board, CTC ports read open-bus 0xFF and INT is
      NEVER asserted, so the ROM's CTC probe times out and auto-selects the video tick — the
      fallback the bare T already relies on. A stray status read or latched INT would break it.
    - **`.state`:** CTC channels (control, counters, time constants, vectors, pending) + Lock +
      daisy-chain pending serialize → **bump `.state` to v3** (`CurrentVersion`/`MinVersion = 3`,
      reject older) AT BUILD TIME, not retroactively — the v1→v2 silent-misload lesson.
    - **Confirmed (ROM disassembly):** CTC ports **`0x88`–`0x8B`** (one per channel; roles above);
      the **control-word bit layout** (CTRLWRD/RESET/TCNEXT/CLKSTRT/ACTTRG/PRE256/CNTMD/INTEN,
      reference §5d); IM2 via M1+IORQ vector-from-channel; and the **presence-probe sequence** (§5e)
      — ch3 programmed as a fast timer (control `0x85` + TC `0x01`, `INTEN`) with its IM2 vector at a
      test handler, **no timeout**: present → the interrupt diverts to the handler, absent → falls
      through to `IM 1`. So the CTC must support **timer mode** (system-clock-driven), and
      absent-CTC = no INT = the bare-T fall-through (the regression to protect). **IM2 vector base
      CONFIRMED = `0x6020`** (I=0x60, base low byte to ch0; ch3/keyboard entry `0x6026`, §5e);
      normal ch3 is CONFIRMED counter mode (control `0xD5`, TC 1) counting the vertical-retrace
      pulse → 20 ms tick (§5e); the probe uses timer mode. Detection diverts the boot flow (the
      handler discards its return address) rather than timing out. **Only open item:** whether
      **Lock gates NMI too or only maskable INT** (§5e) — resolvable during implementation, as it
      only affects whether the reset NMI is also suppressed. Model absent-CTC first.
    - **Tests:** (a) **timer mode** — a channel counts down prescaler × time-constant off the CPU
      clock and fires ZC/INT at the right cycle; (b) **counter mode fires exactly ONCE per TC
      trigger pulses** — clock a counter-mode channel (TC 1) with CLK/TRG pulses and assert one
      INT per pulse (TC N → one per N pulses); catches a double-decrement that would double the
      tick rate (ch3 → 100 Hz instead of 50 Hz); (c) IM2 int-ack puts the interrupting channel's
      vector on the bus and the core vectors correctly; (d) daisy priority — two pending, higher
      wins, lower defers; (e) Lock gate — board present suppresses the video INT and the CTC drives
      the tick, bare T leaves IM1 unchanged; (f) **fallback regression** — no board → CTC open-bus
      + INT silent → existing T-baseline boot and 50 Hz IM1 tick still pass. → commit.
18. **Tape turbo — ROM-trap fast load/save** (reference doc §5b "trap the monitor ROM cassette
    entry points"; the trap itself was deferred at milestone 9 pending addresses). Today
    `TimingPolicy.Turbo` only bypasses the 209-cycle phase engine (faster bit playback) — it does
    NOT skip the ROM's byte-by-byte transfer loop. This adds the real turbo: **trap the
    monitor-ROM cassette entry points and block-copy `.cas`↔RAM directly.**
    - On a trapped **load** (`cas_block_read` / `load_block`) or **save** (`cas_Write` /
      `write_block` / `cas_block_write`, Cassette.asm): read the ROM calling-convention registers
      (buffer pointer, length, block/record), copy the whole block between the mounted `.cas`
      image and emulator RAM, set the result registers/flags exactly as the ROM routine would,
      and `RET`. The bit engine is bypassed — transfer is instant.
    - **Only under `TimingPolicy.Turbo`;** Authentic keeps the port-level phase engine
      (cycle-exact, replay-safe). The trap is a deliberate side-channel that breaks
      cycle-exactness for the transfer (like the load-image command, §3b) — never under Authentic.
    - Host-side `.cas` API (mount/eject/directory) is unchanged and always fast; adds no `.state`
      device block (no version bump).
    - **Needs (source first — same ROM-disassembly pass as the CTC probe):** the exact **trap
      addresses** + **register/flag calling convention** for the load and save entry points (MDCR
      guide §5/§6 name the routines; the addresses were the deferred piece).
    - **Tests:** (a) turbo load of a known `.cas` yields byte-identical RAM to an authentic-mode
      load of the same image; (b) turbo save round-trips (authentic re-load matches); (c) result
      registers/flags after the trap match the ROM's documented post-conditions so BASIC/ROM
      callers continue; (d) Authentic mode fires no trap. → commit.

---

## 14. Deferred (build the seams now, implement later)

Do NOT implement these in this build, but keep the interfaces ready (they're specced in the
reference doc): **P2000M** (different video-memory sharing, 4 KB VRAM); **FDC/floppy**
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
than guess. The **run-loop host / scheduler** is DECIDED (§3b): the wall-clock pacing /
run-pause-turbo thread lives in `P2000.UI` for this build — do NOT add a machine-layer runner
class here yet; that promotion happens with external-IDE integration (§14). Keep the
`RunField`/`StepInstruction`/`Post`/`Snapshot` surface stable so it stays a move, not a redesign.
Ordinary in-project choices: proceed and keep CI green.

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
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)
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
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

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
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

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
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-04 — Milestone 8: keyboard device
- **Assumed:** the 10×8 matrix, KBIEN protocol, and port range (0x00–0x09) were all
  CONFIRMED hardware (reference doc §5f) and implemented as documented.
- **Found (design decision):** the keyboard reads KBIEN live from `CPoutLatch.Kbien` on
  every port read (a direct reference, not an event subscription) — simpler and equally
  correct since KBIEN is sampled only when the CPU does an `IN` instruction, well within
  the same T-state pass.
- **Found (ghosting model):** in a diode-less key matrix, pressing three corners of a
  matrix rectangle causes a phantom fourth keypress. The mechanism: pressing (R,C0),
  (R2,C0), and (R2,C1) lets current loop R → C0 → R2 → C1, pulling C1 low when scanning
  row R even though (R,C1) is not physically pressed. The P2000T keyboard has no
  anti-ghosting diodes, so this phantom behaviour is authentic and some software depends
  on specific multi-key combinations that only register because of it. Implemented
  explicitly in `IsColumnLow` via an O(R×C) search per column rather than an electrical
  circuit simulation — same result for all 3-corner cases, fast enough at 10×8.
- **Found (open-item, to confirm):** the exact row/column layout for SHIFT, CODE, and the
  function/cursor keys in the 10×8 matrix is still "to confirm" (reference doc §5f). The
  device is ready to accept key presses at any crosspoint; the mapping table is a UI
  concern (milestone UI). The existing test suite uses numeric row/col indices.
- **Applies to:** reference doc §5f (keyboard scan protocol, KBIEN, matrix, ghosting) /
  `src/P2000.Machine/Devices/Keyboard.cs`, `src/P2000.Machine/Machine.cs`.
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-04 — Milestone 7: BOOT — embedded monitor ROM + SLOT1 cartridge
- **Assumed:** the monitor ROM auto-load and SLOT1 cartridge load were both straightforward
  — no hardware surprises, all confirmed from reference doc §5b boot sequence.
- **Found (design decision, not a hardware finding):** `PageTable` auto-loads the embedded
  monitor ROM in its constructor rather than requiring callers to call `LoadRom()` — the
  existing `LoadRom()` method is kept for test fixtures that inject synthetic ROM code.
  Existing tests that assumed ROM reads 0x00 before `LoadRom()` were updated to reflect
  the new "ROM is always populated at construction" contract.
- **Found (design decision):** SLOT1 is allocated as a fixed 16 KB `byte[]` regardless of
  the cartridge image's actual size; bytes beyond the image length read as open-bus (0xFF)
  via zero-fill. This means an 8 KB CARS1-only cartridge naturally leaves CARS2 open-bus,
  which is correct hardware behaviour for a partial-slot cartridge.
- **Found (boot outcome confirmed):** both boot outcomes pass with the real ROMs:
  (a) bare machine reaches the cassette-wait loop (VRAM non-zero, PC stays in ROM) in
  well under 5M T-states; (b) with BASIC.bin in SLOT1 the CPU jumps into the BASIC
  cartridge range (PC ≥ 0x1000) in well under 5M T-states. These are now regression-
  gated integration tests.
- **Applies to:** reference doc §5 (memory map, SLOT1, monitor ROM embed), §5b (boot
  sequence) / `src/P2000.Machine/Memory/PageTable.cs`, `src/P2000.Machine/MachineConfig.cs`,
  `src/P2000.Machine/P2000.Machine.csproj`.
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-03 — Milestone 6: interrupt aggregator (video 50 Hz → IM1 RST 0x0038)
- **Assumed:** nothing to confirm on the IM1/RST-38 vector or the wired-OR structure —
  those are CONFIRMED hardware (reference doc §5e/§8) and implemented as documented.
- **Found (design decision, not a hardware finding):** `InterruptAggregator.Acknowledge()`
  returns 0xFF (passive pull-up) regardless of source — for IM1 the CPU ignores the bus
  byte entirely, so any value is correct; 0xFF reflects a real undriven bus rather than
  inventing a fictitious RST-38 byte. When a future IM2 source registers, `Acknowledge`
  will need a priority/daisy-chain scheme to pick the winning vector — flagged in the seam
  comment but deliberately not pre-built (root CLAUDE.md: no hypothetical abstractions).
- **Found (design decision, not a hardware finding):** INT is kept as a continuous level in
  `_pins` (assert while `IntPending`, deassert otherwise) rather than a one-tick pulse.
  The CPU samples it only at instruction boundaries when IFF1=1, so a multi-tick assertion
  is harmless and avoids a race window where a single-tick pulse could be missed if the CPU
  is mid-instruction when it fires.
- **Found (int-ack detection):** M1+IORQ (without RD/WR) is the int-ack signature per the
  Z80 core's Interrupts.cs comment and pin table. Added as the first branch inside the
  `IORQ` block in `Machine.Tick()` — before the plain IORQ+RD read path — so a future
  normal IORQ read can never be mistaken for an int-ack.
- **Applies to:** reference doc §5e (interrupt sources, wired-OR INT), §8 (video 50 Hz →
  IM1 RST 0x0038) / `src/P2000.Machine/Interrupts/InterruptAggregator.cs`,
  `src/P2000.Machine/Machine.cs`.
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-03 — Milestone 5 (rework): fields vs frames, single persistent buffer
- **Assumed:** the machine's 50 Hz video cycle was a progressive FRAME - the original
  implementation rendered BOTH the even and odd sub-scanline rows for every physical scanline
  within a single 50,000-T-state pass, double-buffered, swapping a completed 640×480 image to
  a front buffer once per pass.
- **Found (spec correction, this file's §3):** the P2000T is genuinely INTERLACED at 50
  FIELDS/sec, not 50 progressive frames/sec - a field pass renders ONLY its own parity of
  output rows (even field → even rows, odd field → odd rows) with CRS held constant for the
  whole pass, into a SINGLE PERSISTENT buffer with NO inter-field clear (reproducing the
  authentic interlace "comb" on fast motion). Reworked accordingly:
  - `VideoFetchUnit.TStatesPerFrame`/`FrameComplete` renamed to `TStatesPerField`/
    `FieldComplete` (the 50,000-T-state/240-active-line cycle IS a field; two make a frame).
  - `Saa5050Generator.BeginFrame` renamed to `BeginField`; `RenderField` is now called ONCE
    per cell per pass (not twice) - the field-wide `oddField` parity comes from `Video`, not
    from looping both values internally.
  - `Video` dropped the back/front swap for one persistent `_framebuffer` array, added
    `IsOddField`, and toggles field parity at each `FieldComplete`.
  - `Video.FrameComplete` added (`docs/SAA5050-implementation.md` §5: "FrameComplete
    (odd-field only)") - fires once every TWO fields, after the odd one, for a future
    progressive/composited display consumer; `FieldComplete` (every field, 50 Hz) is what
    milestone 6's interrupt aggregator and a future CTC channel-3 clock must use instead.
  - Buffer height was already 480 (24×20), NOT the 500 (25-row, BBC-heritage) the doc warned
    against - no fix needed there.
- **Found (reference-doc terminology, flagged for sync, not corrected here):** reference doc
  §4a calls the 50,000-T-state/50 Hz cycle a "Frame." Per this file's §3 (now confirmed), that
  cycle is actually a FIELD - a P2000T frame is two fields (25 Hz for a complete interlaced
  image). Worth a wording pass in the reference doc's §4a when synced.
- **Found (scope decision, unchanged from the first milestone 5 pass):** the four
  display-mode options (interlaced/comb default, progressive, even-only, odd-only) in this
  file's §3 are explicitly UI-presentation concerns ("the toggle only affects UI presentation")
  - `Video` only produces the default interlaced/comb buffer plus the two events a UI layer
    would need to build any of the four; no mode-switch was added to the machine layer.
- **Applies to:** this file §3 (framebuffer contract), `docs/SAA5050-implementation.md` §5
  (fields/frames/CRS) / `src/P2000.Machine/Devices/Video.cs`,
  `src/P2000.Machine/Devices/Saa5050/Saa5050Generator.cs`,
  `src/P2000.Machine/Contention/VideoFetchUnit.cs`.
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-04 — Milestone 9: MDCR cassette device (authentic phase-bitstream path)
- **Assumed:** `CprinReader` would keep owning CIP/BET/WEN/RDC/RDA until the cassette device
  landed (milestone 4 finding noted both options were compatible with the fan-out/combine model).
- **Found (design decision):** `MdcrDevice` registers its own read source on port 0x20 for
  bits 3–7, and `CprinReader` was shrunk to printer-only (bits 0–2, currently returning 0x00).
  The OR-combine produces identical observable behaviour. This keeps each device owning the bits
  it drives rather than one class acting as a bridge for another.
- **Found (.cas encoding structure — CORRECTED):** per `docs/MDCR-implementation.md §6`, the
  per-block tape layout is: BOB GAP (6160 phases) → MARK (empty WriteData) → HEADER (32 bytes
  from .cas record offset 0x30) → DATA (WriteData of 1024 bytes from .cas record offset 0x100)
  → EOB GAP (1856 phases). The 32-byte block header IS encoded on tape — the ROM's ZOEK
  directory scan reads headers from the bitstream, and CLOAD-by-name matches against them. An
  earlier pass skipped the header (treated it as host-side metadata only); that was wrong and
  has been corrected in `LoadCasImage` (header WriteData added between MARK and DATA).
- **Found (WEN active sense — RESOLVED):** implemented as bit SET = write-protected, which is
  correct. Confirmed from the owner's monitor-ROM disassembly (`Symbols.asm`: `WEN equ 0x08`;
  `Cassette.asm:47`: "bit 3 = WEN (1=protected, 0=can write)"; `cas_status` decodes CIP|WEN as
  00=loaded+writable / 01=loaded+protected / 11=no-cassette). Our code (`if (_tape.IsProtected)
  _status |= WenBit`) is correct. The old owner code had set WEN=1 for writable (inverted) — our
  implementation fixed that.
- **Found (reverse-direction bit mapping — UNVERIFIED):** `MdcrDevice.BitToStatus()` contains
  the owner's unverified reverse-motor branch: when running in REVERSE, toggles RDA (data)
  instead of RDC (clock). Implemented behind the `ReverseDataBitMapping` bool flag (default
  true = current behaviour) as instructed. Confirm once read-while-reversing is observable on
  the RUN test; then set the flag's default and log outcome here.
- **Found (bare-machine port 0x20 default):** with no tape, status = CIP(0x10) | BET(0x20) =
  0x30. WEN is NOT set when no tape is present (treat as don't-care; ROM's write-protect check
  presumably runs only when CIP is clear). This preserves the pre-milestone-9 observed value
  (0x30 in `Tick_InFrom0x20_ReturnsCassetteStatus_BareMachineDefault`).
- **Found (tape at BOT on insert):** after `LoadCasImage` the tape is rewound to position 0
  (BOT). IsAtEnd is true at BOT → BET bit is CLEAR immediately after insert. The ROM should
  spin the motor forward briefly before attempting to read; verify against the real ROM's CLOAD
  startup sequence in the RUN test.
- **Found (SaveState design decision):** state snapshots save tape position (Position + Side)
  only, not the full 1 MB phase array. The .cas image must be remounted after LoadState. This
  matches the precedent from PageTable (embedded ROM not saved in state). If a recorded/blank
  tape scenario needs full-array save, add an opt-in serialization path later.
- **Applies to:** reference doc §5b (MDCR, CIP live, auto-load 'P' file), §5f (CPRIN/CPOUT
  bit maps, WEN active sense) / `docs/MDCR-implementation.md` (full device spec) /
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs`,
  `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs`,
  `src/P2000.Machine/Io/CprinReader.cs`, `src/P2000.Machine/Machine.cs`.
- **Synced:** yes (2026-07-05, into P2000T-reference.md + device guides)

### 2026-07-05 — Milestone 10: contention model
- **Assumed:** contention scope is "any CPU DRAM access" — ROM (0x0000–0x0FFF) and SLOT1
  (0x1000–0x4FFF) are separate ROM/EPROM chips that do NOT share the DRAM address bus, so
  Z80 MREQ to those addresses cannot collide with a SAA5020 display fetch. DRAM starts at
  VRAM (0x5000).
- **Found (design decision, later corrected):** `IsDramAddress(addr)` was initially `addr >= 0x5000`,
  covering VRAM + all RAM. Corrected 2026-07-06: only VRAM is shared with the SAA5020 — see
  correction entry below.
- **Found (corruption timing):** `CorruptLastFetch()` overwrites the 16 already-rendered
  framebuffer pixels for the fetched cell (step 4 of the tick loop, after bus service). The
  fetch fires BEFORE the CPU step (step 1 via `VideoFetchUnit.Tick()`), rendering happens
  inline in `OnColumnFetch()`, then step 4 blanks the pixels if the CPU contested the slot.
  `LastFetchLine` is captured before `VideoFetchUnit.Tick()` updates `Line`, avoiding an
  off-by-one if the line changes in the same tick (it can't in practice: last fetch slot is
  LineTState 97, line boundary at 159 — they never coincide).
- **Found (default corruption mode):** blank/black cell (16 pixels zeroed). Mode is
  swappable once a logic-analyzer/RGBS capture distinguishes bleed vs suppression vs
  contention-to-garbage (reference doc §4 open item).
- **Found (debug overlay):** a flat 40×24 bool array on `Video` (index = charRow × 40 + col)
  is set when a cell is corrupted; cleared AFTER `FieldComplete` fires so consumers can
  inspect it from the FieldComplete handler. Cleared by `Reset()` too.
- **Applies to:** reference doc §4 (bus contention model, Z80 priority, corruption scope,
  default mode) / `src/P2000.Machine/Contention/VideoFetchUnit.cs`,
  `src/P2000.Machine/Devices/Video.cs`, `src/P2000.Machine/Memory/PageTable.cs`,
  `src/P2000.Machine/Machine.cs`, `tests/P2000.Machine.Tests/Contention/ContentionTests.cs`.
- **Synced:** yes (2026-07-07, into reference doc §4 — corruption default + overlay; window superseded by the correction below)

### 2026-07-06 — Milestone 10 correction: contention address window
- **Assumed (wrong):** `IsDramAddress` used `addr >= 0x5000` — any CPU MREQ to VRAM or RAM
  could cause contention.
- **Corrected (per updated reference doc §4):** only the VRAM chip is shared with the SAA5020.
  Base RAM (0x6000+), expansion RAM, and the banked window are separate DRAM chips that the
  SAA5020 never addresses. The contention window is strictly:
  - P2000T: 0x5000–0x57FF (2 KB VRAM chip)
  - P2000M: 0x5000–0x5FFF (4 KB VRAM chip)
- **Change:** `IsDramAddress` (static, wrong) → `IsVideoRamAddress(addr)` (instance method,
  uses `_videoRamEnd` set per model in the PageTable constructor). Machine.cs updated to call
  `Memory.IsVideoRamAddress(...)`. Contention tests updated: hammering loops point to 0x5000
  instead of 0x6000; `IsDramAddress_*` tests replaced with `IsVideoRamAddress_*` tests
  including T-model boundary (0x57FF → true, 0x5800 → false) and P2000M window (0x5FFF → true,
  0x6000 → false).
- **Applies to:** reference doc §4 (contention window) / `src/P2000.Machine/Memory/PageTable.cs`,
  `src/P2000.Machine/Machine.cs`, `tests/P2000.Machine.Tests/Contention/ContentionTests.cs`.
- **Synced:** yes (2026-07-07, into reference doc §4 — VRAM-only window, T + M)

### 2026-07-05 — Milestone 9a: MDCR cassette WRITE / CSAVE path
- **Assumed (earlier):** the bitstream → .cas serializer was missing; realtime write path was
  already present in `ProcessPhase()`.
- **Found (realtime write already complete):** `MdcrDevice.ProcessPhase()` captures `WDA` phases
  to tape when `WCD=1` — identical format to `LoadCasImage`'s `WriteByte` encoding (bit=1 →
  (T,F), bit=0 → (F,T)), so the ROM's CSAVE output round-trips correctly through `Save()`.
- **Found (bitstream → .cas decoder — direct phase-pair approach):** `MiniTape.Save()` uses
  direct phase-pair reading (first phase of each 2-phase pair = the bit value; second = !bit),
  no PLL simulation needed. Gap alignment: the 0xAA frame lead byte starts with bit0=0 → phase0=F,
  which blends into the all-false gap. After skipping the gap, step back by 1 to re-align to
  phase0 of bit0 of 0xAA. This is reliable for both `LoadCasImage`-encoded and CSAVE-written tapes.
- **Found (TimingPolicy — infrastructure added):** `TimingPolicy` enum (Authentic/Turbo) added.
  Authentic gates the 209-cycle phase engine; Turbo bypasses it. Actual turbo ROM trap addresses
  (`cas_Write`/`write_block`) are still deferred — needs confirmed addresses from the ROM
  disassembly (`Cassette.asm`). Log the addresses here once sourced.
- **Applies to:** `src/P2000.Machine/Devices/Cassette/MiniTape.cs` (`Save`, `TryDecodeFrame`,
  `ReadByte`), `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs` (`Policy`, `SaveTape`),
  `src/P2000.Machine/Devices/Cassette/TimingPolicy.cs` (new).
- **Synced:** yes (2026-07-05)

### 2026-07-07 — Milestone 4 (P2000.UI): MDCR tape block structure + byte order (CLOAD fix)
Two bugs were masking CLOAD success; both confirmed by tracing `Cassette.asm` line by line.

- **Assumed (wrong — prior session):** byte encoding was MSB-first, based on misreading `rla`
  in the CRC path as the byte assembler. `rla` is used ONLY for one-bit-at-a-time CRC/parity
  processing (`xor a; rlc l; rla` extracts one RDA bit into A for the CRC loop).
- **Confirmed (byte order is LSB-first):** the actual byte assembler is `rr d` (Cassette.asm
  lines 1136–1140): eight iterations of `rrc l; exx; rr d` rotate one bit at a time into D'
  via carry — rotate-RIGHT means the first received bit lands in bit7 after shift, i.e. the
  FIRST bit received ends up at bit0 after 8 iterations → LSB-first. `WriteByte` must send
  bit0 first. 0xAA (10101010) is the correct sync byte (confirmed from Cassette.asm line 852
  comment and from `fetch_checksum_postamble` write side: `ld d,0xaa`).
- **Assumed (wrong — prior session):** `LoadCasImage` wrote THREE separate `WriteData` frames:
  MARK, HEADER (32 B), DATA (1024 B), with no gaps between them.
- **Confirmed (correct tape block structure):** per `cas_block_read` (lines 804–918) and
  `read_mark` / `load_block`: the ROM calls `search_marker` → `wait_70ms` → `load_block`.
  `search_marker` validates the MARK via `read_until_timeout` (reads until per-bit timeout
  ~5 ms); it counts `paddingbytes = bytes_read - 3` and retries if `paddingbytes != 0`. With
  no gap after MARK, `read_until_timeout` reads into the HEADER frame → `paddingbytes != 0`
  → `search_marker_loop` retries → eventually times out with 'N' or 'M' error.
- **Fix:** `LoadCasImage` now writes:
  `MARK (0xAA | 0x00 | 0x00 | 0xAA)` + `MarkDataGap (~81 ms silence)` +
  `DATA BLOCK (0xAA | header(32B) | data(1024B) | CRC(2B) | 0xAA)`.
  Header and data share ONE combined frame with ONE CRC — confirmed from `load_block` lines
  912–918 (`0xAA | header(32B) | data(1024B) | CRC(2B) | 0xAA`). The `MarkDataGap` must be
  ≥ 70 ms so the DATA BLOCK's preamble starts after `wait_70ms` completes; using 970 phases
  (~81 ms at 2.5 MHz).
- **MARK validation mechanism:** `read_until_timeout` uses a per-bit timeout of 256 × 51
  T-states (~5 ms). In the silence gap, all-false phases → PLL loses lock → RDC stops toggling
  → `wait_next_bit` times out → `read_until_timeout` exits. The gap must be long enough that
  the PLL loses lock cleanly before `wait_70ms` completes and `load_block` begins.
- **Save() updated** to match: skip gap → TryDecodeFrame(0) for MARK → skip MarkDataGap →
  TryDecodeFrame(1056) for combined HEADER+DATA → split `combined[0..31]` / `combined[32..]`.
- **Applies to:** `docs/MDCR-implementation.md` §6 (tape block structure, byte order) /
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs` (`LoadCasImage`, `Save`, `WriteByte`,
  `ReadByte`, `WriteData`, `UpdateChecksum`, `TryDecodeFrame`).
- **Synced:** yes (2026-07-09, into reference doc §5b — block structure + byte order marked CONFIRMED; NOTE: `docs/MDCR-implementation.md` §6 is this finding's primary home — apply the detailed layout there too, which this pass could not edit)

### 2026-07-05 — Milestone 11: config + state serialization
- **Assumed:** `MachineConfig` fields are JSON-serializable with `System.Text.Json` in-box;
  enum values can be serialized as strings by applying `JsonStringEnumConverter`.
- **Found (enum casing — corrected during implementation):** `JsonStringEnumConverter`
  accepts an optional naming policy. Passing `JsonNamingPolicy.CamelCase` serializes
  `RamVariant.T54` → `"t54"` and `MachineModel.P2000T` → `"p2000T"` — unreadable for a
  human-editable config file. The converter must be constructed WITHOUT a naming policy
  (`new JsonStringEnumConverter()`) so enum values retain their declared names (`"T54"`,
  `"P2000T"`). Property NAMES are still camelCase via the top-level `PropertyNamingPolicy`.
- **Found (ROM not in state — determinism test implication):** ROM bytes are not saved in
  `.state` files (by design: the ROM is read-only, embedded, config-determined — same as the
  PageTable findings above). A determinism test that injects a synthetic ROM via `LoadRom`,
  saves state, and then loads must re-inject the synthetic ROM into the restored machine, or
  the restored machine runs the real embedded monitor ROM from the saved PC, diverging.
  Resolution: the determinism test uses the real monitor ROM end-to-end (no synthetic ROM),
  which is present in both machines by construction. The monitor ROM enters a stable
  CIP-polling loop after one field; both original and restored machines execute identical
  code from identical state and produce matching PC/SP/VRAM after one additional field.
- **Found (HALT + AtInstructionBoundary):** `Z80.Core.AtInstructionBoundary` returns false
  when the CPU is halted (`!_halted` is part of the expression). Using HALT as a synthetic
  ROM terminator causes `SaveAndReload`'s `while (!AtInstructionBoundary)` loop to spin
  forever. Resolution: test synthetic ROMs that need to stop use `JR -2` (0x18 0xFE) for
  an infinite spin that still returns to an instruction boundary between iterations.
- **Found (`.state` binary layout):** "P2ST" magic (4 bytes) + version int32 (LE) +
  config-JSON byte-length int32 (LE) + config JSON UTF-8 + distributed device state stream.
  `StreamStateWriter`/`StreamStateReader` wrap `BinaryWriter`/`BinaryReader` with UTF-8
  encoding. Restore = `new Machine(config)` (full reset) then `machine.LoadState(reader)`.
- **Found (`AtInstructionBoundary` save semantics):** state is saved only at instruction
  boundaries (the public `AtInstructionBoundary` property on Z80.Core). At those points all
  of Z80.Core's private fields (`_phase`, `_tstate`, `_prefix`) are at their known reset-
  compatible defaults (Fetch / 0 / None), so the serialized CPU struct is self-consistent
  without saving any private fields.
- **Applies to:** reference doc §3a (config vs state serialization, versioning) /
  `src/P2000.Machine/State/MachineConfigFile.cs`, `src/P2000.Machine/State/MachineStateFile.cs`,
  `src/P2000.Machine/State/StreamStateWriter.cs`, `src/P2000.Machine/State/StreamStateReader.cs`,
  `tests/P2000.Machine.Tests/State/MachineConfigFileTests.cs`,
  `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs`.
- **Synced:** yes (2026-07-05)

### 2026-07-06 — Milestone 12: slot model formalized
- **Assumed:** SLOT1 loading would stay inside PageTable (raw `byte[]? _slot1`) and the typed
  slot interfaces could wrap around it without changing the constructor.
- **Found (design decision):** moved SLOT1 ROM loading OUT of PageTable entirely into Machine.
  PageTable now accepts `IMemorySlot? cartridge` as a constructor parameter (default null).
  Machine constructs `Slot1Cartridge` from `config.Slot1CartridgePath` and passes it in.
  This makes SLOT1 a first-class typed object (`machine.Slot1`) rather than a hidden raw array,
  which is the whole point of formalizing the slot model.
- **Found (open-bus fix for short images):** the old PageTable code zero-filled the 16 KB
  `_slot1` array, meaning bytes beyond the image length read as 0x00 rather than 0xFF
  (open-bus). `Slot1Cartridge` fixes this: it only allocates `_imageLength` bytes and returns
  `PageTable.OpenBus` (0xFF) for addresses beyond the image. Correct behavior: an unprogrammed
  EPROM reads 0xFF; the old code was wrong but harmless in practice since BASIC.bin is exactly
  16 KB.
- **Found (IIoSlot has no unregister):** Reset-to-apply (locked decision §2.3) means a slot
  card's port listeners are registered once at machine-assembly time and live for the machine's
  lifetime; there is no runtime slot-swap. `IIoSlot.RegisterPorts` is the only seam needed —
  no `UnregisterPorts` method required or added.
- **Found (NMI aggregator seam — binary format change):** `InterruptAggregator.SaveState` now
  writes two booleans (`_intPending`, `_nmiPending`) instead of one. This is a `.state` format
  change — the version field in `MachineStateFile` must be bumped before releasing any
  persisted state files. Bumping deferred to when the UI/file-save path lands (no external
  `.state` files exist yet in the T-first build).
- **Found (NMI test note — SP at reset):** the NMI vector test uses the default T38 machine
  (SP=0x0000). NMI pushes the return address to 0xFFFF/0xFFFE (banked window, no banks →
  writes discarded). The CPU still completes the NMI sequence and jumps to 0x0066 correctly
  — the corrupt stack only matters on RETN, which the test avoids by using a `JR -2` spin.
- **Applies to:** reference doc §5c (slot types, bus connections), §5e (NMI sources) /
  `src/P2000.Machine/Slots/ISlotCard.cs`, `IMemorySlot.cs`, `IIoSlot.cs`, `INmiSource.cs`,
  `Slot1Cartridge.cs`; `src/P2000.Machine/Memory/PageTable.cs`,
  `src/P2000.Machine/Interrupts/InterruptAggregator.cs`, `src/P2000.Machine/Machine.cs`.
- **Synced:** yes (2026-07-07, into reference doc §5c — typed slot/open-bus, §5e + §3a — NMI latch + .state version bump pending)

### 2026-07-09 — Milestone 16: SoundDevice formalized + machine-level audio tests
- **Assumed:** `LoadState` only needed to restore `_beeperState` (the single persisted field).
- **Found:** `LoadState` must also clear `_transitions`. State is always captured at a field
  boundary when `_transitions` is empty (just been cleared by `OnFieldComplete`). If any
  CPOUT writes happened between LoadState and the next field boundary, stale transitions would
  corrupt synthesis. `_transitions.Clear()` added to `LoadState`.
- **Found (test helper — closure trap):** `SoundDevice` captures `Func<int> getFieldTState` at
  construction. The lambda must close over the same local variable that the test helper mutates;
  returning a setter `Action<int>` from `CreateSink()` is the correct pattern. Passing `ref int`
  to a separate RunField helper doesn't work because C# lambdas can't capture `ref` locals.
- **Found (test helper — initial CPOUT byte):** `RunField`'s toggle logic XORs `cpoutByte`
  against the beeper bit. If the latch already holds the beeper bit (e.g. after a previous
  field), starting `cpoutByte` at 0 means the first "toggle" repeats the current state rather
  than changing it → no transition recorded. `RunField` takes an `initialCpout` parameter to
  match the latch's running state.
- **Applies to:** project CLAUDE.md §13.16 / `src/P2000.Machine/Devices/SoundDevice.cs`
  (LoadState fix), `tests/P2000.Machine.Tests/Devices/SoundDeviceTests.cs` (new, 13 tests).
- **Synced:** yes (2026-07-09; SoundDevice seam + LoadState-clears-transitions synced to reference §5 Sound; test-helper items are implementation-only)

### 2026-07-09 — UI Milestone 7: SoundDevice (1-bit beeper, machine layer)
- **Assumed:** CPOUT bit 4 (0x10) is the BEEP line. Reference doc §7 confirms a "1-bit speaker"
  on SLOT2 pin 13A but does NOT name the CPOUT bit; bits 4 and 5 are listed as "unused." This
  assignment follows common P2000T emulator practice (including the canonical MAME driver) and
  produces the audible boot beep. Revisit if a schematic or ROM disassembly confirms otherwise.
- **Found (SoundDevice design):** subscribes to `CPoutLatch.Written`, records `(FieldTState,
  State)` transitions per field. `OnFieldComplete()` synthesizes a 882-sample PCM block at
  44 100 Hz (50 Hz → 882 samples) by walking recorded transitions; fires `event Action<short[]>?
  SamplesReady` with a reusable buffer. One fixed buffer; callers must copy immediately.
- **CORRECTED 2026-07-09 — BEEP is I/O port `0x50` bit 0, NOT CPOUT (0x10) bit 4.** The assumption above was wrong. `SoundDevice` must be **rewired to watch port `0x50` writes (bit 0)** instead of `CPoutLatch.Written`; reference doc §5 Sound + machine §6/§7 updated.
- **Found (SaveState format change):** `Sound.SaveState(writer)` is inserted between
  `Mdcr.SaveState` and `Interrupts.SaveState` in `Machine.SaveState/LoadState`. Any `.state`
  files saved before this milestone are not forward-compatible.
- **Applies to:** reference doc §5 Sound (CPOUT/BEEP bit, audio-output seam), §3a (.state version
  bump) / `src/P2000.Machine/Devices/SoundDevice.cs` (new), `src/P2000.Machine/Machine.cs`.
- **Synced:** yes (2026-07-09, into reference doc §5 Sound — BEEP bit + audio seam, §3a — bump folded in; formalized as machine milestone 16)

### 2026-07-06 — Milestone 13: observer state-snapshot surface
- **Assumed:** the snapshot needed a new FieldTState exposure — `VideoFetchUnit` and `Video`
  had no public `FieldTState` property yet.
- **Found (trivial additive change):** `VideoFetchUnit._fieldTState` was already the master
  counter; adding `public int FieldTState => _fieldTState;` on `VideoFetchUnit` and a
  forwarding property on `Video` was the entire change to existing code.
- **Found (delegate for ReadMemory — one allocation):** `MachineSnapshot.ReadMemory` holds a
  `Func<ushort, byte>` bound to `PageTable.Read`. Allocated once at `TakeSnapshot()` time;
  every subsequent `ReadMemory(addr)` call is a direct delegate invoke with no extra
  allocation. Accepted trade-off: trivial cost for a live, side-effect-free memory view that
  needs no array copy.
- **Found (RunToNextBoundary test helper — post-reset pitfall):** after `new Machine()`, the
  CPU is at `AtInstructionBoundary=true` immediately (reset leaves the core in Fetch/T0/None).
  A naïve "tick until boundary" helper that always ticks at least once skips past the first
  instruction boundary. The helper must check `if (AtInstructionBoundary) return;` before
  ticking.
- **Found (short test ROM pitfall):** a test ROM shorter than the NOP's read window (e.g.
  `new byte[] { 0x00 }`) leaves bytes at index 1+ as monitor-ROM content, causing unexpected
  PC advance after one NOP. Tests that advance exactly one instruction use a full 4 KB ROM
  with a `JR -2` loop at 0x0001 to keep PC in a known range.
- **Applies to:** project CLAUDE.md §3b.1 /
  `src/P2000.Machine/Contention/VideoFetchUnit.cs` (`FieldTState` property),
  `src/P2000.Machine/Devices/Video.cs` (`FieldTState` property),
  `src/P2000.Machine/Debug/MachineSnapshot.cs` (new),
  `src/P2000.Machine/Machine.cs` (`TakeSnapshot()`),
  `tests/P2000.Machine.Tests/Debug/MachineSnapshotTests.cs` (new).
- **Synced:** yes (2026-07-07, into reference doc §3a — observer contract as built)

### 2026-07-06 — Milestone 14: machine-owned breakpoint store
- **Assumed:** the store would need a complex per-tick lookup structure (HashSet etc.) for
  performance.
- **Found (list scan is sufficient):** debugger breakpoints are few (typically 0–5); a linear
  scan of a `List<Entry>` in the hot path is negligible at 2.5 MHz. The only real performance
  contract is the `AnyArmed` fast path: when the list is empty the entire breakpoint block is
  skipped with a single `Count == 0` check.
- **Found (IsPaused + Resume() design):** the machine needs an explicit `IsPaused` flag so
  repeated `Tick()` calls while paused are no-ops rather than re-firing the event on every
  call. `Resume()` also clears `_breakPending` so a deferred mid-instruction hit doesn't
  re-trigger after resuming.
- **Found (exec bp fires before instruction — no advance):** exec bps return early from
  `Tick()` before `Video.Tick()` and `Cpu.Step()` — the instruction at the bp address has NOT
  executed yet; PC is correct for a "about to execute" debugger display.
- **Found (mid-instruction bps deferred to next boundary):** mem/IO bps that fire mid-
  instruction set `_breakPending`; the full tick completes, and the break is raised at the
  START of the next instruction boundary tick before anything else advances.
- **Found (int-ack excluded from IO bps):** M1+IORQ int-ack is NOT a user I/O access — IO bp
  checks live only in the plain IORQ+RD/WR branches, not the M1+IORQ branch.
- **Applies to:** project CLAUDE.md §3b.2 /
  `src/P2000.Machine/Debug/BreakpointKind.cs` (new),
  `src/P2000.Machine/Debug/BreakEvent.cs` (new),
  `src/P2000.Machine/Debug/BreakpointStore.cs` (new),
  `src/P2000.Machine/Machine.cs` (`Breakpoints`, `BreakHit`, `IsPaused`, `Resume()`, `Tick()`,
  `Reset()`),
  `tests/P2000.Machine.Tests/Debug/BreakpointStoreTests.cs` (new).
- **Synced:** yes (2026-07-07, into reference doc §3a — breakpoint semantics as built)

### 2026-07-07 — Milestone 15: command queue (§3b.3)
- **Assumed:** drain could run first at each boundary, then check `_pauseAtNextBoundary`
  immediately in the same tick.
- **Found (ordering bug — corrected):** placing `DrainCommandQueue()` BEFORE the boundary
  checks means `SingleStepCommand` sets `_pauseAtNextBoundary` and the check fires in the
  SAME tick — the instruction never executes. Fix: checks A–D (including `_pauseAtNextBoundary`,
  run-to-cycle, breakpoints) run BEFORE the drain. The drain sets state that is consumed on
  the NEXT boundary. The drain must run even while paused (so `RunCommand` can un-pause), but
  the un-pause takes effect on the following tick.
- **Found (`PauseCommand` must `break`, not `return`):** using `return` in the drain switch
  stopped processing subsequent commands in the same drain pass (e.g. a `SetPcCommand` queued
  after `PauseCommand` was silently dropped). Changed to `break`; drain always exhausts the
  queue.
- **Found (T38 default machine — banked window is open-bus):** `RamVariant.T38` gives
  `EffectiveBankCount = 0`. The banked window 0xE000–0xFFFF has no banks: writes are silently
  discarded, reads return 0xFF. Reset leaves SP=0x0000; CALL wraps to 0xFFFF/0xFFFE (banked
  window) — stack writes discarded, stack reads 0xFF → `RET` sets PC=0xFFFF. Step-over/step-out
  tests must set `m.Cpu.Reg.SP = 0x8000` to use base RAM for the stack.
- **Found (Z80 M1 fetch increments PC at T0):** after `Reset()` (PC=0) + one `Cpu.Step()`,
  PC is already 1 — the fetch consumed the opcode byte and advanced PC before the instruction
  is fully executed. Warm/cold reset tests assert `PC <= 1` rather than `PC == 0`.
- **Found (WarmReset/ColdReset clear the queue):** after a reset the queue is cleared
  (`_commandQueue.Clear()`) and drain returns immediately — subsequent commands in the same
  flush (e.g. a stale `SetPcCommand` from before the reset) are dropped. This is intentional:
  a reset is a full state wipe.
- **Applies to:** project CLAUDE.md §3b.3 /
  `src/P2000.Machine/Debug/MachineCommand.cs` (new — 19 command types),
  `src/P2000.Machine/Memory/PageTable.cs` (`ClearRam()` added),
  `src/P2000.Machine/Machine.cs` (`Enqueue`, `NonReplayableAction`, `DrainCommandQueue`,
  `ApplyStepOver`, `ApplyStepOut`, `GetCallLikeLength`, `GetEdCallLikeLength`, updated
  `Tick()` + `Reset()`),
  `tests/P2000.Machine.Tests/Debug/CommandQueueTests.cs` (new — 26 tests).
- **Synced:** yes (2026-07-07, into reference doc §3a — command queue + ordering rule as built)

### 2026-07-11 — Milestone 17: Z80 CTC + IM2 daisy chain + Lock interlock
- **Assumed:** the CTC ports/control-word bits/vector formula/Lock semantics were all
  CONFIRMED hardware (reference doc §5d/§5e) and implemented as documented.
- **Found (real integration bug — corrected):** `Machine.Tick()`'s int-ack branch called
  `Interrupts.Acknowledge()` on EVERY T-state the int-ack M-cycle holds M1+IORQ asserted
  (Z80.Core's ack M-cycle is 6T, with M1+IORQ together across 3 of them — T2/T3/T4). This was
  harmless for the old stateless IM1 pull-up (`Acknowledge()` just returned a constant 0xFF,
  idempotent), but a daisy-chain `Acknowledge()` has real side effects (clears pending, sets
  in-service) — the second/third call within the SAME M-cycle saw the just-acknowledged
  channel now blocked by its own in-service flag, fell through to the 0xFF pull-up, and
  overwrote the correct vector byte on the data bus before the core's T4 sample. Net effect:
  IM2 always vectored through address `(I<<8)|0xFE` instead of the real vector, landing in
  whatever RAM happened to be there. Fixed by edge-detecting the FIRST T-state of a given ack
  cycle (comparing pins captured before `Cpu.Step()` against after) and caching/re-driving the
  same byte for the rest of the M-cycle. Worth flagging for `P2000.UI`/debugger code too if it
  ever reads bus state mid-M-cycle.
- **Found (confirms existing Z80.Core-documented behaviour, surprised test-writing):** a
  maskable INT acceptance clears BOTH IFF1 and IFF2 (Z80.Core CLAUDE.md §5 ack-cycle T1), so a
  bare `RETI` after a normal (non-nested) interrupt leaves interrupts DISABLED — the ISR must
  `EI` before `RETI` (the standard idiom; reference doc §5e's `enable_interrupts = EI + RETI`
  is not just style, it's load-bearing). A CTC test omitting the `EI` silently "worked" for a
  single interrupt but left the second field's interrupt pending-forever un-acceptable.
- **Found (test-writing gotcha, not a Machine bug):** on this core, returning via `RET`/`RETI`
  to a `HALT` instruction's address does NOT resume the halted state — real Z80 (and this
  core) only re-enters the halt loop by re-executing an actual `HALT` opcode; a return to
  `HALT_addr+1` just continues linear execution. A multi-interrupt test parked on `HALT` will
  fall through into whatever follows (zero-filled ROM reads as NOP; open-bus SLOT1 reads as
  `0xFF` = `RST 38h`) and wander unpredictably. Use a tight `JR -2` spin loop instead (already
  the established pattern in `InterruptAggregatorTests.RaiseNmi_VectorsToNmiHandler_At0x0066`)
  for any test that must survive more than one interrupt cycle.
- **Found (re-confirms milestone-15's T38-SP finding, now load-bearing for IM2 too):** the
  default post-reset SP=0x0000 (T38 banked-window-open-bus finding, milestone 15 above) also
  corrupts `RETI`'s pop when a CTC ISR pushes/pops a return address — any interrupt-driven CTC
  test needs `LD SP,nn` into real RAM (e.g. `0x9FFE`) first.
- **Design decision (not a hardware finding):** `DaisyChain` registrants are individual CTC
  *channels* (ch0..ch3), not the chip as a whole — each channel gets its own
  `IDaisyChainDevice` link, matching how the real chip's channels chain IEI→IEO internally
  (reference doc §5d: "CTC channels register in priority order (ch0 > … > ch3)"). A future
  SLOT2 card registers as a single chip-level link behind them.
- **Design decision (documented default for the one open item, per the milestone's own
  "resolvable during implementation" note):** Lock gates only the maskable video INT, not NMI
  — the front-panel reset button and SLOT1 NMI have no logical tie to the internal-slot board.
  Revisit if a schematic/service-manual capture confirms Lock also gates NMI.
- **Applies to:** reference doc §5d/§5e (CTC ports/control word/vector formula/Lock — all
  implemented as documented, no corrections needed there) /
  `src/P2000.Machine/Devices/Ctc/Z80Ctc.cs`, `CtcChannel.cs` (new),
  `src/P2000.Machine/Interrupts/DaisyChain.cs`, `IDaisyChainDevice.cs` (new),
  `src/P2000.Machine/Interrupts/InterruptAggregator.cs` (Lock + daisy-chain gating),
  `src/P2000.Machine/Machine.cs` (Ctc wiring, RETI snoop, int-ack edge-detection fix),
  `src/P2000.Machine/State/MachineStateFile.cs` (bumped to v3),
  `tests/P2000.Machine.Tests/Devices/Ctc/Z80CtcTests.cs` (new — 12),
  `tests/P2000.Machine.Tests/Interrupts/DaisyChainTests.cs` (new — 6),
  `tests/P2000.Machine.Tests/Interrupts/CtcIntegrationTests.cs` (new — 7),
  `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs` (+2: Ctc/Lock round-trip, v2 now rejected).
- **Synced:** no (pending human sync into P2000T-reference.md + Z80.Core/P2000.UI notes re:
  the int-ack multi-T-state gotcha)

### 2026-07-11 — Milestone 18: tape turbo — ROM-trap fast load/save
- **Assumed:** the milestone's own spec named `cas_block_read`/`load_block` and
  `cas_Write`/`write_block`/`cas_block_write` as candidate trap points, from before a
  disassembly existed.
- **Found (source obtained — owner-supplied commented disassembly, not a fresh disassembly
  pass):** the owner provided `MonitorRom.sym` (full label→address symbol table) plus
  commented `Cassette.asm`/`Startup.asm`. Confirmed addresses: `cas_Read`=0x0552,
  `cas_Write`=0x057A, `cas_block_read`=0x0872, `load_block`=0x091C, `cas_block_write`=0x061F,
  `write_block`=0x0594; RAM variables `transfer`=0x6030, `file_length`=0x6032,
  `record_length`=0x6034, `des1`=0x6068, `des_length`=0x606A, `cassette_error`=0x6017.
- **Design decision (trap point chosen, deviates from the milestone's own placeholder
  names):** traps **`cas_Read`/`cas_Write`**, NOT the lower `cas_block_read`/`cas_block_write`.
  Tracing the calling convention showed `cas_Read`/`cas_Write` own the ENTIRE multi-block
  transfer loop themselves (their own `block_counter` loop calling `get_block_parameters` +
  the block-level routine each iteration) and are entered with a clean, fully-defined RAM
  contract already established by the "cassette" jump-table dispatcher
  (`knowncascommand`/`do_cas_jump`) before jumping in — trapping one level higher means ONE
  intercept per file transfer instead of one per block, and avoids needing to reverse-engineer
  `cas_block_read`/`cas_block_write`'s internal replace-vs-append search logic at all (see
  next finding).
  - Both routines are entered via `jp (hl)` with the return address (`cas_command_return`)
    already pushed by `do_cas_jump` — functionally identical to a `CALL`. The trap performs
    the whole transfer in C#, writes `cassette_error`, and simulates the routine's own final
    `RET` (pop PC off the real stack). It deliberately does NOT replicate
    `cas_command_return`'s cleanup (motor off, `enablekey`, register restore) — that code runs
    completely normally afterwards since the trap only ever intercepts execution AT the
    cas_Read/cas_Write entry point, never past it.
- **Found (block-count/valid-length math, transcribed from `get_length_blocks`/
  `get_block_parameters`):** block count = ceil(file_length/1024) with a minimum of 1
  (replicated as a repeated 16-bit subtract that stops on the first borrow, exactly matching
  the ROM's own loop including its behaviour for file_length==0); each block's real
  (non-padding) byte count = min(remaining, 1024), where `remaining` starts at `record_length`
  and only the LAST block can be partial. The destination/source address always advances by
  exactly 1024 bytes per block regardless of how many of those bytes are "real" — a partial
  last block still consumes a full 1024-byte destination slot.
- **Design decision (write semantics — replace vs append):** the real ROM's `cas_block_write`
  distinguishes REPLACE (overwrite an existing block, found by searching forward for its
  marker) from APPEND (write at the end of written data) via a physical forward tape search,
  because real hardware doesn't know the head position without reading. The emulator always
  KNOWS the exact head position, so the turbo trap skips that search entirely and just writes
  the new MARK+DATA-BLOCK pair at wherever the head currently is — this reproduces the same
  net effect (overwrite in place when parked over an existing block, append when parked on
  blank tape) without needing to port the search/replace logic.
- **Added (`MiniTape`):** `TryReadBlockAtHead`/`WriteBlockAtHead` — head-relative single-block
  decode/encode, refactored out of the existing `Save()`/`LoadCasImage()` per-block logic
  (`TryDecodeBlockAt`/`WriteBlockFrames` are now shared private helpers) so turbo and
  authentic modes produce byte-identical on-tape encoding — confirmed by the read-side test
  (turbo load vs. a real CPU-driven authentic load of the same `.cas`, byte-identical RAM) and
  the write-side test (turbo-written tape decoded via the authentic `Save()` matches the
  source bytes exactly).
- **Found (test-writing pattern):** driving `cas_Read`/`cas_Write` directly (bypassing the
  "cassette" jump-table dispatcher) needs `des_length` (0x606A) set to 0x20 in RAM — the real
  dispatcher always sets it, but a test that jumps straight to `cas_Read` must set it manually
  or the authentic engine's `load_block` skips the header segment entirely (`des_length==0`
  short-circuits it). The turbo trap itself does NOT read `des_length` (treats header size as
  the architecturally-fixed 32 bytes), so this only matters for the authentic comparison leg
  of the test suite, not the trap's own correctness.
- **Applies to:** project CLAUDE.md §13.18 (whole milestone) / `Cassette.asm`/`Startup.asm`/
  `MonitorRom.sym` (owner-supplied, not in this repo) /
  `src/P2000.Machine/Devices/Cassette/CassetteTurboTrap.cs` (new),
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs` (`TryReadBlockAtHead`, `WriteBlockAtHead`,
  `TryDecodeBlockAt`, `WriteBlockFrames` refactor), `MdcrDevice.cs` (`TryReadBlockAtHead`,
  `WriteBlockAtHead`, `IsWriteProtected`), `src/P2000.Machine/Machine.cs` (trap check in
  `Tick()`), `tests/P2000.Machine.Tests/Devices/CassetteTurboTrapTests.cs` (new — 6 tests).
- **Synced:** no (pending human sync into P2000T-reference.md §5b — the confirmed trap
  addresses and RAM variable layout aren't in the reference doc yet, since they came from an
  external symbol table/disassembly not held in this repo).
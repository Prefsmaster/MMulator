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
- **Format & size — CHANGED (2026-07-22, owner request): the machine renders the FULL FIELD,
  not just the active picture.** Full detail and the geometry math live in reference doc §4a
  ("Full raster geometry") — summary here:
  - The framebuffer is a `uint[]` of BGRA pixels, now sized **928 × 626** for the complete
    313-scanline field, minus horizontal retrace (was 640×480, active-picture-only — see
    history below). **Width CORRECTED (2026-07-22, owner's retrace model, same day as the
    original full-field decision — see the dated exchange in §17):** the owner does not have a
    scope to confirm the real video signal directly, but reasons that the chip cuts off
    emission immediately after char-time 64 (end of line) and the following line's char-times
    0–5 (6 char-times — flagged 5-vs-6 ambiguity, manual not fully explicit) are genuine
    **horizontal retrace: the chip emits nothing at all there, not even black.** Only after
    retrace does it resume emitting blanked/porch signal (renderable as black) up to the
    active window. Trailing blank is left intact (owner's explicit instruction — retrace is a
    leading-edge-of-the-NEXT-line phenomenon, not a trailing one). Net effect: leading blank
    shrinks from 15 to **9 char-times**, width shrinks from 1024 to **928**.
  - **Horizontal (928 px):** 16 rendered pixel-lanes per char-time (unchanged anti-aliasing
    lane count) — leading blank (144 px, 9 char-times, retrace's 6 char-times/96 px excluded
    entirely) + active (640 px, 40 char-times) + trailing blank (144 px, 9 char-times,
    unchanged).
  - **Vertical (626 px):** 2 rendered rows/scanline (unchanged CRS line-doubling) × 313
    scanlines = pre-roll blank (98 px, 49 scanlines) + active (480 px, 240 scanlines) +
    post-roll blank (48 px, 24 scanlines). (Vertical retrace not yet addressed — the owner's
    retrace-exclusion request so far only covers horizontal; flag for a future pass if wanted.)
  - **The active 640×480 "graphics window" sits at a fixed offset (144, 98)** within the full
    buffer — a constant crop rectangle, same every field (fixed hardware timing, not
    data-dependent). Horizontally symmetric (144 px border both sides of the 640 px active
    width) as a side effect of the 6-char-time retrace assumption, not independently confirmed.
  - **Blanking pixels are a fixed fill colour, no per-fetch rendering** — no fetch happens
    there (§4/reference doc: no VRAM access outside the active window), so there's no content
    to render and no contention possible; no `CombineRows` smoothing work needed for those rows.
    **CHANGED (2026-07-23, owner request — UI/UX choice, not a hardware fact):** filled with a
    very dark grey (`Video.BlankingColor`, RGB (32,32,32)), not pure black — real hardware's
    blanking signal genuinely IS black, but an all-black-background screen (background colour 0
    is also pure black — reference doc §4/`Saa5050Palette`) would otherwise be visually
    indistinguishable from the surrounding Full-Field margin. Filled once, at construction and
    on `Reset()` (`Array.Fill`, not `Array.Clear`) — still no per-field fill cost, since the
    active-window overwrite on every fetch is unaffected.
  - **Why this is the right owner (continuing the 2026-07-21 ownership correction below):** the
    machine's job is to produce the complete, truthful raw signal; the UI decides how much of
    it to show. This extends that same principle from "which field(s)" to "how much of each
    field, including blanking" — see reference doc §3a's new "Full-Field vs Graphics-window"
    UI toggle (orthogonal to the existing 4-way display-mode toggle; UI-owned, not this file's
    concern, same as the 4-way mode).
  - **history (pre-2026-07-22 framebuffer definition, superseded above but kept for context):**
    the SAA5050 renderer emits **16 pixel-lanes per character** (NOT a naive 6×2=12) — the
    horizontal rounding is computed at sub-pixel resolution, which is why the glyph tables pack
    2 bits/pixel and both jsbeeb and the owner's C# port unroll a 32-bit `chardef` 16× per
    character. The buffer was **640 × 480** for 40×24 (40 chars × 16 lanes = 640 wide; 24 rows
    × 20 rendered scanlines = 480 high, the 20 being 10 logical lines doubled) — do NOT
    "simplify" the width to 480, that discards horizontal smoothing. **NB: NOT 500 high** — the
    owner's reference video code used 640×500 (25 rows), which is **BBC-Micro heritage** (BBC
    teletext is 25 rows); the P2000T is **24 rows = 480**. These per-character/per-row scaling
    facts (16 lanes/char, 2 rows/scanline) are UNCHANGED by the full-field move above — only the
    buffer's overall extent grew to cover blanking too.
  - **Downstream impact — NOT swept in this pass, flagged for Claude Code (§17 below has the
    full list):** this file and `P2000.UI/CLAUDE.md` both mention "640×480" as the framebuffer
    size in many other places (ownership/observer sections, PAL-aspect-correction notes,
    existing tests, `WriteableBitmap` allocation, `CorruptionOverlay` coordinate space). Those
    need a coordinated sweep — see the dated finding below for the concrete list — rather than
    a piecemeal fix; the definition here is the new source of truth to reconcile the rest
    against.
- **Fields vs frames — CORRECTED (2026-07-21, owner-supplied P2000TM Field Service manual +
  owner clarification): the P2000T is NOT interlaced.** The manual states, for the T-version:
  *"the signal CRS is active during the even scanlines of the field. In our system we use only
  the odd scanlines, so no interlacing is used."* There is no real hardware alternation between
  a differently-fetched "even field" and "odd field" — **every field is a complete, independent
  313-line refresh at 50 Hz** (reference doc §4/§4a), and CRS/RA0 picks raw-vs-smoothed
  **sub-scanlines within that one field's already-fetched row data**, not a second field's
  separately-sourced content. The "interlaced, frame = two fields = 25 Hz" model below was
  **BBC-Micro heritage carried over from jsbeeb/MAME** (both genuinely interlaced machines),
  not P2000T hardware fact — flagged and corrected here; the owner agrees.

  **Ownership correction (owner, 2026-07-21): the display-MODE default is a UI setting, not a
  machine one — this file should not have asserted a "machine default" change.** This file's own
  pre-existing 2026-07-05 milestone-5 finding (§17 below) already scoped this correctly: *"the
  four display-mode options... are explicitly UI-presentation concerns... `Video` only produces
  the [raw per-field] buffer plus the two events a UI layer would need to build any of the
  four; no mode-switch was added to the machine layer."* That scoping stands. The owner's
  2026-07-21 decision to default to Odd-only (line-doubled single field) instead of
  Interlaced/comb — because Odd-only is the one that matches the FSM's "only the odd scanlines,
  no interlacing" — is a **P2000.UI-owned setting/preference default**, recorded in
  `src/P2000.UI/CLAUDE.md` §8 and reference doc §3a, not here. This file only needs the
  underlying hardware-timing fact corrected (done above); it should not restate or own the
  UI's default value.

  **WITHDRAWN (2026-07-22, owner correction) — the question below was a mistake, do NOT act
  on it:** this file previously flagged (2026-07-21) whether `Video`'s per-field
  buffer-composition — "each field writes ONLY its own [alternating] scanlines into one
  persistent buffer" — should be collapsed into a single-pass-per-field model, reasoning that
  since there's no true interlace, one field's own data ought to be self-sufficient. **The
  owner caught that this framing risks Claude Code reverting/simplifying real, working
  machinery:** the current implementation already computes distinct even/odd field passes, and
  the existing four display modes (Interlaced/comb, Progressive, Even-only, Odd-only) all
  depend on that dual-pass machinery being intact — collapsing it to "one field, always
  complete" would break Progressive and Interlaced/comb, requiring them to be rebuilt later for
  no benefit. **Correct resolution: change NOTHING about the per-field write/compute pattern.**
  Odd-only already exists today and already presents exactly the single-field, line-doubled
  view the FSM describes as authentic — the only actual change needed is which mode is
  DEFAULT (Interlaced/comb → Odd-only, a preference value in `P2000.UI`, §8/reference doc §3a),
  not a rendering-code change. Keep all four modes' underlying computation exactly as
  implemented.

  The original (now-superseded, BBC-heritage) framing is preserved below for context on the
  internal even/odd sub-scanline mechanism, which likely still has value as an **intra-field**
  concept (CRS toggling within one field's data) even though it is not a true inter-field
  alternation:
  - the P2000T runs **50 fields/sec**; the CRS/RA0-selected **smoothed sub-scanline** ("odd" in
    the old model) is where the diagonal smoothing lands within each field's own data. Consequences:
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
  - **Display mode — four options over the SAME rendered scanlines. This is a UI-owned
    setting/preference (see the ownership correction above) — the four options are listed here
    only as context for what the machine's raw per-field output must support. The current
    default value and the owner's 2026-07-21 decision to change it live in `P2000.UI/CLAUDE.md`
    §8 and reference doc §3a, not here.**
    1. **Interlaced (comb):** both fields, single persistent buffer, present per field, no
       inter-field clear → the interlace comb artifact on fast horizontal motion (as above).
       Per the correction above, this is NOT authentic T behaviour (no real hardware interlace)
       — a legitimate optional/nostalgia mode, not the default (see UI doc for current default).
    2. **Progressive:** both fields composited per frame, no comb, full vertical detail.
    3. **Even-only:** present only the even field (raw sub-scanlines), discard odd.
    4. **Odd-only:** present only the odd field (the SMOOTHED sub-scanlines — CRS/RA0 rounding
       lands here), discard even. This is the FSM-confirmed "true P2000" single-field-repeated
       rendering: one field's fetched data, line-doubled to fill 480, refreshed every field
       (50 Hz) — no waiting on/compositing a second field.
    - Even-only vs odd-only are NOT identical: odd-only looks slightly smoother (it's the rounded
      scanlines), even-only slightly harder-edged. Both eliminate comb (single temporal field) at
      the cost of half the vertical info — this is now understood to be the AUTHENTIC vertical
      resolution the SAA5050 actually renders, not a reduced-fidelity fallback.
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
19. **Floppy Disk Controller (µPD765) — standalone chip + minimal board wiring** (reference
    doc §5c/§5d/§5e + **`docs/P2000T-disk-formats.md`** for the DOS-specific facts below; the
    disk-storage milestone the CTC (M17) was built to enable — its INT has nowhere to go
    without ch0).
    - **Design — standalone chip (DECISION, mirrors the `Z80Ctc`/`SAA5050` pattern):**
      `Upd765 : IDevice`, board-agnostic, its own class + unit tests — the real chip boundary,
      not a speculative abstraction. A thin `InternalExtensionBoard` object instantiates it
      and wires: the chip's INT output → **CTC ch0** (`0x88`, IM2-vectored — the FDC has **no
      direct CPU INT line**, §5d); routes `0x8C`/`0x8D`/`0x90` to the chip and `0x94` to the
      existing `RAMSW` bank register. **Do NOT build the general multi-board RAM-variant
      framework here** (T/54 vs T/102 vs PTC-96K socket population) — that's M20; this
      milestone's board object is deliberately thin, just enough to host one `Upd765` and
      route its ports/INT. M20 extends the SAME board class, it does not replace it.
    - **Register interface, CONFIRMED (ROM-disassembly-authoritative, reference §5d):**
      - `0x8C` `DSKIO1` (IN) — Main Status Register, **bit 7 = RDY**. Post-reset/idle value is
        **exactly `0x80`** (not just bit7 set — the ROM's presence probe does an exact
        `CP 0x80`, see below).
      - `0x8D` `DSKSTAT` (IN/OUT) — data register, consumed byte-at-a-time via `INI` during a
        transfer.
      - `0x90` `DSKCTRL` — **two different registers sharing one address.** OUT = control
        latch: bit0 `ENABLE`, bit1 `Count` (TC), bit2 `RESET`, bit3 `MOTOR`, bit4 `SELDIS`
        (P2C2 board only). **IN = the actual semi-DMA byte-ready flag, bit0** — this, not
        `0x8D` bit2, is what the real driver polls during a transfer (confirmed from
        `getdos`'s `read_track` loop: `IN A,(0x90)` / `RRA` / `JP NC` / `INI`). Already synced
        into reference doc §5d — model both directions as genuinely separate registers, not a
        read-back of the OUT latch (the live OUT value during a transfer has bit0 permanently
        set, which would make the poll never wait).
    - **Presence probe, CONFIRMED exact ROM sequence (supersedes the earlier
      datasheet-generic "reset raises INT" assumption — that path is NOT what this ROM
      does):** `OUT (0x90),0x04` (RESET alone) → a fixed **~256-iteration `DJNZ` delay**
      (~1.3 ms, **no interrupt wait**) → `IN A,(0x8C)` → `CP 0x80` (**exact equality**, not a
      bare bit-7 test) → match → `CALL getdos`; either way `DSKCTRL` is rewritten to `0x00`
      afterward. Absent card → open-bus `0x8C` reads `0xFF` → `CP 0x80` fails → `getdos`
      never called — same "genuine silence" pattern as the CTC (M17) and cassette CIP
      probes. Model `Upd765.Reset()` to leave MSR readable as exactly `0x80` so this succeeds.
    - **Disk boot is 3-gate cartridge/config-conditioned, not a blanket boot-time probe
      (CONFIRMED — synced, reference doc §5b "Disk-boot gate"):** checked in
      order, ALL three required before the presence probe above even runs: (1)
      `memsize == 3` (banked RAM at `0xE000`–`0xFFFF` populated — the ROM's own comment:
      *"mem at 0xE000 is on the extension board, so when no mem is found there are also no
      disk drives"* — treats "RAM populated" and "disk exists" as the same fact); (2) SLOT1
      cartridge present (bit0 of the header byte at `0x1000`); (3) cartridge requests DOS
      (bit1 of the same byte). **Config-validation implication:** a `MachineConfig` with an
      FDC card but `memsize` not reporting 3 is not a hardware-plausible combination — worth
      a validation check, not just a boot-sequence detail.
    - **Command subset, CONFIRMED exact bytes from `getdos` — match dispatch on these
      values, not a reconstructed MT/MF/SK bit-flag theory:**
      SPECIFY `03 60 34` · RECALIBRATE `07 01` · SEEK `0F 01 01` · READ DATA
      `42 01 01 00 01 01 10 0E 00` · WRITE DATA same shape, opcode `45` · SENSE INTERRUPT
      STATUS `08` → 2 result bytes (ST0 + PCN). Byte positions structurally match the
      standard µPD765 9-byte parameter block (drive/unit, cylinder, head, sector, N, EOT,
      GPL, DTL).
    - **`getdos`'s own load sequence (the M19 RUN-gate's exact script):** `disk_init` (IM2,
      FDC reset, a **342 ms** settle — `delay_342ms`, 854,799 T-states, a **pure CPU
      busy-loop needing NO `TimingPolicy` hook**, same as the ~1.3 ms probe delay: the
      cycle-exact core reproduces both for free) → `RETI` → SENSE INTERRUPT STATUS → SPECIFY
      → RECALIBRATE (`HALT`-waits for the completion INT) → motor-on + another 342 ms settle
      → for each of 2 tracks: sets `0x94 = 0x01` (RAMSW bank 1) **once, never toggled** →
      READ DATA → poll `0x90` bit0 → `INI`-loop terminated by the FDC's own result-phase INT
      (routed via CTC ch0, which **redirects the polling loop's return address** rather than
      resuming it — an ISR technique, nothing special needed in the core) → track 1 to
      `0xE000`–`0xEFFF`, track 2 to `0xF000`–`0xFFFF`, **8 KB total** (not 16 KB — an earlier
      figure was a typo, see `docs/P2000T-disk-formats.md` provenance) → checks the loaded byte
      against `0xF3` ("system disk" signature) → cleanup always runs: CTC ch0 reset, FDC off,
      **RAMSW restored to `0x00`** (bank 0) — whatever runs the loaded DOS extension must
      itself re-select bank 1 before jumping into it.
      **`0xF3` signature — CONFIRMED, feeds directly into the RUN-gate test design
      (`docs/P2000T-disk-formats.md` §6/§7); `0xF3` is specifically PDOS's (Philips DOS's) own
      system-disk signature, not a generic "Philips" convention** — confirmed two ways:
      two real JWSDOS disk images have `0x20` at that offset (JWSDOS 5.0's own actual first
      opcode byte, `JR NZ`, not a bad dump) while a real **"Disk BASIC 24K" `.IMD` image —
      presumed to be a PDOS disk, not yet independently confirmed — has `0xF3` there as
      expected; separately, `Disk.asm`'s own `disk_constants` table names this exact RAM
      destination `"Transfer adress for PDOS"` in the disassembler's own comment. `getdos` is
      fundamentally **PDOS's own two-track boot convention**; JWSDOS is a compatible
      third-party DOS reusing the same monitor-ROM entry point, not its originator. **Exact
      branch, so Test (e)'s two fixtures must assert precise values, not just
      "recognized"/"not recognized":** `cp (hl)` against `0xF3` at `0xE000`, `jr z` SKIPS the
      clear-to-0 step — so `sysdisk_status` ends at exactly **`1`** when `0xF3` matches
      (official/PDOS fixture) and exactly **`0`** when it doesn't (JWSDOS fixture, `0x20`) —
      **and this is the CORRECT, expected result for JWSDOS, not a bug to fix.** Do NOT force
      an artificial `0xF3` byte into the JWSDOS test image to make the check "pass." This also
      explains why `sysdisk_status`'s initial value (step 1 above, "no controller/drive/disk...
      OR PDOS was read") reads as ambiguous in the ROM's own comment: `1` is genuinely
      overloaded by design — it covers both "never got this far" and "got here, matched PDOS"
      — only `0` is unambiguous. Remaining open question, not blocking: whether
      `sysdisk_status` actually gates the launch downstream — evidence now leans further
      toward "informational, not a hard gate" (a hard gate on `0` would make JWSDOS unbootable,
      contradicting known reality), but confirm once `getdos`'s caller is sourced.
      **PDOS itself — NEW, per the owner's external documentation research (2026-07-20):** a
      real, distinct, official Philips DOS with its own directory system, separate from and not
      assumed to share `jwsdos5.0.asm`'s directory format (`docs/P2000T-disk-formats.md` §4). Not
      yet in this milestone's scope — flagging so a future PDOS-support milestone doesn't
      silently assume JWSDOS's directory struct applies. M19 as scoped here only needs to boot
      through `getdos` and check the signature; it does not need to parse a PDOS-formatted
      disk's directory.
    - **CTC wiring, exact control words (extends M17's `Z80Ctc`, doesn't change it):** ch0
      (disk-complete) `0xD5` (INTEN|counter-mode|rising-edge|TC-follows), TC `0x01`; ch1
      (disk-not-ready) `0xC5` — same shape, **falling edge**, TC `0x01`; both reset via `0x03`
      when done.
    - **Semi-DMA, software-polled — model the handshake, not real DMA.** No autonomous DMA
      engine; the driver polls `0x90` bit0 and moves each byte itself via `0x8D`.
    - **`TimingPolicy` — chip-timing only, NO ROM trap** (register-level and self-contained,
      unlike the cassette): Authentic honours seek time, motor spin-up, head-load, rotational
      latency, per-byte transfer rate — i.e. how long after a command issues before the
      *emulated chip's* result-phase INT actually fires. Turbo zeroes all of it; register
      results are identical either way. ROM busy-loops (the 342 ms / 1.3 ms delays above) are
      OUTSIDE this seam entirely — they need no hook, Authentic and Turbo both just execute
      them at real T-state cost.
    - **Disk geometry / JWSDOS format — see `docs/P2000T-disk-formats.md` (companion doc, don't
      duplicate here, mirrors the MDCR pattern):** 16 sectors/track, 256 B/sector (CONFIRMED
      from `getdos`); JWSDOS 5.0 itself supports **multiple geometries** (35/40/80-track,
      SS/DS) as a per-disk format-time choice — supersedes the reference doc §5d/§3a's
      "single-sided 35-track" placeholder (**synced** — reference doc §3a/§5b now reflect the
      per-disk geometry + self-describing label). JWSDOS embeds a self-describing geometry
      label on-disk (`docs/P2000T-disk-formats.md` §3) — **but real JWSDOS itself does NOT read this
      back** to auto-configure its own runtime state (it uses live RAM defaults, changed only
      via its own format menu, `docs/P2000T-disk-formats.md` §1). **Design decision:** the emulator's
      `.dsk` loader SHOULD auto-detect geometry from this label anyway — a deliberate
      emulator-side UX improvement beyond replicating real JWSDOS behavior, not "just
      matching the hardware." Keeps the "raw sector dump, no header" file convention
      (reference doc §3a) intact since the label is real on-disk JWSDOS data.
      **Auto-detect is two independent fixed-offset single-byte reads, CONFIRMED
      (`docs/P2000T-disk-formats.md` §3):** side = ASCII `'D'`/`'S'` at raw offset `0x0FEF`; track
      count = binary byte **`− 1`** at raw offset `0x0FFF` (e.g. `0x29` = 41 → 40 tracks). No
      banner-text parsing needed for either field — both are exact-position reads, byte-verified
      against two independent real images.
    - **Host `.dsk` image API** — mount/eject/create-blank/write-protect/browse, always
      host-speed, independent of `TimingPolicy` (the `.cas` API is the template). Read-only
      directory browsing needs only the 32-byte directory-entry struct (`docs/P2000T-disk-formats.md`
      §4) — no allocation logic. **Browse ONLY the confirmed active directory: raw
      `0x1800`–`0x1FFF` (logical sector 25, `dir_side1_prep`'s target, 18 real entries on the
      `Spel1.dsk` test image) — do NOT parse raw `0x1000`–`0x17FF` (sectors 1–8 of track 2) as
      directory data.** That region is real, struct-shaped, but stale/unrelated data (a
      `JWS Systeem Disk` write-path artifact, `docs/P2000T-disk-formats.md` §2/§7 item 3) — parsing it
      would surface phantom files that don't belong to the mounted disk. **Side 2's own
      directory location in a raw `.dsk` file is NOT yet confirmed** (`docs/P2000T-disk-formats.md` §7
      item 2) — for a double-sided image, browse side 1 only until that's sourced; don't guess
      an offset for side 2. Write support (save into a mounted image) needs the gap-reuse/append
      algorithm (`docs/P2000T-disk-formats.md` §5) — scope as a later concern unless M19 needs write
      from the start.
    - **`.state`:** the FDC device block (command/phase state, per-drive motor/head-position/
      selected-drive state) is a new device stream entry → bump `MachineStateFile.
      CurrentVersion`/`MinVersion` to **v4** at build time (reject v3), same discipline as the
      v1→v2 and v2→v3 (CTC/Lock) bumps — never retroactively.
    - **Tests:**
      (a) presence probe, both the unit-level exact-byte sequence (`OUT 0x90←0x04` → no-INT
      settle → `IN 0x8C` → `CP 0x80` exact match; open-bus `0xFF` when absent → probe fails)
      and the integration-level version, which now needs **three** fixture preconditions
      (`memsize==3` config, a 16 KB needs-DOS SLOT1 cartridge with bit1 set at `0x1000`, a
      JWSDOS disk image) — not two, per the 3-gate boot finding above;
      (b) each confirmed command (SPECIFY/RECALIBRATE/SEEK/READ DATA/WRITE DATA/SENSE
      INTERRUPT STATUS) produces the modeled chip's expected phase transitions and result
      bytes, matched on the exact byte sequences above;
      (c) a semi-DMA transfer round-trips the REAL sequence end to end — reset → settle →
      `RETI` → Sense Interrupt Status → SPECIFY → RECALIBRATE (halt-for-INT) → SEEK → READ
      DATA (16 sectors × 256 B) → poll `0x90` bit0 → 4096 bytes via repeated `INI` → result
      INT → CTC ch0 (`0xD5`/TC1) → IM2 vector via `0x6020` → result bytes from `0x8D`;
      (d) FDC INT → CTC ch0 → IM2 vector fires and lands at the correct handler (integration
      test against M17's daisy chain — this is the seam M17 was built for);
      (e) **RUN gate, two fixtures with two different exact `sysdisk_status` end values (not
      "recognized"/"not recognized" — assert the precise byte):** boot with the
      three-precondition fixture from (a), using a JWSDOS image → the loaded 8 KB is present at
      bank 1 (`0xE000`–`0xEFFF`/`0xF000`–`0xFFFF`) → `sysdisk_status` ends at exactly **`0`**
      (JWSDOS's `0x20` first byte doesn't match `0xF3`) → bank restored to 0 on return. Repeat
      with a "Disk BASIC 24K"/PDOS-signed fixture (`0xF3` first byte) → `sysdisk_status` ends
      at exactly **`1`** instead — same load, opposite branch outcome;
      (f) **host `.dsk` API, using `Spel1.dsk`/`jwssytem.dsk` as real fixtures:** geometry
      auto-detect reads raw `0x0FEF` ('D'/'S') and `0x0FFF` (track count `− 1`) and reports
      40-track/DS for `Spel1.dsk`; directory browse returns exactly the 18 real entries from
      raw `0x1800`–`0x1FFF` and does **NOT** surface any of the 20 struct-shaped entries
      sitting at raw `0x1000`–`0x17FF` (the regression guard for the stale-cluster caution
      above — assert the phantom filenames are absent from the returned listing, not just that
      the count is 18); `jwssytem.dsk`'s all-zero track 2 browses as an empty directory, not an
      error. → commit.
19a. **FDC — full µPD765/8272A command set** (fast-follow to M19, mirrors the 9a/13a/20a
    "milestone + a" pattern; owner decision, 2026-07-23 — see **`docs/FDC-implementation.md`
    for the full device guide**, mirroring the SAA5050/MDCR implementation-guide pattern).
    Milestone 19 deliberately scoped to "boot + run" — 6 commands the stock ROM/JWSDOS actually
    issue (SPECIFY, RECALIBRATE, SEEK, READ DATA, WRITE DATA, SENSE INTERRUPT STATUS). This
    milestone is chip fidelity for its own sake: implement the **full 15-command µPD765/8272A
    set**, the same way `Z80.Core` targets the whole instruction set rather than just what one
    ROM uses.
    - **Reference implementations (researched 2026-07-23, see the implementation guide's §1
      for full detail):** MAME `upd765.cpp`/`.h` (primary structural reference — 3-phase
      Command/Execution/Result state machine, per-command handler dispatch; it's a shared
      driver across the WHOLE µPD765 lineage including later enhanced chips, so filter out the
      enhanced-only command entries — this hardware is the plain first-generation chip, 15
      commands not 16-or-more); openMSX `TC8566AF.cc` (independent from-scratch second
      opinion, same 15 commands); QEMU `hw/block/fdc.c` (**cautionary example** — has a
      complete-looking 15-entry dispatch table but Read/Write Deleted Data and the Scan
      commands are stubbed/incomplete on inspection — don't copy without checking handler
      bodies); floooh/chips `upd765.h` (deliberately minimal 7-of-15, a useful "what a boot-only
      subset looks like" comparison, not a target). NEC's own 1978 datasheet + 1979 app note
      are the authoritative source for exact command-byte layout and status-bit meaning,
      cross-checked against MAME's executable command-length logic.
    - **A 7th command is ALREADY confirmed real-usage, not just modeled — SENSE DRIVE STATUS
      (`0x04`), found 2026-07-23 by directly reading `docs/jwsdos5.0.asm`:** JWSDOS's own
      `check_write_enable` routine sends `02 04 <drive>`, reads one result byte, and tests bit 6
      for write-protect — an exact match to the standard ST3 register layout (bit 6 = WP). This
      is the first sourced confirmation that this chip's status-bit semantics apply unmodified
      on real P2000 hardware. Elevate this command from "generic datasheet only" to "confirmed"
      alongside the existing 6 — see reference doc §5d for the citation, now added there.
    - **FORMAT A TRACK — NOW FULLY CONFIRMED (owner, 2026-07-24, disassembly of `JWSFormat.bin`,
      the standalone formatter utility — `docs/jwsformat.asm`), superseding the 2026-07-23 "not
      yet confirmed" note below.** As predicted there, formatting lives in a separate application
      from `jwsdos5.0.asm`'s resident DOS, and the owner has now supplied its disassembly.
      **Exact confirmed command bytes** (6-byte command phase, byte-for-byte match to the general
      datasheet shape already in implementation guide §4 — nothing about the shape itself
      changed, only its status from "modeled" to "confirmed"): `06 4D <HD/US> 01 10h 32h 00h` —
      length 6, opcode `0x0D`\|MF, HD/US byte set at runtime from the user's drive+side choice, N=1
      (256 bytes/sector), SC=16 (decimal, matches confirmed disk geometry), GPL=0x32 (50 decimal),
      D=0x00 fill byte. **Execution phase, also exactly as predicted — reuses the SAME semi-DMA
      byte-poll mechanism already built for Write Data, no new port or transfer plumbing needed:**
      for each of the SC=16 sectors the host feeds exactly 4 bytes (Cylinder, Head, Record, N)
      from a small in-RAM data block via `outi` to port `0x8D`, gated by the same `0x90` bit0
      poll used elsewhere. **Practical implication: Format A Track's execution phase needs no new
      transfer logic in `Upd765`**, just the existing host→FDC byte-poll loop fed 4×SC bytes
      instead of N-bytes-per-sector — exactly per §6's structural plan.
      **Bonus finding — Cylinder-field off-by-one, reinforces existing ID-verification-leniency
      conclusion:** `jwsformat.asm` writes `track_index + 1` (not the real 0-based physical track
      used for SEEK) into each track's format-data Cylinder byte. Combined with the earlier
      `Disk.asm` finding (reference doc §5d) that the ROM's own READ/WRITE DATA driver reuses one
      stale Cylinder byte across two different physical tracks and still succeeds, this is now
      two independent real-software data points that this platform's software never relies on
      strict ID-field Cylinder verification. **Recommendation, carried into this milestone's
      scope: `Upd765` should NOT gate READ DATA/WRITE DATA/FORMAT A TRACK success on an exact
      C-byte match.** Moot anyway for this project's `DskImage`, which already addresses sectors
      by direct `(cylinder,head,sector)` formula rather than by scanning a bitstream for ID marks
      — model Format A Track as simply populating the SC sectors of the currently-seeked
      (cylinder,head) with fill byte D, in host-supplied R order, no ID-mark bookkeeping needed.
      **Two more confirmations from the same source:** HD/US byte bit 2 = side/head select,
      confirmed exactly against the datasheet's `0 0 0 0 0 HD US1 US0` layout (`get_disk_side`'s
      `set 2,a` for side 2); and user-facing drive numbers 1-4 map to internal drive indices
      1, 2, 3, 0 (`get_drive_choice` + `and 003h`: '1'→1, '2'→2, '3'→3, '4'→0 — worth keeping in
      mind if `P2000.UI`'s drive numbering ever needs to match real P2000 software's own
      convention). Sense Drive Status is now independently reconfirmed by a SECOND real program
      (`JWSFormat.bin`'s `check_write_protect` sends the identical `02 04 <drive>` shape and
      tests the identical ST3 bit 6, from a completely separate codebase than `jwsdos5.0.asm`'s
      `check_write_enable`). Full writeup: implementation guide §2.
    - **Structural approach:** extend the EXISTING `Upd765` object (real per-drive state,
      working semi-DMA byte-poll mechanism) to a proper Command/Execution/Result phase state
      machine per the implementation guide §6 — generalize the semi-DMA loop to run in either
      direction (already does host→FDD for Write Data) and to the Format/Scan byte-count shapes;
      **build a real 7-byte (ST0,ST1,ST2,C,H,R,N) result phase for every command, including
      retroactively for READ/WRITE DATA** (M19 deliberately skipped a formal result phase there
      since no known driver reads it — building Scan/Read-Track/Read-ID/Format properly needs
      the same machinery anyway, so backfill it rather than keep two completion models). Do NOT
      build the enhanced-chip-only commands MAME's source also has (`CONFIGURE`/`DUMP_REG`/
      `LOCK`/`PERPENDICULAR`/`MOTOR_ONOFF`/`VERSION`/`SLEEP`/`ABORT`/`SPECIFY2`) — later silicon,
      out of scope.
    - **Test strategy (no portable FDC conformance suite exists to borrow — implementation
      guide §7):** synthetic protocol tests per command against the datasheet-specified
      command/execution/result shapes (the primary validation for the 8 commands with no known
      real caller); a real integration test for Sense Drive Status against `check_write_enable`'s
      actual sequence (write-protected vs. writable `DskImage` fixtures); **Format A Track now
      also gets a real integration test** (2026-07-24: `jwsformat.asm` is a confirmed real caller
      — drive `check_write_protect` gate, the exact `06 4D ...` command bytes, and the 4-bytes/
      sector execution loop against a `DskImage` fixture) in addition to the synthetic protocol
      test against the general datasheet shape.
    - **Applies to:** `docs/FDC-implementation.md` (new, full device guide), reference doc §5d
      (Sense Drive Status confirmed usage + 15-vs-16-command correction, added 2026-07-23; Format
      A Track confirmed bytes + ID-verification-leniency reinforcement + HD/US bit2 + drive-number
      mapping, added 2026-07-24) / `src/P2000.Machine/Devices/Fdc/Upd765.cs` (command/execution/
      result phase generalization).
    - **Synced:** yes (2026-07-23, into P2000T-reference.md §5d — the Sense Drive Status
      confirmation and the 15-command correction; 2026-07-24, Format A Track's confirmed bytes and
      the ID-verification-leniency reinforcement — see §17 findings log) — implementation still
      outstanding.

20. **Philips Expansion Card — RAM-variant status + multi-drive floppy subsystem** (promoted
    from the §14 "multi-board RAM-variant framework" placeholder; reference doc §5c/§5d +
    `docs/P2000T-disk-formats.md`; extends `InternalExtensionBoard`/`Upd765` from M19, does not
    replace them).
    - **RAM-variant half of this milestone is mostly already DONE — recap, not new work.**
      Per the milestone-2 findings-log entry (§17, 2026-07-02), `RamVariant` already implements
      **T38/T54/T102** and `PageTable`/`MachineConfig` already validate them against
      `Board == InternalBoard.FloppyRam` (M19). **PTC-96K remains explicitly OUT of scope here**
      — it's blocked on reference doc open item #4 (whether its extra 64 KB rides port `0x94`
      or a separate scheme), which is still unsourced. Do not model it speculatively; when the
      addressing scheme is sourced, it extends the existing `RamVariant` enum + `PageTable` bank
      register + the P2C2-only `SELDIS` control-latch bit (§5d) — a plug-in, not a redesign.
      **Corroboration, not resolution (2026-07-23, M2200 manual):** M2200's own bank-switch is a
      confirmed 3-bit/6-bank register (32 KB base + 6×8 KB banked = 80 KB exactly, matching
      T/102's total — reference doc §5 memory, `docs/M2200-implementation.md` §2.2). This is
      corroborating evidence for what a T/102-class bank register looks like; it does NOT resolve
      PTC-96K (a different, larger board M2200 isn't a stand-in for) — PTC-96K stays out of scope
      here exactly as before.
    - **The real new work is multi-drive:** today's `Upd765`/`InternalExtensionBoard` model one
      implicit drive (`MachineConfig.FloppyDiskImagePath`, singular, hardcoded to unit 1 per the
      M19 finding). This milestone generalizes to **N independently-configured drives** on one
      card.
    - **Hardware ceiling — RESOLVED (2026-07-23/26, owner-supplied full M2200 manual +
      independent Philips manual cross-check — `docs/M2200-implementation.md` §2.1/§5.2):** the
      earlier "2 drives" figure
      (reference doc §5d) described an assumption for the plain floppy+RAM board with no
      connector-level source of its own. **The M2200 board's own 34-pin floppy connector is now
      CONFIRMED to carry FOUR drive-select lines — `DRISEL0`, `DRISEL1`, `DRISEL2`, `DRISEL3`** —
      decoded from the µPD765's native two US0/US1 pins via an external 2-to-4 decoder (IC139 on
      the real board), gated by the shared motor-on signal (see the MOTOR bullet below, also
      resolved by this same source). This directly supersedes the earlier "recommended 2 physical
      drive slots" guidance from this milestone's first draft. **Recommended model, updated: 4
      physical drive slots**, matching the confirmed M2200 connector — a real hardware ceiling,
      not the arbitrary/unconfirmed cap the milestone previously flagged. The two complicating
      facts from the first draft still stand and don't need re-litigating: the stock ROM driver
      still hardcodes unit-select to drive 1 only (§13.19), and JWSDOS's own head/drive folding
      via `xor 0x04` is still a plausible-not-confirmed reading of "2 drives × 2 sides" — neither
      contradicts a 4-position connector existing; they just mean not every combination the
      connector supports is necessarily exercised by every piece of real software.
      **RESOLVED (owner, 2026-07-23):** whether the PLAIN single-purpose Philips floppy+RAM board
      (as opposed to M2200) has the same 4-position connector was the one remaining open question
      here — a separate, official Philips-authored P2000 manual clearly states the expansion
      board supports up to 4 drives, consistent with M2200's own design intent as a drop-in
      replacement (with extras) for the official Philips card. The earlier "2 drives" figure is
      now understood to trace back to a poor-quality Field Service Manual scan, not a genuine
      2-drive board. **4 physical drive slots is the confirmed ceiling for both boards** — build
      the config/UI surface for up to 4 without further hedging on this point.
      **Independently re-confirmed (2026-07-23):** the design-doc maintainer has since personally
      read the referenced manual in full (official Philips "P2000 System T&M Reference Manual,"
      144 pp., now transcribed in `raw-conversion.md`) — its Ch2 "FLEXIBLE DISKS" states this
      directly: 4 drives, 560k total, 35 tracks × 16 sectors × 256 bytes = 140k/disk. See
      `P2000T-reference.md` §5d for the full citation; no change to the 4-drive figure or the
      config model below, this just upgrades the evidentiary basis from owner-report to
      maintainer-verified primary source.
    - **Config model:** replace `MachineConfig.FloppyDiskImagePath` (singular) with a
      **per-drive collection** — each entry: drive index, `Enabled`, `Capacity` (35/40/80
      tracks), `Sides` (SS/DS), and a nullable mounted-image path. Drive **presence + capacity +
      sidedness is topology** (reset-to-apply, same rule as the drive-vs-image split already
      decided in `P2000.UI` CLAUDE.md §7); the **image mounted in an already-present drive is a
      runtime swap**, exactly like cassette mount/eject — no new split to invent, just apply the
      existing one per-drive instead of once globally. **This per-drive `ImagePath` field is also
      what makes a `.cfg` able to specify an INITIAL mounted image, seeded at construction —
      confirmed built and load-bearing (§17, 2026-07-23 finding: `Machine`'s constructor mounts
      every enabled entry's `ImagePath` at build time). Reference doc §3a (2026-07-26) frames why
      this matters:** it's the mechanism behind "load a `.cfg` → reset-to-apply → the machine
      boots already holding its cartridge + boot floppy," the emulator's equivalent of a real
      P2000T that's already physically wired up before the power switch is flipped. The runtime
      swap capability is additive on top of this, not a replacement for it. **RESOLVED (owner,
      2026-07-26) — cassette gets the same treatment; see milestone 20b below.** `MachineConfig`
      currently carries `Slot1CartridgePath`/`FloppyDrives` but nothing for the cassette — real
      hardware falls through to a cassette-boot wait when no valid cartridge is found (already
      documented, reference doc §3a), so "what's loaded at power-on" legitimately includes the
      tape too, not just symmetry for its own sake.
    - **Two ways to provision a drive's media (owner decision, 2026-07-23):** (a) **mount an
      existing `.dsk` file** — geometry auto-detected from its label, per the rule below; (b)
      **manually defined / create-blank** — no file, just the drive's own configured
      Capacity/Sides (topology axis above) as the geometry, producing a **genuinely unformatted**
      image: correctly sized for that geometry, filled with a neutral erased-media byte, **no
      label written, no directory initialized** — mirrors the cassette's blank-tape decision
      (a blank tape is truly empty, not pre-written with headers) rather than the emulator
      pre-formatting it. A guest DOS (JWSDOS/PDOS) still has to format it via its own format
      routine before it's usable, same as inserting a real blank floppy — the emulator does not
      shortcut that.
    - **Write model — RESOLVED as buffered, mirrors cassette exactly (owner decision,
      2026-07-23; closes the open "write-through vs. buffered" question this milestone
      originally carried):** a mounted or newly-created disk lives as an **in-memory image**,
      the live device state; WRITE DATA commands from the guest mutate that in-memory image only.
      Nothing touches the host filesystem until an explicit **Save / Save as `.dsk`…** action —
      the disk equivalent of ms.13's cassette New/Save/Save-as, not a new pattern. Mounting an
      existing file loads it into that same in-memory image (like `InsertTape()`); create-blank
      starts a fresh one (like `InsertBlankTape()`). **Consequence to flag, mirrors the cassette's
      own accepted trade-off:** ejecting or resetting before an explicit Save discards in-memory
      changes to the host file — same divergence-from-real-hardware the cassette design already
      accepted (a real MDCR writes through to the physical tape as it goes; the emulator's
      `.cas` design chose buffered + explicit save there too, for the same reasons). Whether the
      UI should warn on eject-with-unsaved-changes is a UI-layer call (P2000.UI CLAUDE.md
      milestone 14) — this bullet only fixes the machine-layer model.
    - **Per-drive device state:** `Upd765` already tracks `_cylinder[drive]` per drive (M19) —
      extend the same shape to selected head, write-protect, and the mounted-image reference, all
      indexed by drive. **MOTOR — RESOLVED (2026-07-23, M2200 manual, §5.2's connector table):**
      the 34-pin connector carries a **single, shared `MOTORON` line** (pin 16) — NOT independent
      per physical drive. Model motor-on as **one board-level bit, not a per-drive array** — the
      earlier "tracked per selected-drive only" placeholder was a reasonable conservative guess at
      the time and turns out to match the real wiring, but for a cleaner reason than guessed: it
      isn't that the emulator only bothers to track the selected drive's motor, it's that **real
      hardware has exactly one motor-on signal for the whole card**, gating whichever drive(s) are
      currently addressed. Same source also resolves the drive-select gating question: the US0/US1
      → `DRISEL0`-`3` decoder (hardware ceiling bullet above) is itself only active while
      motor-on is asserted — i.e. no drive can be addressed at all until the shared motor line is
      on, a stronger and more specific gate than "wait ~0.5 s after motor-on before read/write."
      **`WRPROT`/`TRACK00` are also confirmed as per-*selected*-drive sense lines** (read back only
      for whichever drive is currently addressed), not simultaneously-tracked state for every
      drive at once — the emulator's own per-drive write-protect **config** (a host-side flag per
      mounted image, from the write-protect bullet elsewhere in this milestone) is unaffected by
      this; it just means "what value is read back on `WRPROT`" depends on which drive is
      currently selected, same as real hardware, not a new modeling requirement.
    - **Drive-timeout watchdog — RESOLVED, OUT OF SCOPE for this milestone (owner decision,
      2026-07-23): do not model a watchdog device or any drive-door state here; defer
      indefinitely to a future M2200-specific milestone.** Background: real hardware (IC118, per
      the M2200 manual §2.1) monitors the drive's index signal and fires an interrupt after ~1s
      if no index pulse arrives during a transfer (no disk present, or door open) — but this
      chip is sourced ONLY from the M2200 manual, never independently confirmed on the plain
      floppy+RAM board this milestone actually targets (unlike the drive-count/motor-line facts,
      which DID get cross-confirmed for both boards). **Decision: no new device, no door state.**
      Instead, the existing "unmounted drive is a no-op" rule (§13.19 — a read/write to an
      absent drive resolves instantly, zero-filled buffer, no exception) is explicitly WIDENED to
      also cover: (a) a configured/enabled drive that currently has no image mounted, and (b) a
      drive whose image is ejected while a transfer is in flight (a real, newly-reachable path
      once eject is a runtime action available at any time, per this milestone's write model).
      Both resolve exactly like the already-accepted absent-drive case — instant, harmless,
      no timer, no distinct code path: whatever check the FDC already does for "is a drive
      actually there" should read the per-drive mounted-image reference, so "not there" and
      "there but empty" collapse into the same one no-op branch. **Rationale (owner):** the
      cassette earned its real-world-accurate phase-bitstream model because that fidelity is the
      point of that device (`docs/MDCR-implementation.md`); the watchdog is a real-world-only
      edge case (a physical door/missing-media condition) on a chip not even confirmed to exist
      on this board — not worth a second device for. Revisit only if/when a real M2200 milestone
      is scoped, where IC118 actually has a primary source.
    - **New flag, not previously in scope: FDC chip variant.** The M2200 manual reveals that the
      *first ~100 M2200 units* shipped with a **µPD7265** (Sony-compatible recording format), not
      the µPD765 this project models — later units got the µPD765 (`docs/M2200-implementation.md`
      §2.1). This milestone's scope (and M19's) remains µPD765-only; a µPD7265 variant is
      explicitly out of scope unless the owner asks for it later — noted here so "the FDC" isn't
      silently assumed to be one universal chip across every real M2200 unit.
    - **Geometry — auto-detect still wins, config axis is the fallback:** M19 already decided
      the emulator auto-detects capacity/sidedness from the on-disk label
      (`docs/P2000T-disk-formats.md` §3: side at raw `0x0FEF`, track count−1 at raw `0x0FFF`) rather
      than trusting a config value. The new per-drive **Capacity/Sides config axis exists for
      blank/newly-formatted images (no valid label yet) and as a manual override if a label is
      absent or corrupt** — it is NOT a second source of truth competing with the label when one
      is present. State this order explicitly in the implementation (label wins; config is the
      seed for blank media).
    - **Write-protect, per drive, host-side (mirrors the cassette ms.13a pattern):** a live
      `IsProtected` bool per drive, defaults writable, gates WRITE DATA the same way WEN gates
      CSAVE. **Still does NOT round-trip through the `.dsk` file itself** the way cassette
      protect rides spare padding in the `.cas` record container (reference doc §3a) — a raw
      sector-dump `.dsk` has no equivalent spare byte to (ab)use without corrupting real JWSDOS
      data — but this is no longer the only persistence path. **UNBLOCKED (owner, 2026-07-26,
      reference doc §3a "RESOLVED — mounted media CONTENT travels inside `.state`"):** the
      bigger question this was deferred on is resolved — disk `.state` blocks now embed the
      mounted image's content directly (this milestone's own `.state` bullet below). Persist
      `IsProtected` as a plain bool field in that SAME per-drive `.state` block, exactly
      mirroring how `MdcrDevice.Protected` already persists in the cassette's `.state` block
      (`docs/MDCR-implementation.md` §7) — no separate mechanism, no sidecar, nothing UI-layer
      needed. A `.state` save/load round-trips write-protect correctly; a `.dsk` file saved via
      "Save as…" still does not carry it (matches the file-format limitation above, unchanged).
    - **`.state`:** the FDC device block's shape changes from implicit-single-drive to
      per-drive arrays (motor/head/cylinder/write-protect/**embedded disk content**/mounted-path-
      hint × N) → bump `MachineStateFile.CurrentVersion`/`MinVersion` from **v4 to v5** at build
      time (reject v4), same discipline as every prior bump — never retroactively. **RESOLVED
      (owner, 2026-07-26, reference doc §3a):** "mounted-image-ref" is the actual in-memory disk
      bytes, not a path — each drive's block embeds its full raw sector-dump content (already
      compact per-drive, see the hardware-ceiling note above), making `.state` self-contained/
      shareable exactly like the cassette's parallel resolution. The original mount path (if any)
      travels along only as metadata, never re-read on restore; a drive created via "New (blank)
      disk" with no backing file embeds and restores fine with no path at all. Apply gzip/deflate
      over the embedded bytes (reference doc §3a) — disk sector dumps compress well.
    - **Host `.dsk` API:** extend M19's API — **mount** (existing file → in-memory image, geometry
      from label), **create-blank** (drive's configured Capacity/Sides → in-memory unformatted
      image, no file involved yet), **eject** (drops the in-memory image; discards unsaved
      changes per the write-model bullet above), **save/save-as** (serialize the in-memory image
      to a host `.dsk` file — for a plain raw sector dump this is a straight byte-for-byte write,
      no bitstream-style encode step the way `.cas` needed), **write-protect**, **browse** — all
      take a drive index; behaviour per call otherwise unchanged from M19. **Side 2 directory
      browsing stays blocked** on the same open item M19 already flagged — side 2's directory
      location in a raw `.dsk` file is not yet confirmed (`docs/P2000T-disk-formats.md` §7 item 2) —
      do not guess an offset just because multi-drive makes double-sided images more prominent;
      browse side 1 only for a DS-mounted image until that's sourced.
    - **Tests:** (a) config validation accepts 1 to 4 drives (updated ceiling, per the hardware-
      ceiling bullet above) combined with `Board == FloppyRam` and a valid `RamVariant`, same gate
      M19 added; (b) two (of up to four) drives transfer independently with no cross-talk — a
      read in progress on one drive and a seek on another don't corrupt each other's tracked
      cylinder/state (the regression guard the existing `_cylinder[drive]` array already sets up
      for); (c) geometry auto-detect runs per-drive from each mounted image's own label,
      independent of the other drives' config; (d) write-protect on drive N blocks a WRITE DATA
      command targeting drive N only, other drives unaffected; (e) `.state` v5 round-trip with
      multiple drives mid-transfer at different
      head/cylinder positions reproduces identical subsequent frames; (f) v4 `.state` files are
      rejected with a clear version-mismatch error, not a silent misload; (g) **create-blank**
      produces an image of exactly the right byte size for the configured Capacity/Sides, with
      no valid label at the auto-detect offsets (`0x0FEF`/`0x0FFF`) — confirms it reads as
      genuinely unformatted, not silently pre-labeled; (h) a guest write to a freshly created
      blank image followed by **Save as** round-trips byte-for-byte on reload, and ejecting
      the SAME state without saving first leaves no trace in a freshly-mounted copy of the
      original file (the buffered-write regression guard). → commit.
20a. **Cassette + disk — dirty-tracking for the UI's unsaved-changes warning** (fast-follow,
    same "milestone + a" pattern as ms.9a/13a — the UI-layer warning `P2000.UI` CLAUDE.md is
    adding, §14.14a, needs a machine-layer signal to hang off; owner decision, 2026-07-23, that
    both cassette and disk should warn on eject/replace with unsaved changes).
    - **Needs a per-device `IsDirty`-equivalent flag; check before adding a new one.**
      `MdcrDevice`/`MiniTape` (ms.9/9a) already models writes via WCD/WDA capture and the turbo
      write trap — **verify whether it already exposes something this can reuse (e.g. a
      modified-since-load marker) before building a second, redundant one.** If nothing exists
      yet, add `IsDirty` (bool) to both `MdcrDevice` and the per-drive disk state (M20 above):
      set on any write that mutates the in-memory image (WCD/WDA capture or the turbo trap for
      cassette; WRITE DATA for disk), cleared on a fresh mount/create-blank/InsertBlankTape AND
      on a successful Save/Save-as. Eject/replace-mount themselves do NOT clear it — the UI reads
      the flag to decide whether to warn, then the eject/replace proceeds (or is cancelled) per
      the user's choice at the UI layer; the machine layer only tracks and exposes the bit.
    - **`.state` — UNBLOCKED (owner, 2026-07-26, reference doc §3a "RESOLVED — mounted media
      CONTENT travels inside `.state`"):** serialize `IsDirty` too — a session saved with an
      unsaved cassette/disk change pending restores as still-dirty, matching the "bring the
      machine back exactly as it was" goal. Same resolution as write-protect (M20 above): a plain
      bool field in the same per-device `.state` block the content itself now lives in, not a
      separate mechanism and not a UI-state concern. Bump the version alongside the content-
      embedding change for that device (M20's v4→v5 for disk; the cassette block's own bump, per
      reference doc §3a) rather than as a second, independent bump — both land in the same
      device-block shape change. Doesn't change how the UI-layer warning already works (§14.14a):
      it reads the live bit regardless of whether it persists — this only means the bit's value
      is now also correct immediately after a `.state` load, not just during the live session.
    - **Tests:** (a) a freshly mounted/created image (no writes yet) reads NOT dirty; (b) a
      write (authentic or turbo) sets dirty on both cassette and disk; (c) Save/Save-as clears
      dirty; (d) eject/replace do not themselves clear or set dirty — only reads it; (e) a
      second write after Save re-sets dirty (the flag isn't sticky-false after the first save).
      → commit.

20b. **Cassette config-seeded initial mount — `MachineConfig.CassettePath`** (NEW, owner decision
    2026-07-26, reference doc §3a "RESOLVED — cassette gets the same treatment" — the cassette-side
    sibling of what M20 built for disk; extends milestone 9's `MdcrDevice`, does not replace it).
    - **Add `MachineConfig.CassettePath` (nullable `string`),** mirroring `FloppyDrives[i].
      ImagePath`/`Slot1CartridgePath`. `Machine`'s constructor mounts it via the SAME `LoadCasImage`
      path the runtime host-API already uses (§7's mount entry point) — no new tape-loading logic,
      just an additional caller at construction time. `null` (the default) → bare/no-cassette,
      unchanged; this is purely additive and does not touch the "bare by default" locked decision
      (§2.1) any more than `Slot1CartridgePath` already doesn't.
    - **Runtime mount/eject/swap (already locked, §7) is unaffected** — a config-seeded tape is
      just what's in the deck when the machine is BUILT; the user can still eject/insert live
      afterward with no reset, exactly as already true for disk.
    - **Write-protect round-trips for free** — a config-seeded `.cas` file's protect bit (offset
      `0x50` bit 0, ms.13a) is read the same way any other mount reads it; no special-casing.
    - **`.cfg` serialization:** add `CassettePath` to `MachineConfigFile`'s DTO
      (`ToDto`/`FromDto`), same additive pattern as the `RamSeed` fix (§17, 2026-07-23) — purely
      additive/nullable, so **no version bump needed** (an old `.cfg`/`.state` with no
      `cassettePath` key still deserializes to `null`, identical to today's behaviour).
    - **Tests:** (a) a `.cfg` with `CassettePath` set, applied via reset-to-apply, boots with CIP
      already present and the correct tape mounted — no separate runtime mount step needed; (b) a
      `.cfg` with `CassettePath` null boots bare (regression guard — must not change today's
      default); (c) `.cfg` round-trip preserves `CassettePath` (or its absence) exactly like
      `Slot1CartridgePath`/`FloppyDrives`; (d) once running, the config-seeded tape can still be
      ejected and a different one mounted live, with no reset required (regression guard against
      the runtime-swap capability). → commit.

20c. **`Machine.CaptureCurrentConfig()` — derive a `MachineConfig` from LIVE state** (NEW, owner
    decision 2026-07-26, reference doc §3a "RESOLVED — startup configuration"). A third
    derivation direction alongside the two already established (config → machine at construction;
    machine+devices → `.state` capture) — this one goes machine → a fresh, accurate config.
    - **Why this is needed, not just a convenience:** `machine.Config` (the object held since
      construction) goes stale the moment media is mounted/ejected/swapped LIVE — which is exactly
      the runtime-swap capability §3a already locks in for both disk and cassette. Anything that
      wants "what is this machine actually running right now, including what's in its drives" —
      not just "what was it built from" — needs to read current state, not the constructor's copy.
    - **Add `Machine.CaptureCurrentConfig()`:** returns a new `MachineConfig` with `Model`/`Board`/
      `RamVariant`/`BankCount`/`MonitorRomPath`/`Slot1CartridgePath`/`RamSeed` copied from the
      existing config (these aren't live-swappable — SLOT1 has no hot-swap, per the UI investigation
      above), but `FloppyDrives[i].ImagePath` and `CassettePath` read from the LIVE devices
      (`Upd765.GetDisk(i)`'s current mounted path, `MdcrDevice`'s current mounted path — `null` for
      an empty drive/deck) rather than echoed from the original config. This is a read-only query,
      no mutation, callable at any time the machine is running.
    - **Two consumers, both in `P2000.UI` (not this project) — this milestone only builds the
      machine-layer capability:** (a) fixing `ConfigWindowVm.SaveCfgAsync`'s confirmed gap (UI
      CLAUDE.md §7 investigation — it currently only ever serializes its own stale bound fields);
      (b) the new auto-remembered "last session" `.cfg`, written via this same deriver (UI
      milestone 14c). Building one deriver here avoids two UI-side mechanisms that could drift
      apart from each other.
    - **Tests:** (a) capturing on a bare machine returns the equivalent of its own config (no
      drives/cassette mounted); (b) mounting a disk live, then capturing, reflects that drive's
      `ImagePath` even though `machine.Config.FloppyDrives[i].ImagePath` (the ORIGINAL, stale copy)
      still shows whatever it was built with; (c) same for cassette via `CassettePath`; (d) SLOT1/
      RAM/board fields always echo the original config (never re-derived, since they can't drift —
      no live-swap path exists for them); (e) a captured config, fed back into `new Machine(...)`,
      produces a machine with the SAME media mounted as the one it was captured from (round-trip
      sanity check). → commit.

20d. **Disk geometry detection — validate the JWSDOS label instead of trusting it, and detect
    real size mismatches** (NEW, owner decision 2026-07-27, reference doc §5d's "RESOLVED... the
    label-based auto-detect above is JWSDOS-specific and was silently over-trusted" — read that
    block in full before starting). Triggered by real testing with a PDOS boot floppy (no label
    at all) and a genuinely short/incomplete image (32,768 bytes mounted where the configured
    drive expected 327,680).
    - **Replace the current "label wins unconditionally" mount logic with:** (1) read the label
      if the file is long enough to contain it; compute the byte length it implies; only trust
      it if that length equals the actual file length exactly — otherwise treat as unlabeled.
      (2) If unlabeled (or label didn't validate), use the drive's configured Capacity/Sides as
      the geometry — this is the SAME config axis that already existed, just promoted from
      "blank-media seed only" to "the real fallback for any non-JWSDOS-labeled image," since
      that's most real images. (3) If the resulting geometry's implied byte length still doesn't
      match the actual file length, this is now a **reportable mismatch**, not a silent mount.
    - **New query surface for the mount to report back to its caller (`P2000.UI`, which owns the
      dialog — this milestone builds detection only, not UI):** whatever geometry ended up
      chosen, the actual file length, whether it validated cleanly, and — if not — the list of
      OTHER canonical Capacity×Sides combinations (of the 6: 35/40/80-track × SS/DS) whose
      implied byte length exactly equals the actual file length (may be empty, one, or two —
      40-track/DS and 80-track/SS collide at 327,680 bytes, both valid candidates for that
      exact size). Shape this as a simple result type `DskImage`/`Upd765`'s mount API can return
      alongside the mounted image itself, not an exception and not a blocking call.
    - **`DskImage` gains a pad/extend operation:** given a target byte length, extend the
      in-memory sector array to that length, filling new bytes with `0x00` — the SAME fill byte
      already confirmed for FORMAT A TRACK's own unformatted-sector fill (`jwsformat.asm`
      disassembly, §5d above) — reuse it rather than inventing a second "blank" convention.
      Purely in-memory, per the existing buffered-write model — nothing touches the host file
      until an explicit Save/Save-as, exactly like every other disk mutation.
    - **Out-of-range reads get a defined behavior for the first time:** a sector address beyond
      the mounted image's actual byte length (an unpadded short mount, mounted anyway) reads as
      `0x00`, never an exception — mirrors the cartridge's already-confirmed "open-bus reads
      `0xFF` past a short image" shape (§5c), using disk's own fill byte rather than the
      cartridge's.
    - **Nothing here blocks a mount** — every path ends in a mounted image; the new query surface
      exists so `P2000.UI` can inform/offer choices (reconfigure-and-remount, continue, pad), not
      to gate the mount itself.
    - **Tests:** (a) a file whose length matches its own JWSDOS label mounts using the label,
      silently, no mismatch reported (unchanged fast path); (b) a PDOS-style file with no valid
      label but whose length exactly matches the drive's configured Capacity/Sides mounts
      silently using the config, no mismatch reported (the Basic24k boot-floppy regression
      guard); (c) a file whose length matches a DIFFERENT canonical geometry than the one
      configured reports exactly that candidate (single-candidate case); (d) a 327,680-byte file
      with a drive configured as neither 40-track/DS nor 80-track/SS reports BOTH as candidates;
      (e) a file matching no canonical geometry at all reports a mismatch with no candidates and
      the correct actual/expected byte counts; (f) padding a short image to a target length
      leaves original bytes untouched at their original offsets and fills the rest with `0x00`;
      (g) reading a sector beyond an unpadded short image's real data returns `0x00`, not an
      exception; (h) a file exactly matching its configured geometry (the common case, unchanged
      today) reports no mismatch at all — regression guard that this milestone doesn't start
      flagging previously-fine mounts. → commit.

21. **IMD (ImageDisk) read/write — the emulator's new native/preferred disk container** (NEW,
    owner decision 2026-07-27, reference doc §3a "RESOLVED — adopt IMD... as the emulator's
    native/preferred disk container" — read that block in full before starting, it has the
    research/citations behind why IMD and not HFE/TD0/a bespoke format). Legacy `.dsk` support
    (milestone 20d) is UNCHANGED by this — this is a new, additional container, not a
    replacement of raw-`.dsk` mounting.
    - **Add an IMD reader:** parse the published IMD spec (linked in the reference doc block) —
      text header (terminated by its own EOF marker), per-track descriptors (cylinder, head,
      sector count, sector-size code), the sector-order map (physical position of each logical
      sector — this is the interleave data), and sector data blocks (including IMD's own
      "all sectors this value" compression marker, since real IMD files use it for
      unformatted/blank regions — don't assume every sector is stored explicitly). Detect an
      IMD file by its own text header (content-based), not by file extension.
    - **Add an IMD writer:** serialize a `DskImage`'s current content (tracks/sides/sector data)
      into the same structure. **Sector-order map: write a plain sequential order for now** —
      nothing in this project currently generates or tracks real interleave, so there is no
      genuine order data to preserve yet; the map still needs to exist and round-trip correctly
      (an IMD file with a trivial sequential map is a completely valid, standard IMD file, not a
      degenerate one). Do NOT attempt to model rotational-latency-aware timing off this map in
      this milestone — that's a separate, explicitly deferred future step.
    - **`DskImage` needs a way to know and report which format it came from** (raw `.dsk` vs.
      IMD) so `P2000.UI` (milestone 14f) can decide default Save behavior without re-sniffing the
      file itself — a simple enum/flag is enough, set at mount/load time, updated on a
      format-changing Save As.
    - **Write-protect is explicitly OUT of scope for the IMD reader/writer itself** — per the
      reference doc block, it's a config/`.state` concern (already resolved, ms.20/20a),
      identical for both `.dsk`- and IMD-backed drives. Do not add a write-protect field to the
      IMD serialization.
    - **`.state` needs NO change** — its own disk-block embedding (ms.20/20a) already stores raw
      content + explicit Tracks/Sides directly, independent of whatever host file format (if any)
      the image originally came from; this milestone doesn't touch that.
    - **Tests:** (a) a real-world IMD file (construct one matching the published spec's own
      examples, or a small hand-built fixture) round-trips through read→write→read
      byte-identical; (b) a file using IMD's "all sectors same value" compression marker reads
      correctly as a fully-populated track (regression guard against assuming explicit-only
      storage); (c) writing a `DskImage` built from a plain `.dsk` mount (milestone 20d) produces
      a valid IMD file with a correct header/track descriptors and a sequential sector-order map;
      (d) `DskImage`'s format flag correctly reports `.dsk` vs. IMD after each mount path; (e) an
      IMD file's geometry is used AS-IS, no label-validation or config-fallback logic from
      ms.20d runs against it (regression guard that IMD mounting stays fully deterministic, not
      routed through the mismatch machinery meant for ambiguous raw dumps). → commit.

20e. **Extract `DskImage.Mount`'s mismatch detection into a standalone, reusable function** (NEW,
    owner decision 2026-07-27, reference doc §3a's "RESOLVED — the Config window's own
    disk-image picking gets the same geometry-mismatch protection..." block; small, mechanical,
    prerequisite for UI milestone 14g's offline/preview case). `P2000.UI`'s Config window needs
    to run the SAME label-validate → config-fallback → candidate-match logic `Mount` already
    does, but WITHOUT constructing a full `DskImage` — it's only ever previewing whether a
    picked file's bytes agree with a row's currently-set Capacity/Sides, before any machine or
    live drive necessarily exists to mount into.
    - **Add `DskImage.DetectMismatch(byte[] bytes, DiskCapacity configuredTracks, DiskSides
      configuredSides) → DiskGeometryMismatch`** — pull the exact detection logic (label read +
      validate, config fallback, candidate-length matching against the 6 canonical geometries)
      straight out of `Mount`'s body into this new pure function; `Mount` itself now just calls
      it and then builds the `DskImage` from whichever geometry won. **Behavior for every
      existing caller of `Mount` must be byte-for-byte unchanged** — this is a pure refactor
      (extract method), not a logic change.
    - **Tests:** (a) every existing `DskImageTests`/`Upd765Tests`/`MultiDriveFloppyTests` case
      covering `Mount`'s mismatch behavior still passes unmodified (regression guard that the
      extraction changed nothing observable); (b) `DetectMismatch` called directly, with no
      `DskImage` ever constructed, returns the identical `DiskGeometryMismatch` `Mount` would
      have produced for the same inputs, across all of `Mount`'s own existing mismatch-shape
      test cases (label-valid, config-matches, single-candidate, two-candidate collision,
      no-candidate short, no-candidate long). → commit.

---

22. **Directory-format detection dispatch + JWSDOS side/track-sector exposure** (NEW, owner
    decision 2026-07-28, reference doc §3a "RESOLVED — the Disk Drives window's directory browse
    table gets format auto-detection..." block; `docs/P2000T-disk-formats.md` §1/§4/§6a for the
    underlying byte facts). First of a three-part split — this milestone covers only the part
    that's fully unblocked; 22a/22b are placeholders pending owner decisions tracked in the
    reference doc block and `docs/P2000T-disk-formats.md` §7 item 8.
    - **Add a `DiskDirectoryFormat` result** (`Jwsdos` / `PdosWorking` / `PdosSystem` /
      `Unknown`) and a detection entry point — e.g. `DskImage.DetectDirectoryFormat()` — that
      tries JWSDOS's directory pattern first (reuse/extend the existing `ReadDirectory()`
      validity checks — plausible printable-ASCII filenames at the known directory offset, not
      just "bytes are present"), matching the order the owner's original request specified
      (JWSDOS, then PDOS, then PDOS-system marker, then unknown). **Do not implement the PDOS or
      PDOS-system/unknown branches of this dispatch yet** — those depend on milestone 22a's still-
      open disambiguation decision; stub them to return `Unknown` for now with a clear TODO
      pointing at 22a, so this milestone doesn't block on that decision.
    - **Extend the JWSDOS directory-entry type with two new fields**, both derived from data the
      existing M19 reader already parses per entry, not requiring any new byte-level parsing:
      **Side** (0/1, straight from `DE_head`, offset 24) and **start/end sector** (already read
      as `DE_start_sector`/`DE_end_sector`) — expose these on the entry so the UI (milestone 15)
      can render its new Side and Track/Sector columns without re-deriving anything.
    - **Tests:** `DskImageTests` — `DetectDirectoryFormat()` returns `Jwsdos` for every existing
      JWSDOS fixture (`Spel1.dsk`, `jwssytem.dsk`); a JWSDOS directory entry's `Side`/start-end-
      sector fields match the values already independently confirmed in this doc's findings log
      for those fixtures; a non-JWSDOS or garbage image returns `Unknown` (not an exception, not
      a false-positive `Jwsdos`). → commit.
    - **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs`, `P2000.UI` milestone 15
      (consumes this), `docs/P2000T-disk-formats.md` §6a/§7 item 8.

22a. **PDOS FCB directory reader** (NEW, owner decision 2026-07-28, fully unblocked — reference
    doc §3a same RESOLVED block; `docs/P2000T-disk-formats.md` §6a for the full byte-level FCB
    spec and §7 item 8 for the disambiguation decision this milestone implements). Depends on
    milestone 22's `DiskDirectoryFormat` dispatch (implements the `PdosWorking`/`PdosSystem`
    branches that milestone 22 stubbed to `Unknown`).
    - **Disambiguation algorithm (fills in milestone 22's stub) — RESOLVED, owner, 2026-07-28:**
      read track 1, offset 0. If it is NOT `0xF3`, and positions 2–9/10–12/16/17–32 (name,
      extension, sector count, allocation map) look like a plausible FCB (printable ASCII/space
      name, sector count in a sane range, allocation-map entries never referencing records
      `00`–`03`), treat this as `PdosWorking` and parse the full directory (below). If it IS
      `0xF3`, run the SAME validation on the rest of the entry anyway — if it still looks like a
      plausible FCB, this is `PdosWorking` (the `0xF3` is that file's own flag value, not the
      system-disk marker); only report `PdosSystem` if the validation fails. This is the one
      genuine ambiguity in the format — the whole point of validating before trusting byte 0.
    - **Directory parse:** walk all 128 fixed 32-byte slots on track 1 (4096 bytes ÷ 32 B/FCB,
      confirmed full-track fit, no separate end-of-list terminator needed — an unused slot is
      presumed all-zero, per `docs/P2000T-disk-formats.md` §6a). For each non-empty slot: decode
      name (positions 2–9, space-trimmed), extension (positions 10–12), sector count (position
      16 × 256 bytes for a "size" figure matching the existing JWSDOS column's convention), and
      the allocation map (positions 17–32, stopping at the first `0x00` record-number entry).
      **Position 1 is a continuation-sequence index, not a per-file flag in the general case**
      (`0x00` = primary FCB; `0x01`/`0x02`/… = additional FCBs for the same filename+extension
      when one FCB's 16-record map isn't enough) — **except** when validation determines a
      specific `0xF3` value is the system-disk-adjacent flag case above; group/fold continuation
      FCBs sharing a name+extension into one logical file entry (combine their allocation maps
      for the purposes of the track/sector-range and total-sector-count figures the UI needs) —
      your call whether that folding happens here or in the UI layer, whichever is simpler
      against the real types.
    - **Expose, per logical file entry:** name, extension, size (sector count × 256), and either
      the raw allocation-map record numbers or the pre-derived start/end-track + sector-count
      trio (UI milestone 15a's resolved display formula: start track = first record ÷ 4, end
      track = last record ÷ 4, sector count = record-count × 4 — needs no physical-interleave
      exposure, this is plain arithmetic on record numbers).
    - **Tests:** a real or constructed PDOS working-disk fixture (the owner-supplied `volorg.dsk`
      is the known real example, `VOLORG`/`VOLINFO`, if available as a test fixture — confirm
      with the owner/existing test assets before fabricating a synthetic one) parses both real
      entries correctly (name, extension, size, track/sector range) including the confirmed
      `VOLORG` = `0xF3`-flagged-but-still-a-valid-entry case, which is exactly this milestone's
      disambiguation logic exercised for real; a genuine PDOS system disk (the existing
      `0xF3`-signed "Disk BASIC 24K" fixture, if present — machine milestone `getdos`/§6 testing
      already has one) returns `PdosSystem`, not a false-positive directory; a synthetic
      multi-FCB (continuation) fixture, if one is worth constructing, folds correctly into one
      logical entry with a combined sector count.
    - **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs`, UI milestone 15a (consumes
      this), `docs/P2000T-disk-formats.md` §6a/§7 item 8.

22b. **Raw sector-1 read for the system-disk/unknown fallback dump view** (NEW, owner decision
    2026-07-28, fully unblocked). Triggered whenever milestone 22's dispatch returns
    `PdosSystem` or `Unknown`.
    - Likely needs no new API at all if an equivalent to `DskImage.ReadSector` already exists for
      other purposes (the FDC's own read/write path reads sectors constantly) — confirm and
      reuse rather than adding a redundant method. If nothing suitable is exposed publicly today,
      add a minimal `DskImage.ReadRawSector(track, side, sector) → byte[256]` (or equivalent)
      that the UI can call for exactly this dump view — read-only, no FDC/command-sequence
      semantics needed, just raw bytes off the mounted image.
    - **Tests:** reading sector 1 off a known-content fixture returns the expected 256 bytes
      byte-for-byte; reading past a short/padded mount returns the same `0x00` fill-byte
      convention already established for out-of-range reads elsewhere (milestone 20d).
    - **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs`, UI milestone 15b (consumes
      this).

23. **Blank-disk detection — stop defaulting an all-empty directory to `Jwsdos`** (NEW, owner
    decision 2026-07-28, fast-follow onto milestone 22 — reference doc §3a same RESOLVED block's
    part-3 bullet). An all-empty directory region is equally consistent with a blank JWSDOS disk
    or a blank PDOS working disk (both formats read as all-zero there before anything's written)
    — milestone 22's "empty still counts as `Jwsdos`" carve-out was an arbitrary pick between two
    equally-plausible blank states, not a real detection.
    - **Remove the "all-empty slots still count as a valid, just-empty `Jwsdos` directory"
      special case** added in milestone 22. Let an all-empty (or otherwise unrecognized) region
      fall through the same dispatch chain as anything else — it should reach `Unknown` on its
      own once that carve-out is gone, since an all-zero first FCB slot also won't pass
      milestone 22a's "plausible PDOS FCB" validation (zero bytes aren't printable ASCII/space).
      Confirm this falls out naturally rather than needing new fallthrough logic; if it doesn't,
      that's worth understanding before forcing it.
    - **Expose enough for the UI to distinguish "genuinely blank" from "unrecognized garbage"**
      when rendering the `Unknown` case (milestone 16 needs this) — your call whether that's a
      new `DiskDirectoryFormat` value, a separate bool/flag alongside `Unknown`, or just letting
      the UI inspect the already-available sector-1 bytes itself (all-zero → blank) without any
      new machine-layer surface at all. Whichever is simplest against the real types.
    - **Tests:** a genuinely blank/freshly-formatted image (all-zero at both the JWSDOS directory
      offset and PDOS's track-1 offset) returns `Unknown` — REGRESSION-FLIPS milestone 22's own
      existing test asserting `Jwsdos` for this exact case; update that test's expectation rather
      than leaving two contradictory assertions. A real JWSDOS disk with actual (non-empty,
      plausible) entries still returns `Jwsdos` as before — this change must not affect any
      disk with real content, only the all-zero case.
    - **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`DetectDirectoryFormat`), UI
      milestone 16 (consumes this), `docs/P2000T-disk-formats.md` §7 item 8.

24. **Debugger — per-bank access to bank-switched RAM (0xE000–0xFFFF)** (NEW, owner decision
    2026-07-28, motivated by investigating the JWSDOS-activation bug — reference doc §5d's newly
    tracked "TRACKED, not yet investigated" entry). The debugger's memory-watch windows and
    breakpoints currently only ever see whichever bank is LIVE-active at port `0x94` (reference
    doc §5) — no way to inspect a non-active bank's raw contents, nor to distinguish which bank
    triggered a breakpoint at a shared address. Needed uniformly across every banked-RAM card
    this project models (the 1-bit `RAMSW` card — 2 "banks," BANK1's upper/lower 8 KB half — and
    homebrew/T-102-class N-bank cards) — do not special-case by card.
    - **Expose each populated bank's raw backing bytes**, independent of the live active-bank
      value, through the observer/snapshot surface (§3b.1) — e.g. a `GetBankRaw(bankIndex)` (or
      equivalent) on whatever class owns the installed card's bank storage. Your call on the
      exact shape against the real types; the requirement is that reading bank N's bytes must
      not depend on bank N currently being active, and must not mutate the live core (a pure
      snapshot read, like everything else on the observer side).
    - **Expose the currently active bank value** (and, for a card with no banking installed,
      that there IS no banking) as part of the state snapshot, so it can be shown live and
      updates every observer refresh — not just a one-time read at debugger-open.
    - **Expose how many banks the installed card has** (2 for `RAMSW`, `bankCount` for a
      homebrew/N-bank card, 0/none for a non-banked or bare configuration) so the UI can
      populate a per-window/per-breakpoint bank selector without hardcoding a count.
    - **Bank-qualified breakpoints:** extend the existing memory R/W/X + execute breakpoint
      store (§3b.2) so a breakpoint whose address falls in 0xE000–0xFFFF can optionally carry a
      specific bank index alongside it. At evaluation time, a bank-qualified breakpoint fires
      ONLY when the live active-bank value matches its qualifier; an unqualified breakpoint (the
      existing, default shape) fires regardless of which bank is active, exactly as today — no
      behavior change for any breakpoint outside the banked region, or for existing unqualified
      ones inside it.
    - **Tests:** a synthetic multi-bank fixture (distinct known bytes per bank) confirms
      `GetBankRaw(N)` returns bank N's bytes regardless of which bank is currently active, and
      that switching the active bank live doesn't change what a specific `GetBankRaw(N)` call
      returns; the active-bank snapshot value tracks port `0x94` writes exactly; a bank-qualified
      breakpoint at an E000-region address fires only when that bank is active (test with the
      SAME address, two different active banks, only one qualified) and an unqualified
      breakpoint at the same address fires in both; a non-banked configuration reports zero/none
      banks and the qualifier is simply unavailable (existing breakpoint behavior at any address,
      banked-region or not, is completely unaffected).
    - **Applies to:** whatever class owns the installed card's bank storage (reference doc §5 —
      the `RAMSW`/homebrew bank-register device), the breakpoint store (§3b.2), the observer
      snapshot surface (§3b.1), `docs/P2000T-reference.md` §3a "Debugger" section, UI milestone
      17 (consumes all three).

---

## 14. Deferred (build the seams now, implement later)

Do NOT implement these in this build, but keep the interfaces ready (they're specced in the
reference doc): **P2000M** (different video-memory sharing, 4 KB VRAM); **PTC-96K** (blocked on
reference doc open item #4, the unsourced wider-`0x94` addressing scheme — see §13.20; T38/T54/
T102 are already implemented, so this is the only RAM-variant piece still deferred); **hires
overlay board**; **SLOT2 expansion cards**; **80-column mode**; **printer**. The aggregator
(§8), slot model (§12.12), and `TimingPolicy` (§7) are the seams these plug into.

- **M2200's full feature set beyond the shared FDC/RAM-bank-switch** (RTC, RAM disk, Serial/SIO,
  Centronics, and a previously-undiscovered **second Z80 CTC at `0x80`-`0x83`**) is now
  well-documented (`docs/M2200-implementation.md`, expanded 2026-07-23 from the owner-supplied
  full Miniware Technical Manual) but **not scoped into any milestone yet** — M20 above only
  covers the two features M2200 shares with the plain floppy+RAM board. A future M2200-specific
  milestone has a real primary source to build against now (full RTC register set, SIO control
  model + daisy-chain wiring, RAM disk geometry, and the second CTC's role/ports), which it did
  not have before this pass.

(**FDC dropped off this list as of M19** — §13.19. **The multi-board RAM-variant framework and
multi-drive floppy subsystem dropped off this list as of M20** — §13.20; only the unsourced
PTC-96K addressing scheme remains genuinely deferred.)

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

**2026-07-24 — trimmed for size.** This log had grown to ~2000 lines. Every entry was
checked against `P2000T-reference.md` first — see the day's sync pass for details — then
the full historical log (every entry, unedited) was moved to
`docs/CLAUDE_machine_findings_archive.md` for posterity. What's kept live below: entries
still genuinely open (no closing IMPLEMENTED/FIXED entry yet), plus the last couple of
active days, for continuity. Everything fully resolved and already synced lives only in
the archive now — check there before assuming something's missing.

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

### 2026-08-04 — Part I (cc-bugfix-prompt-15): ROOT CAUSE FOUND AND FIXED — a genuine emulator timing gap in `Upd765`'s natural end-of-transfer completion path, not a PDOS/BASIC bug. CONFIRMED END-TO-END: `RUN"VOLORG"` now loads and runs VOLORG.BAS successfully. This closes the entire multi-day "Disk I/O error" investigation (Parts A-I).
- **Trigger:** cc-bugfix-prompt-15, the direct continuation of Part H — reconcile Part B's own
  "channel 0 fires and delivers correctly for all 15 real READ DATA completions" claim with Part
  H's own "the 14th `CALL 6205h` never returns" finding by tracing the completion-to-return path
  for the 14th operation specifically: does the interrupt genuinely fire, is the redirect at
  `&H6135` (`issue_Disk_read_command`/`issue_Disk_write_command`, 0xE8C3 — the routine the prompt
  called "sub_e8c3h") armed correctly, and if so where does the handler's own logic actually go.
- **New permanent regression test:**
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs`
  (`RunVolorg_TracesThe14thOperationsRedirectPathPrecisely`). Traces, across the full
  `RUN"VOLORG"` attempt: every genuine entry to `sub_e8b3h` (0xE8B3, the real physical-read
  issuer reached from `sub_f137h`'s F_READ handler via `lf170h` → `Seek_to_track` →
  `sub_e8b3h` — NOT `sub_e7abh`'s own, differently-redirected `try_read_loop`, confirmed by
  reading `docs/PDOS_wip.asm` directly and cross-checked via embedded `LD HL,nnnn` operand bytes,
  not just label-naming convention); the live value patched into `(0x6135)`; every genuine PC
  arrival at `le916h` (0xE916, the redirect landing point) together with the int-ack-pushed
  return address about to be popped there; `Upd765`'s own `Trace` output for every real FDC
  completion (`transferIndex`/`bufferLength`); and CTC channel 0's `IntPending`/`InService`
  transitions.
- **Two real instrumentation bugs found and fixed while building this trace — both flagged
  precisely rather than mistaken for emulator bugs:**
  1. Reading `(0x6135)` one instruction too early (right after the 3-byte/16-T-state
     `LD (nn),HL` write's OWN next-sequential address, 0xE8CB) caught a STALE value left over
     from the immediately-preceding `Seek_to_track`/`send_seek_or_recalibrate` call (which
     patches the SAME cell to `EI_RETI` moments earlier in the same F_READ cycle) — this
     project's cycle-stepped core can show PC at the next instruction before a multi-T-state
     instruction's own memory write has actually committed, the same general class of timing
     subtlety already hit for `RET` elsewhere in this investigation, here affecting a `LD (nn),HL`
     instead. Fixed by sampling several instructions later (0xE8D2), by which point the write is
     unambiguously done — confirmed genuinely re-armed to `le916h` (0xE916) on every one of the 15
     physical reads once fixed.
  2. Watching bare `PC==0xE96D` (the address right after `busy_wait_for_interrupt`'s own
     `jr nz,le962h`) produced 65,564 "hits" — almost one per loop iteration — because a 2-byte
     conditional `JR` can transiently show its own next-sequential address mid-decode even when
     the branch IS taken (the same PC-fetch-timing artifact class already found for `RET` in
     Parts C/E, here for `JR`). Fixed by only counting a hit as genuine when `BC==0` (the actual
     zero-check the fallthrough is gated on) — collapsing the noise to exactly the 1 real
     exhaustion that occurs in this repro.
- **CONFIRMED, decisively: the redirect mechanism itself is never mis-armed, and channel 0
  genuinely fires and delivers for the 14th completion too — extending, not contradicting, Part
  B's own original claim.** All 15 physical reads (1 initial verify + 14 real VOLORG-data reads)
  issue via `sub_e8b3h`; `(0x6135)` is patched to `le916h` all 15 times; PC genuinely reaches
  `le916h` all 15 times; CTC ch0's `IntPending`→`InService`→cleared cycle completes cleanly for
  all 15 (confirmed via the doubled-but-consistent count, the doubling itself another instance of
  the established PC/state-sampling-artifact class, not a real double-fire).
- **THE DECISIVE FINDING: what got interrupted differs, only for the 14th completion.** `le916h`'s
  own `pop hl` retrieves the CPU's own int-ack-pushed return address — a reliable signal, not a
  live-trace artifact (it's read from the stack, not sampled from a live PC). For completions 1-13,
  this is **always exactly `0xE969`** — a clean instruction boundary inside
  `busy_wait_for_interrupt`'s own idle loop (specifically between its second
  `ld hl,(PDOS_flags)` and `ld a,b`), a safe, throwaway point to discard. **For the 14th
  completion, it is instead `0x6150`** — a *different*, clean instruction boundary, but this time
  INSIDE `dsk_in_loop`, PDOS's own semi-DMA byte-transfer polling loop (between its own `dec e`
  and the following `jp nz,dsk_in_loop`, right after `INI` copies a byte) — i.e., the interrupt
  catches the CPU still mid-transfer, not already idling in the busy-wait.
- **Cross-referencing `Upd765`'s own `Trace` output (source-confirmed against
  `Upd765.DispatchDataCommand`, `sectorCount = Math.Max(1, endOfTrack - startSector + 1)`)
  explains exactly why, and why it's specific to the LAST sector of the track:** PDOS's own FDC
  command (`DISK_RW_Command`) always requests a wide EOT window — **EOT is fixed at 16, R varies
  per call** (matching the confirmed interleave sequence `1,7,13,3,9,15,5,11,2,8,14,4,10,16`) —
  while its software polling loop (`dsk_in_loop`, governed by `sub_e8b3h`'s own hardcoded
  `ld e,001h`) only ever consumes exactly **one sector**, confirmed via `Upd765`'s own trace:
  **`transferIndex` is 256 at every single one of the 15 completions.** For every read except the
  last, this leaves `_transferIndex` (256) well short of the nominal `_transferBuffer.Length`
  (256 up to 4096, since `sectorCount = EOT-R+1` — confirmed varying exactly as expected: 4096,
  4096, 2560, 1024, 3584, 2048, 512, 3072, 1536, 3840, 2304, 768, 3328, 1792, **256**) — so
  completion for those 14 can ONLY come from PDOS's own explicit terminal-count write
  (`dsk_io_done`, reached once the software's own polling loop decides it's done), which this
  emulator DELIBERATELY DEFERS by `MinimumLostWakeupGuardTStates` (200 T-states) — a real,
  intentional fix already landed 2026-07-28 specifically to prevent an interrupt from being
  delivered before the software's own return-and-settle sequence has run (the "lost wakeup" class
  of bug). This deferral gives the CPU a comfortable head start to reach
  `busy_wait_for_interrupt`'s idle loop before the (deferred) interrupt actually arrives — matching
  the consistent `0xE969` result for all 14 non-final completions (the initial verify read
  included). **But for the LAST sector of the track (R=16), the nominal EOT-R+1 window collapses
  to EXACTLY one sector — matching what the software polls exactly — so the transfer instead
  completes via `Upd765.ReadData`'s own NATURAL, perfectly SYNCHRONOUS end-of-buffer check
  (`if (_transferIndex >= _transferBuffer.Length) CompleteTransfer();`), which has NO equivalent
  settle delay.** The interrupt fires immediately, in the same T-state as the CPU's own final
  `INI`, catching the CPU still inside `dsk_in_loop` rather than already idling.
- **The full failure chain, now precisely understood end to end:** PDOS's redirect handler
  (`le916h`) unconditionally discards whatever return context was interrupted, assuming it's
  always the disinterested `busy_wait_for_interrupt` loop — for the 14th completion this instead
  discards `dsk_in_loop`'s own live resume point. The surviving, UNTOUCHED stack frame one level
  further down (`read_disk_bytes`'s own real call-return address, pointing at `le7b8h`'s trailing
  `jp busy_wait_for_interrupt`, never popped by `le916h` itself) then gets popped by `le8f7h`'s
  own unrelated, later `ret` (reached via the redirect's own `RETI`) — so execution accidentally,
  indirectly re-enters `busy_wait_for_interrupt` FRESH, waiting for a SECOND interrupt that will
  never arrive (the FDC has nothing left to signal for this operation). This fresh busy-wait
  genuinely runs its full 65536-iteration course and times out for real — confirmed precisely: the
  one genuine `BC==0` exhaustion in this trace lands 3,802,541 T-states after the 14th completion,
  matching the "~3.8M T-states" figure already cited in Part B's own findings entry almost exactly,
  now with a precise, source-level mechanism behind it rather than just a T-state-window
  observation. `channel_time_out`/`sub_e943h` then fires (from `busy_wait_for_interrupt`'s own
  natural timeout — the SAME call site Part C's trace found, now correctly attributed) and reports
  "Disk I/O error."
- **Verdict, precisely: this is very likely a genuine EMULATOR TIMING GAP, NOT a PDOS/BASIC bug,
  and reconciles cleanly with the real-P2000M no-error data point (Part H/tenth follow-up) without
  requiring PDOS's own read protocol to be considered fragile.** PDOS's technique — request a wide
  EOT window, let software consume only what it needs, force early completion via an explicit TC
  write for every case except the one where the window naturally happens to be exactly one sector
  — is a real, legitimate, and (per the fixed 14th case) apparently EXPECTED-BY-DESIGN edge case
  that real firmware handles correctly. The TC-forced completion path already received an explicit
  settle-delay fix for exactly this class of race (2026-07-28's `MinimumLostWakeupGuardTStates`);
  the natural/synchronous end-of-buffer completion path in `Upd765.ReadData`/`WriteData` never
  received the analogous treatment, because no prior test happened to exercise a transfer whose
  natural end coincides with what the driving software's own polling loop consumes — genuinely
  novel territory, not a previously-known-and-ignored gap. Real silicon's own completion-to-INT-line
  propagation is exceedingly unlikely to be perfectly zero-latency the way this synchronous C#
  check currently is; a real µPD765 finishing its last byte and asserting its INT line almost
  certainly has SOME minimum propagation delay, which would give real PDOS firmware the same safe
  margin the TC-forced path already gets here. This is offered as the most likely reconciliation,
  not asserted as independently confirmed against real silicon timing.
- **FIXED, same pass, owner-authorized ("Yes, implement and test it now").** Added
  `Upd765.DeferNaturalCompletion()` (private helper) and a new `PendingAction.NaturalCompletion`
  enum value; `ReadData`/`WriteData`'s own end-of-buffer branch now calls
  `DeferNaturalCompletion()` (which sets `_pending = PendingAction.NaturalCompletion; _delayCounter
  = MinimumLostWakeupGuardTStates;`) instead of calling `CompleteTransfer()` synchronously;
  `Tick()`'s `switch (_pending)` handles `NaturalCompletion` identically to the pre-existing
  `ForcedCompletion` case (both just call `CompleteTransfer()` once the delay elapses). Applied
  uniformly to EVERY transfer's natural completion, not special-cased to "is this the track's last
  sector" — the asymmetry between the TC-forced path (already deferred) and the natural path
  (previously synchronous) is what mattered, and removing it uniformly is more faithful to real
  silicon (which almost certainly has some non-zero completion-to-INT-line delay regardless of
  which byte ends a transfer) than a narrower, sector-position-specific fix would have been.
- **CONFIRMED END-TO-END, via the full `P2000.Machine.Tests` suite re-run with the real boot/disk
  fixtures used throughout this investigation:** `RUN"VOLORG"` now loads and runs VOLORG.BAS
  completely successfully — its own real menu ("P 2000 DISK UTILITY", with options for
  load-and-run, directory listing, copy/delete/rename file, etc.) renders correctly on screen, with
  **no "Disk I/O error" anywhere** across the owner's own full manual repro sequence
  (`RESET`/`SYSTEM B`×2/`FILES`/`RUN"VOLORG"`×2/`FILES`). `FReadEofHandlingDiag`'s own trace
  (before being retired, see below) independently confirms CR now correctly reaches RC=44 — VOLORG's
  own genuine 44-record file length — the LEGITIMATE CP/M EOF condition, meaning the file now reads
  to completion rather than hanging partway through.
- **A genuinely interesting, unexpected side effect, flagged rather than chased further this
  pass:** plain `RESET` (default drive A, the system/boot disk) NO LONGER produces "Disk I/O
  error" either. This was previously analyzed (Fourth/Fifth owner follow-up, reference doc §5d) as
  CORRECT, INTENTIONAL behavior — "a system disk's track 1 does not hold a normal FCB index (only
  working disks' track 1 does) — reading it as one legitimately fails. Not a bug." That conclusion
  may now need revisiting: if RESET's own directory-read attempt was ALSO racing the same
  natural-completion timing gap (e.g. hitting the same last-sector-of-a-fixed-window condition
  while scanning the system disk's own track 1), this single `Upd765` timing bug could have been
  responsible for more than just the `RUN"VOLORG"` symptom. Not investigated further in this pass —
  flagged for whoever next touches the RESET/system-disk boot path.
- **Twelve pre-existing Part B-H diagnostic tests retired (`[Fact(Skip = "...")]`, not deleted),
  each pinning an exact numeric/textual fact about the CONFIRMED BUG's own specific symptom (a
  count of "13" or "14", the literal text "Disk I/O error", an exact call-site count, etc.) that is
  now definitionally false since the bug is fixed:** `PdosLoadSaveRepro.cs`
  (`Boot_ThenLoadVolorg_TraceFdcCommandsAndScreenOutput`), `SectorAdvancementCapDiag.cs`,
  `FReadReturnValueDiag.cs`, `RecordCounterLiveTraceDiag.cs`, `BasicReadLoopCallSiteDiag.cs`,
  `FReadEofHandlingDiag.cs`, `SubF2fdhJumpTableDiag.cs`, `ReadDataPhysicalTrackDiag.cs`,
  `LoopExitPathDiag.cs`, `Channel0InterruptDuringGapDiag.cs`, `Sube943hCallerDiag.cs`,
  `DiskIoErrorFlagTrace.cs`. Each Skip reason cross-references this Part I entry and
  `FourteenthOperationRedirectDiag.cs`. Retired rather than rewritten/deleted: rewriting each one's
  exact assertions to match the new (correct) behavior would cost hours of CI time re-deriving
  numbers that add no value beyond what this entry and the new regression test already establish
  precisely, and deleting them would lose real investigative/forensic value (their own doc comments
  are a detailed record of exactly how the bug was chased down, Part by Part) — this project's own
  historical convention throughout Parts A-I has been to treat these test files as part of the
  evidence trail, not disposable scaffolding.
- **`FourteenthOperationRedirectDiag.cs` itself rewritten from a bug-diagnosis test into a genuine
  forward-looking regression guard for the FIX:** now asserts every redirect landing's popped
  return address is uniformly `0xE969` (never `0x6150`, the mid-transfer address that was the whole
  root cause), that no genuine busy-wait exhaustion occurs at all, and that the final screen shows
  VOLORG's own "DISK UTILITY" menu with no "Disk I/O error" text — the counts (`issueCount`, etc.)
  are asserted as "at least 15" rather than "exactly 15", since VOLORG now reads its full 44-record
  file rather than stopping early.
- **Tests:** `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs` (+0 new, 13 existing tests
  updated with a `for (var i = 0; i < 300; i++) fdc.Tick();` drain after their final
  `ReadData`/`WriteData` call, mirroring the pattern already established for the TC-forced path —
  every one of these tests assumed the now-removed synchronous natural completion);
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`
  (`WriteProtect_OnOneDrive_BlocksOnlyThatDrivesWriteDataCommand`, same drain added);
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs` (rewritten per above,
  including one follow-up fix: VOLORG's own title renders letter-spaced on screen — `D I S K` not
  `DISK` — so the menu-detection substring was changed to the contiguous `"P 2000"`). Full
  `P2000.Machine.Tests` suite CONFIRMED green: 627 total, 615 passed, 12 skipped (the retired
  files above), 0 failed.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`PendingAction.NaturalCompletion`,
  `DeferNaturalCompletion`, `ReadData`, `WriteData`, `Tick` — the fix itself),
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs` (rewritten),
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`, the 12 retired test files
  listed above, `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited
  — `sub_e8b3h`/`issue_Disk_read_command`/`le916h`/`le8f7h`/`dsk_in_loop`/`dsk_io_done`/
  `busy_wait_for_interrupt` are all pre-existing disassembly, not newly annotated by this pass),
  reference doc §5d's 2026-08-04 entry (this Part I) — this closes the entire "Disk I/O error"
  investigation, Parts A through I.
- **Synced:** no (pending human review of this entry).

### 2026-08-04 — Part H (cc-bugfix-prompt-14): LIKELY CLOSES THE ROOT-CAUSE QUESTION — this is not a graceful "BASIC's loop decided to stop after 14" bug at all. ALL THREE plausible exit checks inside the BASIC-side loop are directly disproven by live trace. The 14th real disk read genuinely gets issued and physically completes at the FDC level (Part E), but its own `CALL 6205h` never returns to BASIC — it is swallowed by the SAME PDOS-side busy-wait/timeout mechanism already fully diagnosed in Parts B/C. "Disk I/O error" is a genuine hang on the 14th operation, not an early, deliberate stop. Independent real-hardware data point (owner, real P2000M, real floppies, no error) argues this is NOT faithful/intended behavior — but the exact reason the 14th call's own PDOS-side completion signal goes missing is still not identified.
- **Trigger:** cc-bugfix-prompt-14, tracing the second counter Part G left open
  (`[pointer+0x24..0x25]`, checked at `0x326C` only once the first, byte-scan counter empties) —
  the last remaining named candidate for the loop's real termination condition. Renewed motivation
  from the owner: an actual P2000M, booted from real floppies, ran `FILES`/`LOAD "VOLORG"`
  without error — a genuine (if not `.dsk`-fixture-parity-confirmed) data point against "this is
  correct, intended behavior," reinforcing the case for continuing to treat this as a real bug.
- **A real instrumentation bug found and fixed while building the live trace — SAME class of
  mid-instruction PC-timing artifact already hit twice before in this investigation, here for a
  DIFFERENT reason (a store, not a return):** the first pass of
  `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs` captured the pointer
  (`0x63A3`) one tick after PC first showed its own init site (`0x37AD`, `LD (63A3h),HL`, a
  3-byte/16-T-state instruction) — WHILE the store was still in progress, giving a bogus `0x0000`.
  Fixing this specific timing bug (trigger on PC reaching the NEXT instruction, `0x37B0`) still
  didn't resolve it — the REAL bug was structural: the fix re-derived the pointer via a one-time
  "capture" heuristic instead of simply re-reading `0x63A3` fresh at every `0x323A` entry, the
  exact approach Part G's own `RecordCounterLiveTraceDiag.cs` had already proven correct. Rewriting
  to match that proven pattern fixed it (`pointer=0x8A90`, matching Part G exactly).
- **CONFIRMED, decisively, once fixed: `[pointer+0x24..0x25]` is set ONCE, to 256, and NEVER
  CHANGES for the rest of the loop** — the same value at all 3329 entries to `0x323A`, including
  the very last one. New regression test
  `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs`
  (`RunVolorg_SecondCounterStaysAt256_CannotGovernLoopExit`). Since `0x326C`'s own check requires
  this counter to reach zero, it CANNOT be what triggers the loop's real exit — Part G's own
  leading candidate is ruled out.
- **CONFIRMED the second candidate mechanism (F_READ's own EOF return value) is also ruled out —
  and by a more careful reading of the actual branch polarity, could never have worked even if
  observed.** New regression test `tests/P2000.Machine.Tests/Boot/FReadReturnValueDiag.cs`
  (`RunVolorg_FReadAlwaysReturnsZero_Never1_AndOnly13Of14ReturnsAreObserved`) watches register A
  at every return from `CALL 6205h` (F_READ) at `0x32A8`. A is confirmed always `0` (standard
  CP/M success), never `1` (EOF). Working through `0x32B0`-`0x3276`'s actual polarity: `DEC A`
  only sets Z when A WAS 1; since A is always 0, the jump to `0x32BA` is NOT taken, DE ends up
  `0x0100`, and the resulting OR-check leaves A nonzero (NZ) — meaning `0x3276: JP NZ,323Ch` IS
  taken. **A=0 makes the loop continue, not exit** — this path could only ever be an exit
  mechanism if A became 1, which never happens.
- **CONFIRMED the third, previously-unexamined candidate (`0x323A`'s own leading check,
  `[pointer+0]==3`, jumping to a completely different ROM address `0x8996` if matched) also never
  fires.** New regression test `tests/P2000.Machine.Tests/Boot/LoopExitPathDiag.cs`
  (`RunVolorg_AltExitCheckNeverFires_PointerFirstByteStaysAt1`). `[pointer+0]` stays at exactly
  `1` for the entire loop; PC never genuinely transitions to `0x8996`.
- **With all three exit checks inside the loop's own body ruled out, the real reconciling fact is
  a count mismatch: only 13 genuine F_READ returns are ever observed at `0x32AB` (confirmed via
  the same trace above — 27 raw hits, but that number is inflated by the SAME PC-fetch-artifact
  class already found in Parts C/E, doubling roughly half of the 13 genuine returns; the genuine
  count is 13, not 14 or 27), even though Part E already confirmed 14 real physical disk
  completions.** Precisely timed: the LAST genuine `0x323A` byte-scan entry (Part G's own detailed
  trace) occurs at `t=2463774` — BEFORE the 14th physical disk completion (Part E, `t≈2485569`).
  This means the 13th cycle's own completion is what TRIGGERS the 14th real disk read (via
  `0x327F`/`0x32A8`/`0x32D0`) — which genuinely gets issued and genuinely completes at the FDC
  level — but that 14th call's own `CALL 6205h` never returns to `0x32AB` at all, ever, within any
  reasonable trace window.
- **Conclusion, precisely: this is NOT a "wrong counter value" bug, and NOT a graceful early-stop
  decision by BASIC's own loop logic.** BASIC's loop is correctly, faithfully waiting for its 14th
  `CALL 6205h` (F_READ) to return; it never does. This is the SAME busy-wait/timeout mechanism
  already fully diagnosed at the PDOS/bank-1 level in Parts B/C (`busy_wait_for_interrupt` →
  `channel_time_out` → `sub_e943h`, confirmed at the instruction level in Part C) — now understood
  to be triggered from a REAL, physically-completing 14th disk operation, not "no further command
  ever issued" as Part E's own framing (accurate as far as it went, for the T-state window it
  examined) suggested. The prompt's own question — real bug vs. intentional behavior — is answered
  precisely: **BASIC's own loop logic is correct** (none of its three exit checks are broken); the
  problem, if there is one, lies in WHY the 14th disk operation's own PDOS-side completion/interrupt
  signal never makes it back to a normal `RET`, matching but not yet explaining the mechanism that
  Part B/C's own trace showed reaches `sub_e943h` via `channel_time_out` (the busy-wait's own
  65536-iteration timeout, not a genuine completion).
- **Still open, precisely narrowed:** WHY does the 14th disk operation's own completion (confirmed
  physically real at the FDC level) fail to deliver its interrupt/redirect back into a normal
  return from the `CALL 6205h` that issued it, when the same mechanism worked correctly for the
  prior 13? This is now a question about the SPECIFIC interrupt-redirect/completion-signaling
  mechanism for exactly one call, not about counters, EOF checks, or loop logic — a narrower,
  different kind of question than anything chased in Parts B-H so far. The independent real-P2000M
  data point (no error on real hardware) is not yet reconciled with this — floppy-content parity
  between the owner's real disks and the `.dsk` fixtures used here is unconfirmed, so this may
  point to a fixture/content difference, an emulator timing/interrupt bug specific to a 14th
  same-track sequential operation, or something else entirely.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs`,
  `FReadReturnValueDiag.cs`, `LoopExitPathDiag.cs` (all new permanent regression guards, each
  asserting the ruled-out status of one of the three candidate exit mechanisms, plus the precise
  13-vs-14/27-raw-hit F_READ-return count).
- **Applies to:** the three new test files above. Cartridge-ROM work only
  (`assets/Basic-24.bin`), same as Part G — `docs/PDOS_wip.asm` not applicable. Reference doc
  §5d's 2026-08-04 entry (Eleventh owner follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Eleventh owner follow-up (2026-08-04, cc-bugfix-prompt-14)” entry; also updated the trailing “Confirmed (owner, 2026-07-28)” status paragraph)

### 2026-08-04 — Part G (owner follow-up, no formal prompt number): found and disassembled the BASIC-side (cartridge ROM) read loop itself — the code that decides how many times to call F_READ/F_DMAOFF. CORRECTS a working hypothesis formed mid-investigation (the loop is a byte-by-byte program scanner through a 256-byte buffer, not a "records remaining" counter decremented once per sector). Confirmed: exactly 13 full 256-byte scan cycles happen, not 14 — the true stopping condition is a SEPARATE, not-yet-traced counter. Investigation paused here at the owner's own request, to decide how to continue.
- **Trigger:** direct owner follow-up to Part F. Two prompts, both addressed: (1) the owner
  patched VOLINFO's own FCB byte 15 from `0x0E`(14) to `0x2C`(44, matching VOLORG's) on a real disk
  image and re-ran the repro — same stop-at-14 result, unchanged, confirming the earlier VOLINFO
  coincidence theory is dead (already logged in the Part E/F transition note above). (2) The
  owner asked whether BASIC issues per-sector commands itself or delegates a single "load file X"
  call to PDOS — answered directly from Part D's own already-confirmed dispatcher trace: BASIC
  issues 14 SEPARATE, discrete `0x1A`/`0x14` call PAIRS through the top-level dispatcher, not one
  bulk call — confirming the stop-decision is made on BASIC's own side, between calls, consistent
  with Part F's own "external to PDOS" conclusion. The owner then asked to continue chasing the
  actual BASIC-side loop.
- **Found the exact BASIC-side call sites, all in the CARTRIDGE ROM (`Basic-24.bin`), NOT the
  disk-loaded chunk `docs/PDOS_wip.asm` covers.** New regression test
  `tests/P2000.Machine.Tests/Boot/BasicReadLoopCallSiteDiag.cs`
  (`RunVolorg_ThreeFixedCartridgeRomCallSitesIssueAllPdosCalls`) traces every genuine CALL to
  `0x6205` (BASIC's own fixed PDOS entry point, per `docs/PDOS-notes-for-annotation.md` §1/§2) and
  finds exactly 3 fixed call sites for the whole `RUN"VOLORG"` attempt: `0x3487` (F_OPEN, once),
  `0x32A8` (F_READ, 14×), `0x32D0` (F_DMAOFF, 14×) — matching Part D's own dispatcher-level counts
  exactly, now pinned to precise ROM addresses.
- **Disassembled the loop around these two repeated call sites** (`tests/P2000.Machine.Tests/Boot/RunTokenReadLoopDisasmDiag.cs`,
  using the project's own `Z80.Disassembler` directly against `Basic-24.bin` — this is cartridge
  ROM content, fully available, unlike PDOS's own disk-loaded driver). Full structure:
  - `0x323A` — the loop-driving routine. Loads a pointer from fixed cell `0x63A3`; checks a 2-byte
    counter at `[pointer+0x26..0x27]`; if nonzero, decrements it and returns ONE BYTE from a
    computed position within a 256-byte buffer (a byte-by-byte scan, not a per-sector operation).
    If that counter is already zero, falls through to `0x326C`, which checks a SECOND, separate
    2-byte counter at `[pointer+0x24..0x25]` — if that is ALSO zero, exits the whole loop
    (`0x3279`: `SCF; LD A,1Ah; RET`); otherwise calls `0x327F`, which issues one real DMAOFF+READ
    pair via `0x32A8`/`0x32D0` to refill the 256-byte buffer.
  - `0x3273`/`0x3276` (the loop's own repeat-or-stop branch): `CALL 327Fh` then `JP NZ,323Ch` —
    loops back based on flags left by `327F`'s own tail (`0x32B0`: `DEC A` on the F_READ result,
    checking specifically for the standard CP/M EOF value 1 — confirmed unreachable per Part E,
    RC never lets CR reach it).
  - `0x63A3` itself is set ONCE, at `0x37AD` (inside LOAD's own setup code, `0x376F`-`0x3830`,
    previously documented in Part B), from `(0x63B1)` — a separate cell not yet traced to its own
    origin.
- **Live-traced the FIRST counter (`[pointer+0x26..0x27]`) directly — CORRECTS the working
  hypothesis formed while reading this code cold.** New regression test
  `tests/P2000.Machine.Tests/Boot/RecordCounterLiveTraceDiag.cs`
  (`RunVolorg_ByteBufferCounter_Runs13FullCyclesThenStops`). The initial reading of the static
  disassembly (recorded in this entry's own first draft, corrected before being finalized here)
  assumed this counter was "records remaining," decremented once per disk sector. Live tracing
  disproves that directly: `0x323A` is entered ~3300 times total (far more than the 14 real disk
  reads), and the counter decrements ONCE PER BYTE, cycling `256→0` repeatedly as BASIC scans
  through the loaded program's own bytes, refilling the 256-byte buffer via a real disk read only
  once each cycle empties. **Confirmed precisely: exactly 13 full 256-byte cycles occur (3328
  bytes total) before the loop stops — not 14** — and the last cycle ends EXACTLY at 0 (not a
  partial cycle cut short mid-buffer).
- **Checked for a content-driven natural stop at the 13-cycle boundary — not found.** Reconstructed
  VOLORG's own logical (interleave-corrected) byte stream directly from `assets/Disks/volorg.dsk`
  and inspected the bytes around the 3328-byte boundary for a plausible BASIC "end of program"
  marker (a null/`0x00 0x00` link-pointer, the common tokenized-BASIC convention) — found none; the
  bytes stay dense and high-entropy straight through the boundary, no zero bytes anywhere nearby.
  So the 13-cycle stop is NOT (at least not simply) "BASIC correctly found the program's real end
  while scanning" — reinforcing rather than resolving the open question.
- **What's now precisely un-explained, narrower than before:** the loop's REAL exit condition is
  governed by the SECOND counter, `[pointer+0x24..0x25]`, checked only once the byte-buffer
  counter (`[pointer+0x26..0x27]`) empties. This second counter's own live value, initial value,
  and update rule have NOT been traced — that is the concrete, well-scoped next step. The
  three-way relationship between "13 full byte-scan cycles," "14 real disk sector reads" (one
  cycle's worth of buffer-refill reads ahead of what's been scanned, i.e., the 14th disk read
  fetches a sector never actually consumed by the byte-scanner before the loop exits), and this
  second counter's own threshold is not yet reconciled.
- **Investigation deliberately paused here, at the owner's own explicit request** ("write it up,
  and log. Then I'll decide how we continue") — not because a natural stopping point was reached,
  but to let the owner choose whether to continue into the second counter next.
- **Tests:** `tests/P2000.Machine.Tests/Boot/BasicReadLoopCallSiteDiag.cs`,
  `RunTokenReadLoopDisasmDiag.cs`, `RecordCounterLiveTraceDiag.cs` (all new permanent regression
  guards, asserting the exact call-site counts, the disassembled instruction shapes at the
  confirmed key addresses, and the 13-cycle/256-start/0-end counter behavior respectively).
- **Applies to:** the three new test files above. `docs/PDOS_wip.asm` NOT applicable to this
  entry — every address found and disassembled this pass is in the CARTRIDGE ROM
  (`assets/Basic-24.bin`), a different code region entirely from PDOS's own disk-loaded driver
  that file covers. Reference doc §5d's 2026-08-04 entry (Tenth owner follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Tenth owner follow-up (2026-08-04)” entry; also updated the trailing “Confirmed (owner, 2026-07-28)” status paragraph)

### 2026-08-04 — Part F (cc-bugfix-prompt-13): the three candidate "2-sectors-short" mechanisms (`leb9eh`/`sub_f447h`/`lf555h`) are ALL EXONERATED — the interleave table is complete, and the index computation is a clean, UNCAPPED counter that would correctly continue to the real sectors 6/12 if called again. This re-narrows (does not close) the investigation: the actual stop is external to PDOS's own low-level sector-advancement code, most likely in BASIC's own record-reading loop, a different, not-yet-disassembled code region. NOT a genuine hardware/real-PDOS limitation confirmed either way — still open.
- **Trigger:** cc-bugfix-prompt-13, following directly from Part E's own flagged candidate
  locations for the "14-of-16 sectors" mechanism: `leb9eh`, `sub_f447h`, and the `lf555h`
  interleave-lookup table (all named in Part E's own "brief look," not yet disassembled there).
- **Static disassembly confirms the interleave table (`lf555h`, `docs/PDOS_wip.asm`, read but not
  edited) is genuinely COMPLETE, not short.** Its raw bytes (mis-rendered by the disassembler as
  garbage instructions, same phenomenon as `sub_f2fdh`'s own jump table in Part D) are the full,
  correct 16-entry sequence `01 07 0D 03 09 0F 05 0B 02 08 0E 04 0A 10 06 0C` (decimal
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16,6,12`) — indices 14 and 15 genuinely hold 6 and 12. The table
  itself was never the bug.
- **A real instrumentation bug found and fixed while building the live trace, worth flagging for
  any future work in this codebase:** `sub_f447h`'s subtrahend involves a DOUBLE INDIRECTION —
  `ld hl,(0f662h)` loads a *pointer* from that cell, and `ld c,(hl)`/`ld b,(hl+1)` read the real
  subtrahend from wherever THAT pointer points, not from `0f662h`'s own bytes directly. The first
  pass of the new test read `0f662h` directly (missing the second indirection), producing
  nonsensical index values (166-195, far outside the table's 0-15 range) that briefly looked like
  a genuine out-of-bounds read bug before the fix revealed it was purely a test artifact.
- **CONFIRMED, decisively, once the instrumentation was fixed: the table-index computation is a
  clean, UNCAPPED linear counter in BOTH contexts tested.** New regression test
  `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs`
  (`SystemBAndRunVolorg_TableIndexComputationIsUncapped_StopIsExternalToThisCode`) traces every
  genuine call to `sub_f447h`, distinguishing its two call sites by return address (`leb9eh`'s own
  internal loop-check at 0xEB9E is a DIFFERENT, unrelated subtraction — not a table index at all;
  only `lebe5h`'s call at 0xEBF0 computes the real table index). Across both `SYSTEM B` (directory
  read) and `RUN"VOLORG"` (file-data read): the index cleanly advances `0,1,2,...,13` in both
  contexts, `carry` is FALSE on every single call (no underflow/boundary condition is EVER
  signaled — the datasheet-style "stop" mechanism this code would need to genuinely halt at 13
  simply never fires), and every table byte read matches the confirmed interleave exactly. **This
  disproves the prompt's own working hypothesis** (a fixed loop counter, a short table, or an
  off-by-one in the interleave walk) — none of the three named candidate locations contain any
  cap at all. Called an additional 1-2 times, this exact code would correctly compute indices
  14/15 and read the real sectors 6/12.
- **What this means: the actual "stop after 14" decision is made ENTIRELY OUTSIDE this code.**
  Combined with Part D's own confirmed dispatcher-level trace (PDOS's top-level dispatcher,
  `CPM_entry_point`, receives NOTHING after the 14th `0x14`/F_READ call — not a 15th F_READ, not
  `F_CLOSE`, not any other function code at all) and Part E's own confirmed CR/RC tracking (CR
  stops advancing at exactly 13, RC stays at VOLORG's genuine 44 throughout, EOF never triggers) —
  the picture is now: PDOS's own bank-1 driver code is a passive, correctly-functioning component
  that would read further if asked. Whatever decides to stop asking lives in BASIC's own
  record-reading loop — a separate, disk-loaded code region (the same `&H698D`-`&H69D5`-adjacent
  area first identified in Part A, not the PDOS bank-1 driver `docs/PDOS_wip.asm` covers) — and
  has NOT been disassembled by this investigation. Disassembling THAT loop is the natural next
  step, but is a genuinely different code region from everything traced in Parts B-F.
- **Owner follow-up experiment, 2026-08-04 (post-Part-F): directly disproves the VOLINFO-byte15
  coincidence theory.** The owner patched VOLINFO's own FCB byte 15 from `0x0E`(14) to `0x2C`(44,
  matching VOLORG's own value) on a real disk image and re-ran the repro — **same stop-at-14
  effect, unchanged.** This confirms the "14" is NOT read from or influenced by VOLINFO's FCB at
  all; the earlier byte-15 match was pure coincidence, exactly as the owner suspected. Also
  confirmed (same follow-up exchange, citing the already-established Part D dispatcher trace):
  **BASIC issues discrete, per-record `0x1A`(F_DMAOFF)/`0x14`(F_READ) call PAIRS through the
  top-level `CPM_entry_point` dispatcher, 14 times — NOT a single "load file" call that lets PDOS
  loop internally.** PDOS's own bank-1 driver never receives more than one record's worth of work
  per call; the decision to stop after the 14th pair is made entirely on BASIC's own side, between
  calls — reinforcing Part F's own "the stop is external to PDOS" conclusion via a second,
  independent line of evidence.
- **Does NOT resolve the "is this correct, faithful P2000 hardware/software behavior, or a genuine
  bug" question the prompt asked to settle — still open, in either direction.** The dense, real
  data confirmed present in the skipped sectors (6, 12) and in VOLORG's later tracks (Part E's own
  raw-byte check, corroborated by the owner directly: VOLINFO's own FCB byte 15 = 0x0E = 14,
  matching a DIFFERENT file's real "needs only 14 of its 16 allocated sectors" case, distinct from
  VOLORG's own byte 15 = 0x2C = 44, meaning VOLORG genuinely needs all 44) makes an intentional,
  correct 14-sector stop implausible for VOLORG specifically — but WHY BASIC's own loop would stop
  early regardless is not yet identified, so this is not confirmed as a bug either. Flagged
  precisely rather than guessed at, per this investigation's own standing convention.
- **Sub_e706h discrepancy (Part D/E, "30 vs 29"):** not chased this pass — no new cheap signal
  surfaced during this investigation to make it worth a dedicated look; remains open exactly as
  Part D left it.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs` (new permanent
  regression guard) — asserts the table-index computation reaches exactly indices 0-13 with
  `carry=False` throughout, in both the `SYSTEM B` and `RUN"VOLORG"` contexts, and that every
  table byte read matches the confirmed interleave sequence.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs` (new),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited — no new
  addresses beyond what Part D/E already cited), reference doc §5d's 2026-08-04 entry (Ninth owner
  follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Ninth owner follow-up (2026-08-04, cc-bugfix-prompt-13)” entry)

### 2026-08-03 — Part E (cc-bugfix-prompt-12 + addendum): LIKELY RESOLVES THE ORIGINAL "Disk I/O error" INVESTIGATION — RUN"VOLORG" is NOT stuck on a directory scan that never finds a match. PDOS's function codes are a direct CP/M 2.2 BDOS clone; VOLORG's own file DATA is already being read correctly, record by record, via genuine F_READ/F_DMAOFF calls. The read simply stops 2 sectors short of a full track — the SAME "14-of-16 sectors" limitation already flagged as an unrelated loose end in Part B — long before CP/M's own EOF condition would ever fire. Neither of the prompt's own theories (a)/(b) is correct; a third, better-supported explanation is confirmed instead.
- **Trigger:** cc-bugfix-prompt-12, disassembling the three `sub_f2fdh` handler bodies Part D
  pinned down (`0x0F`→`0xF370`, `0x14`→`0xF3A0`, `0x1A`→`0xF3CA`), to determine whether PDOS's own
  FCB-name compare (a) legitimately never matches VOLORG's real FCB content, or (b) matches
  correctly but nothing acts on it. **A user-supplied addendum arrived mid-investigation** with a
  decisive piece of outside context: PDOS's function codes `0x0F`/`0x14`/`0x1A` match standard
  CP/M 2.2 BDOS exactly — F_OPEN/F_READ/F_DMAOFF (source:
  [seasip.info/Cpm/bdos.html](https://www.seasip.info/Cpm/bdos.html)) — and the dispatcher was
  already labeled `CPM_entry_point` in the existing disassembly. This reframed the whole
  investigation: the 14-15 physical reads Parts B/C/D all labeled "the directory scan" were never
  actually confirmed against the documented directory track/sector location — `0x1A`/`0x14`
  alternating is the textbook CP/M idiom for reading a file's DATA sequentially (DMAOFF then
  READ), not how directories are searched (that's F_SFIRST/F_SNEXT, `0x11`/`0x12` — confirmed
  never called, Part C/D's own trace). The addendum asked for one cheap check FIRST, before more
  disassembly: confirm what physical track these reads actually target.
- **Static disassembly of the three handler bodies** (`docs/PDOS_wip.asm`, read but not edited —
  no owner annotations exist yet around these three addresses):
  - `0x0F`→`0xF370`: `call sub_f2d4h; call sub_f068h; jr lf388h`. `sub_f2d4h` extracts the low 5
    bits of the current FCB's flag byte (offset+0), treats values ≥0x1E as out-of-range (early
    return), otherwise clears those bits in-place and calls `sub_f2c2h` (drive-select bookkeeping,
    compares `lf583h` against `Active_drive`). `sub_f068h` copies bytes out of the current FCB
    into a working buffer (guarded by `sub_f032h`, which aborts if `(lf585h)==0xFF`) — consistent
    with F_OPEN's job of locating/opening a file and priming its working state.
  - `0x14`→`0xF3A0`: `call sub_f2d4h; call sub_f137h; jr lf3fah`. `sub_f137h` reads standard CP/M
    FCB fields directly — `sub_ec39h` reads FCB offset `+0x20` (CP/M's `CR`, current record) into
    `lf665h` and offset `+0x0F` (CP/M's `RC`, record count) into `0xf664h` — compares them
    (`CR < RC` → issue the next physical read via `Seek_to_track`/`sub_e8b3h`, the same confirmed
    real-FDC-command source from Part B/C; `CR >= RC` → set `lf582h=1`, the EOF-equivalent result
    that flows into the actual return value through `sub_f2fdh`'s own `lf3fah` epilogue, and return
    immediately). This is a byte-for-byte match to F_READ's own textbook CP/M shape.
  - `0x1A`→`0xF3CA`: a different shape entirely — `ld a,(lf58bh); rra; jr nc,lf3d8h`. If bit 0 of
    `lf58bh` is set, copies the current FCB pointer (`0xF579`) directly into `lf589h` — the exact
    cell `le229h`'s function-`0x39` handler reads to know which file's data to read; if clear,
    falls to mere bookkeeping (`lf2f3h`, copies the FCB pointer into `lf587h`/`lf52eh`, no I/O).
    Matches F_DMAOFF exactly: a pure buffer-address-priming operation with no disk I/O of its own.
- **Live-trace results — four new regression tests, all decisive:**
  1. **`FcbCompareHandlerTraceDiag.cs`** (`RunVolorg_VolorgIsAlreadyTheActiveFcb_MatchBranchAlwaysTaken`):
     `lf58bh` bit 0 is SET on all 15 real `0x1A` entries, and the "prep `lf589h` for a real read"
     branch is taken every single time — never once does it fail. VOLORG's own FCB (independently
     confirmed via raw bytes at `assets/Disks/volorg.dsk` track-1 slot 0: byte0=`0xF3`,
     name=`"VOLORG  "`, ext=`"BAS"`, sector-count=`0x2C`=44, allocation map records
     `{4,5,6,7,12,13,14,15,16,17,18}`) is the "current" FCB pointer at all 27 `sub_f137h` entries
     too. **A real gotcha found and fixed along the way, same class as the RET-fetch artifact from
     Part C:** the test's own raw "no-match branch (0xF3D8) taken" count matched the match-branch
     count 1:1 — a dead giveaway it's a PC-fetch-increment artifact from the 2-byte `jr lf3dbh` at
     `0xF3D6` transiently showing PC at `0xF3D8` before the jump completes, not a genuine second
     execution path. Kept as a raw diagnostic count, not asserted as a real branch outcome.
  2. **`ReadDataPhysicalTrackDiag.cs`** (`RunVolorg_ReadDataCommands_TargetTrackOneNotVolorgsOwnDataTracks`):
     watches every real FDC transfer completion's physical cylinder/head/sector. **CONFIRMED:
     exactly ONE initial read on cylinder 0 (1-based track 1, the directory — presumably F_OPEN
     locating VOLORG's FCB), then ALL 14 remaining reads target cylinder 1 (1-based track 2) —
     VOLORG's OWN real data track**, independently computed from its allocation map (records
     4-7 → track 2 under the confirmed `1-based-track = record/4 + 1` formula). **A second real
     gotcha found and fixed:** `Upd765.CurrentTransfer.Sector`, sampled at the `COMPLETE` trace
     event, reports one sector PAST the one that just finished (`_transferIndex` has already
     advanced to the full transferred byte count — 256 bytes, one whole sector — by the time the
     trace fires, so `_transferStartSector + _transferIndex/_transferSectorSize` is off by exactly
     +1). Corrected by subtracting 1. **The corrected track-2 sector sequence is
     `1,7,13,3,9,15,5,11,2,8,14,4,10,16` — EXACTLY the documented full 16-sector interleave
     (`docs/P2000T-disk-formats.md` §6a), missing only 6 and 12** — the identical "14-of-16"
     pattern already flagged as an unrelated loose end in Part B (2026-07-28 entry, there
     attributed to directory reads). This is not a coincidence: it's the SAME underlying
     sector-advancement mechanism, now confirmed to also govern real file-data reads.
  3. **`FReadEofHandlingDiag.cs`** (`RunVolorg_CrNeverReachesRc_StandardCpmEofIsNeverTriggered`):
     tracks CR/RC directly off VOLORG's own FCB at every `sub_f137h` entry. **CONFIRMED: RC stays
     constant at 44 throughout (VOLORG's genuine record count, never corrupted); CR only ever
     reaches 13 across the whole attempt — nowhere near RC.** CP/M's own EOF condition (`CR>=RC`)
     is NEVER triggered for this repro. The eventual busy-wait/"Disk I/O error" is confirmed NOT to
     be an EOF-handling bug — there is no EOF condition here at all to mishandle.
  4. **`SubF2fdhJumpTableDiag.cs`** (Part D's own test, unchanged) still confirms the underlying
     30-vs-29-entry `sub_e706h` discrepancy remains open and unexplained — per the prompt's own
     "only if genuinely cheap" scoping, not chased further this pass (a quick recheck found no
     additional cheap signal beyond what Part D already reported).
- **What this means for the prompt's own theory (a) vs (b) framing: NEITHER is correct.** (a) is
  directly disproven — there is no FCB-compare failure of any kind; VOLORG's FCB is the active FCB
  from the very first relevant read and stays so throughout. (b) is also not quite right as
  framed — it's not that a successful compare's result gets dropped somewhere else (e.g. on the
  BASIC side); PDOS's own F_READ machinery IS correctly acting on VOLORG's FCB, repeatedly and
  successfully, for as long as it runs. **The real, confirmed mechanism is a third explanation:**
  the physical-sector-read-advancement machinery underlying BOTH directory reads (sub_e774h) and
  file-data reads (`sub_f137h`/`lf170h`) shares a genuine limitation that stops exactly 2 sectors
  short of a complete 16-sector track, every time, regardless of context — and this happens long
  before any CP/M-level EOF condition would legitimately end the read. Once that limit is hit, no
  further FDC command is ever issued (Part B/C/D's own confirmed observation), and the hardcoded
  busy-wait/timeout eventually fires, reporting "Disk I/O error" — not because of a disk-content
  problem with `volorg.dsk`, and not because of a dropped BASIC-side decision, but because of this
  shared sector-advancement limitation. **This directly un-flags the "14-of-16 sectors" loose end
  from Part B's own 2026-07-28 entry** — it was never a separate, unrelated curiosity; it is very
  likely the actual root cause of the entire "Disk I/O error" symptom chain investigated across
  Parts A-E. Confirming the EXACT mechanism inside the shared sector-advancement code that causes
  the 2-sector shortfall (a genuinely different, much narrower disassembly task — likely in
  `leb9eh`/`sub_f447h`/the `lf555h` interleave-lookup table, per this pass's brief look at
  `sub_ec19h`/`leb9eh`) is the natural next, tightly-scoped step, not attempted in this pass.
- **`volorg.dsk` is very likely NOT a damaged/bad fixture** — the read pipeline (FCB location,
  F_READ dispatch, physical track targeting, CR/RC bookkeeping) all work correctly right up to the
  sector-advancement limit; nothing about VOLORG's own FCB or file content is implicated.
- **Tests:** `tests/P2000.Machine.Tests/Boot/FcbCompareHandlerTraceDiag.cs`,
  `ReadDataPhysicalTrackDiag.cs`, `FReadEofHandlingDiag.cs` (all new permanent regression guards,
  finalized with assertions matching the confirmed results above).
- **Applies to:** the three new test files above, `docs/PDOS_wip.asm` (owner's own
  work-in-progress disassembly, read but not edited — `sub_f2d4h`/`sub_f068h`/`sub_f137h`/the
  `0x1A` handler's `lf58bh`-gated branch are this investigation's own raw disassembly, not yet
  owner-annotated), reference doc §5d's 2026-08-03 entry (Eighth owner follow-up), and directly
  supersedes/resolves the "14-of-16 sectors" open item from Part B's own 2026-08-03 entry above.
- **Synced:** yes (P2000T-reference.md §5d, “Eighth owner follow-up (2026-08-03, cc-bugfix-prompt-12 + a decisive mid-investigation addendum)” entry; also un-flagged the Fifth follow-up's "14-of-16 sectors" loose end in place)

### 2026-08-03 — Part D (cc-bugfix-prompt-11): LIVE-CONFIRMED that `RUN"VOLORG"` really does reach `sub_f2fdh` — and this CORRECTS Part C's own speculative account. Not one shared handler: THREE distinct function codes reach here, each landing on its own distinct jump-table target. Sizes the next disassembly pass at exactly 3 targets, not "a dozen candidates" or "zero, unreached."
- **Trigger:** cc-bugfix-prompt-11, the owner's own sharp follow-up to Part C: Part C's account of
  `le12dh` → `le149h` → `sub_e705h` → `sub_f2fdh` as "the path 0x1A/0x14 take," and `sub_f2fdh`
  itself as "a second, internal jump table... confirmed structurally (not yet functionally)," was
  read purely from `docs/PDOS_wip.asm`'s static byte patterns — never confirmed by watching PC
  actually walk that path during a real repro. This prompt: answer with the same live-trace rigor
  already used for the dispatcher and `sub_e943h`, before investing in disassembling a dozen
  candidate subroutines that might not even be the ones (or reached at all) for this repro.
- **New regression test:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs`
  (`RunVolorg_ReachesSubF2fdh_WithThreeDistinctCodesEachLandingOnItsOwnTarget`). Watches every
  entry to `sub_f2fdh` (0xF2FD), verified via the SAME genuine-CALL-bytes discipline
  `Sube943hCallerDiag.cs`'s own RET-fetch-artifact gotcha already established (checking the actual
  3 bytes at `returnAddr-3` decode to `CD FD F2` — the one real call site, `sub_e705h`'s own
  `call sub_f2fdh` at 0xE712 — rather than trusting a bare `PC == 0xF2FD` match). For each genuine
  entry, reads the routing code PDOS itself stores at `0xF578` (the same cell `sub_e705h` writes
  before calling), computes the jump-table slot as `lf307h + 2*code` (0xF307-based, per Part C's
  own structural read), reads the STORED little-endian target from that memory address, and
  independently reconfirms by watching for the CPU's own PC to actually reach that computed target
  within the following ~2000 T-states.
- **Result 1 — CONFIRMED: execution DOES reach `sub_f2fdh`.** 30 genuine calls observed across the
  whole `RUN"VOLORG"` attempt (verified via the real `CD FD F2` bytes, not a bare PC hit). This
  answers the prompt's own three-way framing decisively: it is neither "zero, unreached" nor
  "reaches a wholly different, undocumented path" — Part C's citation of the dispatch chain into
  `sub_e705h`/`sub_f2fdh` was directionally correct.
- **Result 2 — CORRECTS Part C's own speculative account: 0x14 and 0x1A do NOT share a single
  handler.** Three distinct routing codes reach `sub_f2fdh` in this repro — `0x0F` (×1), `0x14`
  (×14), `0x1A` (×15) — and **each lands on its own distinct jump-table target**, not a shared one:
  `0x0F` → `0xF370`, `0x14` → `0xF3A0`, `0x1A` → `0xF3CA`. Every one of the 30 computed targets was
  independently reconfirmed by directly observing the CPU's own PC actually arrive there (30/30) —
  this isn't inferred from the static table alone. **This sizes the next disassembly pass exactly:
  3 distinct handler addresses (`0xF370`, `0xF3A0`, `0xF3CA`), not "a dozen unread subroutines"**
  as Part C's own raw-byte read of the surrounding region suggested (that dozen-plus subroutine
  list — `sub_f068h`, `sub_f09bh`, etc. — remains unconfirmed as to which, if any, of those
  addresses these three targets actually correspond to; the exact targets are now known precisely,
  their own bodies are still undisassembled).
- **A genuine, flagged discrepancy, not explained away per the prompt's own instruction: 30
  `sub_f2fdh` entries vs. Part C's own 29 `CPM_entry_point`-level entries.** One more call reaches
  `sub_f2fdh` here than Part C's own dispatcher-level trace counted at the top-level entry point
  (0xE000) — meaning at least one call to `sub_e705h`/`sub_f2fdh` in this repro does NOT originate
  from BASIC's own top-level `CALL &H6205` PDOS invocation. `sub_e705h` has a second, alternate
  entry point (`sub_e706h`, immediately following it in `docs/PDOS_wip.asm`, which skips the
  `ld c,a` step and assumes C is already set) — the most likely explanation is that PDOS's own
  internal code calls into `sub_e706h` directly at least once, reusing the same numeric code space
  for an internal purpose, bypassing BASIC's dispatch entirely. **Not confirmed — flagged as an
  open, secondary thread, not chased further in this pass** (explicitly out of scope per the
  prompt: this pass is about sizing the jump-table targets, not tracing every caller).
- **Also worth noting (not a new finding, corroborates Part C):** the single `0x0F` call seen here
  does NOT come from the SAME `0x0F` handling Part C traced at the top-level dispatcher (that
  branch — `le0b4h`'s "FCB extension == 'BAS'?" check, `docs/PDOS_wip.asm` lines 172-205 — never
  calls `sub_e705h` anywhere in its own body, confirmed by re-reading it during this pass). So this
  repro's `0x0F` arrival at `sub_f2fdh` is itself part of the same "bypasses the top dispatcher"
  discrepancy above, not a contradiction of Part C's own separate `0x0F`-dispatch account.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs` (new permanent regression
  guard) — asserts execution reaches `sub_f2fdh` at least once; every routing code observed is one
  of the three actually confirmed here; exactly 3 distinct codes and exactly 3 distinct jump
  targets (not 1, not a dozen); and every genuine entry's computed target is independently
  reconfirmed via live PC observation (not just a static memory read).
- **Applies to:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs` (new),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited — the three
  target addresses `0xF370`/`0xF3A0`/`0xF3CA` are newly pinned down by this investigation, not yet
  named/annotated in the owner's file), reference doc §5d's 2026-08-03 entry (Sixth owner
  follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Seventh owner follow-up (2026-08-03, cc-bugfix-prompt-11 dispatched to CC...)” entry; also corrected the Sixth follow-up's “identical handler” claim in place)

### 2026-08-03 — Part C NARROWED FURTHER, STILL NOT RESOLVED (cc-bugfix-prompt-10): PDOS's dispatcher is CONFIRMED to never receive any function code beyond the directory-scan set (0x0F/0x1A/0x14) during `RUN"VOLORG"` — the decision to skip the real file-data read is NOT a PDOS-dispatch-level branch failing, it's that PDOS is never even asked. The actual decision point is narrowed to one specific, not-yet-disassembled internal jump table.
- **Trigger:** cc-bugfix-prompt-10, continuing directly from Part B's confirmed mechanism (entry
  immediately below): after the directory scan's last real sector completes, execution falls into
  a hardcoded busy-wait that times out with no operation ever having been issued. This prompt's
  question: WHY does PDOS never proceed from "FCB found" to "read the file's data"?
- **Step 1 — precisely identified WHICH of the four `sub_e943h` (0xE943) call sites fires, at the
  instruction level, via live execution tracing (not T-state-pattern inference as Part B used).**
  New regression test `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs`
  (`RunVolorg_ReachesSubE943hExactlyOnce_ViaChannelTimeOutOnly`) watches every entry to 0xE943 and
  reads the return address off the stack to identify the real caller.
  - **Gotcha found and fixed along the way, worth flagging for any future PC-address-based
    tracing in this codebase:** a naive `PC == 0xE943` check alone produces 30 FALSE POSITIVES for
    every 1 real call in this exact repro. `sub_e937h`'s own `ret` instruction sits at 0xE942,
    immediately before `sub_e943h`'s label with no intervening jump — Z80 cores bump PC past a
    fetched single-byte opcode before that opcode's own semantics (here, the stack pop a `RET`
    performs) complete, so PC transiently reads 0xE943 for several T-states on every return from
    `sub_e937h`, with SP still pointing at the not-yet-popped return address. The first pass at
    this trace misread these artifacts as two brand-new, previously-uncatalogued call sites
    (`0xE8F7`/`0xE96F`) — both are real, legitimate call sites, but for
    `Get_7_disk_status_bytes`/`sub_e937h`, entirely unrelated to `sub_e943h`. Fixed by verifying
    the actual 3 bytes at `returnAddr-3` decode to `CD 43 E9` (a genuine `CALL 0xE943`) before
    counting a hit.
  - **Result, CONFIRMED: exactly one genuine call, from `channel_time_out` (0xE978).** This
    precisely reconfirms Part B's own conclusion (busy-wait timeout) with call-site-level ground
    truth, and rules out the other two hypotheses that were still open at Part B's end: the
    function-0x0F "FCB extension == 'BAS'?" inline call (0xE0EE) and the `lea6ah` third
    interrupt-vector-payload path (0xEA6B) — NEITHER ever fires for this repro.
- **Step 2 — traced every function code PDOS's own dispatcher (`CPM_entry_point`, 0xE000) ever
  receives during the whole `RUN"VOLORG"` attempt.** New regression test
  `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs`
  (`RunVolorg_DispatcherNeverReceivesAnyCodeOutsideTheDirectoryScanSet`).
  - **Gotcha found and fixed along the way:** the first pass read register A at the exact moment
    PC==0xE000, which is BEFORE the dispatcher's own `ld a,c` (the real calling convention passes
    the function code in **C**, not A) — every entry showed the same stale leftover A value. Fixed
    by reading C instead.
  - **Result, CONFIRMED: only codes 0x0F, 0x1A, and 0x14 are EVER dispatched, across all 29
    dispatcher entries observed for the whole attempt — no other code, including the candidate
    0x39, ever appears.** 0x39 (`docs/PDOS_wip.asm`'s own `le229h`/0xE229 handler — reads a value
    via `(ix_pointer)`, converts it via `sub_eb23h`, stores the result into `RW_cmd_track`; this
    investigation's own working hypothesis for "the real file-data-read trigger," based on its
    shape looking like "convert an FCB allocation-map record into a track") is never sent. The
    scan simply cycles 0x1A (read next directory sector, dispatches into `sub_e774h`→`sub_e7abh`
    per Part B)/0x14 through all 14 real sectors and then stops entirely — no further dispatcher
    entry of ANY kind follows the last one, straight into the busy-wait.
- **Both 0x1A and 0x14 route to the exact SAME PDOS-side handler**, confirmed directly in
  `docs/PDOS_wip.asm`: `le12dh`'s dispatch chain sends both (along with 0x12/0x18/0x19/0x1B-0x1E)
  to `le149h` → `call sub_e705h` (0xE705). `sub_e705h` stores the function code (in C) into a byte
  at `0xF578` and the FCB pointer (from `lf52ch`) at `0xF579`-`0xF57A`, then calls `sub_f2fdh`
  (0xF2FD) — **this is where the real per-code behavior must live**, since PDOS's own top-level
  dispatcher treats 0x1A and 0x14 identically.
- **`sub_f2fdh` (0xF2FD) is a SECOND, internal jump table, indexed by the SAME function code —
  this is PDOS's actual FCB-compare/decision engine, and it is the concrete, narrowed location of
  whatever decides "keep scanning" vs. "found it, transition to a real read."** Confirmed
  structurally (not yet functionally): computes `lf307h + 2*code` and jumps to the 2-byte address
  stored there — a genuine jump table, whose raw table-entry bytes the current disassembly
  mis-renders as garbage instructions (visible around `0xF321`-`0xF344` in `docs/PDOS_wip.asm`'s
  raw dump) because nothing yet marks that region as data, not code. Beyond the table, roughly a
  dozen distinct case-handler blocks are visible in the raw disassembly (calling `sub_f068h`,
  `sub_f09bh`, `sub_f137h`, `sub_f186h`, `sub_f0adh`, `sub_f045h`, `sub_ef3dh`, `sub_eeadh`,
  `sub_ef57h`, `sub_ecd5h`, `sub_f24ch`, `sub_f2c2h`, `sub_f2d4h`) — **none of these addresses or
  routines are from the owner's own `docs/PDOS_wip.asm` annotations; they are this
  investigation's own raw disassembly, read only far enough to identify the jump-table shape and
  the existence of these call targets, NOT their individual behavior.**
- **What's still open, narrower than at the start of Part C:** two live possibilities, NOT yet
  distinguished — (a) `sub_f2fdh`'s own C=0x14/C=0x1A case handlers run a real FCB-name-compare
  and, for THIS fixture's FCB content, never signal "match found, transition to read" (a fixture-
  or FCB-validation-content question, per the prompt's own framing); or (b) the handlers DO signal
  a match correctly, but whatever should happen next (issue function code 0x39 or equivalent) is a
  decision made entirely on the BASIC side (the disk-loaded LOAD/RUN token driver — a DIFFERENT
  code region from PDOS's own bank-1 driver, per Part A's `&H698D`-`&H69D5` wrapper finding) that
  never gets there. **Disambiguating these needs disassembling `sub_f2fdh`'s C=0x14/C=0x1A jump
  targets and their sub-handlers** — a substantial follow-on task (a dozen-plus unread
  subroutines), not completed in this pass. Per the prompt's own instruction not to guess: this is
  reported as the confirmed narrowing achieved, not resolved further.
- **Tests:** `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs` (rewritten from a pure
  diagnostic into a permanent regression guard — asserts exactly one genuine call to `sub_e943h`,
  from call site 0xE978) and `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs`
  (new permanent regression guard — asserts every dispatched function code during the attempt is
  one of {0x0F, 0x1A, 0x14} and that 0x39 never appears).
- **Applies to:** `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs`,
  `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs` (both new/rewritten),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly — NOT edited by this
  investigation; `sub_f2fdh`'s jump table and its case handlers are this investigation's own raw
  disassembly, explicitly flagged above as not from the owner's file), reference doc §5d's
  2026-08-03 entry.
- **Synced:** yes (P2000T-reference.md §5d, “Sixth owner follow-up (2026-08-03, cc-bugfix-prompt-10 dispatched to CC...)” entry)

### 2026-08-03 — Part B NARROWED, NOT YET RESOLVED (cc-bugfix-prompt-9): `RUN`/`LOAD`'s persistent "Disk I/O error" is CONFIRMED not an FDC/CTC/interrupt-emulation bug — the real gap is that PDOS never issues the file-data read at all after a successful directory scan. Root cause of THAT still open; not guessed at.
- **Trigger:** cc-bugfix-prompt-9's Part B, continuing directly from Part A's confirmed
  `&H698D`-`&H69D5` wrapper mechanism (see that entry, immediately below) — and from a live,
  collaborative disassembly session with the owner, who is independently hand-annotating PDOS
  (`docs/PDOS_wip.asm`, work-in-progress) and spotted a CTC channel-1 timer/interrupt-counting
  mechanism ("ignore 1024 interrupts, ~20s, then time out") that looked like a promising lead for
  the original "genuinely stuck" LOAD/RUN symptom (project CLAUDE.md's own 2026-07-28 entry).
- **The CTC-timeout chase — a real, confirmed mechanism, but ultimately a RED HERRING for this
  specific symptom.** Traced `exit_and_set_disk_off_timer` (the common exit path for nearly every
  PDOS command) live: it DOES arm CTC ch1 fresh (prescaler 256, TC 0xFF, matching a real
  ~65,280-T-state period, confirmed via live reflection into `CtcChannel`'s own internal
  `_control`/`_timeConstant`/`_downCounter`/`_started`/`_softReset` fields) on every single
  command exit — and ch1 also gets reprogrammed into a SEPARATE counter-mode/TC=1 shape
  (`sub_e8c3h`) during active transfers, confirming the owner's own "set up in different ways"
  observation. Chasing this down (multiple live traces, described in full in this entry's earlier
  drafts, since trimmed) found the loop-counter math didn't add up at first (flag flipping after
  ~15 interrupts, not the expected ~1020) — but this turned out to be because the software
  loop-counters DO get freshly reset every time (confirmed via a corrected-offset direct RAM read:
  real addresses `&H6165`/`&H6166`, not the initially-hand-counted `&H6164`/`&H6165` — an earlier
  off-by-one in this same investigation), and the actual firing trigger for THIS symptom isn't the
  CTC path at all — see below.
- **The real mechanism, CONFIRMED via `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs`
  (new, kept as a permanent regression guard):** traced the REAL FDC command stream (`Upd765.Trace`)
  together with CTC channel 0's own `IntPending`/`InService` state (channel 0 = the FDC completion
  interrupt, wired `Fdc.ResultReady += () => Ctc.ClkTrg(0)`) across a full, real
  `RUN"VOLORG"` attempt.
  - **Channel 0 fires and delivers CORRECTLY, every single time, for all 15 real `READ DATA`
    completions** in the directory scan, in the confirmed interleave order
    (1,7,13,3,9,15,5,11,2,8,14,4,10,16 — `docs/P2000T-disk-formats.md` §6a). No missed interrupt,
    no delivery bug — our FDC/CTC/interrupt-daisy-chain emulation is proven correct for every
    disk operation this repro actually performs.
  - **After the LAST directory sector actually read (16) completes at t≈2.5M, the FDC trace goes
    completely silent — no further command is EVER issued.** `RUN"VOLORG"` never attempts to read
    VOLORG's actual file DATA at all, despite its FCB being confirmed present and correctly
    located (project CLAUDE.md's own 2026-07-28 entry). Execution instead falls into a hardcoded
    65536-iteration busy-wait loop (`le95fh`/`le962h` in `docs/PDOS_wip.asm`, ~3.8M T-states —
    confirmed via a PC-visit-frequency scan showing >2 MILLION visits concentrated in a 12-address
    range starting IMMEDIATELY when the gap begins) that exists to be interrupted EARLY by a real
    disk operation's own completion signal — a stack-manipulation redirect installed at `&H6135`
    by `sub_e8c3h` (POPs the busy-wait's own return address off the stack and pushes a real
    continuation address instead, so a genuine completion interrupt jumps OUT of the wait loop
    entirely rather than returning into it). Since no operation was ever started here, nothing
    ever redirects it, and the loop burns through its full duration pointlessly.
  - Once the loop naturally expires, `channel_time_out`/`sub_e943h` fires (confirmed via the
    trace's own final `CTRL 00` entry, matching that routine's `xor a; out (DSKCTRL),a` — motor/FDC
    off entirely): writes the confirmed "always error" class `0x02` into the wrapper's own result
    byte at `&H6165`/`lf51eh` (see the entry below — this is the SAME byte the `&H698D`-`&H69D5`
    wrapper reads at `&H60BB` to decide whether to print "Disk I/O error"), and sets `FDOS_flags`
    bit 6. This is what actually prints the error — confirmed to be entirely independent of
    whatever result the directory scan itself found.
  - **Small, separate loose end noticed while reading this trace back (owner question,
    2026-08-03): the directory scan only ever reads 14 of the 16 sectors the documented physical
    interleave defines for a track.** `docs/P2000T-disk-formats.md` §6a's own confirmed pattern is
    four groups of 4 — `{1,7,13,3}/{9,15,5,11}/{2,8,14,4}/{10,16,6,12}` — but the REAL resident
    driver, in every trace across this investigation (and independently already in the project's
    own 2026-07-28 entry, which recorded the identical sequence without remarking on it), issues
    exactly `1,7,13,3,9,15,5,11,2,8,14,4,10,16` and STOPS — three full groups plus only the first
    half of the fourth, never touching sectors 6 or 12 at all. Sector 16 is NOT a failure point —
    it completes as cleanly as every other sector in the scan (confirmed RESULT bytes, no errors);
    the scan simply never continues past it. Given the identical partial pattern shows up in
    OTHERWISE-successful operations too (`FILES`'s own listing, which correctly shows both real
    files), this does not look like the cause of the "Disk I/O error" itself — flagged as its own
    small, independent open question (by design, needing only 14 sectors' worth of FCB slots for
    whatever the search logic checks? or a genuine, harmless quirk?) for whoever disassembles the
    directory-scan loop next, not conflated with the main finding above.
- **What's now open, narrower than "genuinely stuck, FDC trace shows everything correct" (the
  2026-07-28 entry's own conclusion):** WHY does PDOS's own logic, immediately after successfully
  finding VOLORG's FCB in the directory scan, decide NOT to proceed to reading the file's data —
  falling instead into a busy-wait/timeout path clearly designed for "wait for an ACTUAL disk
  operation," not "there's nothing to wait for"? That decision sits between the directory-scan
  dispatch (PDOS function codes `0x0F`/`0x1A`/`0x14`, already traced call-by-call in this
  investigation) and `sub_e7abh`/`le7b0h` (the real file-data-read entry point,
  `docs/PDOS_wip.asm`) — most likely a real FCB validation/allocation-map check the `volorg.dsk`
  fixture's FCB doesn't satisfy (a format/fixture-content question, not necessarily an emulator
  bug at all), but this has NOT been confirmed — flagged, not guessed, per this project's own
  convention. Needs a disassembly of that SPECIFIC narrow code region to resolve further; the
  owner's own in-progress `docs/PDOS_wip.asm` annotation is the natural next place to look.
- **A related data point, owner-observed, worth connecting rather than treating as a separate
  mystery:** `FILES` (a genuinely different PDOS command from `RUN`) correctly lists both real
  files on `volorg.dsk` — its OWN directory-reading job visibly succeeds — and STILL prints a
  trailing "Disk I/O error" afterward (already noted in the Part A entry above, and reproduced in
  `DiskIoErrorFlagTrace.cs`'s own regression assertions). This is NOT a contradiction of this
  entry's finding — it's corroborating evidence for the SAME class of bug: directory/FCB data
  transfers correctly (confirmed, repeatedly, across `SYSTEM B`/`FILES`/`RUN`'s own initial scan);
  something DOWNSTREAM of a successful directory operation still trips the error path. `FILES` may
  be hitting a structurally similar "expected to be interrupted by something that never arrives"
  gap for its own, not-yet-traced reason (e.g. its own end-of-listing step entering a similar
  busy-wait/redirect pattern) — flagged as the most promising related thread for whoever
  disassembles the `sub_e7abh`/`le7b0h` decision point above, not confirmed to be the identical
  code path.
- **Tests:** `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs` (new) — the
  conclusive trace above, kept as a permanent regression guard: asserts the full 16-sector
  directory scan completes in the confirmed interleave order, that NO FDC command is EVER issued
  after the last completion (the direct evidence that the failure is not FDC-related), and that
  the machine settles back to a clean ready state afterward. `DiskBasicDisasmDiag.cs` (extended,
  Part B additions) — LOAD's full token handler disassembled end to end (no direct PDOS calls of
  its own — it delegates to shared subroutines) and a whole-cartridge scan for every PDOS call
  site, mapping which function codes (`0x0D`-`0x1A`) this ROM actually uses.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs` (new),
  `DiskBasicDisasmDiag.cs` (extended), `docs/PDOS_wip.asm` (owner's own work-in-progress
  disassembly, referenced throughout — NOT edited by this investigation), reference doc §5d's
  2026-08-02/03 entries.
- **Synced:** yes (P2000T-reference.md §5d, “Fifth owner follow-up (2026-08-02/03, cc-bugfix-prompt-9 dispatched to CC)” entry, Part B paragraph)

### 2026-08-02 — Part A RESOLVED (cc-bugfix-prompt-9): `SYSTEM B`'s "stale-retry" behavior is real, correct PDOS behavior — NOT a bug, no fix needed. Also found and fixed a real bug in the test harness itself (wrong VRAM row stride).
- **Trigger:** cc-bugfix-prompt-9's Part A, following Step 0's instrumentation instruction — trace
  `&H6091` (the Adresboekje's own named "Flag for Disk I/O error") live across the owner's exact
  manual repro (`RESET` → `SYSTEM B` ×2 → `FILES` → `RUN"VOLORG"` ×2 → `FILES`) BEFORE any
  disassembly, per the prompt's own "instrument first" instruction.
- **Step 0 instrumentation** (`tests/P2000.Machine.Tests/Boot/DiskIoErrorFlagTrace.cs`, new):
  reproduces the FULL owner-observed symptom pattern exactly, byte-for-byte: `RESET` → "Disk I/O
  error"+"Ok" (flag 0x00→0x02, expected — a system disk's track 1 legitimately has no FCB index);
  `SYSTEM B` (1st) → "Disk I/O error"+"Ok", flag stays 0x02; `SYSTEM B` (2nd, identical command) →
  "Ok" alone, flag clears to 0x00; `FILES` lists both real files correctly AND ALSO prints a
  trailing "Disk I/O error" (the owner's own "newest data point" — a command whose own core logic
  visibly succeeds can still report the error); both `RUN"VOLORG"` attempts fail identically, fresh,
  every time (rules out a stale flag for THAT symptom specifically — see Part B).
- **Real mechanism found by disassembly** (`tests/P2000.Machine.Tests/Boot/DiskBasicDiskLoadedDisasmDiag.cs`,
  new): every PDOS call returns through a wrapper at `&H698D`-`&H69D5`. This code is NOT in
  `Basic-24.bin` (the 16K cartridge ROM, confirmed exactly 16384 bytes, mapped linearly at
  0x1000-0x4FFF by `Slot1Cartridge.cs` with no bank tricks) — it's in the ~8K "missing interpreter
  chunk" the Adresboekje says loads from the boot floppy's tracks 3-5 into RAM at `&H6200`.
  Reconstructed that exact chunk directly from `diskbasic_1.6uk.dsk` (cylinders 2-4, 0-based =
  tracks 3-5 1-based, 16 sectors/cylinder, plain sequential read — the same "READ A TRACK" shape
  the boot loader itself uses, no logical/interleaved reordering) and confirmed the reconstruction
  is correct by finding the literal text "PHILIPS DISK BASIC" at exactly the offset the Adresboekje
  predicts (`&H693D`) before trusting anything disassembled from it.
  - The wrapper reads a result/class byte PDOS's own driver leaves at `&H60BB` after each call and:
    classes `{0x02, 0x0A, 0x0B, 0x0C}` → **unconditionally** set `&H6091=2` and jump to the error
    print path (`&H69BB`, matching the Adresboekje's own naming exactly), regardless of whether the
    underlying disk operation itself succeeded; class `0x1A` → **leave `&H6091` completely
    untouched** (a real, legitimate no-op path — the actual "stale flag persists" mechanism);
    otherwise → clear `&H6091=0` (success).
  - Confirmed via an instruction-level trace (register dump at every PC inside the wrapper) that
    `SYSTEM B`'s own drive-select call (`C=0x0E` at the BASIC-token level, `&H34D2`-`&H34F8` in the
    ROM — also disassembled, confirms the token handler itself does nothing but `CALL 0x6205` then
    `RET`, no flag logic of its own) returns through this wrapper with the result byte reading
    `0x02` on the FIRST call — one of the unconditional-error classes — which is exactly why the
    flag gets set and "Disk I/O error" prints, with NO fault in the FDC-level scan itself (the
    directory read that runs as part of it is fully correct, all 16 sectors, right interleave, no
    retries). *Why* the second call reports a different class is decided inside PDOS's own driver
    (loaded from the boot floppy's tracks 1-2 into bank 1 at boot — a third, separate code region,
    not yet disassembled) — genuinely out of scope for this pass, flagged rather than guessed.
- **A real, separate bug found and fixed along the way — in this investigation's OWN test tooling,
  not the emulator:** an earlier pass concluded "`SYSTEM B` hangs forever, prints nothing" — wrong.
  `SnapshotScreenText` (this file, and the identical pre-existing helper in `PdosLoadSaveRepro.cs`)
  read video memory with a 40-byte row stride. The real layout is 80 bytes/row with
  `Video.PanX`-based windowing (`src/P2000.Machine/Devices/Video.cs`'s own confirmed
  `BufferColumns=80`/`OnColumnFetch`) — verified directly with a raw VRAM hex dump
  (`tests/P2000.Machine.Tests/Boot/VramLayoutDiag.cs`, new), which shows every real line starting
  exactly on an 80-byte boundary (0x5000, 0x5050, 0x50A0, …). The 40-stride version only ever
  exposed the FIRST 12 of 24 real screen rows, and coincidentally still looked "readable" for any
  message under 40 characters (real content lands on every other logical line, blank padding on
  the rest) — exactly enough to fool a human or an assertion checking the first few lines, while
  silently hiding anything printed further down. `SYSTEM B`'s own error message landed at real rows
  12-13, one row past where the broken reader could see. Fixed in this file's `SnapshotScreenText`
  (now `row*80 + (PanX+col)%80`); **`PdosLoadSaveRepro.cs`'s identical helper still has the bug,
  not fixed here** (out of this prompt's scope; flagged for whoever next touches that file — it
  could equally be hiding output in ANY of that file's own repros, not just this one).
- **Conclusion: no code fix for Part A.** The wrapper logic is disk-loaded PDOS data this project
  faithfully executes, not emulator code to change — and it now demonstrably reproduces the exact
  documented/owner-observed behavior. "SYSTEM B needs a retry" is real, correct, by-design PDOS
  behavior (consistent with the manual's own "after DISK I/O ERROR the statement RESET has to be
  given"), not a bug.
- **Tests:** `DiskIoErrorFlagTrace.cs` (new) — the Step 0 repro, now a proper regression guard for
  the confirmed pattern (RESET/SYSTEM B set-then-clear, FILES' trailing error, RUN's fresh
  double-failure). `DiskBasicDisasmDiag.cs` (new, diagnostic) — disassembles SYSTEM/RESET/
  FILES/LOAD token entry points straight from `Basic-24.bin` via `Z80.Disassembler` (added as a
  test-only `ProjectReference` in `P2000.Machine.Tests.csproj`). `DiskBasicDiskLoadedDisasmDiag.cs`
  (new, diagnostic) — reconstructs and disassembles the disk-loaded PDOS wrapper.
  `VramLayoutDiag.cs` (new, diagnostic) — the raw VRAM dump that found the stride bug.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/DiskIoErrorFlagTrace.cs`,
  `DiskBasicDisasmDiag.cs`, `DiskBasicDiskLoadedDisasmDiag.cs`, `VramLayoutDiag.cs` (all new),
  `tests/P2000.Machine.Tests/P2000.Machine.Tests.csproj` (new `Z80.Disassembler` reference),
  `docs/Adresboekje-DiskBASIC-parsed.md` (ground truth used throughout), reference doc §5d's
  2026-08-02 entry.
- **Synced:** yes (P2000T-reference.md §5d, “Fifth owner follow-up (2026-08-02/03, cc-bugfix-prompt-9 dispatched to CC)” entry, Part A paragraph)

### 2026-07-31 — FIXED: `DskImage.ReadDirectory()` now reads BOTH sides — closes the "missing side 1" gap identified in the entry immediately below
- **Trigger:** owner bugfix request, direct follow-through on the investigation entry immediately
  below. Fix, not further audit — the root cause and both confirmed real locations were already
  established.
- **Fixed:** `DskImage`'s directory reading now computes each side's raw offset via the SAME CHS
  formula every other sector read uses (`SectorOffset(cylinder: 1, head, sector: head==0?9:1)`),
  replacing the old hardcoded `DirectoryOffset = 0x1800` constant entirely — per the prompt's own
  explicit instruction, `0x1800` is no longer read for directory purposes at all, not kept as a
  fallback alongside the two new reads. `ReadDirectory()`/`DetectDirectoryFormat()`/
  `IsPlausibleJwsdosDirectory()`/`IsDirectoryRegionBlank()` all now walk BOTH sides (side 1 first,
  then side 2 — a presentation-order choice, explicitly left for the UI to reconsider per the
  prompt); side 2 is skipped entirely when `Sides == 1` (mirrors JWSDOS's own `is_disk_SS` gate).
- **Single-sided images confirmed a genuine no-op, not just a special case:** for `Sides == 1`,
  the CHS formula for (cylinder 1, head 0, sector 9) collapses to exactly `0x1800` — the same
  value the old hardcoded constant always used — so `volorg.dsk`/`diskbasic_1.6uk.dsk` (both
  single-sided PDOS fixtures) are byte-for-byte unaffected. Confirmed both mathematically and by
  running the full existing test suite unchanged for these fixtures.
- **Verified the switch away from `0x1800` is safe across every other real double-sided
  fixture in this project's asset folder, not just `Spel1.dsk`, before landing it (per the
  prompt's own explicit caution):**
  ```
                        raw 0x1800 (old)         raw 0x2800 (side1, new)     raw 0x3000 (side2, new)
  Spel1.dsk             "Tralieenspel..."        "Fraxxon..." (20 entries)   "Tralieenspel..." (18)
  jws-sytem.dsk          all-zero                 REAL 14-entry catalog       all-zero
  empty-jws.dsk          all-zero                 REAL 2-entry catalog        all-zero
  hires_demo.dsk         all-zero                 REAL 16-entry catalog       REAL 5-entry catalog
  ```
  **This is not a coincidental-duplicate-vs-real-location wash — three of four real double-sided
  fixtures had GENUINE directory content the old `0x1800` offset was silently missing entirely**
  (`jws-sytem.dsk`/`empty-jws.dsk`/`hires_demo.dsk` all read as having NO real directory under the
  old code; all three actually have real, well-formed catalogs). `jws-sytem.dsk`'s newly-surfaced
  14 entries are exactly what you'd expect on a JWSDOS system/utility disk: "JWS Systeem Disk"
  (the writer program itself, listed as a file), "Format", "AUTORUN", "Disk-report 2.1",
  "Disk-duplicator", "Disk Inhoud Spec", "Multi-file Copy", "Back-updata 1.1", "Disk Util.3 in 1",
  "Diskzoeker", "Edit 40", "Edit 80", "Filecopy 1.4", "Tetris" — all `DE_head=0`, all plausible,
  well-formed 32-byte entries. `empty-jws.dsk` — despite sharing `jws-sytem.dsk`'s identical
  track-1 boot code/label (already established) and the identical FIRST two side-1 entries
  ("JWS Systeem Disk", "Format") — is NOT a byte-identical copy of `jws-sytem.dsk` overall and
  genuinely has only those 2 entries, not 14; checked directly rather than assumed from the
  shared boot code alone.
- **One entry's `TransferAddress` field changes value on `Spel1.dsk` specifically — confirmed
  correct, not a new bug.** AUTORUN's transfer address moves from `0x7000` (read from the `0x1800`
  duplicate) to `0x6547` (read from the real `0x3000` location) — this is precisely the ONE byte
  already identified (2026-07-31 audit, below) as differing between the duplicate and the real
  content, now empirically confirmed to matter for exactly the field/entry predicted. Every OTHER
  field of every other entry on `Spel1.dsk` is unaffected, since the duplicate matches the real
  content everywhere except that single byte.
- **Tests:** `DskImageTests.cs` (machine layer) — both-sides-populated ordering/head-matching
  (synthetic), single-sided no-op, the unrelated cylinder-0/head-1 region still never surfaces,
  a side-2-only synthetic image still detects correctly. `RealFixtureTests.cs` — `Spel1.dsk`
  returns all 38 real entries (20 side-1 + 18 side-2) in side-1-then-side-2 order with correct
  per-entry `Head` values; `jws-sytem.dsk` returns its real 14-entry side-1 catalog instead of
  reporting empty; `DetectDirectoryFormat` correctly flips from `Unknown` to `Jwsdos` for
  `jws-sytem.dsk` now that its real content is found. `DiskDriveVmTests.cs` (UI layer, machine
  ms.24's own duplicated test helper) — the same both-sides/no-op cases at the
  `DiskDriveVm.ReadDirectory()`-consumption level; `Spel1.dsk` now shows all 38 rows split
  correctly across the Side 1/Side 2 groups. Full `P2000.Machine.Tests`: 605/605 green (was 603);
  `P2000.UI.Tests`' `DiskDriveVmTests` (the directly-relevant subset): 51/51 green across 3
  repeated runs — the handful of intermittent failures elsewhere in that project's own full-suite
  run are the SAME pre-existing Avalonia headless `IFontManagerImpl`/dispatcher-timing environment
  flakiness already documented earlier this session (different tests fail each run, never in code
  this fix touches).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`DirectoryCylinder`,
  `DirectoryRawOffset`, `ReadDirectory`, `EnumerateAllDirectorySlots`, `EnumerateDirectorySlots`,
  `IsPlausibleJwsdosDirectory`, `IsDirectoryRegionBlank`), `src/P2000.UI/ViewModels/DiskDriveVm.cs`
  (stale comment only — the rendering loop itself needed no change),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`,
  `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs`. `docs/P2000T-disk-formats.md` §2 (on-disk
  layout table) and §7 items 2/2a (side-2 location — now answered — and the ex-"stale cluster,"
  now understood as side 1's genuine directory), `docs/P2000T-reference.md` §3a (UI milestone 15
  note, needs its "side 1 only" framing corrected).
- **Synced:** yes (2026-07-31, into `docs/P2000T-disk-formats.md` §2 (bug note updated to
  FIXED, with the 3-of-4-fixtures real-content discovery) and §7 items 2/2a (both closed as
  fixed); into `docs/P2000T-reference.md` §3a (UI milestone 15 note updated to FIXED)).

### 2026-07-31 — Follow-up: `DskImage.ReadDirectory()` only ever reads SIDE 2's directory — the "stale 20-entry cluster" was a real mislabeling, not stale data at all; the Floppy Drives window is missing half of `Spel1.dsk`'s real files
- **Trigger:** owner observation — `Spel1.dsk`'s real menu shows 37 files/options; this
  project's Floppy Drives window shows only 18, all reported as "Side 2." Asked whether this is
  a further consequence of the same offset-reinterpretation work. **It is — directly.**
- **Confirmed by parsing both real directory regions and checking every entry's own embedded
  `DE_head` byte (offset 24, a property of the STORED entry, independent of which region it's
  read from):**
  ```
  raw 0x2800-0x2FFF (dir_side1_prep's real target, confirmed 2026-07-31 above): 20 real,
    well-formed entries, EVERY ONE with DE_head=0.
  raw 0x3000-0x37FF (dir_side2_prep's real target, confirmed 2026-07-31 above): 18 real
    entries, EVERY ONE with DE_head=1.
  Combined: 38 files, zero filename overlap between the two lists.
  ```
  38 is very close to the owner's reported 37 (off by one — plausibly a boot/loader entry like
  `AUTORUN` not counted as a "file" by the real on-disk menu, or an off-by-one in either count;
  not investigated further, doesn't change the conclusion). **This settles it: the region at
  raw `0x2800`-`0x2FFF` is NOT stale/leftover data from a different disk — every one of its 20
  entries carries a self-consistent `DE_head=0`, exactly matching a genuine SIDE 1 directory,
  written by JWSDOS's own ordinary `save_directory`→`dir_side1_prep` path (the SAME routine
  pair that writes side 2's directory, just for the other side) — not by `JWS Systeem Disk`
  (confirmed separately today: that program never touches this region at all, per the write-
  scope entry immediately below). The original "stale cluster, zero overlap = leftover from
  another disk" theory from BEFORE this session's geometry work was a real mislabeling — it
  correctly observed the zero-overlap fact but drew the wrong conclusion from it. Zero overlap
  is exactly what a working double-sided disk's two INDEPENDENT per-side catalogs look like,
  not evidence of contamination from elsewhere.**
- **Root cause of the UI symptom, confirmed directly in source:** `DskImage.ReadDirectory()`/
  `EnumerateDirectorySlots()` (`Devices/Fdc/DskImage.cs`) reads from exactly ONE fixed absolute
  offset, `DirectoryOffset = 0x1800` — which is `dir_side2_prep`'s real target (side 2 only).
  There is no second read anywhere in this class for `dir_side1_prep`'s target. This is why
  every row the Floppy Drives window shows reports `Head=1`/"Side 2" (UI milestone 15's own Side
  column, `docs/P2000T-disk-formats.md` §4) — it's not that the Side column is wrong, it's that
  side 1's entire directory is never read at all, so no side-1 row can ever appear.
  **`DirectoryOffset`'s own value (`0x1800`) needs no change** — it was always a correct, fixed
  raw position (confirmed independently multiple times this investigation); what's missing is a
  SECOND fixed read at raw `0x2800`-`0x2FFF` (`dir_side1_prep`'s confirmed real target, same
  2048 B / 8-sector / 64-slot shape) alongside it.
- **Not yet fixed — this is the investigation only, per the owner's own "add to report" framing
  (not "please fix").** If a fix is wanted: add a second fixed-offset region
  (`DirectoryOffset2 = 0x2800`, same size/slot-count/emptiness rules as the existing one) to
  `EnumerateDirectorySlots()`/`ReadDirectory()`, and expose both sides' entries — the existing
  `DiskDirectoryEntry.Head` field already round-trips which side each entry belongs to (per its
  own embedded byte), so the UI's existing Side column needs no change once both sides are
  actually read; only the machine-layer read needs extending. Whether side 1's entries should be
  interleaved with side 2's in on-disk/read order, or listed side-1-then-side-2, is a
  presentation choice, not something this investigation resolves.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`ReadDirectory`,
  `EnumerateDirectorySlots`, `DirectoryOffset`), `docs/P2000T-disk-formats.md` §2 (the "stale
  cluster" framing needs replacing with "side 1's own genuine directory") and §4/§7 item 3,
  `P2000.UI`'s Disk Drives window (milestone 15, consumes `ReadDirectory()` — currently
  structurally incapable of showing a side-1 row regardless of anything UI-side, since the
  machine layer never returns one).
- **Synced:** yes (2026-07-31, into `docs/P2000T-reference.md` §3a -- new bullet under UI
  milestone 15 flagging the missing side-1 read; into `docs/P2000T-disk-formats.md` §2 (new
  "FINAL CORRECTED PICTURE" block, "duplicate content" characterization) and §7 item 2a (new)).

### 2026-07-31 — Follow-up: item 4 (`JWS Systeem Disk` write-scope claim) closed — owner supplied `docs/jwssysdisk.asm`, a real disassembly of the writer program itself
- **Trigger:** the audit entry immediately below flagged item 4 as unverifiable — no disassembly
  of `JWS Systeem Disk` (as opposed to `jwsdos5.0.asm`, the resident DOS, a different program)
  existed in this repo. The owner has since supplied `docs/jwssysdisk.asm` (a partial disassembly
  of `JWSsysdiskwriter.bin`, per its own header comment), closing this out the same way items 1-3
  were settled: direct execution against the real `Upd765`/`DskImage` code, not more arithmetic.
- **The §7 item 3 write-scope CLAIM itself is CONFIRMED CORRECT, straight from source.**
  `Write_JWSDos`'s `write_track_loop` (`docs/jwssysdisk.asm:62-95`): for track 1 (1-based),
  `E=16` (all 16 sectors); for track 2, `E=8` (only 8 sectors) — both starting at sector 1
  (`dsk_transfer_cmd_sec` is unconditionally set to `1` at the top of every loop iteration,
  never anything else). Confirmed via a replayed write against a real `DskImage`: the resulting
  image has all 16 sectors of "track 1" written and only the FIRST 8 sectors of "track 2"
  written — the second 8 sectors are left completely untouched (still zero on the blank test
  image), exactly matching the original claim.
- **But the RAW OFFSET this write-scope claim resolves to has changed, exactly as flagged as a
  possibility — confirmed via direct execution, not arithmetic:** `dsk_transfer_cmd_head`
  (`0x6076`, the same monitor-ROM `disk_side` cell used everywhere else in this investigation)
  is NEVER referenced anywhere in `jwssysdisk.asm` — it isn't even in the file's own equates
  list. It's left exactly as the initial 24-byte `ldir` from the ROM's own `disk_constants`
  template set it (`0x00`) and never touched again — **this writer program is single-sided in
  its own behaviour, always head 0, regardless of which SS/DS choice the user makes at its
  `Get_SideCount` prompt** (that prompt only stores a label byte for later reference, at
  `0x79EF`, not a different write path). Combined with the same SEEK mechanism already confirmed
  (`MON_DSK_gotrack`, 1-based track minus 1 = 0-based cylinder): under the corrected
  cylinder-major formula, "track 2 sectors 1-8" resolves to **raw `0x2000`-`0x27FF`**
  (cylinder 1/head 0) — NOT raw `0x1000`-`0x17FF`, where the old side-major-based understanding
  would have placed it.
  - **This lands EXACTLY on the same physical region already identified 2026-07-30 as `getdos`'s
    real second-boot-track-read target** (the real DOS code, `3E 0C CD 4A 10 CD E1 1A...`) — a
    clean, mutually-reinforcing confirmation from a completely independent program (the writer)
    landing on the same block the reader (`getdos`) targets.
  - **And it's directly, physically adjacent to — but never overlapping — `dir_side1_prep`'s own
    real target** (raw `0x2800`-`0x2FFF`, sectors 9-16 of the SAME cylinder/head, confirmed
    earlier this same day): this writer's own 8-sector limit is exactly why that region holds
    LEFTOVER/stale directory content instead of anything `JWS Systeem Disk` itself put there —
    sectors 9-16 of cylinder 1/head 0 are simply outside this program's write path, full stop,
    confirming (not just repeating) the format doc's own existing "sectors 9-16 are entirely
    outside this program's write path" framing — now anchored to the correct physical location.
  - **Verified via direct execution:** replayed `write_track_loop`'s exact command sequence
    (SEEK cylinder 0 → WRITE 16 sectors head 0; SEEK cylinder 1 → WRITE 8 sectors head 0) against
    a real blank `DskImage`, with distinguishable markers per track. Confirmed: raw
    `0x0000`-`0x0FFF` (track 1) and `0x2000`-`0x27FF` (track 2, sectors 1-8) hold the markers;
    raw `0x1000`-`0x1FFF` (cylinder 0/head 1), `0x2800`-`0x2FFF` (cylinder 1/head 0, sectors
    9-16), and `0x3000`-`0x3FFF` (cylinder 1/head 1) are all untouched — matching every claim
    above exactly.
- **No emulator bug, no fix — this closes item 4 as a documentation-only correction**, same
  shape as items 2/3 below: the underlying claim was right, its raw-offset interpretation needed
  updating for the corrected formula.
- **Applies to:** `docs/P2000T-disk-formats.md` §7 item 3 (write-scope claim — now fully
  re-verified against real source, raw offset corrected from `0x1000`-`0x17FF` to
  `0x2000`-`0x27FF`), §2 (on-disk layout table, same correction). No source files changed.
- **Synced:** yes (2026-07-31, into `docs/P2000T-disk-formats.md` §2 (on-disk layout table
  corrected, write-scope raw offset corrected to 0x2000-0x27FF) and §7 item 3 (rewritten,
  "stale cluster" theory retired)).

### 2026-07-31 — Audit: JWSDOS directory/geometry conclusions re-derived under the corrected cylinder-major formula, plus a head-selection encoding check — one real prior mislabeling found and corrected, no emulator bug found
- **Trigger:** owner follow-up to the 2026-07-30 `SectorOffset` fix — several of this project's
  own conclusions about `Spel1.dsk`'s directory layout (which routine reads what, and where)
  were derived using the OLD, disproven side-major formula, or from disassembly arithmetic
  alone. Audit request, not a presumed bug. All four items below were settled by driving the
  real `Upd765`/`DskImage` code directly against real `Spel1.dsk` bytes (temporary scratch
  tests, removed after use) — not by more disassembly-only reasoning, per the prompt's own
  instruction (arithmetic-only reasoning is exactly what produced the geometry bug's own false
  start the same day).
- **Item 1 — the drive-byte-bit-2-for-head convention: CHECKED DIRECTLY, no bug.**
  `jwsdos5.0.asm`'s `execute_disk_IO` sets an explicit head byte (`dsk_transfer_cmd_head`,
  `0x6076`) AND separately XORs the transfer command's own drive byte
  (`dsk_transfer_cmd_drive`, `0x6074`) by `0x04` for head 1 — but SEEK/RECALL always use the
  plain, un-XORed drive number. Traced these RAM addresses against `Symbols.asm` and confirmed
  they are the EXACT SAME cells the monitor ROM's own `getdos`/`Disk.asm` command template uses
  (`disk_drive_num=0x6074`, `disk_track_num=0x6075`, `disk_side=0x6076`, `disk_sector_num=
  0x6077`) — JWSDOS literally reuses the ROM's own command buffer, not a separate one, and
  routes actual FDC command issuance through the SAME monitor-ROM entry points (`MON_DSK_gotrack`
  = `0x0F7D` = `Disk.asm`'s `disk_gotrack`, already fully traced in the 2026-07-30 investigation).
  `Upd765.DispatchDataCommand` (`Devices/Fdc/Upd765.cs:645,658`) reads `drive` from
  `_commandBuffer[1] & 0x03` (masks bit 2 away entirely) and `head` from the SEPARATE, explicit
  `_commandBuffer[3] & 0x01` (the real H field) — so JWSDOS's bit-2-of-drive XOR is completely
  inert for this emulator's dispatch: it gets masked away regardless of whether it was applied,
  and the real head selection comes from the explicit H byte, which JWSDOS sets correctly and
  independently of the XOR either way. `DispatchSeek`/`DispatchRecalibrate` mask the same way
  (`_commandBuffer[1] & 0x03`), so `_cylinder[drive]` is indexed identically by SEEK and by the
  following transfer regardless of head — no cylinder-tracking mismatch, contrary to the
  hypothesis raised in the prompt. **No bug, no fix needed.**
- **Item 2 — `dir_side1_prep`'s real target, traced via direct FDC execution against real
  `Spel1.dsk`: raw `0x2800`, NOT `0x1800`.** Replayed the exact command bytes `dir_side1_prep`
  issues (SEEK to cylinder 1 via the shared `disk_gotrack`/1-based-track-minus-1 mechanism
  already confirmed 2026-07-30; then a TRUE READ DATA transfer — found along the way that
  JWSDOS's own read opcode is `0x46`/`FDC_mode_read`, confirmed from `jwsdos5.0.asm:136-137`,
  NOT `0x42`/READ A TRACK the way `getdos` uses — JWSDOS needs `R` honoured to start mid-track at
  sector 9, which READ A TRACK's "ignore R" behaviour would break) directly against a `DskImage`
  built from the real file. Result: byte-for-byte identical to raw `0x2800`, the STALE 20-entry
  cluster ("Fraxxon + scores..."), NOT the active 18-entry directory ("Tralieenspel"/"BABA") at
  `0x1800`. **This corrects the 2026-07-30 first-pass doc correction, which (done from
  disassembly alone, explicitly flagged as leaving "which routine" open) had assumed
  `dir_side1_prep` was the routine landing on the active directory.** It isn't — it's the stale
  cluster.
- **Item 3 — `dir_side2_prep` IS the routine that reads the active directory, confirmed at
  raw `0x3000` (cylinder 1/head 1), and the `Spel1.dsk` "duplicate content" oddity is CONFIRMED
  real, not coincidental.** Same direct-execution method: `dir_side2_prep`'s exact command
  (same SEEK target — cylinder selection is head-agnostic — then READ DATA with `H=1`, `R=1`,
  drive byte `0x05` i.e. drive 1 XOR'd by 4, confirming the XOR really is inert per item 1) landed
  on raw `0x3000`, matching the active directory byte-for-byte. **This is the first
  byte-confirmed answer to `docs/P2000T-disk-formats.md` §7 item 2 ("where does side 2's
  directory live") — cylinder 1/head 1, raw `0x3000`-`0x37FF`** — superseding the arithmetic-only
  "strong candidate" status it's carried until now.
  - **The duplicate-content puzzle is now precisely characterized (not solved — the owner is
    still thinking about the WHY, deliberately not chased further here):** raw `0x1000`-`0x1FFF`
    (cylinder 0/head 1, "the flip side of the boot track") is NOT a duplicate of one clean 4 KB
    block — it's a duplicate of TWO DIFFERENT sector ranges stitched together: its first half
    (`0x1000`-`0x17FF`) is an EXACT byte-for-byte match of `dir_side1_prep`'s real target's full
    8-sector span (`0x2800`-`0x2FFF`, the stale cluster); its second half (`0x1800`-`0x1FFF`) is
    a 2047-of-2048-byte match of `dir_side2_prep`'s real target's full 8-sector span
    (`0x3000`-`0x37FF`, the active directory) — the ONE differing byte sits at directory-entry
    offset 22 (the transfer-address field, format doc §4) of one specific entry, itself
    consistent with the doc's own existing "stale RAM snapshot at write time" theory rather than
    a new anomaly. Also confirmed while tracing this: cylinder 1/head 0's OWN first half
    (`0x2000`-`0x27FF`, sectors 1-8, never read by any prep routine) is the real DOS-code content
    already identified 2026-07-30 as matching `getdos`'s second boot-track read target; cylinder
    1/head 1's own second half (`0x3800`-`0x3FFF`) is genuinely blank (all-zero) — consistent with
    only 8 of that region's 16 sectors ever being written.
- **Item 4 — the `JWS Systeem Disk` write-scope claim (format doc §7 item 3): UNVERIFIABLE with
  what's available in this repo, not re-derived — flagged, not guessed.** Searched
  `docs/Monitor Documented Disassembly/` (contains `Cassette.asm`/`Disk.asm`/`P2000ROM.asm`/
  `Printer.asm`/`Startup.asm`/`Symbols.asm` — the monitor ROM only) and grepped the whole `docs/`
  tree for "systeem" — no disassembly of the `JWS Systeem Disk` PROGRAM itself (as opposed to
  `jwsdos5.0.asm`, the resident DOS, a different program) exists in this repo. `docs/JWS.pdf`
  exists (10 pages) but could not be rendered in this environment (`pdftoppm` unavailable); by
  filename and page count it reads far more likely as a scanned manual than a disassembly
  listing, consistent with every other disassembly in this project being supplied as plain
  `.asm` text rather than PDF — but this is not confirmed either way. The §7 item 3 write-scope
  claim (`JWS Systeem Disk` writes a full track 1 plus only sectors 1-8 of track 2) still rests
  entirely on the owner's own 2026-07-20 disassembly pass, done before the geometry fix, and
  remains **not yet re-verified** against the corrected formula.
- **Summary for the human's sync pass:** items 2 and 3 together mean `docs/P2000T-disk-
  formats.md` §2's on-disk-layout table and §7 items 2-3 need a real content update (which
  routine reads which raw range, and cylinder 1/head 1 as side 2's confirmed directory location),
  not just the "raw offsets unchanged, CHS labels flip" framing the first-pass correction used.
  Item 4 stays an open item, now explicitly re-flagged as unverified-post-fix rather than
  silently assumed still valid.
- **Applies to:** `docs/P2000T-disk-formats.md` §2 (on-disk layout table, `dir_side1_prep`/
  `dir_side2_prep` identification), §7 items 2 (side 2 directory location — now answered) and 3
  (write-scope claim — flagged unverified), `docs/P2000T-reference.md` §5d. No source files
  changed — this is an audit with no code fix; `src/P2000.Machine/Devices/Fdc/Upd765.cs`
  (`DispatchDataCommand`/`DispatchSeek`/`DispatchRecalibrate`) confirmed correct as-is for item 1.
- **Synced:** yes (2026-07-31, into `docs/P2000T-disk-formats.md` §2 (FINAL CORRECTED
  PICTURE block, DE_head tension fully resolved) and §7 items 2/2a/3 (rewritten); into
  `docs/P2000T-reference.md` §3a (UI milestone 15 bug note) and §5d (follow-up audit
  paragraph correcting the prior "duplicate content at 0x2800/0x3000" mislocation)).

### 2026-07-30 — CORRECTION (supersedes the entry immediately below): disk-image raw layout FIXED to cylinder-major/head-minor — the entry below's "no bug" conclusion was wrong
- **This entry OVERTURNS the "geometry mapping vs. getdos's fixed-side-0 read" entry immediately
  below.** That entry concluded the side-major formula was correct, based on a same-day, real
  ROM-driven execution test matching raw offset `0x1000` exactly. That match was real but
  **circular**: it only proved the emulator consistently reads from wherever its OWN formula
  points, not that the formula points to the historically/hardware-correct location. It never
  independently validated `0x1000` as correct.
- **Owner-provided, independent ground truth broke the circularity:** two reference files
  (`assets/JWSDosSpel1disk.bin`, then a corrected `assets/JWS.bin` — a clean, known-good JWSDOS
  binary not derived from this project's own disk fixtures or its own formula) were compared
  directly against raw bytes in `assets/Disks/jws-sytem.dsk` and `Spel1.dsk`. Both real disks'
  content for what should be `getdos`'s SECOND track — the confirmed clean reference's own
  `0x1000-0x113F` region — is byte-for-byte identical to raw disk offset `0x2000-0x213F`, NOT
  `0x1000` (blank on `jws-sytem.dsk`; unrelated directory data on `Spel1.dsk`). The clean
  reference's first ~292 bytes also matched raw `0x0000` on both disks exactly, confirming the
  alignment (reference byte 0 = raw disk offset 0 = memory `0xE000`) before the divergence at
  `0x2000`.
- **Owner's own direct authority on the monitor ROM sealed it:** `getdos` loads exactly two
  PHYSICAL CYLINDERS, both head 0 (the monitor ROM has NO double-sided support at all) — cylinder
  0/head 0 then cylinder 1/head 0. Under the disk's real raw layout, cylinder 0/head 0 = raw
  `0x0000` (agreed by everyone, always was) and cylinder 1/head 0 = raw `0x2000` — the owner's
  own hex-editor inspection of `jws-sytem.dsk`, a genuine, real, working JWS boot-disk image (not
  a utility/template disk — confirmed directly by the owner, ruling out that alternative theory).
- **Fixed:** `DskImage.SectorOffset` (`Devices/Fdc/DskImage.cs`) changed from side-major/
  cylinder-minor (`head * Tracks * BytesPerTrack + cylinder * BytesPerTrack + ...`) to
  **cylinder-major/head-minor** (`cylinder * Sides * BytesPerTrack + head * BytesPerTrack +
  (sector-1) * BytesPerSector`) — a cylinder's heads are stored back-to-back before the next
  cylinder begins, not all of side 0's cylinders before side 1 starts. `ImdFormat.Read`/`Write`
  (`Devices/Fdc/ImdFormat.cs`) independently duplicated the OLD formula in two places (never
  routed through `DskImage.SectorOffset`) — both updated to match. Fixed absolute-offset
  constants (`DirectoryOffset=0x1800`, `PdosFcbOffset=0x0000`, `SideIndicatorOffset=0x0FEF`,
  `TrackCountOffset=0x0FFF`) needed NO value changes — they're literal raw byte positions, not
  derived via the CHS formula — but `DirectoryOffset`'s doc comment was corrected: under the new
  layout, raw `0x1800` is (cylinder 0, head 1, sector 9), not (cylinder 1, head 0, sector 9) as
  previously assumed. This also resolves an unrelated, previously-flagged tension in
  `docs/P2000T-disk-formats.md` §2 (the active directory's own entries carry a confirmed
  `DE_head=1` byte, which never fit the old "cylinder 1, head 0" reading — it fits the corrected
  "head 1" reading cleanly). Which specific JWSDOS routine (`dir_side1_prep` vs.
  `dir_side2_prep`) targets this region is a separate, still-open question, not re-verified here.
- **Single-sided images are unaffected** — when `Sides == 1`, cylinder-major and side-major
  formulas are numerically identical (the head term multiplies by 0 for the only head), so this
  fix only changes behavior for double-sided images.
- **Tests updated (5 existing tests broke and were fixed, not weakened):**
  `DskImageTests`/`RealFixtureTests`/`Upd765Tests`/`DiskBootTests` — every test that hardcoded a
  raw offset under the old formula (synthetic marker placement, `Spel1.dsk`/`jws-sytem.dsk`
  raw-byte comparisons, and critically the REAL ROM-driven `DiskBootTests.
  GetDos_LoadsBothTracksByteIdentical_FromRealJwsdosImage` integration test) now targets `0x2000`
  instead of `0x1000` for cylinder 1/head 0, and `0x2800` instead of `0x1800` for cylinder 1/head
  0/sector 9. The real-fixture regression pinning the CHS→offset identity was moved off
  `Spel1.dsk` (whose own `0x2800` region has an unresolved, separately-flagged "duplicate
  content" oddity the owner is still investigating — deliberately not built on top of it) onto
  `jws-sytem.dsk`'s clean, unambiguous real-code match at `0x2000` instead. Full
  `P2000.Machine.Tests`: 604/604 green; `P2000.UI.Tests` disk/config-related suites (`DiskDriveVm`/
  `ConfigWindow`, 71/71) unaffected — the 3 unrelated failures elsewhere in that project's own
  suite are pre-existing Avalonia headless-rendering environment issues (`IFontManagerImpl`
  unavailable), not caused by this change.
- **CONFIRMED against real hardware-equivalent usage (owner, 2026-07-30, same day): "just tested
  and JWS Dos boots perfectly now."** This closes out the original JWSDOS-activation bug
  (reference doc §5d) end to end — the third and final hypothesis investigated for it (after the
  checksum hypothesis and the port-0x94-0x97-aliasing hypothesis, both independently disproven
  earlier the same day) was the real root cause. Everything up to this point in the entry was
  verified via test fixtures and byte-level analysis only; this is the first confirmation via an
  actual observed boot.
- **Still open, deliberately deferred (owner's own call, 2026-07-30):** the "duplicate content"
  puzzle in `Spel1.dsk` (the same directory-shaped bytes appearing a second time, shifted by
  exactly `0x1800`, at raw `0x2800`/`0x3000`) — owner theory is stale data from the JWS system
  tool or the disk-dumping process, not investigated further this pass. Unrelated to the fix
  above and does not affect JWSDOS booting correctly, per the confirmation immediately above.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`SectorOffset`, class doc comment,
  `DirectoryOffset` doc comment), `src/P2000.Machine/Devices/Fdc/ImdFormat.cs` (`Read`/`Write`
  offset computation, class doc comment), `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Boot/DiskBootTests.cs`. Reference doc §5d's disk-geometry block and
  `docs/P2000T-disk-formats.md` §2's "Generalized raw sector-offset formula" (needs re-deriving
  from cylinder-major, not side-major) and §7 item 9.
- **Synced:** yes (2026-07-30, into `docs/P2000T-reference.md` §5d — bug entry re-headered
  RESOLVED AND FIXED with the full false-start-then-correction narrative appended; into
  `docs/P2000T-disk-formats.md` §2 — sector-offset formula corrected to cylinder-major/head-minor,
  with the DE_head tension re-resolved and item 2/item 9 updated to match).

### 2026-07-30 — SUPERSEDED BY THE CORRECTION ABOVE — Bugfix investigation: disk-image geometry mapping vs. getdos's fixed-side-0 read — mapping and FDC dispatch BOTH confirmed correct; likely non-bug explanation for the observed symptom found instead
- **Trigger:** a third JWSDOS-activation hypothesis, opened by the owner's own new empirical
  test using the per-bank debugger (milestone 24): cold-start boot (JWSDOS disk in drive 1,
  ordinary BASIC cartridge in slot 1) shows bank 1 selected twice during a genuine two-track
  disk-boot read; `0xE000`-`0xEFFF` correctly holds JWSDOS's image, but `0xF000`-`0xFFFF` is
  entirely zero. The owner's theory: `getdos`'s own command bytes request (cylinder, side 0)
  for BOTH track reads — only the cylinder advances via a separate seek, the side never does —
  and if this disk's actual system image spans both PHYSICAL SIDES of one cylinder (under a
  "cylinder-major, side-minor" real-disk convention) rather than two side-0 cylinders,
  `getdos` as literally written could never load it correctly. Two things needed checking
  against the real C#: (1) does `DskImage`'s (cylinder,head,sector)→offset formula actually
  match that stated real-disk convention; (2) does the FDC honor the command's side byte
  literally.
- **(1) Geometry mapping — checked directly, does NOT match the stated convention, and the
  code's existing formula is independently confirmed correct instead.** `DskImage.SectorOffset`
  (`Devices/Fdc/DskImage.cs:295-296`) is `head * Tracks * BytesPerTrack + cylinder *
  BytesPerTrack + (sector-1) * BytesPerSector` — **side-major, cylinder-minor** (all of side
  0's cylinders contiguous, then side 1's), not cylinder-major/side-minor. This is not a new
  finding in isolation (the class's own doc comment and `docs/P2000T-disk-formats.md` §2 already
  documented and cross-validated it against four real disk images back on 2026-07-22) — what's
  new here is checking it specifically against the owner's freshly-stated alternative
  convention, which turns out to conflict with it. **Verified independently, not by re-reading
  the doc's claim:** directly searched the real `Spel1.dsk` fixture's raw bytes for known
  directory filenames — `"Fraxxon"` (the stale directory cluster, confirmed `docs/P2000T-
  disk-formats.md` content) is at raw file offset `0x1000` exactly, and `"Tralieen"`/`"BABA"`
  (the active directory) at `0x1800` exactly. Under side-major (cylinder=1,head=0,sector=1/9)
  these ARE `0x1000`/`0x1800`. Under the owner's cylinder-major convention, the same
  (cylinder=1,head=0) location would instead be `0x2000`/`0x2800` — and those offsets hold none
  of this data. **The owner's stated real-disk convention does not hold for this project's
  actual `.dsk` fixtures; the existing side-major implementation is the one that's empirically
  correct**, not a bug to fix. (Raw sector-dump tools for 8-bit disk formats commonly ARE
  cylinder-major/side-minor — that's the more common convention in general — but it is
  demonstrably not what these specific JWSDOS-era `.dsk` files use.)
- **(2) FDC command handling — checked directly, honors the command literally, exactly as
  designed.** `Upd765.DispatchDataCommand` (`Devices/Fdc/Upd765.cs:643-705`) reads `head` from
  `_commandBuffer[3] & 0x01` — the command's own explicit H byte, read fresh on every dispatch,
  never cached or derived from other state — while `cylinder` comes from `_cylinder[drive]`,
  the chip's own internally-tracked physical head position (updated ONLY by
  `DispatchSeek`/`DispatchRecalibrate` → `BeginSeek`, both of which take a `drive` and a
  `targetCylinder` and nothing else — no head parameter exists anywhere in the seek path, so
  seeking genuinely cannot have a side effect on head selection). This is independently correct
  behavior, not merely "happens to match" — it mirrors real µPD765 hardware exactly (the C byte
  is for ID-field verification against physical media, not addressing; the chip reads/writes
  wherever the head physically is). Cross-checked against the real command bytes in
  `docs/Monitor Documented Disassembly/Disk.asm`'s `disk_constants` table (now available in
  full): the "Disk IO" template's drive/head byte (`0x01`, head-bit clear) and its explicit H
  byte (`0x00`) are copied to RAM ONCE and never rewritten across the two-track loop — only
  `disk_track_num` changes, driving the separate "Goto Track" search command (opcode+drive+
  track only, confirmed no head field in the transmitted bytes either). So the literal
  sequence really is (cylinder=0,head=0) then (cylinder=1,head=0), and the emulator delivers
  exactly that.
- **Combined conclusion: no bug in either the geometry mapping or the FDC dispatch — both
  independently checked and both correct.** Per the prompt's own instruction, no fix landed;
  none was needed.
- **A materially simpler, non-bug explanation for the observed symptom, found while cross-
  checking the geometry claim against real fixtures — not yet confirmed against the owner's
  actual repro disk, but directly relevant:** this project already has two real fixtures most
  plausibly matching "a JWSDOS boot disk" — `assets/Disks/jws-sytem.dsk` and `assets/Disks/
  empty-jws.dsk` — and both were already independently confirmed (`docs/P2000T-disk-
  formats.md`'s own 2026-07-13 provenance entry, re-verified directly here by reading the raw
  bytes again) to have their entire raw `0x1000`-`0x1FFF` region (cylinder 1/"track 2", head 0
  — exactly `getdos`'s second read target) **all zero**, while track 1 (`0x0000`-`0x0FFF`)
  starts with the same confirmed JWSDOS boot code (`0x20` = `JR NZ`, matching the owner's own
  "0xE000-0xEFFF correctly holds JWSDOS's image" observation). **If either of these is the disk
  the owner actually mounted for the cold-start repro, "bank 1's 0xF000-0xFFFF ends up all
  zero" is not a symptom of anything going wrong — it's `getdos` correctly and faithfully
  reading this specific disk's genuinely blank second track.** This doesn't resolve the
  underlying "why does the machine end up back at BASIC" question (all-zero RAM there is
  itself just an unbroken `NOP` run to `PC` wraparound, a real and separate consequence,
  already correctly identified in the reference doc entry as mechanically different from the
  two previously-disproven hypotheses) — but it means the ROOT CAUSE, if this is indeed the
  disk in play, is squarely "this disk's own content doesn't have what `getdos`'s fixed read
  pattern expects," not an emulator defect anywhere in the read path. **Recommended next
  step, cheap to check:** confirm which specific `.dsk` file was mounted for this exact repro
  and whether its raw `0x1000`-`0x1FFF` is genuinely blank (matching these two fixtures) or
  holds real data at a DIFFERENT offset than `getdos` reads.
- **Secondary question (per the prompt's own "don't spend much time" scoping) — checked
  quickly, no discrepancy found.** `Startup.asm`'s disk-boot gate (`docs/Monitor Documented
  Disassembly/Startup.asm:574-595`) is unchanged from what reference doc §5d already
  documents: `getdos` only runs when `memsize==3` AND a cartridge is present (header byte at
  `0x1000`, bit 0 clear) AND that header's bit 1 ("needs DOS") is set. If `getdos` genuinely
  ran for "an ordinary BASIC cartridge" in this repro, that specific cartridge's own header
  byte must have bit 1 set — i.e. it's a DOS-requesting BASIC cartridge (e.g. a "Disk BASIC"
  variant), not a cartridge lacking that flag entirely. No correction needed to the reference
  doc's existing claim; just a note that "ordinary BASIC cartridge" in the new repro's own
  description is doing double duty and is worth being precise about.
- **Tests added (even though no bug was found, confirming this exact behavior needed
  independent pinning against real ground truth, not just self-consistency):**
  `DskImageTests.ReadSector_Cylinder1Head0Sector9_ReadsFromRawFileOffset0x1800_
  NotCylinderMajorOffset0x2800` builds a raw byte array directly (bypassing `DskImage`'s own
  write path) and confirms `ReadSector` reads from the side-major offset, not the
  cylinder-major candidate. `RealFixtureTests.Spel1Dsk_ReadSectorCylinder1Head0Sector9_
  MatchesRawFileBytesAtOffset0x1800` cross-checks the same identity against the real `Spel1.dsk`
  file's own raw bytes (read independently via `File.ReadAllBytes`, not through `DskImage`).
  `Upd765Tests.GetdosTwoTrackSequence_BothReadsUseHead0Literally_CylinderAdvancesViaSeekOnly`
  replays `getdos`'s exact real command bytes (both the "Disk IO" read template and the "Goto
  Track" search template) against three distinct markers at (cyl0,head0)/(cyl1,head0)/
  (cyl1,head1), confirming both reads land on head 0 and the cylinder advances via SEEK alone —
  with an explicit `NotEqual` against the head-1 marker so a regression that ever drifted
  "current side" would be caught, not silently pass by coincidence. Full
  `P2000.Machine.Tests`: 603/603 green (was 600).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`SectorOffset` — unchanged,
  confirmed correct), `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`DispatchDataCommand`,
  `BeginSeek`/`DispatchSeek`/`DispatchRecalibrate` — unchanged, confirmed correct),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`. Reference doc §5d's "Re-reading
  `getdos` precisely" paragraph and `docs/P2000T-disk-formats.md` §7 item 9 — this closes both
  of that entry's own "still need checking" items with a negative result on the bug hypothesis,
  plus the jws-sytem.dsk/empty-jws.dsk all-zero-track-2 alternative explanation for the human to
  weigh.
- **Synced:** yes (2026-07-30, kept as historical record — into `docs/P2000T-reference.md` §5d
  and `docs/P2000T-disk-formats.md` §7 item 9, as the "false start, same day" paragraph explaining
  how this conclusion was reached and then overturned by the CORRECTION entry above).

### 2026-07-30 — Bugfix investigation: does the bank-switch device over-listen on ports 0x94-0x97? NO — hypothesis disproven by direct source read; no fix landed
- **Trigger:** the JWSDOS-activation bug's "Revised bit-level read" paragraph (reference doc
  §5d — see the entry immediately below for the fuller mechanism trace) flagged this project's
  own bank-switch device as the prime remaining suspect once the checksum was ruled out by
  direct experiment: if it responded to `0x95`-`0x97` (the M2200 RAM-disk ports
  `init_ramdisk` probes) as well as `0x94`, an unintended bank switch mid-probe could derail
  execution out of bank 1 while still physically inside `0xE000`-`0xFFFF`.
- **Checked directly against the actual C# (not re-derived from the disassembly, per the
  prompt's own instruction) — hypothesis does NOT hold, on three independent counts:**
  1. **`Io/PortDispatch.cs` cannot represent a port RANGE at all — it's structurally a
     single-port-only dispatcher**, not a coincidentally-narrow one: `_writeListeners`/
     `_readSources` are each a fixed 256-entry array indexed by the exact port byte;
     `RegisterWrite(byte port, ...)`/`RegisterRead(byte port, ...)` both take one literal
     `byte`, with no range/mask overload anywhere in the type. Grepped every
     `RegisterWrite`/`RegisterRead` call site in `src/` (`Machine.cs`,
     `InternalExtensionBoard.cs`) — all seven registrations (keyboard 0x00-0x09 via an
     explicit per-port loop, CPOUT 0x10, CPRIN 0x20 ×2, sound 0x50, bank-select 0x94, CTC
     0x88-0x8B via an explicit per-channel loop, FDC 0x8C/0x8D/0x90) are single exact-byte
     registrations — none of them, anywhere in this project's I/O dispatch, register a range.
     So this isn't just "the bank-switch device happens to listen on 0x94 only" — no device in
     this codebase COULD be registered against a range even if someone tried; over-wide
     registration is not a bug shape this architecture can currently produce.
  2. **`Machine.cs:163`** — `Ports.RegisterWrite(PageTable.BankSelectPort, Memory.SelectBank)`
     — `PageTable.BankSelectPort` is the literal `0x94`. Exactly one write listener on exactly
     one port.
  3. **`PageTable.SelectBank`/`ReadBank`/`WriteBank`** (`Memory/PageTable.cs:123-127,244-253`)
     match the documented homebrew/T-102-class behavior exactly: `SelectBank` stores the raw
     unmasked byte (`_bankIndex = index`), and `ReadBank`/`WriteBank` gate on
     `_bankIndex < _banks.Length` — an index at or beyond the populated bank count reads open
     bus / discards the write, never masked or wrapped into range. Confirmed against source,
     not assumed from the doc comment.
  4. **Ports `0x95`/`0x96`/`0x97` have ZERO registered listeners** (confirmed by the same
     grep as #1 above) — nothing in this project wires anything there (the M2200 RAM-disk
     feature is deferred, project CLAUDE.md §14). `PortDispatch.Write` returns immediately
     when `_writeListeners[port]` is `null` (a genuine no-op, not a silently-absorbed write);
     `PortDispatch.Read` returns `OpenBus` (0xFF) when `_readSources[port]` is `null`/empty —
     the same open-bus convention used everywhere else in this project.
- **Traced the actual JWSDOS execution against that confirmed behavior (not just the code in
  isolation) — the probe fails safely, exactly as designed, with zero side effects:**
  `init_ramdisk` (`docs/jwsdos5.0.asm:2828-2859`) writes to `ramdisk_Track`/`ramdisk_Sector`
  (`0x95`/`0x96`, both genuine no-ops here) then does `in a,(c)` on `ramdisk_IO` (`0x97`,
  which returns open-bus `0xFF`) at line 2842, comparing that against `17` and `65` (lines
  2856/2858) — neither matches, so `ret nz` at line 2859 returns immediately, well BEFORE the
  signature check or the `otir` directory-erase block ever run. No bank-select write, no
  stray I/O, no PC corruption — the RAM-disk probe is inert on this emulator by construction,
  same "genuine silence" shape as every other presence-probe in this codebase.
- **Conclusion (per the prompt's own item 5 — reporting plainly, not forcing a fix): this
  specific hypothesis is DISPROVEN, not just unconfirmed.** No fix landed because there was
  nothing to fix — the bank-switch device listens on `0x94` only, applies the documented
  range-check (not mask/wrap) behavior, and `0x95`-`0x97` are genuinely inert. **No parallel
  mechanism of the same shape exists elsewhere in the I/O dispatch either** — every
  registration in the project is single-exact-port (enumerated above), so there's no second
  over-wide-listener candidate to chase.
- **Where this leaves the JWSDOS-activation bug (still open, root cause elsewhere):** since
  the ramdisk-probe-derails-bank-1 mechanism is ruled out, whatever produces the "lands back
  at BASIC" symptom is NOT in this project's port/bank-select dispatch. The reference doc
  entry's own remaining open question — what actually loads JWSDOS's binary into bank 1 at
  `0xE000` in the first place when booting from a plain BASIC cartridge with no `getdos`
  auto-boot — is the more promising next thread; that boot-loader code hasn't been
  disassembled/supplied yet, so it can't be checked against this project's implementation the
  way this specific hypothesis was.
- **Tests (added even though no bug was found, since this exact behavior was previously
  uncovered and is directly load-bearing for the ruled-out hypothesis above):**
  `MachineTests.cs` (+7): `Tick_OutToPort95To97_DoesNotAffectTheActiveBank_OnT102Card` (3
  cases, one per port) and `..._OnHomebrewCard` (3 cases, `BankCount = 3`) confirm an `OUT` to
  each of `0x95`/`0x96`/`0x97` leaves the live-active bank untouched, on both the atomic
  floppy+RAM board's 6-bank shape and an explicit homebrew `BankCount` override;
  `Tick_OutTo0x94_StillWorksExactlyAsBefore_AlongsideInertPorts95To97` confirms `0x94` still
  works correctly even after `OUT`s to the three inert ports immediately precede it (regression
  guard that the inertness isn't accidentally achieved by breaking `0x94` itself). Full
  `P2000.Machine.Tests`: 600/600 green (was 593).
- **Applies to:** `src/P2000.Machine/Io/PortDispatch.cs`,
  `src/P2000.Machine/Memory/PageTable.cs` (`SelectBank`/`ReadBank`/`WriteBank`,
  `BankSelectPort`), `src/P2000.Machine/Machine.cs:163`,
  `tests/P2000.Machine.Tests/MachineTests.cs`. Reference doc §5d's "Revised bit-level read"
  paragraph — this closes that paragraph's own "recommended next step" (have the actual C#
  read) with a negative result.
- **Synced:** yes (2026-07-30, into `docs/P2000T-reference.md` §5d — new "PORT-ALIASING
  HYPOTHESIS DISPROVEN" paragraph replacing the "Revised bit-level read" section's open
  recommendation, plus a rewritten "Where this leaves the bug" closing paragraph; §5c's M2200
  port-table cross-reference updated from "REOPENED, still-live" to disproven; also into
  `docs/P2000T-disk-formats.md` §7 item 9, header + body updated to match).

### 2026-07-28 — Milestone 24 IMPLEMENTED: debugger per-bank access to bank-switched RAM
- **Trigger:** owner decision, motivated by a debugger gap hit while investigating the (separate,
  untouched-here) JWSDOS-activation bug — the debugger's memory watches/breakpoints only ever saw
  whichever bank is live-active at port `0x94`, with no way to inspect a non-active bank or tell
  which bank triggered a shared-address breakpoint.
- **Found — no separate "1-bit RAMSW card" code path exists to special-case:** the milestone's
  own framing (2 banks for RAMSW, `bankCount` for a homebrew card) suggested two shapes to build
  against, but `PageTable._banks` is already ONE generic N-bank array regardless of how it's
  reached — the atomic floppy+RAM board (§17 2026-07-23 entry) just always lands on 6 banks via
  `RamVariant.T102`, a homebrew `RamOnly` card uses the identical array via an explicit
  `MachineConfig.BankCount`. Nothing to make "uniform across every card" — it already is, by
  construction. Documented this directly in `PageTable.BankCount`'s own doc comment so a future
  reader doesn't go looking for a RAMSW-specific class that was never built.
- **Built:**
  - `PageTable.BankCount` (`int`, `_banks.Length`) and `PageTable.GetBankRaw(int bankIndex)` —
    returns a DEFENSIVE COPY of that bank's 8 KB, independent of which bank is live-active,
    never mutating the live core. Throws `ArgumentOutOfRangeException` for an index ≥ `BankCount`
    (including on a 0-bank/unbanked machine — there's nothing valid to return).
  - `MachineSnapshot.BankCount`/`ActiveBank` (`int?`, `null` when `BankCount == 0` — "no banking"
    is a distinct, deliberate value, not a meaningless bank index) — populated by
    `Machine.TakeSnapshot()` every call, so it's refreshed every observer tick like the rest of
    the snapshot, not just read once at debugger-open.
  - Bank-qualified breakpoints: `BreakpointStore`'s `Entry` gained an `int? Bank` field;
    `AddExec`/`AddMemRead`/`AddMemWrite`/`AddMemAccess` gained an optional `bank` parameter
    (IoRead/IoWrite did NOT — ports have no relationship to the banked window). `Check(kind,
    address, activeBank)` now takes the live active bank and skips a bank-qualified entry whose
    `Bank` doesn't match; an unqualified entry (`Bank: null`, the only shape that existed before
    this milestone) is unaffected — matches on kind+address exactly as before, ignoring
    `activeBank` entirely. `Machine.Tick()`'s three call sites (`CheckExec`/`CheckMemRead`/
    `CheckMemWrite`) now pass `Memory.CurrentBank`; `CheckIoRead`/`CheckIoWrite` unchanged (no
    bank parameter, pass `activeBank: 0` internally — inert, since no I/O breakpoint can ever
    carry a qualifier).
  - Validation: `BreakpointStore.Add` throws `ArgumentException` if a non-null `bank` is given for
    an address OUTSIDE the banked window (0xE000-0xFFFF) — a bank qualifier anywhere else is
    structurally meaningless. Deliberately does NOT validate `bank < installedBankCount` — the
    store has no reference to the page table's bank count, and a qualifier for a bank that
    doesn't exist just never matches `Memory.CurrentBank`, which is self-descriptive enough
    (never crashes, never fires) without adding a second dependency for a purely defensive check.
- **`.state`:** no change — `BreakpointStore` was never serialized (confirmed: no `SaveState`/
  `LoadState` on it, and nothing calls into it from `MachineStateFile`), so the new `Bank` field
  needed no version bump.
- **Tests:** `PageTableTests` (+8) — `BankCount` for unbanked/T102/homebrew-override; `GetBankRaw`
  returns bank N's bytes regardless of the live-active bank and is unaffected by a later live
  switch + write to a DIFFERENT bank; confirms the returned array is a copy (mutating it doesn't
  touch the live core); out-of-range and no-banking-at-all both throw.
  `MachineSnapshotTests` (+3) — `BankCount`/`ActiveBank` are `0`/`null` for an unbanked machine;
  `ActiveBank` tracks `OUT (0x94),n`-equivalent port writes exactly for both the RAMSW shape
  (`RamVariant.T102`) and a homebrew shape (`RamOnly` + explicit `BankCount`).
  `BreakpointStoreTests` (+7) — a bank-qualified Exec/MemWrite breakpoint at the SAME address
  fires only under its qualified active bank, not a different one; an unqualified breakpoint at
  the same address fires under every active bank tested; a 0-bank/unbanked machine's existing
  unqualified-breakpoint behavior is completely unaffected (regression guard); all four
  bank-capable `Add*` methods reject a bank qualifier outside the banked window.
- **Applies to:** `src/P2000.Machine/Memory/PageTable.cs` (`BankCount`, `GetBankRaw`),
  `src/P2000.Machine/Debug/MachineSnapshot.cs` (`BankCount`, `ActiveBank`),
  `src/P2000.Machine/Debug/BreakpointStore.cs` (bank-qualified `Entry`/`Add*`/`Check*`),
  `src/P2000.Machine/Debug/MachineCommand.cs` (`Bank` on the four breakpoint-add commands),
  `src/P2000.Machine/Machine.cs` (`TakeSnapshot`, the three tick-loop Check* call sites, the
  command-queue dispatch), `tests/P2000.Machine.Tests/Memory/PageTableTests.cs`,
  `tests/P2000.Machine.Tests/Debug/MachineSnapshotTests.cs`,
  `tests/P2000.Machine.Tests/Debug/BreakpointStoreTests.cs`. UI milestone 17 (consumes all of the
  above), reference doc §3a's Debugger section, reference doc §5 (bank-switching facts).
- **Synced:** yes (2026-07-30, into `docs/P2000T-reference.md` §3a — Debugger section's RESOLVED
  paragraph rewritten to IMPLEMENTED with the concrete build details: `GetBankRaw`/`BankCount`,
  `MachineSnapshot.BankCount`/`ActiveBank`, bank-qualified `BreakpointStore`, and the "no separate
  RAMSW-card path needed" finding).

### 2026-07-28 — Milestone 23 IMPLEMENTED: blank-disk detection no longer defaults to `Jwsdos`
- **Trigger:** owner decision, fast-follow onto milestone 22 (reference doc §3a same RESOLVED
  block's part-3 bullet; `docs/P2000T-disk-formats.md` §7 item 8's third sub-question). Milestone
  22's own "an all-empty directory still counts as a valid, just-empty JWSDOS directory" carve-out
  was an arbitrary pick between two equally-plausible blank states (a blank JWSDOS disk and a
  blank PDOS working disk both read as all-zero at their own directory offsets before anything's
  written) — not a real detection.
- **Removed the carve-out from `IsPlausibleJwsdosDirectory()`:** it now requires at least one
  non-empty slot (`sawNonEmptySlot`) before returning `true`; an all-empty region returns `false`
  instead of vacuously `true`. Confirmed this falls through to `Unknown` on its own, exactly as
  the milestone predicted — no new fallthrough logic was needed: `DetectDirectoryFormat()`'s
  existing PDOS check (`IsPlausiblePdosFcb`) already rejects an all-zero first FCB slot (its name/
  extension bytes are `0x00`, not printable ASCII/space), and byte 0 of an all-zero slot is `0x00`,
  not `0xF3`, so the final `PdosSystem`/`Unknown` branch correctly lands on `Unknown`.
- **Exposed `DskImage.IsDirectoryRegionBlank()`** for the UI to distinguish "genuinely blank" from
  "unrecognized garbage" once `DetectDirectoryFormat()` returns `Unknown` — simplest option against
  the real types (no new `DiskDirectoryFormat` enum value, no new flag alongside it): reuses the
  same two enumerations (`EnumerateDirectorySlots`/`EnumeratePdosFcbSlots`) `DetectDirectoryFormat`
  itself already calls, returning `true` only when NEITHER has any non-empty slot at all — i.e. both
  formats' directory regions are genuinely all-zero, not just individually implausible.
- **Real JWSDOS disks with actual entries are unaffected — verified, not just assumed:** every
  existing `Jwsdos`-detection test (`Spel1.dsk`, `jwssytem.dsk`'s non-empty side) still returns
  `Jwsdos`; only `jwssytem.dsk`'s own real all-empty track 2 (previously the milestone-22 test's
  `Jwsdos` assertion) now returns `Unknown` — that test's expectation was flipped, not left
  contradicting the new one.
- **Tests:** `DskImageTests` — the milestone-22 test asserting `Jwsdos` for an all-empty directory
  had its expectation flipped to `Unknown`; new test confirms `IsDirectoryRegionBlank()` is `true`
  for that same all-empty fixture and `false` for both a real non-empty JWSDOS fixture and a
  genuinely non-blank garbage image; existing non-empty-JWSDOS-fixture tests (`Spel1.dsk` etc.)
  confirmed unchanged.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`IsPlausibleJwsdosDirectory`,
  `IsDirectoryRegionBlank`), `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`, UI milestone
  16 (consumes `IsDirectoryRegionBlank`), `docs/P2000T-disk-formats.md` §7 item 8.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §3a — the RESOLVED block's part-3
  bullet updated from "RESOLVED" to "IMPLEMENTED," covering the removed carve-out, the
  no-new-fallthrough-logic confirmation, `IsDirectoryRegionBlank()`'s mechanism, and the
  real-fixture confirmations; `docs/P2000T-disk-formats.md` §7 item 8's third sub-question also
  updated to IMPLEMENTED).

### 2026-07-28 — Bugfix investigation: "Disk I/O error" on every post-boot LOAD/SAVE — THREE real `Upd765` bugs found and fixed via instrumentation; root cause of the full symptom still not fully closed
- **Trigger:** owner-reported bug, reference doc §5d "TRACKED, not fixed (owner-reported,
  2026-07-28)". Per that entry's own instructions, instrumented `Upd765` (temporary `Trace`
  diagnostic hook — `public Action<string>? Trace`, cheap/null by default, left in permanently as
  a debug aid) rather than guessing, then drove the REAL repro end-to-end: real `Basic-24.bin`
  cartridge, real `assets/Disks/diskbasic_1.6uk.dsk` boot floppy (drive 1) + real
  `assets/Disks/volorg.dsk` (drive 2), booted through the real embedded monitor ROM into real
  Philips Disk BASIC 1.6 UK, then typed `LOAD "B:VOLORG"` via the real keyboard-matrix device
  (new test `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs` — see its own class doc comment
  for the full trace-by-trace narrative).
- **What the trace showed, and three real bugs found — all only reachable through a command
  shape never previously exercised by anything else in this project: TC (Terminal Count)-forced
  early transfer completion.** Real Disk BASIC's resident LOAD driver requests a WIDE EOT window
  on READ DATA (`46 02 01 00 01 01 10 0E 00` — R=1, EOT=0x10, a 16-sector/whole-track window),
  takes only the ONE sector it actually wants via the data register (256 bytes), then writes the
  TC control-latch bit (`OUT (0x90),0x0E`) to abort the rest — a real, legitimate technique (the
  ROM's own fixed-EOT boot reads always complete NATURALLY, never via TC, so this path was
  entirely unexercised before now):
  1. **`LastSectorResultFields()` reported the EOT window's TAIL sector (16), not the sector
     actually transferred (1).** It computed the result's R field from
     `_transferBuffer.Length` (the ORIGINALLY REQUESTED length) instead of `_transferIndex` (bytes
     ACTUALLY moved before TC fired) — identical for a naturally-completing transfer (invisible to
     every prior test + the ROM's own boot read), but wrong the instant TC ends things early.
     Fixed: bound by `_transferIndex`. Same bug, same fix, also applied to `CommitSectorWrites`
     (was about to commit the zero-initialized TAIL of the full requested WRITE DATA buffer, not
     just what the host actually sent — directly relevant since the repro also reports SAVE
     failing), and for consistency to `CommitFormat`/`CompleteScan` (same bug class, no confirmed
     real caller uses TC with them yet, fixed anyway rather than shipping a known-latent copy).
  2. **`CompleteTransfer`'s ST0 was unconditionally `0x00` on a normal completion, never encoding
     the addressed drive/head (datasheet D2 HD / D1-D0 US1/US0) the way `DispatchSenseInterruptStatus`
     already does (`0x20 | drive`).** Invisible whenever drive 0/head 0 was involved (every prior
     test + the ROM's own boot read use drive 1 exclusively) — very much NOT invisible for THIS
     repro's drive 2. This was the fix that stopped an observed 14-28× identical-sector retry loop
     outright (drive-2 reads got ST0=0x00, and the driver's own integrity check evidently treats
     the addressed-unit mismatch as a failure and retries the SAME read verbatim until giving up).
  3. **TC-forced completion fired `ResultReady` SYNCHRONOUSLY inside the triggering `WriteControl`
     call — the exact same "lost wakeup" bug class already found and fixed for SEEK/RECALIBRATE**
     (the existing `MinimumTurboSeekTStates` guard, renamed `MinimumLostWakeupGuardTStates` since
     it now has two real callers): a driver writes the TC bit then HALTs waiting for the disk-
     complete interrupt a few instructions later; completing (full IM2 dispatch + ISR + RETI)
     INSIDE the same OUT instruction delivers and fully consumes that interrupt before the driver
     ever reaches its own HALT. Fixed by deferring TC-forced completion through the same
     `PendingAction`/`Tick()` mechanism SEEK already uses (new `PendingAction.ForcedCompletion`),
     applied under BOTH `TimingPolicy` values (unlike SEEK's Turbo-only guard — Authentic's own
     per-byte transfer pacing does NOT provide a natural gap here, since the risk window is
     driver-code-length between the TC-issuing OUT and its own wait point, not transfer pacing).
     This fix alone changed the FDC's behavior from "identically retries sector 1 forever" into "a
     real, complete, correctly-parameterized scan across all 16 directory sectors" — the search
     started actually advancing once the chip stopped potentially eating its own wakeup signal.
- **Combined effect, verified via the trace:** before these three fixes, EVERY `LOAD`/`SAVE`
  against ANY drive hit at least bug #2 (wrong ST0 whenever the drive/head isn't 0/0) and, on any
  read/write actually needing a TC-truncated transfer, bugs #1 and #3 too — matching the repro's
  own "universal, not drive-selective" framing exactly. After all three fixes, the SAME repro's
  FDC-level trace is now fully datasheet-correct (right status, right data, right timing) and the
  driver performs a real, advancing, non-repeating directory scan — a dramatic, independently
  verified improvement, NOT just a plausible-sounding rationalization: confirmed by literally
  watching the sector sequence change from `1,1,1,1,1,1,1,1,1,1,1,1,1,1` (14-28×, identical) to
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16` (all 16 sectors, exactly once each).
- **NOT fully resolved — genuinely stuck past this point, exactly the scenario this bug-fix
  prompt's own instructions anticipated.** The repro's `LOAD "B:VOLORG"` (and `LOAD "B:VOLINFO"`,
  tried separately to rule out the target FCB's own incidental `0xF3` first byte as a red herring)
  still ends in "Disk I/O error" after the driver scans all 16 directory sectors — including the
  one holding the target file's real, independently-byte-verified FCB (confirmed via
  `DskImage.ReadPdosDirectory()`/`ReadSector` directly against `volorg.dsk`, matching milestone
  22a's own prior fixture confirmation exactly). Two hypotheses were tested and DISPROVEN by
  direct experiment rather than left unchecked: (a) the result's C (cylinder) field should echo
  `track+1` rather than the true 0-based physical cylinder, by analogy with `jwsformat.asm`'s own
  confirmed off-by-one — tested both with and without the ST0 fix in place, neither changed the
  outcome; (b) the target FCB's own incidental `0xF3` first byte (coincidentally the SAME value as
  the ROM's PDOS-system-disk signature) causes the driver to misidentify the disk — disproven by
  getting the identical failure searching for `VOLINFO` instead, whose FCB does NOT start with
  `0xF3`. **What would be needed to go further:** a disassembly of Philips Disk BASIC's own
  resident LOAD driver (still not sourced/planned, per the reference doc block's own note) showing
  exactly what it does with the 256 bytes read back from each candidate directory sector — the
  trace proves the FDC's own command/status/data are now all correct by every datasheet-derivable
  measure available without that source, so whatever check still fails is either a driver-internal
  detail with no external signature (a checksum? a specific expected probe count/order?) or a
  genuine PDOS-format-level nuance this project's `DskImage`/FCB model doesn't yet capture — not
  something further trace-staring or guessing can distinguish.
- **Follow-up (same day, owner's own question) — SAVE traced too, for comparison:** ran
  `SAVE "B:TEST"` against the identical booted machine
  (`Boot_ThenSaveTest_TraceFdcCommandsAndScreenOutput`). New information, not just a repeat of the
  LOAD finding: SAVE reads directory track1/sector1 EXACTLY ONCE (same command shape, same
  now-fixed-correct status/data the LOAD investigation already validated), then reports "Disk I/O
  error" immediately — it **never issues SENSE DRIVE STATUS at all** (no write-protect check ever
  runs) and **never attempts a WRITE DATA**. This rules out a write-protect-detection bug as the
  cause (that code path isn't reached) and shows SAVE's give-up threshold differs from LOAD's
  (SAVE bails after ONE read; LOAD's own search tries all 16 directory sectors before giving up) —
  consistent with the two being different algorithms that both stumble on the same still-
  unresolved check, but this doesn't further localize where that check lives. Sector 1 genuinely
  has 6 free FCB slots in this fixture (only 2 of 8 occupied — VOLORG, VOLINFO), so a correctly
  functioning free-slot search should succeed reading just this one sector. Still stuck at the
  same point as the LOAD investigation — recorded here so a future disassembly-armed pass doesn't
  have to re-derive that SAVE's failure is this early/this shaped.
- **Second follow-up (same day, owner: "I tried a save on a clean disk. also got an disk I/O
  error, but the error took... a little longer to appear") — a genuine, non-illusory difference,
  now fully explained in SHAPE (not in root cause):** three-way comparison, all three ending in
  "Disk I/O error":
  1. Real `volorg.dsk` (VOLORG's own FCB happens to start with `0xF3`): SAVE reads sector 1 ONCE,
     fails immediately (the finding directly above).
  2. A genuinely blank/unformatted disk (`DskImage.CreateBlank`, all-zero, no `0xF3` anywhere):
     SAVE scans ALL 16 directory sectors, in the EXACT same order LOAD's own search uses
     (`1,7,13,3,9,15,5,11,2,8,14,4,10,16`), THEN fails — confirms the owner's "took longer"
     observation was real.
  3. Real `volorg.dsk` bytes, unchanged EXCEPT patching that one byte `0xF3`→`0x00` (VOLORG's FCB
     stays fully occupied/non-zero otherwise): ALSO scans all 16 sectors like case 2, not just 1 —
     isolating the variable decisively. **It's the `0xF3` BYTE VALUE specifically, not "occupied
     vs. empty" directory content, that makes SAVE short-circuit to one read.**
  Very plausibly Disk BASIC's own legitimate design, not an emulator bug: `0xF3` at this exact
  track1/sector1/offset0 location is the SAME byte the ROM's own `getdos` checks to recognize a
  PDOS system disk (reference doc §5d) — real Disk BASIC's SAVE very plausibly refuses to write
  to what it believes is a system disk, protecting boot media from a naive SAVE, same as a real
  DOS would. VOLORG's own FCB colliding with that exact byte value is the SAME "one genuine
  ambiguity in the format" already flagged in `docs/P2000T-disk-formats.md` §7 item 8 (milestone
  22a) — not a new finding, just a newly observed consequence of it. **Critically, this does NOT
  explain the remaining bug** — cases 2 and 3 (no `0xF3` anywhere) BOTH still end in "Disk I/O
  error" after their full scan, so the `0xF3` check only governs how many sectors get scanned
  before failing, not whether the command ultimately succeeds. The real root cause remains exactly
  as open as before this comparison, downstream of (or unrelated to) this `0xF3` gate.
- **Third follow-up (same day, owner: since after the directory scan zero further FDC activity
  happens before "Disk I/O error," maybe PDOS caches the directory in RAM — "could it be that
  something is not working well in that caching area? Basic24 needs at least a switchable bank")
  — chased the banking angle directly, then owner pushed further ("did you check that anything
  landed in banked memory and that the banks are present, and that the switches have the desired
  effect? The boot ROM doesn't perform extensive banked memory tests"), a fair challenge since the
  first pass only searched for the delivered directory text, not the banking mechanism itself.
  Three things checked:
  1. **Real, closed coverage gap:** every prior banking test only ever verified banks 0 and 1
     (`BankedWindow_SelectBank_SwitchesToAnIsolatedBank`) — banks 2-5 had literally never been
     checked for isolation on their own. New
     `tests/P2000.Machine.Tests/Memory/PageTableTests.cs`'s `BankedWindow_AllSixT102Banks_AreMutuallyIsolated`
     writes a distinct marker to all 6 T102 banks and confirms all 6 persist independently — passes.
  2. **Banks are genuinely present/populated/distinct in the LIVE repro machine**, not just in
     isolated `PageTableTests` construction: dumping all 6 banks' own first 16 bytes at 0xE000
     shows bank 1 holding real, recognizable Z80 code (`F3 ED 5E DD 22 32 F5 FB DB 00 3C 20 FB F3
     ED 73` = DI/IM2/LD(nn),IX/LD(nn),A/EI/IN A,(0)/INC A/JR NZ,-3/DI/LD(nn),SP — a sensible ISR-
     setup preamble, consistent with this being exactly the DOS driver code `getdos` loaded into
     bank 1 at boot), banks 2-5 showing DIFFERENT still-untouched power-on noise (allocated
     correctly, simply unused by this PDOS session), bank 0 all-zero (plausibly Disk BASIC's own
     cleared extended-workspace use of that bank, unrelated to the disk driver). No open-bus,
     aliasing, or cross-bank corruption visible.
  3. **Switches have real, observable effect during the live SAVE attempt:** exactly 12 real
     bank-select writes (alternating 0x01/0x00) occur while attempting to save just one directory
     sector — consistent with BASIC's own code (bank 0) repeatedly calling into DOS driver
     subroutines (bank 1) and switching back after each. Caught and fixed a real instrumentation
     bug along the way: the diagnostic's own bank-dump helper temporarily re-selects banks to peek
     at their content, which briefly polluted the SAME log meant to capture only the driver's real
     switches — fixed by dumping "before" banks before subscribing to the trace, and unsubscribing
     before the "after" dump.
  **Conclusion:** the banking MECHANISM itself (isolation, persistence, real addressing effect)
  checks out — doesn't look like a `PageTable` bug. Does NOT rule out a more specific timing/
  ordering interaction between disk I/O and a bank switch (a switch landing one instruction off
  from what real hardware would allow, say) — only a disassembly could confirm that one way or
  the other. Still the same open root cause as the two follow-ups above; this narrows where it
  ISN'T (the banking primitive), not where it is.
- **Tests:** `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs` (new) — the real end-to-end
  repro itself, with a regression assertion that the retry loop specifically (bugs #2+#3's
  combined symptom) stays fixed: every READ DATA issued during the LOAD attempt must target a
  DISTINCT sector, never repeat one. `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs` (+4,
  synthetic, fast): TC-truncated READ DATA reports the sector actually read, not the EOT window's
  tail; TC-truncated WRITE DATA commits only the sectors actually received (sector 2, never sent,
  stays untouched); `CompleteTransfer` reports the addressed drive/head in ST0 (drive 2/head 0 →
  `0x02`, not `0x00`); TC-forced completion is not synchronous (mirrors the existing
  `Recalibrate_Turbo_CompletesAfterAFewTStates_FiresResultReady` test). Full
  `P2000.Machine.Tests`: 568/568 green (was 561 before the 4 new `Upd765Tests.cs` cases; +3 net
  for `PdosLoadSaveRepro.cs`'s own diagnostic tests — the LOAD repro, plus the three-way SAVE
  comparison below); `P2000.UI.Tests`: 217/217 green, unaffected.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`Trace`, `LastSectorResultFields`,
  `CommitSectorWrites`, `CommitFormat`, `CompleteScan`, `CompleteTransfer`'s ST0,
  `MinimumLostWakeupGuardTStates` rename, `PendingAction.ForcedCompletion`, `WriteControl`'s TC
  branch), `src/P2000.Machine/Memory/PageTable.cs` (`CurrentBank`, `BankSelected` — diagnostic-only
  additions, same pattern as `Upd765.Trace`), `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs`
  (new), `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Memory/PageTableTests.cs` (`BankedWindow_AllSixT102Banks_AreMutuallyIsolated`,
  new). Reference doc §5d's "TRACKED, not fixed" block.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §5d — the "TRACKED, not fixed"
  block replaced with "PARTIALLY FIXED," covering the three confirmed bugs, the combined-effect
  interleave-order confirmation, the disproven hypotheses, the SAVE-specific trace, both owner
  follow-ups (the `0xF3` write-refusal explanation and the banking-mechanism-cleared finding),
  and the still-open root cause).

### 2026-07-28 — Milestone 22b IMPLEMENTED: raw sector-1 read for the fallback dump view (no new API)
- **Trigger:** owner decision (reference doc §3a same RESOLVED block). Third and last of the
  three-part split — triggered whenever milestone 22/22a's dispatch returns `PdosSystem` or
  `Unknown`.
- **Found — confirmed no new API was needed, exactly as the milestone's own text hedged:**
  `DskImage.ReadSector(cylinder, head, sector)` (milestone 19) already does precisely what this
  milestone asked for — read-only, no FDC/command-sequence semantics, and its existing out-of-
  range/short-mount behavior already returns the `0x00` fill-byte convention (milestone 20d), not a
  different fallback value. `ReadSector(cylinder: 0, head: 0, sector: 1)` is track 1/sector 1.
  Nothing added to `DskImage.cs` itself for this milestone — only tests, pinning the SPECIFIC call
  the UI fallback view (milestone 15b) makes, since the general behavior was already covered
  incidentally by other tests but not framed around this exact use case.
- **Real-fixture confirmation:** reading sector 1 off `diskbasic_1.6uk.dsk` (real PDOS system disk)
  and `volorg.dsk` (real PDOS working disk) both match the source files' own raw bytes exactly;
  a short/blank mount's sector-1 read returns all-zero, matching every other out-of-range read.
- **Applies to:** `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`, `P2000.UI` milestone 15b (consumes
  `ReadSector` directly).
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §3a — part-3 bullet updated from
  "fully UNBLOCKED" to "IMPLEMENTED," noting `ReadSector` needed no changes and citing the real-
  fixture byte-exact confirmation; `docs/P2000T-disk-formats.md` §6 also got the "Disk BASIC 24K
  is confirmed, not just presumed, a PDOS disk" update from the owner's real boot test).

### 2026-07-28 — Milestone 22a IMPLEMENTED: PDOS FCB directory reader + system-disk disambiguation
- **Trigger:** owner decision (reference doc §3a same RESOLVED block; `docs/P2000T-disk-formats.md`
  §6a for the FCB byte-level spec, §7 item 8 for the disambiguation this milestone implements).
  Second of the three-part split — depends on milestone 22's `DetectDirectoryFormat()` dispatch
  (fills in the `PdosWorking`/`PdosSystem` branches it stubbed to `Unknown`).
- **Added:** `DskImage.ReadPdosDirectory()` (parses all 128 fixed 32-byte FCB slots on track 1,
  raw `0x0000`, folding continuation FCBs sharing a name+extension into one logical
  `PdosDirectoryEntry`) and the disambiguation logic in `DetectDirectoryFormat()`: validate the
  FIRST FCB slot's name/extension/sector-count/allocation-map (`IsPlausiblePdosFcb`) regardless of
  what position 1 says; if plausible, `PdosWorking` (even if position 1 happens to be `0xF3` — a
  real per-file flag value, not the system-disk marker); if not plausible AND byte 0 is genuinely
  `0xF3`, `PdosSystem`; otherwise `Unknown` (neither format matched at all — this last branch isn't
  explicitly spelled out in the milestone text but follows directly from reference doc §3a point 3
  and milestone 22's own original stub behavior).
- **Sane-sector-count check, not otherwise specified by the source doc:** a slot's own sector-count
  byte (position 16) must satisfy `ceil(sectorCount / 4) == recordCount` (real record count from
  its allocation map) — confirmed to hold EXACTLY across all three known real/worked examples
  (`docs/P2000T-disk-formats.md` §6a: VOLORG 44/11 exact fit, VOLINFO 14/4 with 2 sectors' slack,
  the source docx's own 27/7 worked example), so used as the "sane range" validation the milestone
  asked for without further specifying the exact bound.
- **Found, correcting the milestone/reference-doc's own shorthand:** both docs describe the
  track/sector display formula as "start track = first record ÷ 4" (no `+1`). Real confirmed data
  contradicts a literal reading of that: `docs/P2000T-disk-formats.md` §6a's own independently-
  reconstructed-interleave finding states plainly **"`VOLINFO.BAS` (track 3, records 8-11)"** —
  and `8 ÷ 4 = 2`, not `3`. The doc's OWN separately-stated track↔record mapping ("track N's four
  records are numbered `(N-1)×4` through `(N-1)×4+3`") resolves this: 1-based track = `record ÷ 4
  + 1`. Implemented with the `+1`; confirmed exactly against both real `volorg.dsk` entries
  (`RecordToTrack`, verified: record 8 → track 3, matching the doc's own quoted fact). Flagging
  here since the "resolved display formula" phrasing in both the reference doc and this file's own
  milestone 22a text needs this correction synced.
- **Continuation-FCB folding — an assumption, not sourced (no real multi-FCB fixture exists):**
  each contributing FCB's own sector-count byte (position 16) is SUMMED for the folded entry's
  total `FileLength`, and their allocation maps are concatenated in ascending position-1 order for
  the combined track range. Exercised only by a synthetic test
  (`ReadPdosDirectory_ContinuationFcbs_FoldIntoOneEntry_WithCombinedSectorCount`) — flagging this
  as an assumption in case a real multi-FCB disk ever surfaces to confirm or correct it.
- **Real-fixture confirmation:** `volorg.dsk` (`VOLORG`/`VOLINFO`) — `VOLORG`'s FCB carries
  position 1 = `0xF3` and still validates as a plausible entry, exactly the real case this
  disambiguation exists for; `DetectDirectoryFormat()` now returns `PdosWorking` for it (was
  `Unknown` under milestone 22's stub). The owner-supplied real "Disk BASIC 24K" system-disk
  fixture (`assets/Disks/diskbasic_1.6uk.dsk`, confirmed `0xF3` at track 1 offset 0, genuine Z80
  boot code — not a plausible FCB — occupying the rest of that slot) now returns `PdosSystem`,
  not a false-positive directory. Note: the existing `getdos`/§6 boot-path tests
  (`tests/P2000.Machine.Tests/Boot/DiskBootTests.cs`) use a SYNTHETIC `0xF3`-patched `Spel1.dsk`
  copy, not this real fixture — this milestone's tests are the first to use the real one.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`ReadPdosDirectory`,
  `PdosDirectoryEntry`, `IsPlausiblePdosFcb`, `TryCountPdosAllocationMapRecords`,
  `EnumeratePdosFcbSlots`, `ReadPdosFcbSlot`, `RecordToTrack`),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`, `P2000.UI` milestone 15a (consumes
  this), `docs/P2000T-disk-formats.md` §6a/§7 item 8.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §3a — part-2 bullet updated to
  IMPLEMENTED with the disambiguation logic, the sane-sector-count rule, the corrected `+1`
  track formula, and the continuation-FCB-folding assumption flagged; `docs/P2000T-disk-
  formats.md` §7 item 8 and §6a's position-1 note updated to match).

### 2026-07-28 — Milestone 22 IMPLEMENTED: `DiskDirectoryFormat` detection dispatch (JWSDOS-only part)
- **Trigger:** owner decision (reference doc §3a "RESOLVED — the Disk Drives window's directory
  browse table gets format auto-detection..."), first of a three-part split — this milestone
  covers only the fully-unblocked JWSDOS-detection piece; PDOS/PDOS-system detection is milestone
  22a, stubbed here.
- **Added:** `DiskDirectoryFormat` enum (`Jwsdos`/`PdosWorking`/`PdosSystem`/`Unknown`) and
  `DskImage.DetectDirectoryFormat()`. JWSDOS detection reuses `ReadDirectory()`'s own "non-empty
  slot" rule, then additionally requires every non-empty slot's filename+extension+filetype bytes
  (offsets 0-19) to be plausible printable ASCII/space — matching this codebase's existing self-
  consistency-checking spirit (`Mount`'s label-length validation). An all-empty directory (every
  slot zero-padded, e.g. `jws-sytem.dsk`'s real empty track 2) still returns `Jwsdos`, not
  `Unknown` — there's nothing there to contradict a valid, just-empty JWSDOS directory. PDOS/PDOS-
  system branches are stubbed to `Unknown` with a `// TODO: milestone 22a` pointer, per the
  milestone's explicit "don't implement PDOS detection yet" instruction.
- **Found, not assumed going in:** the `DiskDirectoryEntry.Head`/`StartSector`/`EndSector` fields
  the milestone spec asked to "extend the entry with" were **already exposed** — milestone 19's
  original `ReadDirectory()` implementation already parsed and surfaced all three. Nothing to add
  there; the UI (ms.15) reads `Head`/`StartSector`/`EndSector` directly, no field rename.
- **Real-fixture confirmation used for tests:** `volorg.dsk` (a real PDOS working disk, owner-
  supplied) has no JWSDOS label AND arbitrary binary (non-printable-ASCII) bytes at JWSDOS's
  directory offset (`0x1800`) — an ideal real "must not false-positive as Jwsdos" case, used
  directly rather than fabricating synthetic garbage for that specific test. Also confirmed
  (independent of this milestone, useful for UI ms.15's Track/Sector column): `Spel1.dsk`'s
  `AUTORUN` entry's confirmed `StartSector`/`EndSector` (622/632) maps via the 16-sectors/track
  linear formula to track 39 sector 14 through track 40 sector 8.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`DiskDirectoryFormat`,
  `DetectDirectoryFormat`, `IsPlausibleJwsdosDirectory`, `EnumerateDirectorySlots`),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`, `P2000.UI` milestone 15 (consumes
  this), `docs/P2000T-disk-formats.md` §1/§4/§6a/§7 item 8.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §3a — the RESOLVED block's part-1
  bullet updated from "UNBLOCKED, spec'd below" to "IMPLEMENTED," folding in the all-empty-
  directory→`Jwsdos` design call, the already-exposed `Head`/`StartSector`/`EndSector` finding,
  and the real-fixture confirmations; `docs/P2000T-disk-formats.md` §4 also got the `AUTORUN`
  622/632→`T39 S14-T40 S8` cross-check and §7 item 2 got the no-mixed-sides-fixture flag).

### 2026-07-27 — Housekeeping: `docs/JWSDOS-format.md` renamed to `docs/P2000T-disk-formats.md`
- **Trigger:** owner decision (`docs/P2000T-disk-formats.md` §8 provenance entry) — the doc grew a
  substantial, confirmed PDOS section (§6a) alongside its original JWSDOS content, and the old
  JWSDOS-branded filename had started actively misdescribing about a fifth of its own content.
- **Done:** `git mv docs/JWSDOS-format.md docs/P2000T-disk-formats.md` (history preserved, not
  delete+recreate). Swept every `docs/JWSDOS-format.md` citation in source-code comments (`DskImage.cs`
  and its test files across both `P2000.Machine.Tests` and any XML-doc pointers) and in this file's
  own forward-looking §13 build-order milestone text (14 occurrences, lines 717–1171), plus
  `docs/M2200-implementation.md`'s one cross-reference — all updated to the new path. No behavior
  change; comment/doc text only.
- **Left alone, deliberately:** this section's OWN two dated entries citing the old path (just
  above, in the 20d write-up) and all of `docs/CLAUDE_machine_findings_archive.md` and
  `docs/implementation-handoff-2026-07-22.md` — historical records of what was true when written,
  not live references. Any findings-log entry from here on that needs to cite this doc should use
  the new path.
- **Applies to:** `docs/P2000T-disk-formats.md` (renamed), `src/P2000.Machine/CLAUDE.md` §13,
  `src/P2000.Machine/Devices/Fdc/DskImage.cs`, `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/RealFixtureTests.cs`, `tests/P2000.Machine.Tests/Boot/DiskBootTests.cs`,
  `docs/M2200-implementation.md`.
- **Synced:** n/a — no reference-doc content changed, this is a path-reference sweep only.

### 2026-07-27 — Milestone 20e IMPLEMENTED: extracted `DskImage.DetectMismatch`
- **Trigger:** owner decision to close the Config window's disk-image-picking gap (reference doc
  §3a "RESOLVED — the Config window's own disk-image picking gets the same geometry-mismatch
  protection..."), which needs `Mount`'s mismatch logic runnable without constructing a `DskImage`.
- **Found:** `Mount`'s label/config/candidate decision already cleanly separated into "which
  geometry wins" + "what mismatch (if any) resulted" — no behavior needed to change, just a
  shape split. Introduced a private `DetectMismatchCore` returning `(Tracks, Sides, Mismatch)`;
  `Mount` calls it then builds the `DskImage`, and the new public `DskImage.DetectMismatch(bytes,
  configuredTracks, configuredSides) → DiskGeometryMismatch` just discards the winning geometry.
  Kept the signature's parameter types as `int, int` (matching `Mount`'s existing signature and
  every caller's `sides == DiskSides.Double ? 2 : 1` convention) rather than the reference doc
  block's illustrative `DiskCapacity`/`DiskSides` typing — no such `DiskCapacity` type exists in
  this codebase; `Capacity` is a plain `int` on `FloppyDriveConfig`.
- **Note:** `DetectMismatch` intentionally does NOT sniff IMD (`ImdFormat.IsImdFile`) — IMD is
  fully self-describing and never mismatches, so there's nothing for a preview to detect there;
  a caller previewing an IMD file must check `IsImdFile` itself first if it cares.
- **Follow-up (same day, found while building UI ms.14g on top of this):** `ImdFormat` itself is
  `internal` — `P2000.UI`'s offline-preview code (which needs exactly the "check `IsImdFile` first"
  escape hatch the note above calls for) can't reach it. Added a small public passthrough,
  `DskImage.IsImdFile(byte[] bytes) => ImdFormat.IsImdFile(bytes)`, rather than making `ImdFormat`
  itself public (keeps the format-detail-parsing type internal; only the yes/no sniff is exposed).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs`; tests in
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a — new "IMPLEMENTED (machine
  milestone 20e, 2026-07-27)" paragraph).

### 2026-07-27 — Milestone 21 IMPLEMENTED: IMD (ImageDisk) reader/writer
- **Trigger:** owner decision to adopt IMD as the emulator's native/preferred disk container
  (reference doc §3a "RESOLVED — adopt IMD... as the emulator's native/preferred disk
  container"), following on from ms.20d/UI ms.14e's geometry-mismatch work for legacy `.dsk`.
- **Spec source note:** the primary published spec PDF (`oldcomputers-ddns.org/.../imd.pdf`,
  cited in the reference doc block) was unreachable while building this (`WebFetch` failed with
  `connect ECONNREFUSED`). Built instead directly from MAME's own `imd_dsk.cpp` parser (one of
  the reference doc's OTHER cited sources) — a precise, load-bearing technical extraction, not a
  guess: ASCII header terminated by literal `0x1A`; no separate geometry header field at all —
  `Tracks`/`Sides` are derived from the highest cylinder/head seen across all track descriptors,
  read until end-of-file (matches MAME's own approach); per-track 5-byte descriptor (mode,
  cylinder, head-byte with optional cylinder-map/head-map flag bits, sector count, size code);
  sector size = `128 << sizeCode`; sector data type byte 0-8 (unavailable / normal / compressed /
  ×deleted-DAM / ×bad-CRC combinations).
- **Built — `ImdFormat` (new internal static class, `Devices/Fdc/ImdFormat.cs`):**
  `IsImdFile`/`Read`/`Write`. `DskImage.Mount` sniffs `ImdFormat.IsImdFile` FIRST, before any
  label/config-fallback logic — an IMD mount always returns
  `DiskGeometryMismatch.None`, never touching ms.20d's Candidate/NoCandidate machinery at all
  (IMD is fully self-describing, same "embedded state is authoritative" shape as
  `DskImage.FromEmbeddedState`). No existing call site (`Machine.cs`, `DiskDriveVm.MountBytes`)
  needed to change. New `DskImage.Format` (`DiskImageFormat.Dsk`/`.Imd`) property, defaulting to
  `Dsk` for every pre-existing construction path; new `DskImage.GetImdBytes()` mirroring the
  existing `GetBytes()`.
- **Sector-order (interleave) map IS preserved across an unmodified read→write round trip** —
  new `DskImage.SectorOrderMaps` (keyed by (cylinder, head), populated on IMD read) is reused
  verbatim by `GetImdBytes()` when present; only a `.dsk`-mounted or freshly-created image (no
  real interleave data to preserve — `SectorOrderMaps` is `null`) falls back to a plain
  sequential map. This matches the reference doc's explicit "round-tripping the per-sector order
  map faithfully" — the milestone text's "write a plain sequential order for now" turned out to
  describe only the NEW-data case (`.dsk`→IMD conversion, blank disks), not a re-save of an
  already-IMD-backed image.
- **Writer always emits sector data type 1 (normal, uncompressed) only** — no compression, no
  deleted-DAM/bad-CRC modeling, since `DskImage` has no fields for either. **Flagged limitation**
  (not covered by the milestone text): reading a file that uses IMD's optional per-track
  cylinder-map/head-map bits (`0x80`/`0x40` on the head byte) parses past them correctly (so the
  file still loads) but does NOT preserve or re-emit them — re-saving such a file would not be
  byte-identical. No known P2000 image is expected to need this; flagged rather than silently
  assumed away.
- **Sector-size validation:** `Read` throws `InvalidDataException` if any track's IMD size code
  doesn't resolve to exactly `DskImage.BytesPerSector` (256) — an honest "this isn't a P2000 disk
  image" rejection, distinct in kind from ms.20d's fail-soft mismatch handling (that's for
  ambiguous-but-plausible raw dumps; this is for a file whose own self-described structure
  doesn't fit this project's fixed sector size at all).
- **Mode byte value (250 kbps MFM, value 5) is a cosmetic/non-functional choice**, inferred from
  this project's already-documented 5¼" double-density/300 RPM/MFM FDC model, NOT independently
  re-confirmed against real P2000 hardware or captured real IMD files — nothing in this
  emulator's timing model reads the mode byte back, so it has no behavioral effect either way.
- **Tests:** `ImdFormatTests` (new, 9 cases) covers the milestone's own 5-item list plus 4 extra:
  (a) round-trip byte-identical including a genuine non-sequential order map on one track (proves
  preservation, not just structural validity); (b) the "all sectors this value" compression
  marker reads as a fully-populated track; (c) a `.dsk`-mounted `DskImage`'s `GetImdBytes()`
  produces a valid IMD with correct header/track descriptors and sequential order maps on every
  track; (d) `Format` reports `Dsk`/`Imd` correctly per mount path; (e) an IMD mount never
  reports a mismatch even with a deliberately wrong configured geometry; plus sector-size
  rejection, and `IsImdFile` true/false on both formats. Full `P2000.Machine.Tests`: 529/529
  green (was 520).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/ImdFormat.cs` (new),
  `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`Format`, `SectorOrderMaps`, `Mount`,
  `GetImdBytes`), `tests/P2000.Machine.Tests/Devices/Fdc/ImdFormatTests.cs` (new). Reference doc
  §3a.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a — new "IMPLEMENTED (machine
  milestone 21...)" paragraph, including a correction to the reference doc's own "plain
  sequential order map" scoping now that real-interleave round-trip preservation is confirmed
  built, plus the spec-source/cylinder-map/sector-size-rejection notes).

### 2026-07-27 — Two follow-up fixes found while building UI ms.14e on top of ms.20d
- **`DskImage.ReadDirectory()` crashed on any short/unpadded image — a direct consequence of
  ms.20d making short mounts a NORMAL, always-allowed path for the first time.** `ReadDirectory`
  unconditionally did `_data.AsSpan(0x1800, 0x0800)`, silently assuming `_data` was always ≥
  `0x2000` bytes — true for every real disk image, but no longer true once ms.20d started
  letting genuinely short files mount instead of throwing. Every "mount a too-short file" UI path
  now calls `FormatDirectory` → `ReadDirectory` immediately after mounting, so this threw
  `ArgumentOutOfRangeException` on the very first real test of ms.14e's own regression case.
  **Fixed:** builds a zero-filled `DirectorySize`-byte buffer first, copies in whatever real
  bytes overlap the directory region (none, if the image is shorter than `0x1800`), and reads
  from that — same "out-of-range reads as `0x00`" convention `ReadSector` already established,
  just applied to the directory region too. An unpadded short image now correctly browses as an
  empty directory rather than crashing.
- **`Machine.CaptureCurrentConfig()`'s `FloppyDriveConfig.Capacity`/`.Sides` always echoed the
  ORIGINAL construction-time `Config`, even after UI ms.14e's "reconfigure the drive and remount"
  recovery action changes a drive's REAL geometry live.** Same class of staleness
  `FloppyDriveConfig.ImagePath` was already fixed for in ms.20c — a live reconfigure is exactly
  the kind of drift that deriver exists to catch. **Fixed:** `Capacity`/`Sides` are now read from
  the live mounted `DskImage`'s own `Tracks`/`Sides` when a disk is mounted, falling back to the
  original `Config` entry only for an empty drive (nothing live to read).
- **Tests:** `DskImageTests` (+1): an unpadded short image's `ReadDirectory()` returns empty, not
  an exception. `MachineTests`/`MultiDriveFloppyTests`: no new dedicated test added for the
  `CaptureCurrentConfig` geometry fix here — it's exercised indirectly by UI ms.14e's own
  `ReconfigureAndRemount` tests, which assert the live disk's `Tracks`/`Sides` changed; a
  dedicated machine-layer test would be redundant with those. Full `P2000.Machine.Tests`:
  520/520 green (was 519).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`ReadDirectory`),
  `src/P2000.Machine/Machine.cs` (`CaptureCurrentConfig`),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`. Same reference doc §5d block as
  milestone 20d above.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §5d — folded into milestone 20d's
  own "IMPLEMENTED" paragraph as the two real bugs found along the way).

### 2026-07-27 — Milestone 20d IMPLEMENTED: validate the JWSDOS label, detect real geometry mismatches
- **Trigger:** real end-to-end testing with a genuine PDOS boot floppy (Basic24k's own boot disk —
  no JWSDOS label at all) and a genuinely short mount (32,768 bytes into a drive configured for
  327,680) that produced zero feedback — reference doc §5d "RESOLVED — the label-based
  auto-detect above is JWSDOS-specific and was silently over-trusted."
- **Built — `DskImage.Mount(bytes, configuredTracks, configuredSides)`, a new static factory
  alongside (NOT replacing) the existing `DskImage(string)`/`DskImage(byte[])` constructors:**
  those two constructors keep their original unconditional-label-trusting behavior UNCHANGED
  (including the throw-if-too-short-for-the-label-bytes case) — dozens of existing tests and
  fixtures across `DskImageTests`/`Upd765Tests`/`MultiDriveFloppyTests`/`RealFixtureTests`
  construct real/synthetic images directly via them and don't need the mismatch dance. `Mount`
  is the new entry point for the two REAL mount call sites instead: `Machine`'s constructor (a
  `.cfg`'s `FloppyDrives[i].ImagePath`) and `DiskDriveVm.MountBytes` (the live UI mount, wired in
  UI ms.14e). Algorithm: (1) read the label only if the file is long enough to contain it, and
  only trust it if its implied byte length equals the file's actual length exactly; (2) otherwise
  fall back to the drive's configured Capacity/Sides — promoted from "blank-media seed only" to
  the real fallback for any non-JWSDOS image; (3) if THAT doesn't match either, check the file's
  exact length against the other 5 canonical Capacity×Sides combinations (35/40/80-track × SS/DS)
  and report whichever match (0, 1, or 2 — 40-track/DS and 80-track/SS collide at 327,680 bytes).
  **Never throws, never fails to mount** — every path returns a usable `DskImage`, mounted using
  whichever geometry won (label, or configured as the fallback).
- **New result type:** `DiskGeometryMismatchKind` (`None`/`Candidate`/`NoCandidate`) +
  `DiskGeometryMismatch` (`Kind`, `ActualLength`, `ExpectedLength`, `Candidates`, plus a computed
  `CanPad` — true only for a `NoCandidate` mismatch where the file is actually SHORTER than
  expected, since a longer-but-still-no-match file has nothing to pad, just unused trailing bytes
  per reference doc §5d point 5).
- **Built — `DskImage.ExtendTo(targetLength)`:** pads the in-memory sector array with `0x00` —
  the SAME fill byte confirmed for FORMAT A TRACK's unformatted-sector fill (`jwsformat.asm`
  disassembly, §5d), reused rather than inventing a second convention. Purely in-memory, per the
  existing buffered-write model (no-op if already long enough; sets `IsDirty`, same as any other
  content mutation).
- **Built — out-of-range reads/writes now have defined behavior for the first time:**
  `ReadSector` past the image's actual byte length (an unpadded short mount, continued anyway)
  returns `0x00` fill instead of throwing — mirrors the cartridge's confirmed "open-bus reads
  `0xFF` past a short image" shape (§5c), using disk's own fill byte. A sector straddling the
  boundary returns real bytes for the in-range prefix and fill for the rest. `WriteSector`
  out-of-range is silently dropped (same as write-protected — there's nowhere to put the bytes
  without implicitly growing the image, which only `ExtendTo` does explicitly).
- **Built — `Upd765` carries the mismatch per drive:** a new `MountDisk(drive, image, mismatch)`
  overload (the existing 2-arg `MountDisk(drive, image)` is unchanged, now just forwards `null`)
  plus `GetMismatch(drive)`; `EjectDisk` clears it. This is how a construction-time (`.cfg`-
  authored) mismatch survives past machine assembly — nothing can show a dialog at that point, so
  `P2000.UI` (ms.14e) polls `GetMismatch` the first time a window observes the drive.
- **`Machine`'s constructor updated:** `Board.Fdc.MountDisk(drive.DriveIndex, new DskImage(drive.ImagePath))`
  → `DskImage.Mount(File.ReadAllBytes(drive.ImagePath), drive.Capacity, sides)` then
  `MountDisk(drive.DriveIndex, image, mismatch)`, with `image.MountedPath` stamped explicitly
  afterward (`Mount`'s object-initializer construction path doesn't go through the
  `DskImage(string)` constructor, so nothing sets it automatically the way that constructor does).
- **Tests:** `DskImageTests` (+12): label wins over a mismatched config when it validates; an
  unlabeled (all-zero-label) file whose length matches the configured geometry mounts silently
  (the Basic24k regression guard); a labeled file matching its own config too (ordinary case,
  regression guard against flagging previously-fine mounts); single-candidate and two-candidate
  (the 40DS/80SS collision) mismatches; a no-candidate mismatch reports correct actual/expected
  byte counts and `CanPad=true`; a no-candidate mismatch that's LONGER than expected reports
  `CanPad=false`; `ExtendTo` preserves original bytes and zero-fills the rest, and no-ops when
  already long enough; out-of-range reads (fully and partially beyond the data) return zero-fill;
  out-of-range writes are silently dropped, not thrown. `Upd765Tests` (+4): the mismatch
  plumbing itself (null by default, 2-arg overload leaves it null, 3-arg overload stores it,
  eject clears it). `MultiDriveFloppyTests` (+2): a `.cfg`-authored mount surfaces `None` for a
  correctly-sized image and `NoCandidate` (with the real byte counts) for a genuinely short one,
  through `Machine`'s constructor end-to-end. Full `P2000.Machine.Tests`: 519/519 green (was 501).
- **Flag (per the reference doc block's own note, not silently skipped):** `docs/JWSDOS-format.md`
  wasn't available to edit this pass — it still documents the geometry label without noting that
  it must now be validated against actual file length before use (this milestone). Whoever next
  touches that file should add a short note pointing at this resolution.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`Mount`, `ExtendTo`, `ReadSector`/
  `WriteSector` bounds handling, `DiskGeometryMismatch`/`DiskGeometryMismatchKind`),
  `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`MountDisk` overload, `GetMismatch`),
  `src/P2000.Machine/Machine.cs` (constructor mount path), `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`. Reference doc §5d's "RESOLVED
  — the label-based auto-detect above is JWSDOS-specific" block.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §5d — new "IMPLEMENTED (machine
  milestone 20d...)" paragraph confirms this was built exactly as designed, plus documents the
  two out-of-range read/write behaviors and the `docs/JWSDOS-format.md` companion note, now
  also added).

### 2026-07-26 — Milestone 20c IMPLEMENTED: `Machine.CaptureCurrentConfig()` (+ a real prerequisite gap found and closed: neither device tracked its own mount path at all)
- **Found before building anything (the actual blocker for this milestone):** the milestone's own
  spec assumes reading "the LIVE devices' current mounted path" is a simple query —
  `Upd765.GetDisk(i)`'s mounted path, `MdcrDevice`'s mounted path. Neither existed. Grepped the
  whole `src/` tree for any existing path-tracking concept (`MountedPath`/`MountPath`/`SourcePath`/
  a private `_path` field) — zero matches. Path tracking lived ONLY at the UI layer
  (`CassetteDeckVm`/`DiskDriveVm`'s own private `IStorageFile? _backingFile`, used solely to decide
  Save vs. Save-as), never on the machine-layer device objects themselves. Since
  `Machine.CaptureCurrentConfig()` lives in `P2000.Machine` (no dependency on `P2000.UI`, and must
  stay that way per the dependency direction), the device objects had to grow this capability
  themselves — not delegate up to a ViewModel that doesn't exist at this layer.
- **Built (prerequisite):** `DskImage.MountedPath` (public settable `string?`) — the
  `DskImage(string path)` constructor now sets it automatically (`=> MountedPath = path` on the
  constructor initializer); the bytes-only constructor, `CreateBlank`, and `FromEmbeddedState`
  all leave it `null` (unbacked), matching the existing "no path = no backing file" convention
  `IsDirty`/`WriteProtected` already established. `MdcrDevice.MountedPath` (same shape) — `InsertTape`
  gained an optional `string? path = null` parameter (fully backward-compatible — every existing
  caller across `Machine.cs`/tests/`CassetteDeckVm` still compiles unchanged) that sets it;
  `InsertBlankTape`/`EjectTape` both clear it back to `null`. `Machine`'s constructor now passes
  `Config.CassettePath` through to `Mdcr.InsertTape(bytes, Config.CassettePath)` — disk needed no
  equivalent change since it already mounts via `new DskImage(drive.ImagePath)`, which now stamps
  the path for free.
- **Built (the milestone itself):** `Machine.CaptureCurrentConfig()` — `Model`/`Board`/`RamVariant`/
  `BankCount`/`MonitorRomPath`/`Slot1CartridgePath`/`RamSeed` echo straight from `Config` (none are
  live-swappable); `FloppyDrives[i].ImagePath` is rebuilt per configured drive index from
  `Board?.Fdc?.GetDisk(i)?.MountedPath` (topology — `DriveIndex`/`Enabled`/`Capacity`/`Sides` —
  still echoes `Config`, only the path is re-derived); `CassettePath` is `Mdcr.HasTape ?
  Mdcr.MountedPath : null` (a mounted-but-unbacked blank tape correctly still yields `null`, same
  as no tape at all — `MachineConfig.CassettePath` has no "start with a blank tape" concept to
  capture toward). Read-only, callable any time the machine is running.
- **Scope note — only iterates `Config.FloppyDrives`' own configured drive indices, not a raw
  0-3 sweep:** matches the spec's own framing (a drive already present in config gets a DIFFERENT
  image live-mounted, never a brand-new drive index appearing out of nowhere — topology is fixed,
  and nothing in this build lets a client mount into an unconfigured drive slot anyway).
- **Tests:** `MachineTests` (+5): bare machine captures equivalent to its own config (no drives/
  cassette); a live disk swap (`Fdc.MountDisk` over an already-config-seeded drive) is reflected
  in the captured `ImagePath` while `machine.Config.FloppyDrives[i].ImagePath` stays stale at its
  original construction-time value (the core regression this milestone exists to fix); same for
  cassette via a live `Mdcr.InsertTape` swap; SLOT1/RAM/board fields always echo the original
  config; a captured config fed into `new Machine(captured)` mounts the same media (round-trip
  sanity check). Full `P2000.Machine.Tests`: 501/501 green (was 496).
- **Applies to:** `src/P2000.Machine/Machine.cs` (`CaptureCurrentConfig`, cassette-mount path
  wiring), `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`MountedPath`),
  `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs` (`MountedPath`, `InsertTape` overload),
  `tests/P2000.Machine.Tests/MachineTests.cs`. Reference doc §3a's "RESOLVED — startup
  configuration" block.
- **Synced:** yes (2026-07-27, into `docs/P2000T-reference.md` §3a, "RESOLVED — startup
  configuration" block — new "IMPLEMENTED (machine milestone 20c...)" paragraph documents this
  `MountedPath` prerequisite and `CaptureCurrentConfig()` exactly as built).

### 2026-07-24 — CONFIRMED: Format A Track's real P2000 command bytes + execution mechanism (owner-supplied disassembly of the standalone JWSFormat.bin formatter)
- **Trigger — owner:** delivered `docs/jwsformat.asm`, a personally-produced disassembly of
  `JWSFormat.bin` (the standalone formatter utility flagged as a separate application on
  2026-07-23), following through on "I will provide more information, and hopefully a
  disassembly, later."
- **Supersedes the 2026-07-23 entry below's "NOT confirmed as JWSDOS's format mechanism" finding**
  — that was correct as far as it went (format isn't in `jwsdos5.0.asm`'s resident DOS), and is
  now completed by this separate formatter's source.
- **Exact confirmed FORMAT A TRACK command bytes, byte-for-byte match to the general-datasheet
  shape already modeled in `docs/FDC-implementation.md` §4** (nothing about the 6-byte command
  phase needed to change — only its status, modeled → confirmed): `06 4D <HD/US> 01 10h 32h 00h`
  — length 6, opcode `0x0D`\|MF(bit6), HD/US set at runtime, N=1 (256 B/sector), SC=16 sectors/
  cylinder (matches confirmed disk geometry), GPL=0x32 (gap-3, 50 decimal), D=0x00 fill byte.
- **Execution phase confirmed exactly as predicted — reuses the existing Write Data semi-DMA
  byte-poll mechanism, no new transfer plumbing needed in `Upd765`:** per sector (×SC=16), the
  host feeds 4 bytes (Cylinder, Head, Record, N) via `outi` to port `0x8D`, gated by the same
  `0x90` bit0 "byte ready" poll used elsewhere.
- **Bonus finding — Cylinder off-by-one, reinforces the existing ID-verification-leniency
  conclusion (reference doc §5d, `Disk.asm`):** `jwsformat.asm` writes `track_index + 1` into
  each track's format-data Cylinder byte, NOT the real 0-based physical track used for SEEK.
  Combined with `Disk.asm`'s own READ/WRITE DATA driver reusing one stale Cylinder byte across
  two different physical tracks and still succeeding, this is now **two independent real-software
  data points** that P2000 software never relies on strict ID-field Cylinder verification.
  Recommendation carried into milestone 19a's scope: `Upd765` should not gate READ/WRITE/FORMAT
  success on an exact C-byte match (moot anyway for this project's formula-addressed `DskImage`).
- **Two more confirmations from the same source:**
  - HD/US byte bit 2 = side/head select, confirmed exactly against the datasheet's
    `0 0 0 0 0 HD US1 US0` layout (`get_disk_side`'s `set 2,a` for side 2 of a drive).
  - User-facing drive numbers 1-4 map to internal drive indices **1, 2, 3, 0**
    (`get_drive_choice` + `and 003h`: '1'→1, '2'→2, '3'→3, '4'→0) — relevant if `P2000.UI`'s
    drive-tab numbering (§14) ever needs to match real P2000 software's own convention.
  - **Sense Drive Status independently reconfirmed by a SECOND real program:**
    `JWSFormat.bin`'s own `check_write_protect` sends the identical `02 04 <drive>` shape and
    tests the identical ST3 bit 6, from a completely separate codebase than `jwsdos5.0.asm`'s
    `check_write_enable`.
- **Applies to:** `docs/FDC-implementation.md` §2 (full rewrite of the Format A Track paragraph),
  §13.19a above (Format A Track bullet rewritten from "not confirmed" to "fully confirmed" +
  test-strategy bullet updated to add a real integration test), reference doc §5d (Format A
  Track confirmed bytes + ID-verification-leniency reinforcement + HD/US bit2 + drive-number
  mapping, all added).
- **Synced:** yes (2026-07-24, into `docs/FDC-implementation.md` §2, this project's own §13.19a/
  §17, and `P2000T-reference.md` §5d — all three done this pass) — implementation still
  outstanding.

### 2026-07-24 — Milestone 19a IMPLEMENTED: full 15-command µPD765 set + opcode-identity correction for the real ROM's "READ DATA" byte
- **Trigger:** the Format A Track confirmation above unblocked the milestone; picked up in full
  (generalized FSM + all 9 remaining commands + backfilled result phase + tests), not staged.
- **Opcode-identity finding — the byte milestone 19 confirmed and labelled "READ DATA" (`0x42`)
  is actually READ A TRACK, not READ DATA.** Derived from already-established project facts, not
  new disassembly: WRITE DATA's confirmed real byte (`0x45 = 0x05|0x40`) already proves the MF
  bit (bit 6) is set platform-wide (settling the FM-vs-MFM open item flagged in reference doc
  §5d — MFM is confirmed). Given that, `0x42` can only decode as `0x02|0x40` (READ A TRACK's base
  opcode per the datasheet's own numbering) — it can never equal `0x06|0x40 = 0x46` (READ DATA's).
  **Behaviourally invisible in every known real usage** (R is always `1` in the confirmed bytes,
  and READ A TRACK's "ignore R, always start at sector 1" is byte-identical to "R=1, respected"),
  so nothing that previously worked changes — `getdos`'s two-track load and both real disk-image
  fixtures still pass unchanged. A genuine, separate READ DATA (`0x06`) is now modeled as one of
  the 7 commands with no known real P2000 caller. Full derivation in `Upd765`'s class doc comment.
- **Built — full 15-command Command/Execution/Result FSM** (`Upd765`, per
  `docs/FDC-implementation.md` §6): dispatch now keys on the command byte's masked base opcode
  (bits 4-0), not a literal per-caller byte, generalizing milestone 19's own "match on real bytes"
  rule to the whole command space. All 9 previously-unimplemented commands added: READ DATA
  (0x06), READ DELETED DATA (0x0C), WRITE DELETED DATA (0x09), READ A TRACK (0x02, reclassified
  from 0x42 above), READ ID (0x0A), FORMAT A TRACK (0x0D, confirmed bytes from the entry above),
  SCAN EQUAL/LOW-OR-EQUAL/HIGH-OR-EQUAL (0x11/0x19/0x1D), SENSE DRIVE STATUS (0x04, already
  confirmed real usage since 2026-07-23 but not yet wired into dispatch until now).
- **Result phase backfilled onto every command, including retroactively onto Read/Write Data —
  and this turned out to be REQUIRED, not just chip-fidelity nicety.** Milestone 19's own doc
  comment claimed "the ROM driver never reads it." Re-reading `docs/Monitor Documented
  Disassembly/Disk.asm` while wiring this up found the opposite: the disk-complete interrupt
  handler (`disk_IO_interrupt` → `read_IO_status`) calls `read_status_bytes` with **B=7** — the
  ROM has always drained exactly 7 result bytes after a completed READ A TRACK. Under milestone
  19's no-result-phase model this silently read back open-bus `0xFF`×7 into a buffer nothing else
  consulted, so it happened to work; now it reads the real ST0-ST2/C/H/R/N block. **Consequence
  for anything driving the chip directly (tests, `P2000.UI`):** the chip now stays busy
  (`ReadStatus()` non-`0x80`) until those 7 bytes are drained — a command byte written to
  `WriteData()` while a result is still pending is silently dropped (same `Phase.Idle`-only gate
  RESET already had). Real ROM code already does this drain; test code driving the chip directly
  now must too — updated `Upd765Tests.cs`, `MultiDriveFloppyTests.cs` (both drained 0 bytes before
  chaining a second command), and `P2000.UI.Tests`' `DiskDriveVmTests.HeadAndSector_...` (switched
  from the reclassified `0x42` to real READ DATA `0x46` to keep testing "R is honoured").
- **Minor correctness fix along the way:** `CommitSectorWrites` (formerly inlined in
  `CompleteTransfer`) previously hardcoded the destination sector as `1 + s` instead of
  `_transferStartSector + s` — invisible before now (every real caller starts at sector 1) but
  wrong for a WRITE DATA/WRITE DELETED DATA starting at any other R. Fixed as part of the
  generalization, not a separate pass.
- **Modeling decisions for the 7 commands with no known real P2000 caller** (full reasoning in
  `Upd765`'s doc comments, kept here for the reference-doc sync): READ DELETED DATA always
  reports ST2 CM=1 + ST0 abnormal termination (this project's `DskImage` has no per-sector
  deleted-DAM marker, so every sector it ever encounters is the "wrong mark type" a real chip
  would report); WRITE DELETED DATA writes normally (content correctness over untracked DAM
  metadata); READ ID reports the tracked cylinder/HD-byte head/sector 1/N=1 (no per-sector ID-mark
  model to scan, same reasoning as Format's don't-care CHRN); SCAN EQUAL/LOW/HIGH implement the
  full SH/SN algorithm from `docs/FDC-implementation.md` §5 against the mounted disk's real bytes.
- **`.state` bumped v6→v7** (`MachineStateFile`): the FDC block's result buffer grew 2→7 bytes and
  gained `_transferKind`/`_formatFillByte`/`_formatSectorSize` fields — a v6 file's FDC block is a
  different shape entirely, not just missing new fields.
- **Tests:** `Upd765Tests.cs` — 3 existing 0x42 tests renamed/re-commented to READ A TRACK (no
  assertion changes needed beyond draining the new result phase), +1 covering R being ignored;
  +3 Sense Drive Status (WP/writable/T0+TS bits); +1 Read ID; +1 true Read Data (proves R IS
  honoured, unlike Read A Track); +1 Read Deleted Data (CM+abnormal termination); +1 Write
  Deleted Data; +4 Scan (Equal match/mismatch, Low, High); +2 Format A Track (synthetic shape +
  a real integration test replaying JWSFormat.bin's exact confirmed command bytes and 4-bytes-
  per-sector execution loop, asserting the resulting `DskImage` sectors are D-filled).
  `MultiDriveFloppyTests.cs`/`P2000.UI.Tests` updated per the result-phase-drain note above. Full
  `P2000.Machine.Tests`: 482/482 green; `P2000.UI.Tests`: 149/149 green.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (full rewrite),
  `src/P2000.Machine/State/MachineStateFile.cs` (v7 bump), `docs/FDC-implementation.md` (command
  table + opcode-identity note), `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`,
  `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs`.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §5d — the command-bytes table's
  `0x42` row relabeled READ A TRACK with a "CORRECTED" note explaining the MF-bit derivation and
  behavioral invisibility, plus a second note correcting the old "ROM never reads the result
  phase" claim with the `read_IO_status` B=7 finding).

### 2026-07-23 — New milestone flagged (not yet implemented): FDC full 15-command set, plus two real findings from a direct source read
- **Trigger — owner:** don't stop the FDC at "passes the current boot/run test" — implement all
  15 commands the real µPD765/8272A supports, learning from prior emulator implementations of
  the same chip family the way this project already did for SAA5050 (MAME/jsbeeb) and MDCR.
- **Research done (design-doc maintainer pass, web research + a direct grep of this project's
  own `docs/jwsdos5.0.asm`):** full writeup now in new companion doc `docs/FDC-implementation.md`
  (mirrors the SAA5050/MDCR implementation-guide pattern). Summary of the two things that
  actually changed what's "confirmed" vs. "assumed" for THIS platform specifically (as opposed
  to generic chip-datasheet facts, which the new doc also has in full):
  - **SENSE DRIVE STATUS is real, confirmed usage, not just a datasheet command.** Direct read
    of `jwsdos5.0.asm`'s `check_write_enable` routine: sends `02 04 <drive>`, reads 1 result
    byte, tests bit 6 for write-protect — exact match to the standard ST3 layout. First sourced
    confirmation this chip's status-bit semantics apply unmodified here. Synced into reference
    doc §5d.
  - **FORMAT A TRACK is NOT confirmed as JWSDOS's format mechanism — checked specifically and
    not found.** The owner expected this was "undoubtedly" used by JWSDOS/PDOS format
    utilities; `jwsdos5.0.asm`'s resident DOS command table (LOAD/SAVE/RUN/ZOEK/WIS/VP/SYS) has
    no format command at all, and `VP` (checked directly on suspicion it might be a Dutch
    "voorbereiden"/prepare command) turned out to be an unrelated load-with-relocation variant.
    Either the real formatter is a separate utility program not in this disassembly, or it
    works some other way — genuinely open, not resolved. Build Format A Track from the general
    datasheet regardless (it's real, useful chip behavior and the priority within the new
    milestone per the owner's request) but don't claim P2000-specific confirmation that isn't
    there. Revisit if the owner sources the actual format-utility code.
  - Also corrected a small pre-existing inaccuracy: this project's own docs said "the complete
    16-command µPD765 set" in one place — that number came from eyeballing MAME's C++ enum,
    which includes enhanced-later-chip-only commands beyond the real base-chip 15. Corrected to
    15 in reference doc §5d, with the enhanced-chip entries explicitly named as out of scope.
- **New milestone added:** project CLAUDE.md §13.19a (fast-follow to M19) — full writeup there
  and in `docs/FDC-implementation.md`. Not yet implemented.
- **Applies to:** `docs/FDC-implementation.md` (new), reference doc §5d (Sense Drive Status
  confirmation + 15-command correction) / `src/P2000.Machine/Devices/Fdc/Upd765.cs` (future
  implementation target).
- **Synced:** yes (2026-07-23, into P2000T-reference.md §5d) — implementation outstanding.

### 2026-07-23 — IMPLEMENTED: Upd765 live current-sector tracking (closes the flag below) + a real seek-status bug fix found along the way
- **Implements the flag immediately below** (owner authorization): `Upd765.TransferStatus`
  gained a `Sector` field. Two new fields, `_transferStartSector`/`_transferSectorSize`, are
  set in `DispatchReadWrite` from the command's own R/N bytes (already read locally, just not
  retained); `CurrentTransfer.Sector` computes `_transferStartSector + _transferIndex /
  _transferSectorSize` (guarded against `_transferSectorSize == 0`) — advances live as bytes
  move through the semi-DMA loop, not pinned to the starting sector for the whole transfer.
- **Found (real bug, not introduced by this change but surfaced while touching the same
  struct):** `BeginSeek` never set `_transferDrive` — `CurrentTransfer.Drive` during a SEEK
  reported whichever drive last did a READ/WRITE DATA transfer (or 0, if none ever had), not
  the drive actually being sought. The host status surface (`P2000.UI` milestone 14's
  `DiskDriveVm`) would have lit up the wrong drive's activity indicator during a seek on a
  different drive. Fixed: `BeginSeek` now sets `_transferDrive = drive`.
- **Scope call — Head/Sector during a SEEK stay a known, accepted cosmetic imprecision, NOT
  fixed to be command-type-aware:** a SEEK is also `Phase.ExecutionPhase` but has no head/
  sector of its own; `CurrentTransfer.Head`/`.Sector` during a seek show whatever the LAST
  real READ/WRITE DATA transfer's values were (stale, not meaningful) rather than something
  seek-specific. Building real per-command-type status (and Format/Scan's own shapes) is
  milestone 19a's job (full command-phase generalization), not a one-off patch here — see the
  entry further below.
- **`.state` bumped v5→v6, MinVersion 5→6 (reject v5) — this time the byte layout itself
  changed, not just the config JSON:** the two new int32 fields are written/read mid-stream in
  `Upd765.SaveState`/`LoadState`, between the existing transfer-drive and byte-ready fields. A
  v5 file's FDC block is 8 bytes shorter than v6 expects — reading it under the new layout
  would misalign every field after that point, not just silently drop the new ones.
- **Tests:** `Upd765Tests` (+3): existing `CurrentTransfer_DuringReadData...` test extended
  with a `Sector` assertion; new `CurrentTransfer_MultiSectorTransfer_SectorAdvancesAsBytesMove`
  (Turbo policy, isolates the arithmetic from timing); new
  `CurrentTransfer_DuringSeek_ReportsTheSeekingDrive_NotAStaleOne` (regression guard for the
  bug fix — completes a transfer on drive 0 first, then seeks drive 2, confirms `Drive` reports
  2 not stale 0). `MachineStateFileTests` (+1): `Load_VersionFive_Throws`. Full
  `P2000.Machine.Tests`: 468/468 green (was 465).
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`TransferStatus.Sector`,
  `_transferStartSector`/`_transferSectorSize`, `BeginSeek` fix, `SaveState`/`LoadState`),
  `src/P2000.Machine/State/MachineStateFile.cs` (v6 bump),
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs` — consumed by
  `src/P2000.UI/ViewModels/DiskDriveVm.cs` (`HeadText`/`SectorText`, `P2000.UI/CLAUDE.md` §18).
- **Synced:** no (implementation-only; the sector-tracking DECISION itself was already synced
  via the flag entry below when it was authorized).

### 2026-07-23 — Flag (not yet implemented): Upd765 needs a live current-sector value during a transfer
- **Trigger — owner, resolving what `P2000.UI` milestone 14 scoped out** ("sector" flagged as
  not persisted by `Upd765` outside an active transfer's own command bytes, so it was left off
  the live status row rather than guessed): *"that is officially not fed back from the drive,
  so only the starting sector of a multi sector read is known, however, we could plug into the
  internals of our FDC emulator and find out from there, I suppose?"*
- **Decision (this entry IS the authorization to implement):** yes — extend
  `Upd765`'s transfer-status tracking (the same `TransferStatus`/`CurrentTransfer` surface M14
  already added for `Head`) with a running **current sector** value, derived from state the
  chip already implicitly has during a semi-DMA transfer: the command's starting sector (R, from
  the 9-byte parameter block) plus however many bytes have moved through the `0x8D`/`INI`
  byte-loop so far. `current_sector = R + floor(bytes_transferred / bytes_per_sector)`,
  wrapping at EOT per normal CHS sector-increment rules — this is exposing already-tracked
  internal state, not adding new state, same category as the `MotorOn`/`GetCylinder`/
  `CurrentTransfer` accessors M14 already added. For a single-sector command this collapses to
  just R (the "at least the starting sector is knowable" case); for a real multi-sector run it
  should visibly advance as the transfer progresses.
  - **Idle (no command in flight): no sector value** — matches the parallel head-value decision
    (`P2000.UI` CLAUDE.md §14 "Live status row" — owner, 2026-07-23): both head and sector show
    "–" when nothing is happening, since neither is a real persistent register on idle
    hardware; both show the REAL value once something is.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`TransferStatus`/
  `CurrentTransfer` — add current-sector tracking) — consumed by `P2000.UI/CLAUDE.md` §14's
  live status row (`DiskDriveVm`).
- **Synced:** no (implementation-only accessor addition — no new hardware fact; "sector isn't a
  real fed-back register on idle hardware" was already true and unchanged).

### 2026-07-23 — Flag (not yet implemented): floppy+RAM board is an atomic package, not board+separate-RAM-tier
- **Trigger — owner's request:** add UI to let the user add the Philips memory/CTC/FDC
  extension board (and its memory) to their machine. In discussion, the owner clarified the
  intended shape directly: *"I would make the Philips extension board an 'atomic unit'.
  Homebrew or 3rd party memory card: that one should be configurable regarding # of banks."*
- **Found (design-doc pass — this over-constrains the wrong axis today):**
  `Machine`'s constructor (added during M19, §17 2026-07-22 entry) currently throws unless
  `Board == InternalBoard.FloppyRam` implies `RamVariant == RamVariant.T102` exactly — i.e. it
  validates board-vs-RAM-tier as two independently-set fields that happen to be cross-checked,
  rather than modeling RAM capacity as something that BELONGS to whichever board is chosen.
  That's the wrong shape for what's being asked now, even though it happened to enforce the
  right real-world outcome (floppy+RAM ⇒ T/102) as a side effect of a stricter equality check.
- **Decision (this entry IS the authorization to implement):**
  - **Floppy+RAM is atomic.** Selecting it is ONE choice — FDC + CTC + the one confirmed real
    RAM capacity (T/102, 80 KB) all appear together, same as plugging in one physical card.
    **No separate memory-size control exists for this board** — there was never a smaller or
    larger "official" version to pick between, so don't build a dial with only one legal
    position; just auto-set the capacity and show it as read-only/implied, the same way there's
    no separate CTC checkbox next to it.
  - **RAM-only board is the configurable axis.** It models a homebrew/3rd-party RAM-expansion
    card — no single official product (reference doc's own existing note: "homebrew RAM cards
    decode more bits for more banks"). THIS is where a bank-count control belongs. T/54 is a
    reasonable default value for it, not a second hardcoded "official tier."
  - **`MachineConfig` shape implication:** `RamVariant` as a flat, independently-set enum
    crossed against `Board` is the wrong representation of this. Prefer something closer to:
    `Board` (None/RamOnly/FloppyRam) where `Board == FloppyRam` implies a fixed, non-configurable
    capacity (no user input needed/possible), and `Board == RamOnly` carries its own bank-count
    value that IS user-set (bounded to some sane range — exact real-world bank-count ranges for
    homebrew cards are not sourced; pick a reasonable bound and flag it as unverified rather than
    inventing false precision). `Board == None` is the fixed T/38 baseline, not a "variant."
    Whether this is best expressed as reshaping `RamVariant` itself or replacing it with a
    board-scoped capacity field is an implementation-level call — the constraint that matters is
    the one above (no dial on FloppyRam, a real dial on RamOnly), not the exact C# shape.
  - **Machine.cs validation should relax accordingly:** instead of "FloppyRam requires
    RamVariant==T102 exactly" (a coincidentally-correct equality check), the real invariant is
    "FloppyRam always HAS T102-equivalent capacity, by construction, not by user choice" — there
    should be no code path where FloppyRam could be paired with a different capacity to reject
    in the first place, because the UI/config layer should never offer that combination. If the
    equality check stays as a defensive assertion after this refactor, that's fine — just not as
    the primary mechanism enforcing the rule.
  - **Config-window UI implication (mirrors into `P2000.UI/CLAUDE.md` §7 + a milestone-14-
    adjacent UI task, not yet scoped as its own numbered milestone here):** checking
    "floppy+RAM" in the board selector should immediately imply FDC+CTC+RAM together with no
    further memory choice shown; checking "RAM-only" should reveal a capacity/bank-count
    control; checking "none" hides both.
  - **Drive-config retention on board removal — DECIDED (owner, 2026-07-23):** switching the
    board away from Floppy+RAM should PRESERVE the configured `FloppyDrives` list (not clear
    it) — the machine layer simply doesn't mount any of it while `Board != FloppyRam`; switching
    back to Floppy+RAM should restore the drives exactly as configured. This is a config-
    retention concern (don't null out `MachineConfig.FloppyDrives` just because the board
    changed), not a new validation rule.
- **Not resolved here — needs a real number before the RAM-only dial can ship:** what bank-count
  range is plausible/authentic for a homebrew card (the current T/54 tier implies at least one
  real reference point, but the useful UPPER bound for "3rd-party card" is unsourced) — pick a
  reasonable placeholder and flag it, don't block the atomic/floppy+RAM half of this work on it.
- **Applies to:** reference doc §3a (Config axes — "Board/RAM coupling model", updated same
  date) / `src/P2000.Machine/Machine.cs` (constructor validation), `MachineConfig.cs`
  (`RamVariant`/`Board` shape) — `src/P2000.UI/CLAUDE.md` §7 (Config window axes, updated same
  date), a future Config-window UI milestone (board selector + conditional capacity control).
- **Synced:** yes (2026-07-23, into P2000T-reference.md §3a — the coupling model decision) —
  implementation (both machine-layer validation relaxation and the config-window UI) still
  outstanding.
- **Status update (2026-07-23, after `P2000.UI` milestone 14 — see that project's CLAUDE.md
  §14 write-up):** the UI-side "no dial on Floppy+RAM" half is effectively already true —
  `ConfigWindowVm` auto-forces `RamVariant.T102` and disables the RAM selector the moment
  Floppy+RAM is chosen, as a side effect of milestone 14's new board selector, not because this
  entry was specifically implemented. **Still outstanding:** (a) the RAM-only board still only
  offers the same three fixed named tiers (T/38 · T/54 · T/102) rather than a genuine
  bank-count dial for homebrew/3rd-party cards — the actual "configurable axis" half of this
  decision; (b) `Machine.cs`'s validation is still the coincidental equality check
  (`FloppyRam` requires `RamVariant == T102` exactly), not restructured to make the invalid
  combination unrepresentable by construction; (c) whether drive-config is preserved (not
  cleared) when the board is switched away from Floppy+RAM was not confirmed one way or the
  other by the milestone-14 write-up — needs checking before this entry is considered done.

### 2026-07-23 — CHANGED (owner request): blanking margin is now dark grey, not pure black
- **Trigger:** owner reported the Full-Field crop's blanking margins render as full black,
  making the boundary against an all-black active picture (background colour 0 is also pure
  black — `Saa5050Palette.ColorTable`) invisible. Requested a very dark grey instead, purely
  for visual debugging — NOT a hardware-accuracy claim; real hardware's blanking signal is
  genuinely black.
- **Fix:** added `Video.BlankingColor` (`internal const uint`, `0xFF202020` — BGRA8888, opaque,
  RGB (32,32,32); channel order is irrelevant for a pure grey). `Video`'s framebuffer is now
  filled with this (`Array.Fill`) instead of zeroed (`Array.Clear`) at both construction
  (`CreateBlankedFramebuffer()`) and `Reset()`. Nothing else changes — the active window still
  overwrites its own pixels every fetch regardless of what the margin holds, so there's no
  added per-field cost, and `CorruptLastFetch`'s contention-corruption blanking (a distinct,
  already-documented "black/suppression" concept — reference doc §4) is deliberately left as
  pure black, unaffected by this change.
- **Tests:** `VideoTests` (+1): a freshly-constructed machine's margin pixel is
  `Video.BlankingColor`, not `0`. Updated 2 pre-existing tests that asserted an untouched pixel
  is exactly `0u` (`FirstField_IsEven...`'s odd-row check, `Reset_ClearsTheFramebuffer...`) to
  expect `Video.BlankingColor` instead — genuine behavior changes, not incidental breakage.
  `MachineTests.Reset_ClearsTheVideoFramebuffer` renamed to
  `Reset_FillsTheVideoFramebufferWithTheBlankingColor` and updated the same way. Full
  `P2000.Machine.Tests`: 459/459 green (was 458). `P2000.UI.Tests` not re-run this pass — the
  owner's own `P2000.UI` instance was running locally and holding a file lock on
  `P2000.Machine.dll`; no `P2000.UI` source changed, so no regression expected, but flag to
  re-run once free. **The owner's running instance predates this fix — needs a relaunch to
  show the new margin colour** (same caveat as the 2026-07-21 pre-roll fix entry above).
- **Applies to:** `src/P2000.Machine/Devices/Video.cs` (`BlankingColor`,
  `CreateBlankedFramebuffer`, `Reset`), `tests/P2000.Machine.Tests/Devices/VideoTests.cs`,
  `tests/P2000.Machine.Tests/MachineTests.cs`.
- **Synced:** no (a deliberate debug/UX choice, explicitly not a hardware fact — nothing to
  correct in the reference doc, which correctly still says real hardware's blanking is black).

### 2026-07-23 — FIXED: RamSeed never serialized in .cfg/.state (gap flagged during M20/20a)
- **Bug:** `MachineConfigFile`'s `ConfigDto`/`ToDto`/`FromDto` never included
  `MachineConfig.RamSeed` (`ulong?`) — only `Model`/`Board`/`RamVariant`/`BankCount`/
  `MonitorRomPath`/`Slot1CartridgePath`/`FloppyDrives` round-tripped. A `.cfg` or `.state`
  saved with an explicit `RamSeed` silently lost it on load (fell back to a fresh random seed
  via `EmulationRunner`, or `PageTable.DefaultRamSeed` elsewhere) — a real, silent correctness
  gap against `RamSeed`'s own doc comment, which describes it as exactly the kind of override a
  saved config should be able to pin (e.g. to reproduce a specific bug report that names its
  seed).
- **Fix:** added `RamSeed` to `ConfigDto` and wired it through `ToDto`/`FromDto` in
  `src/P2000.Machine/State/MachineConfigFile.cs`. `MachineStateFile.cs` needed NO change —
  it only ever serializes the config via `MachineConfigFile.Serialize`, so the fix is entirely
  upstream of it.
- **No version bump (`.cfg` or `.state`), and this is a deliberate call, not an oversight:**
  the field is purely additive and nullable. An old file with no `ramSeed` key still
  deserializes to `null` — IDENTICAL to today's (buggy) behaviour, so no old file's meaning
  changes. This differs from the M20 `FloppyDrives` bump (which renamed/reshaped an EXISTING
  field, so an old file's disk-mount intent would have silently changed under the new DTO) —
  that was a real semantic break; this is a new field with no prior semantics to break. Matches
  this file's own established precedent: `BankCount`/`MonitorRomPath`/`Slot1CartridgePath`/
  `FloppyDrives` were all added to this same DTO over time without bumping
  `MachineConfigFile.CurrentVersion` (still `1`).
- **Tests:** `MachineConfigFileTests` (+2): explicit `RamSeed` round-trips; absent `RamSeed`
  still defaults to `null`. `MachineStateFileTests` (+1): a full `.state` save/reload preserves
  an explicit `RamSeed` via the embedded config. Full `P2000.Machine.Tests`: 458/458 green (was
  455); `P2000.UI.Tests`: 99/99, unaffected (no call site changed).
- **Applies to:** `src/P2000.Machine/State/MachineConfigFile.cs` (`ConfigDto.RamSeed`,
  `ToDto`/`FromDto`), `tests/P2000.Machine.Tests/State/MachineConfigFileTests.cs`,
  `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs`.
- **Synced:** no (implementation-only bug fix, no new hardware content).

### 2026-07-23 — Milestones 20/20a IMPLEMENTED: multi-drive floppy config + cassette/disk dirty-tracking
- **Assumed (per the milestone's own text):** the multi-drive generalization would require real
  chip-layer (`Upd765`) changes — per-drive head/motor/state arrays.
- **Found (the chip layer already modelled 4 drives — M19 built ahead of when M20 was
  written):** `Upd765._drives` (`DskImage?[4]`) and `_cylinder` (`int[4]`) were already
  per-drive arrays since milestone 19; only `_selectedDrive`/transfer state are singular, which
  is correct (real hardware addresses one drive at a time). **The actual gap was entirely at the
  config layer:** `MachineConfig.FloppyDiskImagePath` (singular, implicitly drive 1) and
  `Machine`'s constructor only ever calling `MountDisk(1, ...)`. No `Upd765`/`DskImage` chip
  logic changed for M20 itself beyond the two additive host-API members below.
- **Built (M20):** `MachineConfig.FloppyDrives` (`IReadOnlyList<FloppyDriveConfig>`, replacing
  `FloppyDiskImagePath`) — each entry: `DriveIndex` (0-3), `Enabled`, `Capacity`, `Sides`
  (`DiskSides` enum), `ImagePath`. `Machine`'s constructor validates ≤4 drives, indices in 0-3,
  no duplicates, then mounts every enabled entry with a non-null `ImagePath` at its own index
  (no more hardcoded unit 1). `DskImage.GetBytes()` added for host Save/Save-as (byte-for-byte
  copy, no bitstream encode needed for a raw sector dump) — write-protect and directory-browse
  needed NO new API since `DskImage.WriteProtected`/`ReadDirectory()` are already per-instance
  (reachable via `Upd765.GetDisk(drive)`), and create-blank needed none either
  (`Upd765.MountDisk(drive, DskImage.CreateBlank(tracks, sides))` is already a one-liner) — kept
  the API surface to exactly what wasn't already a one-liner, per root CLAUDE.md's
  no-premature-abstraction rule.
- **Built (M20a):** `IsDirty`/`MarkClean()` added to `DskImage` (set on a real `WriteSector`,
  i.e. not write-protected; cleared by `MarkClean()`) and mirrored on `MiniTape`
  (`IsDirty`/`MarkSaved()`, set on `Write`/`WriteBlockAtHead`, cleared at the end of
  `LoadCasImage` and by `MarkSaved()`) with a same-named proxy pair added to `MdcrDevice`
  (`IsDirty`/`MarkClean()`) mirroring its existing `IsWriteProtected`/`SetWriteProtected`
  pattern. No new device/flag needed beyond these — checked first per the milestone's own
  "verify before adding a second one" instruction; neither device had anything reusable.
- **`.state` bumped v4→v5, MinVersion 4→5 (reject v4), per the milestone's explicit
  instruction — even though the FDC device-state BLOCK's own byte layout is unchanged.** The
  reason is the embedded config JSON, not the device stream: a v4 file's config JSON has
  `floppyDiskImagePath` and no `floppyDrives` key; deserializing it under the new
  `MachineConfigFile` DTO would silently default to an empty drive list rather than failing
  loudly, so a v4 save's mounted disk would silently go unmounted on load with no error — exactly
  the silent-misload class of bug the version-gate discipline exists to catch.
- **Found (pre-existing gap, adjacent but out of this milestone's scope, left as-is):**
  `MachineConfigFile`'s DTO never serialized `RamSeed` at all (only `Model`/`Board`/
  `RamVariant`/`BankCount`/`MonitorRomPath`/`Slot1CartridgePath` round-tripped) — a `.cfg`/
  `.state` load has always silently dropped an explicit `RamSeed`. Not fixed here (unrelated to
  the disk axis this milestone touches); flagged for a separate follow-up.
- **`P2000.UI` compile compatibility (not milestone 14 — that's still explicitly out of scope,
  gated on `P2000.UI/CLAUDE.md` §14.14):** `EmulationRunner.Reconfigure`'s manual field-copy
  (needed because `MachineConfig` has no `with` expression) updated
  `FloppyDiskImagePath = config.FloppyDiskImagePath` → `FloppyDrives = config.FloppyDrives` —
  the only call site outside `P2000.Machine` that referenced the removed field. `MakeConfig()`
  never set it either way, so this is a pure rename with no behavior change; the actual
  multi-drive UI (config axis, drive window, dirty-tracking eject warning) remains UI milestone
  14/14a, unbuilt.
- **Tests:** `MultiDriveFloppyTests.cs` (new) — config validation (0-4 drives accepted, >4/
  duplicate-index/out-of-range-index all throw), per-drive mount-from-config at arbitrary
  indices, disabled drives never mounted, an enabled drive with no `ImagePath` resolves to the
  existing "absent drive" no-op (no new state), two-drive seek independence with no cross-talk,
  per-drive geometry auto-detect, write-protect gating only the targeted drive, create-blank +
  guest-write + Save round-trip, eject-without-save discarding in-memory changes. `DskImageTests`
  (+9): create-blank exact byte size + no label at the auto-detect offsets, `IsDirty`/
  `MarkClean` transitions, `GetBytes` round-trip + copy-not-reference. `MdcrDeviceTests` (+8):
  the same `IsDirty`/`MarkClean` transitions mirrored for the cassette. `MachineConfigFileTests`
  (+2): `FloppyDrives` round-trip (multiple drives, mixed `Enabled`/geometry/path) and the
  empty-by-default case. `MachineStateFileTests` (+2): v4 rejected, a real multi-drive `.state`
  round-trip (two drives seeked to different cylinders, restored, `SENSE INTERRUPT STATUS`
  confirms drive 1's cylinder survived independent of drive 0's). Full `P2000.Machine.Tests`
  suite: 455/455 green (was 416); `P2000.UI.Tests`: 99/99 green, unaffected.
- **Applies to:** project CLAUDE.md §13 milestones 20/20a /
  `src/P2000.Machine/MachineConfig.cs` (`DiskSides`, `FloppyDriveConfig`, `FloppyDrives`),
  `src/P2000.Machine/Machine.cs` (validation + per-drive mount loop),
  `src/P2000.Machine/Devices/Fdc/DskImage.cs` (`IsDirty`, `MarkClean`, `GetBytes`),
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs` (`IsDirty`, `MarkSaved`),
  `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs` (`IsDirty`, `MarkClean`),
  `src/P2000.Machine/State/MachineConfigFile.cs` (DTO `FloppyDrives`),
  `src/P2000.Machine/State/MachineStateFile.cs` (v5 bump),
  `src/P2000.UI/Runner/EmulationRunner.cs` (field-copy rename only),
  `tests/P2000.Machine.Tests/Boot/DiskBootTests.cs` (updated to `FloppyDrives`),
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs` (new),
  `tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`,
  `tests/P2000.Machine.Tests/Devices/MdcrDeviceTests.cs`,
  `tests/P2000.Machine.Tests/State/MachineConfigFileTests.cs`,
  `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs`.
- **Synced:** no (implementation-only — no new hardware facts beyond what M20's own spec
  already carried; the RamSeed serialization gap noted above is a pre-existing bug, not new
  hardware content, and is flagged rather than fixed here).


### 2026-07-19 — Flag (not yet verified against source): VideoFetchUnit vertical/field-position offset
- **Trigger:** owner reported Ghosthunt display glitches concentrated in the **top ~15%
  of the screen**, and asked whether contention modelling accounts for the video chip
  only fetching VRAM during the active display window within a field, not across the
  whole field.
- **New sourced fact (owner-supplied P2000TM Field Service manual, "T-VERSION VIDEO
  GENERATION"):** T-version field = **313 scanlines**; active/displayable window =
  **scanlines 49–289 (240 lines)**. This means **48 lines (~7,680 T-states) of vertical
  blank precede the active window**, and ~24–25 lines (~3,840–4,000 T-states) follow it
  — an asymmetric split, not an even ~36/36. Full detail and T-state math now in
  reference doc §4 ("Display-start offset") and §4a ("Vertical structure").
- **Explicit correction from the owner, must not be lost in any fix:** *"assuming that
  all 50000 cycles are used during the 640×480 area is wrong"* — only ~38,400 of the
  50,000 T-states/field are inside the active window; the rest must be contention-free
  regardless of CPU RAM activity during those T-states.
- **Leading hypothesis (UNVERIFIED — this project's CLAUDE.md instance has not read
  `VideoFetchUnit.cs`, per the design-doc-maintainer role; needs checking against the
  real source, not assumed):** if `VideoFetchUnit`'s fetch/contention-eligible window
  currently starts at field-T-state 0 rather than being offset by ~7,680 T-states
  (48 lines) into the field, it would incorrectly treat real hardware's pre-roll
  vertical-blank T-states as fetch-eligible — producing spurious contention/glitches
  concentrated at the top of the frame. 48/313 ≈ 15.3%, closely matching the reported
  "top 15%" symptom, which is why this is the leading hypothesis, but **verify against
  the actual implementation before changing anything** — neither of the two existing
  milestone-10 findings entries (2026-07-05, 2026-07-06 below) address vertical
  raster position at all, so this is genuinely unaddressed ground, not a re-litigation
  of settled work.
- **If confirmed:** the fix is presumably to gate fetch-slot scheduling (and therefore
  contention eligibility) so it only runs during field-T-states corresponding to
  scanlines 49–289 (i.e., skip/no-op the first ~7,680 T-states and the last
  ~3,840–4,000 T-states of each field), rather than across the full 50,000. Confirm the
  exact current start/end behaviour first — this note does not assume the bug exists.
- **RESOLVED (2026-07-21, owner clarification):** the manual's *"no interlacing is
  used"* statement for the T-version is CONFIRMED correct and the owner agrees — the
  P2000T has no real even/odd field pairing into a frame; every field is an
  independent 313-line refresh. This corrected §3 above ("Fields vs frames").
  **Ownership correction (also 2026-07-21):** the display-mode DEFAULT is a
  **P2000.UI-owned setting**, not a machine one (§3's own pre-existing milestone-5
  finding already scoped this correctly — see §3's "Ownership correction" note); the
  owner's decision to default to Odd-only (line-doubled single field) instead of
  Interlaced/comb belongs in `src/P2000.UI/CLAUDE.md` §8 and reference doc §3a, both
  updated. This file (`src/P2000.Machine/CLAUDE.md`) only carries the underlying
  hardware-timing correction, not the UI default.
- **RESOLVED (2026-07-21):** `docs/SAA5050-implementation.md` — the owner supplied the
  actual file content; it now has a local working copy (`SAA5050-implementation.md`,
  de facto canonical as of this pass) and has been updated in parallel with the same
  interlacing correction (§5 "Fields, frames, and CRS").
- **NEW flag, unverified, machine-layer (2026-07-21) — distinct from the UI default
  question above:** whether `Video`'s raw per-field buffer-composition ("each field
  writes only its own alternating half-lines into a persistent buffer") still holds
  now that no true interlace exists — see §3's "Separate, machine-level question" note
  for detail. This is about what data the machine hands to the UI each field, not
  which of the 4 modes the UI defaults to presenting.
- **Applies to:** reference doc §4 (Display-start offset) and §4a (Vertical structure) /
  `src/P2000.Machine/Contention/VideoFetchUnit.cs`, possibly `src/P2000.Machine/Devices/Video.cs`.
- **Synced:** yes (2026-07-19, into P2000T-reference.md §4/§4a) — implementation-side
  verification and any resulting fix still outstanding.
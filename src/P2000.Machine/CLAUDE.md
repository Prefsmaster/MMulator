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
    doc §5c/§5d/§5e + **`docs/JWSDOS-format.md`** for the DOS-specific facts below; the
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
      figure was a typo, see `docs/JWSDOS-format.md` provenance) → checks the loaded byte
      against `0xF3` ("system disk" signature) → cleanup always runs: CTC ch0 reset, FDC off,
      **RAMSW restored to `0x00`** (bank 0) — whatever runs the loaded DOS extension must
      itself re-select bank 1 before jumping into it.
      **`0xF3` signature — CONFIRMED, feeds directly into the RUN-gate test design
      (`docs/JWSDOS-format.md` §6/§7); `0xF3` is specifically PDOS's (Philips DOS's) own
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
      assumed to share `jwsdos5.0.asm`'s directory format (`docs/JWSDOS-format.md` §4). Not
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
    - **Disk geometry / JWSDOS format — see `docs/JWSDOS-format.md` (companion doc, don't
      duplicate here, mirrors the MDCR pattern):** 16 sectors/track, 256 B/sector (CONFIRMED
      from `getdos`); JWSDOS 5.0 itself supports **multiple geometries** (35/40/80-track,
      SS/DS) as a per-disk format-time choice — supersedes the reference doc §5d/§3a's
      "single-sided 35-track" placeholder (**synced** — reference doc §3a/§5b now reflect the
      per-disk geometry + self-describing label). JWSDOS embeds a self-describing geometry
      label on-disk (`docs/JWSDOS-format.md` §3) — **but real JWSDOS itself does NOT read this
      back** to auto-configure its own runtime state (it uses live RAM defaults, changed only
      via its own format menu, `docs/JWSDOS-format.md` §1). **Design decision:** the emulator's
      `.dsk` loader SHOULD auto-detect geometry from this label anyway — a deliberate
      emulator-side UX improvement beyond replicating real JWSDOS behavior, not "just
      matching the hardware." Keeps the "raw sector dump, no header" file convention
      (reference doc §3a) intact since the label is real on-disk JWSDOS data.
      **Auto-detect is two independent fixed-offset single-byte reads, CONFIRMED
      (`docs/JWSDOS-format.md` §3):** side = ASCII `'D'`/`'S'` at raw offset `0x0FEF`; track
      count = binary byte **`− 1`** at raw offset `0x0FFF` (e.g. `0x29` = 41 → 40 tracks). No
      banner-text parsing needed for either field — both are exact-position reads, byte-verified
      against two independent real images.
    - **Host `.dsk` image API** — mount/eject/create-blank/write-protect/browse, always
      host-speed, independent of `TimingPolicy` (the `.cas` API is the template). Read-only
      directory browsing needs only the 32-byte directory-entry struct (`docs/JWSDOS-format.md`
      §4) — no allocation logic. **Browse ONLY the confirmed active directory: raw
      `0x1800`–`0x1FFF` (logical sector 25, `dir_side1_prep`'s target, 18 real entries on the
      `Spel1.dsk` test image) — do NOT parse raw `0x1000`–`0x17FF` (sectors 1–8 of track 2) as
      directory data.** That region is real, struct-shaped, but stale/unrelated data (a
      `JWS Systeem Disk` write-path artifact, `docs/JWSDOS-format.md` §2/§7 item 3) — parsing it
      would surface phantom files that don't belong to the mounted disk. **Side 2's own
      directory location in a raw `.dsk` file is NOT yet confirmed** (`docs/JWSDOS-format.md` §7
      item 2) — for a double-sided image, browse side 1 only until that's sourced; don't guess
      an offset for side 2. Write support (save into a mounted image) needs the gap-reuse/append
      algorithm (`docs/JWSDOS-format.md` §5) — scope as a later concern unless M19 needs write
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
    `docs/JWSDOS-format.md`; extends `InternalExtensionBoard`/`Upd765` from M19, does not
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
      existing one per-drive instead of once globally.
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
      (`docs/JWSDOS-format.md` §3: side at raw `0x0FEF`, track count−1 at raw `0x0FFF`) rather
      than trusting a config value. The new per-drive **Capacity/Sides config axis exists for
      blank/newly-formatted images (no valid label yet) and as a manual override if a label is
      absent or corrupt** — it is NOT a second source of truth competing with the label when one
      is present. State this order explicitly in the implementation (label wins; config is the
      seed for blank media).
    - **Write-protect, per drive, host-side (mirrors the cassette ms.13a pattern):** a live
      `IsProtected` bool per drive, defaults writable, gates WRITE DATA the same way WEN gates
      CSAVE. **Does NOT round-trip through the `.dsk` file** the way cassette protect rides
      spare padding in the `.cas` record container (reference doc §3a) — a raw sector-dump `.dsk`
      has no equivalent spare byte to (ab)use without corrupting real JWSDOS data. **Persistence
      mechanism DEFERRED, not a UX call to pick freely (owner, 2026-07-23) — depends on a bigger,
      still-open question: whether/where UI-layer session state (open windows, memory-watch
      ranges, etc.) gets persisted at all** (reference doc §3a, "OPEN DESIGN QUESTION"). The
      owner's reasoning: cassette write-protect already lives in `.state` (`MdcrDevice.Protected`),
      and a `.state` load is supposed to restore the machine exactly as it was — so disk
      write-protect should land in whatever container ends up holding "resume exactly where the
      user left off," not be decided in isolation as session-only vs. a sidecar file. **Do not
      pick a mechanism here before that's resolved** — this bullet is genuinely blocked, not just
      unprioritized.
    - **`.state`:** the FDC device block's shape changes from implicit-single-drive to
      per-drive arrays (motor/head/cylinder/write-protect/mounted-image-ref × N) → bump
      `MachineStateFile.CurrentVersion`/`MinVersion` from **v4 to v5** at build time (reject v4),
      same discipline as every prior bump — never retroactively. **"mounted-image-ref" is
      deliberately vague, not yet decided (owner, 2026-07-23) — reference doc §3a "self-contained
      `.state`" note:** whether this is a path (remount required on load, matching the cassette's
      existing precedent) or the actual in-memory disk bytes (making `.state` self-contained/
      shareable) is an open question shared with the cassette's own reopened embedding decision —
      don't build one silently before that's settled; the version bump above happens regardless
      of which way it lands, so it doesn't block starting this milestone.
    - **Host `.dsk` API:** extend M19's API — **mount** (existing file → in-memory image, geometry
      from label), **create-blank** (drive's configured Capacity/Sides → in-memory unformatted
      image, no file involved yet), **eject** (drops the in-memory image; discards unsaved
      changes per the write-model bullet above), **save/save-as** (serialize the in-memory image
      to a host `.dsk` file — for a plain raw sector dump this is a straight byte-for-byte write,
      no bitstream-style encode step the way `.cas` needed), **write-protect**, **browse** — all
      take a drive index; behaviour per call otherwise unchanged from M19. **Side 2 directory
      browsing stays blocked** on the same open item M19 already flagged — side 2's directory
      location in a raw `.dsk` file is not yet confirmed (`docs/JWSDOS-format.md` §7 item 2) —
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
    - **`.state` — DEFERRED, same reason and same dependency as M20's write-protect item (owner,
      2026-07-23):** whether `IsDirty` should serialize into `.state` (a session saved with an
      unsaved cassette/disk change pending would then restore as still-dirty) or stay a
      live/transient UI hint is tied to the still-open "what does resuming a session persist"
      question (reference doc §3a). Same logic as write-protect: `.state` is meant to bring the
      machine back exactly as it was, so this isn't an independent UX call to make now — it lands
      wherever that broader question lands (inside `.state` itself, a UI-state sidecar, or
      elsewhere). Doesn't block the UI-layer warning from being built either way (the flag exists
      and works live regardless of whether it persists) — only the persistence question is
      deferred.
    - **Tests:** (a) a freshly mounted/created image (no writes yet) reads NOT dirty; (b) a
      write (authentic or turbo) sets dirty on both cassette and disk; (c) Save/Save-as clears
      dirty; (d) eject/replace do not themselves clear or set dirty — only reads it; (e) a
      second write after Save re-sets dirty (the flag isn't sticky-false after the first save).
      → commit.

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
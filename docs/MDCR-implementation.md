# MDCR-implementation.md

Implementation guide for the cassette (MDCR — Mini Digital Cassette Recorder) device, for
P2000.Machine **milestone 9**. Read this when starting that milestone. It's grounded in the
project owner's working implementation (`MdcrDevice.cs`, `MiniTape.cs`, `MDCR_Status.cs`,
`MDCR_Control.cs`) and the confirmed I/O ports (reference doc §5b/§5f). It **supersedes the
sketch-level cassette notes** in the reference doc where they differ — this is the detailed
spec.

Priorities: faithful hardware behaviour AND practical implementation, equally.

---

## 0. The big picture: this is a PHASE-BITSTREAM model, not a block model

The owner's implementation is **more physically faithful than the reference doc's original
"authentic vs turbo" sketch assumed.** The tape is modelled as an actual **phase bitstream** —
1,088,520 phases per side, each stored as one bit — and the MDCR device does **real
phase-locked bit recovery** (clock + data recovered from phase transitions), exactly as the
hardware's read circuitry does. The ROM's own cassette read routine then recovers bytes from
the status bits with NO special help. This is the authentic path, taken all the way down to
flux-transition level.

**Two paths (owner decision — keep both):**
1. **Authentic (default): the phase bitstream.** `MdcrDevice` + `MiniTape` as built. Deterministic,
   drives off the master clock (209 cycles/phase), the ROM does real bit recovery. This IS the model.
2. **Turbo (optional fast path): ROM-trap block transfer.** Trap the monitor ROM's load/save
   routines and move whole blocks between the `.cas` image and RAM instantly, bypassing the
   bitstream. Optional, opt-in via `TimingPolicy` — for users who want instant loads.

Both operate on the same `.cas` content. Authentic is the reference behaviour; turbo is a
convenience. Point any determinism/regression tests at **authentic**.

---

## 1. The two I/O ports (confirmed — reference doc §5f)

The MDCR is an I/O device on the shared CPOUT/CPRIN ports. It owns specific bits; the port
dispatch fan-out/combine (machine §6) routes them.

### CPOUT (0x10, write) — control, lower 4 bits (`MDCR_Control`)
| Bit | Flag | Meaning |
|-----|------|---------|
| 0x01 | WDA | Write Data (the data phase level to write) |
| 0x02 | WCD | Write Command (1 = writing, 0 = reading) |
| 0x04 | REV | Motor reverse |
| 0x08 | FWD | Motor forward |
- `Run = REV | FWD` (motor moving). Note bits 4–7 of 0x10 are keyboard KBIEN + printer — NOT the
  MDCR's; the device masks `value & 0x0F`.

### CPRIN (0x20, read) — status, upper bits (`MDCR_Status`)
| Bit | Flag | Meaning (active sense per reference doc §5f) |
|-----|------|-----|
| 0x08 | WEN | Write-enable: SET when writable (0 = protected, per doc `(N)` — see §5 below) |
| 0x10 | CIP | Cassette-in-place: SET when NO cassette (per doc `(N)`) |
| 0x20 | BET | Begin/End of tape: SET when tape OK, clear at an end |
| 0x40 | RDC | Read Clock — toggles per recovered bit |
| 0x80 | RDA | Read Data — recovered data bit |
- Bits 0–2 (PRI/READY/STRAP) are the printer's — not the MDCR's.

---

## 2. Timing: the 209-cycle phase

- A bit is written as **2 phases of 209 processor cycles each** (from `MiniTape` header math:
  ~91 s × 2,500,000 Hz / 209 ≈ 1,088,516 phases/side; rounded to 1,088,520).
- `MdcrDevice.Tick()` accumulates master-clock ticks and processes one phase per 209 cycles.
  This is how the device slaves to the deterministic master clock (machine §3 tick loop).
- **Integration:** the machine's tick loop calls `mdcr.Tick(1)` each T-state (or batches); the
  device does tape motion + head read/write only while the motor runs (`Run`), one phase per
  209 accumulated cycles.

---

## 3. Tape motion & the transport (`MiniTape`)

- `Position` is a phase index 0…PhasesPerSide-1; `Forward()`/`Reverse()` step it by one phase
  (clamped at the ends). The motor bits (FWD/REV) drive which way each phase step goes.
- **EOT** = `Position == 0 || Position == PhasesPerSide-1` (either physical end). Drives BET.
- Two sides; `SetSide()` flips and inverts `Position` (tape physically reversed).
- A fresh `MiniTape` fills both sides with **random garbage** and starts at a random-ish
  position — modelling a blank/unknown tape (so the ROM's search finds noise until real data).
- **Protection:** `Protected` (per side) → drives WEN. A loaded `.cas` sets write-protect on.

---

## 4. The read path: phase-locked bit recovery (the clever part)

When reading (WCD clear) and the motor runs, each phase the device reads a tape bit and runs a
**software PLL** to recover clock+data, mirroring the hardware:
- **Unlocked:** watch for a phase transition (`phaseOld != phaseNew`). First transition = first
  bit → emit it, set `phaseLocked`, reset `phaseCount`.
- **Locked:** every **2nd** phase must differ from the first (a real bit = a hi-lo or lo-hi
  transition across the 2-phase pair). If it does → emit the bit, `phaseCount = 0`. If it
  doesn't → **lock lost**, resynchronise.
- Emitting a bit = `BitToStatus()`: toggles **RDC** (the read clock the ROM watches) and sets
  **RDA** to the data level. The ROM polls CPRIN, sees RDC flip, samples RDA — exactly the
  self-clocking read pair from reference doc §5f.

**Keep this PLL intact** — it's what makes the ROM's real read routine work against the
bitstream without any trapping. It's the heart of the authentic path.

### Reverse-direction handling — TOGGLEABLE (owner: unverified)
`BitToStatus(newPhase, flipBits)` has a **reverse-motor branch** the owner flagged as
unverified: when running in REVERSE, it flips **RDA** (data) instead of **RDC** (clock),
assuming the clock bit lands on the data pin when the motor runs backward. This is a
hypothesis, not confirmed hardware.
- **Implement it as a TOGGLE** (owner instruction): a config/option flag selecting the
  reverse-direction bit mapping, defaulting to the current behaviour, so it can be flipped off
  or changed when hardware behaviour is confirmed.
- Log the outcome in the machine findings log (§17) once real read-while-reversing is observed.

---

## 5. The write path

- When WCD set (writing) and motor runs, `tape.Write(WDA)` stores the current data phase level
  at `Position`. So CSAVE's bitstream is captured phase-by-phase into the tape — symmetric with
  read.
- **WEN active sense — RECONCILE with reference doc §5f:** the reference doc's §5f table says
  bit 3 (WEN) `(N)`: **0 = writable, 1 = protected**. This implementation's `Reset()` **sets**
  the WEN bit when the tape is writable (not protected). These are opposite senses — the doc
  table says WEN=1 means protected; the code sets WEN=1 when writable. **CONFIRM which is
  correct against the ROM's write-protect check** (the ROM reads CPRIN bit 3 and decides). This
  is exactly the kind of `(N)` active-low bit the doc warned about (§5f). Flag in findings and
  make the implementation match whatever the ROM actually tests. (The milestone-4 CprinReader
  finding already implemented the DOC's sense — bit set = protected — so these two components
  must agree; resolve before wiring them together.)

---

## 6. The `.cas` format — AUTHORITATIVE (owner-confirmed)

Documented here as the authoritative P2000T `.cas` layout (from `MiniTape.LoadCasImage`). **Do
NOT use MSX/Atari `.cas` formats — different formats.**

**Per-record layout (1280 bytes per block record in the file):**
```
[0x000-0x0FF]  256 bytes: P2000 memory 0x6000-0x60FF
               0x00-0x2F  ignorable (internal: keyboard status etc.)
               0x30-0x4F  BLOCK HEADER (32 bytes — name, length, type…)
               0x50-0xFF  ignorable (internal: BASIC ROM variables)
[0x100-0x4FF]  1024 bytes: DATA block
```
- File = a sequence of these 1280-byte records; `blocks = casImage.Length / 1280`.
- Loader copies header from `+0x30` (32 bytes) and data from `+0x100` (1024 bytes) per record.
- **The 'P' file-type auto-boot** (reference doc §5b) reads the file **type** from the 32-byte
  header at 0x30 — so header parsing must expose the type byte.

**Tape-image layout `LoadCasImage` writes (phases):**
```
BOT GAP       5800 phases   (skipped at start)
per block (×N):
  BOB GAP     6160 phases
  MARK        (empty marker via WriteData)
  1024 data   + framing
  EOB GAP     1856 phases
EOT GAP       (remainder, zero-filled)
```
- Each `WriteData(bytes)` frames as: `0xAA` lead, the bytes (each via `WriteByte` as 2-phase
  transitions), a **16-bit checksum**, `0xAA` trail.
- **Checksum:** `UpdateCheckSum` — per bit: XOR into checksum, if low bit set XOR with `0x4002`,
  rotate right (a CRC-16 variant). Reproduce exactly; the ROM verifies it.
- `WriteByte`: bit=1 → hi-lo transition (Write(true) then advance), bit=0 → lo-hi. LSB-first.

**Full-tape sizing (from the header comment, for sanity):** 725 + 40×3258 + 2692 ≈ 133,737
bytes of the 136,065-byte side — ~40 data blocks per tape with slack.

---

## 7. IDevice + state

Implement `IDevice` (machine §4):
- **Reset:** status = BET; if no tape → set CIP+WEN; if tape → WEN per protection; control = All.
- **SaveState/LoadState:** serialize `status`, `control`, `tickCount`, PLL state (`phaseOld`,
  `phaseLocked`, `phaseCount`), and the tape (`Position`, `Side`, and either the phase data or a
  reference to the mounted `.cas` + position — decide whether snapshots embed full tape data or
  just position; full data makes snapshots self-contained but large).
- **InsertTape/EjectTape are RUNTIME operations** (reference doc §5b): inserting sets CIP present
  live (the bare-machine ROM busy-waits on CIP); ejecting clears it and resets. NOT reset-to-apply.

---

## 8. Host-side `.cas` API (separate, always-fast — machine §7)

Independent of the authentic/turbo timing policy: mount/eject, load `.cas` (→ `LoadCasImage`),
save (`MiniTape.Save`), write-protect, browse the block directory (from the headers), side
select. Always instant; not gated by the bit-timing.

---

## 9. What to KEEP vs improve from the owner's implementation

**The owner's implementation is a strong STARTING POINT — the phase-bitstream model and PLL are
the hard-won correct core; preserve their behaviour, improve the form freely (tests as safety
net).**

**Preserve behaviour:** the 209-cycle phase timing, the phase-locked recovery loop, the `.cas`
framing + checksum, the tape motion/EOT/side logic.

**Improve / change / add:**
- **Add the optional turbo ROM-trap path** (§0) — not in the current code; a `TimingPolicy`
  selecting bitstream (authentic) vs block-trap (turbo).
- **Make the reverse-direction bit mapping a toggle** (§4) — currently hardcoded + unverified.
- **Reconcile the WEN active sense** with the reference doc / CprinReader (§5) — must agree.
- **Wire into the shared port dispatch** (machine §6): the MDCR registers its bits on 0x10
  (control, write) and 0x20 (status, read), combining with keyboard (0x10) and printer (0x20)
  via fan-out/combine — rather than owning whole ports. (The milestone-4 `CprinReader` currently
  owns CIP/BET/WEN/RDC/RDA directly; decide whether the MDCR device takes those over via its own
  read source or keeps feeding CprinReader — see milestone-4 finding.)
- **Remove debug file-writes** (`c:\temp\… headers.bin`) and `#if DEBUG` host-path writes — not
  suitable for the cross-platform device.
- **Framebuffer-style vs PixelEngine** N/A here (that was the video device).
- Implement `IDevice`/state (§7) — the current class predates the machine's device interface.

**Drop:** the random-garbage tape fill is a nice touch for realism (unformatted tape = noise) —
keep it, but make it deterministic under a seed for reproducible tests (a `Random` with no seed
breaks determinism; the machine must stay deterministic — machine §2). Use a fixed/seeded RNG or
fill pattern so save-state/replay is exact.

---

## 10. Validation

- **Round-trip:** `LoadCasImage` a known `.cas` → read it back via the authentic bitstream path
  → recovered bytes + checksum match the source blocks.
- **PLL:** synthetic phase streams (clean, and with a dropped/edge case) recover the right bits;
  lock/resync behaves.
- **Port behaviour:** CPOUT control bits drive motor/write; CPRIN status bits (CIP/BET/WEN/RDC/
  RDA) read correctly incl. the active senses (§1/§5).
- **Integration (RUN milestone):** boot bare → insert a real `.cas` at runtime (CIP flips) →
  ROM rewinds + auto-loads the 'P' file → program runs. (Ghosthunt end-to-end.)
- **Determinism:** with a seeded/fixed blank-tape fill, save-state → load reproduces identical
  subsequent behaviour.
- Point authentic-path tests at deterministic timing; turbo is not replayable identically.

---

## 11. Findings to record (machine CLAUDE.md §17)

Log during milestone 9: the reverse-direction bit-mapping outcome (once observable); the WEN
active-sense resolution (code vs doc §5f — which the ROM actually tests); any `.cas` header
field details confirmed (the type byte for 'P' auto-boot); whether turbo ROM-trap was
implemented and its trap points. The owner syncs these into the reference doc.

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
- **Monitor-ROM reference routines (from the owner's disassembly `Cassette.asm`):** the write
  path is `cas_Write` → `write_block` → `cas_block_write`; the bit-level output is `out (CPOUT),a`
  ("Bit to tape") with authentic phase timing documented inline (e.g. the 354T-total / 175T
  comments). Use these as the authoritative reference for BOTH the turbo trap points (trap
  `cas_Write`/`write_block`) AND the realtime write timing.
- **WEN active sense — RESOLVED (monitor disassembly):** bit 3 WEN = **1 means protected, 0
  means writable** (`Symbols.asm`: `WEN equ 0x08`; `Cassette.asm:47` "bit 3 = WEN (1=protected,
  0=can write)"; the `cas_status` routine decodes CIP|WEN as 00=loaded+writable /
  01=loaded+protected / 11=no-cassette / 10=invalid). **The reference doc / CprinReader sense was
  correct; the old `MdcrDevice.cs` (set WEN=1 for writable) was INVERTED — fix it to set WEN=1
  when protected.** Reconcile so device + CprinReader agree on this sense.

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
  HEADER      32 bytes    ← ON TAPE (see correction below)
  1024 data   + framing
  EOB GAP     1856 phases
EOT GAP       (remainder, zero-filled)
```
- **CORRECTION (owner-confirmed) — the 32-byte block HEADER is encoded ON THE TAPE, not just
  `.cas` file metadata.** Each on-tape block = **header (32 bytes) + 1024 data bytes**. The ROM's
  directory scan (ZOEK) reads these headers *from the tape*, and CLOAD-by-name matches against
  them. So the tape encoder must write the header (from `.cas` record offset 0x30) ahead of the
  data block — do NOT skip it. (An earlier implementation pass assumed the header was host-side
  only and encoded data blocks alone; that is wrong and would break ZOEK/CLOAD-by-name. The RUN
  integration test would surface it.)
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
  - **STALE — this was actually decided, the opposite way, during milestone 9 implementation
    (2026-07-05 finding, `P2000.Machine` CLAUDE.md §17): `.state` saves `Position`+`Side` ONLY,
    NOT the phase array.** The mounted `.cas` must be remounted after `LoadState` — matching the
    ROM-bytes-not-saved precedent (embedded/external content isn't duplicated into `.state`).
    This doc's own text above was never updated to reflect that outcome — fixed now. **REOPENED
    (owner, 2026-07-23) for a different reason than originally framed:** the owner wants a
    `.state` file to be fully self-contained and shareable ("send a state to a friend") —
    covered in `P2000T-reference.md` §3a's "OPEN DESIGN QUESTION," alongside the same question
    for mounted disk images (M20). Revisit this bullet once that's settled; see the note there
    for why embedding the compact `.cas`-format bytes (§8 below), not the raw ~1 MB phase array,
    is probably the right shape if/when this gets built.
- **InsertTape/EjectTape are RUNTIME operations** (reference doc §5b): inserting sets CIP present
  live (the bare-machine ROM busy-waits on CIP); ejecting clears it and resets. NOT reset-to-apply.

---

## 8. Host-side `.cas` API (separate, always-fast — machine §7)

Independent of the authentic/turbo timing policy: mount/eject, load `.cas` (→ `LoadCasImage`),
save (`MiniTape.Save`), write-protect, browse the block directory, side select. Always instant;
not gated by the bit-timing.

**Directory = scan ALL blocks, collect each header (NOT a top-of-tape index).** There is no
single directory block. Each of the ~40 blocks carries its own **32-byte header** (at 0x30 in
the file record / framed in the bitstream). The tape "directory" (the ROM's ZOEK command) is
built by **scanning the whole tape and reading every block's header** — which is why ZOEK
physically spools the tape on real hardware. Implementation: walk every 1280-byte record (file)
or every framed block (bitstream), pull the 32-byte header, build the list.
- **Multi-block programs:** a program spanning several 1024-byte data blocks repeats its header
  across its blocks (with sequence/count fields), so **group blocks into one directory entry per
  program** rather than one-entry-per-block. CONFIRM which header fields carry name / total
  length / block index+count (from the disassembly / M2000) to de-duplicate correctly.
- Host-side directory (UI browse) scans instantly; the **emulated ZOEK in realtime mode takes
  authentic spool time** (timing policy, not format).

**Bitstream → `.cas` serializer (MISSING in the current code — must be built):** `MiniTape.Save`
currently dumps the raw phase-bit array (`data[Side]`), not a `.cas` file. For the UI "Save as
.cas" round-trip, add the **inverse of `LoadCasImage`**: walk the phase stream, find block
framing (BOB gaps, the `0xAA` lead/trail marks), recover bytes via the same PLL/phase logic the
read path uses, strip the framing + verify checksum, and reassemble **1280-byte `.cas` records**
(header at 0x30, data at 0x100). This is what lets a CSAVE'd tape persist as `.cas`. Keep the raw
phase-array save too if useful for debugging, but the user-facing save is `.cas`.

**Now doubly motivated (2026-07-23) — also the natural building block for a self-contained
`.state`, if/when §7's reopened embedding question is decided that way.** The raw phase array is
~1 MB/side and not worth embedding directly; this serializer's output (the compact `.cas`-format
bytes: `1280 B × block count`, typically tens of KB, not ~1 MB) is a far cheaper thing to embed in
`.state` and reuse `LoadCasImage` symmetrically on restore. Build it once, for BOTH consumers —
don't build a separate, redundant "serialize for .state" path.

**Write round-trip:** `.cas` → bitstream (load) → CSAVE mutates bitstream (realtime WCD/WDA or
turbo block-trap) → bitstream → `.cas` (save). Blank tape (no load) → CSAVE builds a tape in
memory → Save as `.cas`.

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

---

## 12. Cross-check against the official T&M Reference Manual (2026-07-23, design-doc
maintainer pass — this file itself was re-added to the docs folder this same pass; it existed
in a prior session but was missing from this one, leaving dangling `docs/MDCR-implementation.md`
references in the machine/UI CLAUDE.md files, now resolved)

The owner separately supplied the official Philips "P2000 System T&M Reference Manual" (144 pp.,
transcribed in full in `raw-conversion.md`). Its Chapter 5 "MINI-DIGITAL CASSETTE" independently
describes the same hardware this doc's phase-bitstream model implements. Cross-checked below —
mostly strong corroboration, plus a resolution of an open question from the reference doc.

**RESOLVED — encoding scheme is phase encoding, NOT FM. This doc's own §4 PLL model IS phase
encoding, and now settles a discrepancy `P2000T-reference.md` §5b had flagged but not fixed.**
§4 above describes recovery as: locked → "every 2nd phase must differ from the first (a real bit
= a hi-lo or lo-hi transition across the 2-phase pair)" — that is a textbook description of
**phase encoding** (a bit's value is carried by the transition DIRECTION at a fixed clock edge),
matching the manual's Ch5.1 verbatim: *"a one bit is represented by the transition between a low
value and a high value... and a zero bit by a high-low transition."* Two independent sources — the
manual's prose AND the owner's actual working `MdcrDevice`/`MiniTape` implementation (not just a
description of intent) — now agree exactly. This means the reference doc's "FM-encoded" phrase
(Storage section, §5b Storage subsection) was the incorrect one; corrected there in this same
pass, citing both this file and the manual as the converging evidence, per this project's
"correct with a dated note, don't silently overwrite" discipline.

**CONFIRMED — 6000 baud arithmetic checks out.** 209 cycles/phase ÷ 2,500,000 Hz = 83.6 µs/phase;
2 phases/bit = 167.2 µs/bit → **5,981 bits/s ≈ 6000 baud**, matching the reference doc's quoted
figure. First time this project has derived the baud figure from the phase-timing constants
directly rather than just citing it.

**CONFIRMED — block gap timings match the manual almost exactly**, converting this doc's phase
counts at 83.6 µs/phase: **BOB GAP 6160 phases ≈ 515.1 ms** (manual's "start gap... approximately
515 ms" — Ch5.1.2 "BLOCK") and **EOB GAP 1856 phases ≈ 155.2 ms** (manual's "end gap...
approximately 155 ms") — both essentially exact matches. The manual's "mark gap" (~85 ms, between
the 4-byte mark and the data record) isn't broken out as a separate phase count in this doc's
diagram (the "MARK" step is written via the same `WriteData` framing helper as the data blocks),
so it's not independently confirmable from this doc alone — not a conflict, just not itemized here.

**FLAGGED, NOT resolved — BOT gap is ~2× shorter in this doc than the manual states.** This doc's
`BOT GAP` = 5800 phases ≈ **484.9 ms**; the manual (Ch5.2.1) states *"to read past this gap takes
approximately 1 second."* A real, if minor, discrepancy — could be the manual's figure being a
loose round number, or the implementation's BOT gap being an approximation rather than a
hardware-measured value. Not load-bearing for correctness (BOT is only encountered once, at the
physical start of tape) but flagged rather than silently reconciled.

**STRENGTHENED, still not fully resolved — cassette capacity now has 2-vs-1 sources favoring 40
blocks/side over 42.** This doc's own "Full-tape sizing" note (§6) computes **"~40 data blocks
per tape with slack"** from the real block/gap byte counts in the working implementation — this
independently lines up with the manual's explicit *"Each track (or side) of a cassette is divided
into 40 blocks... A file may comprise between 1 and 40 blocks"* (Ch5.2). That makes TWO sources
(this doc's own implementation-derived sizing, and the official manual) landing on 40, against
ONE source (the owner's printed BASIC manual) stating 42. `P2000T-reference.md` §5b's existing
"CONFIRMED... 42 blocks per side" note and its more recent "40 vs. 42, unresolved" flag are both
updated to note this shift in evidentiary weight — still not silently overwritten to 40, since the
BASIC manual is a real primary source too and could describe a different capacity convention
(e.g. a practical/recommended limit vs. the physical maximum), but the balance now leans 40.

**CONFIRMED — header offset alignment.** This doc's per-record layout places the 32-byte BLOCK
HEADER at file offset `0x30` within a 256-byte copy of P2000 memory `0x6000`-`0x60FF` — i.e.
memory address **`0x6030`**, exactly the cassette file descriptor start address independently
confirmed from the ROM disassembly and the manual's own Ch5.2.2/5.3.1 (`P2000T-reference.md` §5b).
This doc does not itemize the header's individual field byte-offsets, so it neither confirms nor
resolves the still-open "name split ...0x17–0x1E vs. the manual's 'reserved' at that offset" note
already flagged in `P2000T-reference.md` §5b — that stays open.

**Additive, no conflict:** this doc's specific checksum algorithm (a CRC-16 variant, polynomial
`0x4002`, XOR-then-rotate-right per bit) is a level of precision the manual doesn't attempt
("check-sum - 2 bytes holding a value which can be used to check for reading errors") — useful,
not contradictory.
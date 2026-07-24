# FDC-implementation.md

Implementation guide for the **µPD765/8272A floppy disk controller**, for the P2000.Machine
**FDC device (milestone 19, full-command-set fast-follow milestone 19a)**. Read this when
extending `Upd765` beyond the 6-command boot/run subset milestone 19 already built. It
distills reference implementations of the SAME chip family, the authoritative datasheet, and
this project's own confirmed real-usage findings.

Priorities (per project owner): implement the **full 15-command set**, not just what the
stock monitor ROM / JWSDOS resident driver happen to issue — milestone 19 deliberately scoped
to "boot + run," this milestone is chip fidelity for its own sake, the same way `Z80.Core`
targets the full instruction set rather than just what one ROM uses.

---

## 0. The chip, and why "15" not "16"

The real silicon is the plain **µPD765/765A** (NEC) / **Intel 8272A** (second-source, same
command architecture) — a first-generation FDC, not one of the later enhanced-generation
parts (82077AA-class) that later machines used. The base command set is **15 commands**.

An earlier pass through this project's docs said "16-command µPD765 set" — that number came
from eyeballing MAME's C++ `enum` for its command dispatch, which has MORE than 15 entries
(`C_CONFIGURE`, `C_DUMP_REG`, `C_LOCK`, `C_PERPENDICULAR`, `C_MOTOR_ONOFF`, `C_VERSION`,
`C_SLEEP`, `C_ABORT`, `C_SPECIFY2`, plus `C_INVALID`/`C_INCOMPLETE` as internal sentinels).
MAME's `upd765_family_device` is a shared driver across the WHOLE µPD765 lineage including
much later enhanced chips — those extra entries are enhanced-FDC-only and **not part of the
base chip this hardware uses**. Corrected: **15 real commands**, confirmed against the
original 1978 NEC datasheet, not just emulator source.

---

## 1. Reference implementations consulted (chip-emulation heritage, same idea as the SAA5050/
MDCR guides citing MAME/jsbeeb/the owner's own port)

| Source | Command coverage | Notes |
|---|---|---|
| **MAME** `src/devices/machine/upd765.cpp`/`.h` | All 15 base + later-chip extras | Primary structural reference. Shared "family" driver (plain 765/765A, 8272A, and 82077AA-class descendants via subclassing). Genuinely cycle/bit-accurate against a real MFM bitstream (`live_info` state machine) — more granular than this project needs (we're semi-DMA/software-polled, not bit-level MFM), but the 3-phase command/execution/result structure and the per-command `_start()` handler shape is exactly the pattern to imitate. |
| **openMSX** `src/fdc/TC8566AF.cc` | All 15 base commands | Toshiba TC8566AF, µPD765/8272-command-compatible, used on several MSX disk interfaces. Independent, from-scratch C++ implementation (not a MAME derivative) — a good second opinion on MAME's design choices. Same 4-phase (Idle/Command/Execution/Result) shape, MSR-style RQM/DIO gating. |
| **QEMU** `hw/block/fdc.c` | All 15 in its dispatch table, but **several stubbed** | Has a complete 15-entry command table by name/opcode, but on inspection Read/Write Deleted Data log "unimplemented" and return an abnormal-termination status rather than real deleted-sector semantics, and the Scan commands don't implement the SH/SN inequality nuance (§4 below). **Cautionary example, not a model to copy blindly** — a complete-looking dispatch table doesn't mean complete behavior; check handler bodies, not just the table. |
| **floooh/chips** `chips/upd765.h` | 7 of 15 (Read Data, Write Data, Read ID, Recalibrate, Sense Interrupt Status, Specify, Sense Drive Status) | Deliberately minimal — its own comment says "Initially, only the features required by Amstrad CPC are implemented." Structurally similar to where THIS project's milestone 19 already stands (a "6/7-of-15, whatever the target ROM needs" subset). Useful as a comparison baseline for "why go further than this," not as a target. |
| **NEC µPD765 datasheet** (Dec 1978) + **App Note** (Mar 1979) | Authoritative, all 15 | The ultimate source of truth for command-byte layout and status-bit meaning; cross-checked against MAME's `check_command()` switch (executable, so byte-count-precise) — the two agree on every command's byte length. |

**Don't copy MAME's enhanced-chip command entries** (`CONFIGURE`/`DUMP_REG`/`LOCK`/
`PERPENDICULAR`/`MOTOR_ONOFF`/`VERSION`/`SLEEP`/`ABORT`/`SPECIFY2`) — out of scope, later
silicon. **Don't copy QEMU's Scan/Deleted-Data handling** without checking it actually does
what its dispatch table implies — verified stubbed on inspection (§1 above).

---

## 2. Already CONFIRMED + IMPLEMENTED (milestone 19, real P2000 ROM usage)

These 6 are sourced from the monitor ROM's own `getdos` disassembly (reference doc §5d) —
exact command bytes, not reconstructed from the MT/MF/SK bit theory:

| # | Command | Opcode | Real bytes issued | Result |
|---|---|---|---|---|
| 1 | SPECIFY | `0x03` | `03 60 34` | none |
| 2 | RECALIBRATE | `0x07` | `07 01` | none (completion via INT) |
| 3 | SEEK | `0x0F` | `0F 01 01` | none (completion via INT) |
| 4 | READ DATA | `0x42` (MF\|base) | `42 01 01 00 01 01 10 0E 00` | data phase, semi-DMA |
| 5 | WRITE DATA | `0x45` (MF\|base) | same shape | data phase, semi-DMA |
| 6 | SENSE INTERRUPT STATUS | `0x08` | `08` | 2 bytes: ST0, PCN |

**A 7th, newly CONFIRMED (2026-07-23, design-doc maintainer's direct read of
`docs/jwsdos5.0.asm`) — SENSE DRIVE STATUS, from JWSDOS's own resident driver, not `getdos`:**

```
check_write_enable:
    ld a,(system_drive)
    ld (target_drive_for_status),a
    ld hl,dsk_cmd_get_status         ; db 002h,004h,0   -> len=2, opcode 0x04, drive#
    call MON_DSK_send_command
    ld b,001h                       ; only one result byte needed
    call MON_DSK_read_status_bytes
    ld a,(MON_working_mem)
    bit 6,a                         ; bit 6 = write protect (1=protected)
    ret z                           ; can write!
    ...  JWS_ERR_WRITE_PROTECTED
```

This is real, working P2000 software reading **ST3 bit 6 = WP** to gate a write — an exact
match to the standard datasheet's ST3 layout (§3 below), the first sourced confirmation that
this chip's status-register bit semantics apply unmodified on this hardware (as opposed to
being reconstructed from the general datasheet only). **Elevate Sense Drive Status from
"modeled, unconfirmed" to "confirmed real usage" in the milestone below.**

**Format A Track — checked, NOT found in this source.** The owner expected JWSDOS/PDOS format
utilities would obviously use this command; `docs/jwsdos5.0.asm`'s resident "#" DOS command
table was searched specifically and contains exactly **LOAD, SAVE, RUN, ZOEK (search), WIS
(delete), VP, SYS** — no FORMAT/PREPARE command. `VP` was checked directly (in case it was a
Dutch-language format command, e.g. "voorbereiden") and is actually a LOAD-with-memory-
relocation variant (`vp_file_fits`/`move_vars_down` — BASIC array/variable space
adjustment), not a formatter. **CONFIRMED (owner, 2026-07-23): formatting IS a separate application, not part of
`jwsdos5.0.asm`.** The absence above wasn't a gap in this disassembly's coverage — the owner
confirmed formatting genuinely lives in a standalone utility program, not the resident DOS
extension this file is. The owner plans to supply more information, and hopefully a
disassembly of that formatter, in a future session. Until that arrives, Format A Track must
still be built from the general datasheet only (§4/§5/§6) — this platform's real command-byte
shape for it remains unsourced, just no longer an open question about WHERE to look.

**One ambiguous, inconclusive sighting, not a confirmed usage:** a lone `db 002h,04ah,001h`
(len=2, opcode `0x4A` = MF\|READ ID, drive#) sits in `jwsdos5.0.asm` right after
`check_ramdisk_signature`'s `ret`, with no visible call site pointing at it — most likely
unreferenced/dead data (the file has at least one other confirmed dead-data comment nearby,
"Dead code/data?"), not a real READ ID invocation. Don't treat this as confirmed READ ID
usage.

---

## 3. Status register bit layouts (datasheet-authoritative, cross-checked against MAME source)

- **ST0**: D7-D6 IC (Interrupt Code: `00` normal, `01` abnormal — command started but didn't
  finish, `10` invalid command, `11` abnormal by polling — drive went not-ready mid-command),
  D5 SE (Seek End), D4 EC (Equipment Check), D3 NR (Not Ready), D2 HD, D1-D0 US1/US0.
- **ST1**: D7 EN (End of Cylinder), D5 DE (Data Error, CRC in ID/data field), D4 OR (Over Run),
  D2 ND (No Data — sector specified not found; applies to Read Data, Write Deleted Data, and
  the Scan commands), D1 NW (Not Writable — write-protect during a write/format), D0 MA
  (Missing Address Mark).
- **ST2**: D6 CM (Control Mark — deleted-DAM sector hit during a normal read, or vice versa),
  D5 DD (Data Error in Data Field), D4 WC (Wrong Cylinder), D3 SH (Scan Equal Hit), D2 SN
  (Scan Not Satisfied), D1 BC (Bad Cylinder), D0 MD (Missing Address Mark in Data Field).
- **ST3**: D7 FT (Fault), **D6 WP (Write Protected — CONFIRMED real usage, §2 above)**, D5 RY
  (Ready), D4 T0 (Track 0), D3 TS (Two Side), D2 HD, D1-D0 US1/US0.

---

## 4. Full 15-command table

Modifier bits sit in the command byte's top bits: **MT** (bit7, multi-track), **MF** (bit6,
MFM vs FM — always set on this platform, per the confirmed `0x42`/`0x45` opcodes), **SK**
(bit5, skip deleted-data sectors) — not every command uses all three. Most commands' 2nd
command-phase byte is a unit-select byte: `0 0 0 0 0 HD US1 US0`.

| # | Command | Opcode (base) | Cmd bytes | Execution | Result | Status |
|---|---|---|---|---|---|---|
| 1 | READ DATA | `0x06` | 9: cmd,HD/US,C,H,R,N,EOT,GPL,DTL | FDD→host | 7: ST0,ST1,ST2,C,H,R,N | **CONFIRMED, built** |
| 2 | READ DELETED DATA | `0x0C` | same 9 | FDD→host, reads deleted-DAM sectors | 7 | modeled only |
| 3 | WRITE DATA | `0x05` (no SK) | same 9 | host→FDD | 7 | **CONFIRMED, built** |
| 4 | WRITE DELETED DATA | `0x09` | same 9 | host→FDD, writes deleted-DAM | 7 | modeled only |
| 5 | READ A TRACK | `0x02` (no MT) | 9, R ignored | FDD→host, every sector in physical order | 7 | modeled only |
| 6 | READ ID | `0x0A` | 2: cmd,HD/US | none — latches next ID AM's C,H,R,N | 7 (the ID just read) | modeled only (§ ambiguous sighting above) |
| 7 | FORMAT A TRACK | `0x0D` | 6: cmd,HD/US,N,SC,GPL,D | host→FDD, 4 bytes/sector (C,H,R,N) via data register, ×SC | 7 (CHRN largely don't-care) | modeled only — **priority, see §5/§6** |
| 8 | SCAN EQUAL | `0x11` | 9: ...,EOT,GPL,STP | byte compare, disk vs host | 7 | modeled only |
| 9 | SCAN LOW OR EQUAL | `0x19` | same 9 | compare, disk ≤ host | 7 | modeled only |
| 10 | SCAN HIGH OR EQUAL | `0x1D` | same 9 | compare, disk ≥ host | 7 | modeled only |
| 11 | RECALIBRATE | `0x07` | 2: cmd,US | steps to track 0 | 0 (→ Sense Interrupt Status) | **CONFIRMED, built** |
| 12 | SEEK | `0x0F` | 3: cmd,HD/US,NCN | steps to NCN | 0 (→ Sense Interrupt Status) | **CONFIRMED, built** |
| 13 | SENSE INTERRUPT STATUS | `0x08` | 1: cmd | none | 2: ST0, PCN | **CONFIRMED, built** |
| 14 | SPECIFY | `0x03` | 3: cmd,SRT/HUT,HLT/ND | none | 0 | **CONFIRMED, built** |
| 15 | SENSE DRIVE STATUS | `0x04` | 2: cmd,HD/US | none | 1: ST3 | **CONFIRMED (JWSDOS), §2** |

Parameter glossary (datasheet verbatim): **EOT** = final sector # on a cylinder. **GPL** = Gap-3
length. **DTL** = data length when N=0 (else sector length is 128×2^N). **SC** = sectors/
cylinder (Format). **D** = format filler byte. **STP** = Scan step (1 = contiguous sectors,
2 = alternate). **NCN**/**PCN** = New/Present Cylinder Number. **SRT/HUT/HLT/ND** (Specify) =
Step Rate Time / Head Unload Time / Head Load Time / Non-DMA mode.

---

## 5. The three most-involved unimplemented commands

### FORMAT A TRACK (`0x0D`) — build this one first, see §6
Command phase is only 6 bytes (no C/H/R/EOT/DTL — nothing to target, there's no existing
data). Once execution starts, the FDC does **not** know sector addresses in advance — for
each of the **SC** sectors on the track, the host must push **4 bytes** through the data
register (C, H, R, N for that sector); the FDC writes an ID field from those 4 bytes, a
Gap-3, then a full sector of the **D** filler byte, repeating SC times. Total host bytes
pushed during execution = 4×SC, not just the 6-byte command block. Result CHRN content is
effectively don't-care per the datasheet (MAME just echoes the last N; a reasonable
simplification, not a bug, worth matching rather than inventing stricter behavior).

### READ A TRACK (`0x02`, "Read Diagnostic")
9-byte command phase, same shape as Read Data, but **R is not used to search** — the FDC
reads every sector it physically encounters starting right after the index pulse, through
EOT, regardless of ID match. MT is **not** a valid modifier here (only MF/SK are) — differs
from Read/Write Data. Result reflects the LAST sector processed.

### SCAN EQUAL / LOW-OR-EQUAL / HIGH-OR-EQUAL (`0x11`/`0x19`/`0x1D`)
9-byte command, same shape as Read/Write Data but the last param is **STP** instead of
**DTL**. Execution: FDC reads each sector's data field and compares byte-by-byte against
bytes the host feeds through the data register (mechanically like a Write, but comparing
instead of storing). **ST2 SH/SN semantics** (confirmed from MAME's compare loop, matches
datasheet prose): **SH** starts SET, clears the instant any byte pair mismatches — so SH=1 at
completion means "every byte compared exactly equal," regardless of which Scan variant is
running. **SN** starts CLEAR, sets on a mismatch, UNLESS that specific mismatch already
satisfies the variant's inequality (disk<host for Scan-Low, disk>host for Scan-High), in
which case SN clears again. So: SH=1 → perfect equality found. SN=1 → scan ended without ever
satisfying its condition. SH=0 & SN=0 → the ≤/≥ condition (not exact equality) was satisfied
at least once (Scan-Low/High only). ST1 ND is reused here to mean "R not found," per the
datasheet's own note that ND applies to "READ DATA, WRITE DELETED DATA, or SCAN Command."

---

## 6. Structural approach — extend `Upd765` to the MAME/openMSX 3-phase shape

Milestone 19's `Upd765` already has real per-drive state (`_cylinder[]`, motor, selected
drive) and a working semi-DMA byte-poll mechanism (`0x8D`/`0x90` bit0) — this milestone
extends the SAME chip object, not a rewrite:

1. **Command phase:** bytes written to `0x8D` while `main_phase == Command` append to a
   command buffer. After each byte, a dispatcher (keyed on the masked opcode, §4's table)
   decides: need more bytes (stay in Command phase), or command complete → move to Execution.
   **Match on exact confirmed bytes where confirmed (§2), general opcode+modifier decode for
   the other 9** — same discipline as milestone 19's own "match on real bytes, not a
   reconstructed bit theory" rule, just necessarily generalized for the commands nothing on
   this platform is confirmed to issue yet.
2. **Execution phase:** generalizes the existing semi-DMA byte-loop to (a) run in either
   DIRECTION — FDD→host (Read family) or host→FDD (Write family, Format, Scan) — the current
   implementation already does host→FDD for Write Data, so this is extending an existing
   capability, not inventing one; (b) support the Format/Scan byte-count shapes (4×SC for
   Format, one comparison byte per data byte for Scan) instead of assuming "N bytes of sector
   data" uniformly.
3. **Result phase — build the FORMAL 7-byte (or shorter) result block for every command,
   including retroactively for READ/WRITE DATA.** Milestone 19 deliberately skipped a formal
   result phase for Read/Write Data (no driver on this platform was found to read it). Building
   Scan/Read-Track/Read-ID/Format properly requires real ST0/ST1/ST2/C/H/R/N result-phase
   machinery anyway — backfill it onto Read/Write Data too rather than maintaining two
   different completion models side by side. Sense Interrupt Status (2 bytes) and Sense Drive
   Status (1 byte, §2) keep their shorter shapes.
4. **Don't build the enhanced-chip commands** (§0) — out of scope, not real silicon here.

---

## 7. Test strategy

**No portable, chip-generic FDC conformance test suite was found** during research — MAME's,
openMSX's, and QEMU's own tests are internal to those emulators' harnesses and tied to their
own device models, not extractable as a standalone test vector set. Two-pronged approach
instead:

1. **Synthetic protocol tests, per command, against the datasheet-specified shapes in §4** —
   drive each command through its command/execution/result phases directly (bypassing the
   Z80 core, same pattern as `Upd765Tests.cs` already uses for the confirmed 6+1), asserting
   byte counts, status-bit values, and (for Scan) the SH/SN truth table in §5. This is the
   primary validation for the 9 commands with no known real-P2000-software caller.
2. **Real-usage integration tests where a caller is confirmed** — Sense Drive Status now has
   one: drive a machine through `check_write_enable`'s real sequence (`02 04 <drive>` → 1
   result byte → bit 6 test) against both a write-protected and writable mounted `DskImage`,
   confirming the ROM-level gate actually denies/allows the write — same fixture-driven
   pattern as the existing `Spel1.dsk`/`jwssytem.dsk` integration tests.
3. **Format A Track — flag, don't force:** build the synthetic protocol test now (§6); if the
   owner later sources JWSDOS's or PDOS's actual FORMAT utility, add the real end-to-end test
   then (drive the real utility → Format A Track issued with real bytes → resulting `DskImage`
   readable). Don't invent a fake "format utility" caller just to have an integration test —
   that would test this project's own assumptions against itself, not real software.

---

## 8. Findings to record (machine CLAUDE.md §17)

Log during the full-command-set milestone: which of the 9 modeled-only commands turn out to
have a real P2000-software caller once more disassembly/source becomes available (Format A
Track especially); any datasheet ambiguity resolved by testing against real behavior; ST1/ST2
error-bit edge cases hit during implementation. The owner syncs these into the reference doc
and this guide.

# JWSDOS format — device/format guide

Companion to `docs/P2000T-reference.md` and `docs/MDCR-implementation.md`, same division of
labor as MDCR: **the reference doc keeps the generic µPD765 chip facts** (ports `0x8C`/`0x8D`/
`0x90`, MSR/control-latch bits, command/execute/result phases, semi-DMA, CTC ch0/ch1 roles —
reference doc §5d/§5e). **This doc keeps everything specific to how JWSDOS actually uses that
chip and what it writes to disk** — the boot-load sequence as literally executed, the on-disk
layout, the directory format, the allocation model, and the geometry label. Reference this doc
from the reference doc and `CLAUDE.md` rather than duplicating its contents there, mirroring
how MDCR-implementation.md is referenced from cassette-related sections.

**Sources:** the project's own monitor-ROM disassembly (`Disk.asm`, owner-supplied,
2026-07-13 — covers the disk-boot gate and the full `getdos` routine, i.e. what the
**monitor ROM** does before JWSDOS is even running), the owner's own **manual disassembly
of the JWSDOS 5.0 binary itself** (`jwsdos5.0.asm`, owner-supplied, 2026-07-13 — recovered
real symbol names, e.g. `DE_filename`, `DIR_side_1_mem`, `is_disk_SS`, straight from the
owner's own labeling of the binary; this is the **highest-confidence source in this doc**
for anything about how JWSDOS itself behaves, as distinct from what the monitor ROM's boot
loader does), the owner's own research into the JWSDOS 5.0 directory/allocation format and
the on-disk geometry label (owner-supplied, 2026-07-13), byte-level inspection of a real
disk image (`Spel1.dsk`, owner-supplied, 2026-07-13), and MAME PR #7577's
`src/lib/formats/p2000t_dsk.cpp` (open, unmerged — checked as a cross-reference, not itself
authoritative). Confidence is marked per claim below: **CONFIRMED** (literally what the
disassembly/research states), **INFERRED** (my analysis layered on top, flagged as such),
**OPEN** (still unresolved).

---

## 1. Disk geometry

**CONFIRMED (from `getdos`'s FDC command bytes):** 16 sectors/track, 256 bytes/sector, MFM
encoding, single-sided access from the boot loader (side# is always sent as `0x00` in
`getdos`'s own commands — this reflects what the boot loader touches, not necessarily the
physical medium's side count, see below). 16 × 256 = 4096 bytes/track.

**CONFIRMED (owner research):** JWSDOS 5.0 supports **multiple physical geometries** —
35, 40, or 80 tracks, single- or double-sided. This is not a fixed hardware constant; it's a
per-disk, format-time choice (§3). The reference doc's earlier "single-sided, 35-track"
expectation was a placeholder, superseded by this.

**Cross-reference (MAME PR #7577, unmerged — corroborating, not authoritative):** its disk
format table independently defines the same 16 sectors/track × 256 B geometry across seven
track-count/side-count combinations (3.5"/5.25", 35/40/80 tracks, SS/DS), with gap-length
parameters explicitly marked "Unverified" by its own author and no comment singling out one
variant as canonical. Consistent with, not additional evidence beyond, the owner-research
finding above — MAME's author was independently hedging on the same multi-geometry fact.

**CONFIRMED — verified directly against a real image (`Spel1.dsk`, 327,680 B, owner-supplied
2026-07-13):** 40 tracks, double-sided. The geometry label (§3) reads byte-exact; independently,
the directory's own data (§4) corroborates it from a completely different angle — the highest
logical sector referenced by any file entry on this disk is **640**, and 40 tracks × 16
sectors/track = **640** exactly. Two independent lines of evidence inside the same image agree.
Matches MAME's 5.25"/DSDD/40-track format (16 × 40 × 2 × 256 = 327,680 B = 320 KB).

**CONFIRMED (from `jwsdos5.0.asm` — important correction to how geometry is actually
determined at runtime):** JWSDOS does **not** auto-detect single/double-sided-ness or track
count by reading the on-disk label (§3) back into itself. `is_disk_SS` and
`get_sectors_per_side` — the two routines every directory/sector-math operation calls to
know the disk's shape — read **pure live RAM state**: a byte `SS_DS_Char` (`'S'` or `'D'`)
and a byte `number_of_tracks` (track count **+1**, same encoding as the on-disk label's
`$FFF` byte). Both default, at DOS load, to **`"DS "` / `80+1`** — i.e. JWSDOS assumes
double-sided, 80 tracks until told otherwise. The **only** place either variable changes is
the operator-facing format menu (arrow keys cycle `35`/`40`/`80` via `trackinfo_35/40/80`;
`S`/`D` keys toggle `SS_DS_Char`). No code path that re-reads `SS_DS_Char`/`number_of_tracks`
from an inserted disk's own label was found in this pass (see §7 — flagged open, not
disproven, since the disassembly wasn't exhaustively traced). **Practical implication:** on
real hardware, inserting a disk whose actual geometry differs from whatever
`SS_DS_Char`/`number_of_tracks` currently hold (leftover from the last format operation, or
the 80-track/DS power-on default) would make JWSDOS compute the wrong sector-to-track math
for that disk — the operator is expected to manually match the format menu's SS/DS and track
settings to the physical disk before using it. See §3's revised design implication for what
this means for the emulator.

---

## 2. On-disk layout overview

**Superseded by `jwsdos5.0.asm` — the previous version of this section conflated two
different things.** `getdos` (monitor ROM) loads raw track "1" (0x0000–0x0FFF → RAM
0xE000–0xEFFF) and raw track "2" (0x1000–0x1FFF → RAM 0xF000–0xFFFF) as one generic,
directory-unaware 2-track boot load. It is **not** directory-aware — it just happens that
raw track "2" lands in the exact RAM range (0xF000–0xFFFF) that JWSDOS's own directory
buffers occupy (`DIR_side_1_mem = 0xF000`, `DIR_side_2_mem = 0xF800`, both **CONFIRMED**
constants from `jwsdos5.0.asm`). This resolves the previous update's open question ("does
`getdos`'s track-2 load land on the directory or skip it?") — it doesn't skip anything, it
just isn't precise about what it's loading; JWSDOS's own code re-reads the relevant pieces
more precisely once it actually needs the directory.

**CONFIRMED, byte-verified against `Spel1.dsk`, cross-checked against `jwsdos5.0.asm`'s
`dir_side1_prep`/`dir_side2_prep` routines:**

```
0x0000–0x0FBE  Side-1 DOS boot code, track "1"
0x0FBF–0x0FFF  Geometry/system label (§3)
0x1000–0x17FF  Track "2", sectors 1–8 (2048 B) — a SECOND, DIFFERENT set of 20 real
               directory entries (Spel1.dsk: "Fraxxon + scores" … "Superlaser"), byte-
               identical struct shape to §4, but NOT the active directory — see below.
0x1800–0x1FFF  Track "2", sectors 9–16 (2048 B) — the disk's ACTUAL, currently-active
               side-1 directory (Spel1.dsk: "Tralieenspel" … "BABA", 18 real entries,
               zero-padded after). CONFIRMED via `dir_side1_prep`: `DE_start_sector` =
               0x19 (=25, "sector 9 track 2" in the side's own 1–16/17–32/… linear
               numbering), `DE_filelen` = 0x0800 (2048 B = 8 sectors), destination
               `DIR_side_1_mem` = 0xF000. `read_directory`/`save_directory` both funnel
               through this same routine — this is the ONLY on-disk location the running
               DOS ever reads or writes for side 1's catalog.
0x2000–0x2FFF  More DOS code (unchanged from previous finding, not re-examined this pass)
```

**Why sectors 1–8 (0x1000–0x17FF) hold a second, different directory — HIGHLY PLAUSIBLE
theory (owner, 2026-07-13), supersedes the previous "older JWSDOS build" guess, still
pending validation against the responsible utility's own code.** `dir_side1_prep`
unconditionally targets sector 25 (track-2 sector 9); nothing found in `jwsdos5.0.asm` ever
reads or writes sectors 17–24 (track-2 sectors 1–8) as directory data. Yet those bytes are
shaped exactly like real directory entries and list 20 files with **zero filename overlap**
with the 18 files in the active directory. The owner's theory: the JWSDOS system disk
carries **two separate utilities**, not part of the `jwsdos5.0.asm` binary disassembled so
far — a **`Format`** program (low-level FDC formatting — sector/track layout) and a
**`JWS Systeem Disk`** program (writes the two DOS system tracks onto an already-formatted
disk, turning it into a bootable system disk). It's this second program the theory concerns:
it writes RAM `0xE000`–`0xEFFF` as track 1 and RAM `0xF000`–`0xFFFF` as track 2 onto the
target disk — i.e. it dumps the **live DOS-code RAM image**, not a purpose-built "blank track
2." Since `0xF000`–`0xFFFF` is the same RAM range `getdos` unconditionally loads at **every
boot** (§6) and that JWSDOS's own directory buffers occupy, `JWS Systeem Disk` only needs to
clear/rewrite *part* of that range (the active directory portion, sectors 9–16, presumably
via the same `dir_side1_prep`/`save_directory` machinery) before writing the track —
whatever was sitting in the rest of that RAM window (sectors 1–8) at the time, including a
stale directory from **whatever disk was booted or read right before it ran**, gets carried
straight onto the newly written disk as an accidental byte-for-byte copy.

**Empirical support for this theory (this pass):** a second real image, `jwssytem.dsk`
(327,680 B, owner-supplied 2026-07-13), lines up with it directly. Its track 1 + label
(`0x0000`–`0x0FFF`) are **byte-for-byte identical** to `Spel1.dsk`'s — both share the same
DOS boot code and the same `"...DS 40Tr drive"` label, consistent with both having been
formatted from the same master. But its entire track 2 (`0x1000`–`0x1FFF`, both halves) is
**all zero** — no stale cluster, no active entries, nothing — exactly what the theory
predicts if this particular disk happened to be formatted right after a boot where
`0xF000`–`0xFFFF` genuinely held nothing (e.g. formatted immediately, before any directory
read/write touched that RAM). `Spel1.dsk`'s stale 20-file cluster, by contrast, is exactly
what the theory predicts if some *other* disk (with those 20 files in its own directory) had
been booted or read shortly before `Spel1.dsk` was formatted. Not proof — the owner's own
next step (reading `JWS Systeem Disk`'s disassembly) is what would confirm it — but a real,
independent second data point that fits cleanly.

**Corrected — NOT "directory for side 1(?)" and NOT side 2's directory sitting out of
place.** Both clusters above have every entry's side-byte (§4 offset 24) equal to **0** —
this is all side-1 data, one currently-active and one stale. **Side 2's own directory**
(`dir_side2_prep`: sector 17 = track-2 sector 1, same 2048 B length, destination
`DIR_side_2_mem = 0xF800`, but issued with FDC **head = 1** — a physically different disk
surface) lives somewhere else entirely in the raw `.dsk` file, depending on how this image
interleaves the two physical sides. Not located in this pass — **new open item**, see §7.

---

## 3. The geometry / system label

**CONFIRMED (owner research):** near the end of the DOS boot area, JWSDOS embeds a
human-readable banner plus two machine-readable bytes, all **rewritten by the DOS itself
when formatting a system disk** — i.e. this is a real superblock, not something the emulator
has to infer or that the operator has to configure separately.

| Offset | Field |
|---|---|
| `$FBF` | ASCII banner — **CONFIRMED byte-exact against `Spel1.dsk`:** `"JWS DISK SYSTEM.(c)-1986....versie 5.0.NL....DS 40Tr drive "` (the `.` marks non-ASCII display color/position attribute bytes, not literal characters — confirmed to be single bytes, e.g. `0x8C`, `0x04 0x03 0x02`, `0x83`, `0x86`, interleaved between text runs). Doubles as the boot-screen banner and a human-readable geometry record. |
| `$FFE` | System drive number — **CONFIRMED**, value `0x01` in `Spel1.dsk`. Not a geometry field, noting separately so it doesn't get conflated with track/side count. |
| `$FFF` | Track count **+1** — **CONFIRMED**, value `0x29` = 41 → 40 tracks; matches "40Tr" in the same image's banner text exactly, and independently matches the directory's own highest-referenced sector (§1, §4). |

All three fields verified at their literal absolute offsets `0x0FBF`/`0x0FFE`/`0x0FFF` in the
raw image — i.e. within the **first** 4096-byte block (see §2's corrected layout).

**Design implication — revised in light of §1's RAM-vs-disk finding.** The label is real,
byte-exact, on-disk data, so an emulator `.dsk` loader **can** read it without adding an
emulator-specific geometry header (keeping the "raw sector dump, no header" convention from
reference doc §3a intact). But it's now clear real JWSDOS itself does **not** read this
label back to auto-configure `SS_DS_Char`/`number_of_tracks` (§1) — so an emulator that
auto-detects geometry from the label would be doing something **more convenient than the
real DOS does**, not simply replicating existing JWSDOS behavior. Worth treating as a
deliberate emulator-side UX improvement (call it out as such in the milestone doc) rather
than "just matching the hardware," since a real P2000T user had to get the format-menu
settings right manually.

**OPEN:** is there a dedicated numeric side-count byte parallel to `$FFF`'s track count, or
is "SS"/"DS" only recoverable by parsing the banner text? Not yet confirmed either way.

---

## 4. Directory entry format (32 bytes)

**CONFIRMED — field layout and names now sourced directly from `jwsdos5.0.asm`'s own
`DE_*` symbols (offsets relative to `DE_current_header = 0x6030`, the "active directory
entry" work buffer — the same 32-byte layout as an on-disk entry), cross-validated against
the real entries in `Spel1.dsk`'s active directory (§2: raw `0x1800`–`0x1A3F`, 18 files):**

| Offset | Size | Field | Source name |
|---|---|---|---|
| 0–15 | 16 B | Filename (space-padded to 16) | `DE_filename` |
| 16–18 | 3 B | Extension (every entry on this disk: `"BAS"`) | `DE_extension` |
| 19 | 1 B | **File type** stamp — every entry on this disk: `'B'` (Basic) | `DE_filetype` |
| 20–21 | 2 B | File length in bytes, little-endian word | `DE_filelen` (`DE_filelen_LO`/`_HI`) |
| 22–23 | 2 B | Load/transfer address, little-endian word | `DE_transfer` |
| 24 | 1 B | Head / side | `DE_head` |
| 25–26 | 2 B | First logical sector #, little-endian word | `DE_start_sector` |
| 27–28 | 2 B | Last logical sector #, little-endian word | `DE_end_sector` |
| 29 | 1 B | Transient FDC-transfer scratch — **not meaningful per-file data**, see below | `DE_sec` (alias `DE_sec_trk` lo byte) |
| 30 | 1 B | Transient FDC-transfer scratch — **not meaningful per-file data**, see below | `DE_trk` (alias `DE_sec_trk` hi byte) |
| 31 | 1 B | Transient FDC-transfer scratch — **not meaningful per-file data**, see below | `DE_sec_count` |

**Corrected — offset 19 is not a "creator identifier character."** `jwsdos5.0.asm` names it
`DE_filetype` and the only site that writes it (`write_file`) hardcodes `ld a,'B'` with the
comment "indicate type is Basic file." The DOS also recognizes an `"OBJ"` extension
(`ext_OBJ`, used for relocatable/binary loads via `set_extension_OBJ`), suggesting other
filetype values may exist for non-BASIC saves — not observed on this disk (every entry here
is `"BAS"`/`'B'`) and not confirmed by a second write site.

**Validation:** for all 18 active entries, `ceil(file_length / 256) == (last_sector −
first_sector + 1)` **exactly**, with zero exceptions — confirms offsets 20–28 and that
first/last-sector are logical sector numbers spanning the side (not per-track). Sequential
allocation confirmed too: entry N's last sector is always entry N+1's first sector minus
one, files packed back-to-back with no gaps.

**Load address note — re-verified against the correct active-directory cluster, count
revised:** of the 18 active entries, 11 load at `0x6547` (`BASIC_start_of_prog`, i.e. a
normal BASIC program load), 6 load at `0x67BC` (`Tralieenspel`, `klemvast`, `Elevatie`,
`Risk`, `Info Bat.S.`, `Battle star`), and one (`AUTORUN`) loads at `0x7000`. Not explained
by anything in this disk alone; flagging as an observed 3-way variation, not asserting a
cause.

**Note — a real `AUTORUN` file exists in the active directory, distinct from the string
lookalike below.** This active directory's `AUTORUN` entry (raw `0x19C0`) is a genuine
32-byte file entry — filename `"AUTORUN"`, extension `"BAS"`, loads at `0x7000` — i.e. this
specific disk really does have an autorun program, and the boot-code string constant noted
below is what the DOS uses to search for and match it.

**RESOLVED — offsets 29–31, previously three unconfirmed candidate explanations, now
sourced with certainty from `jwsdos5.0.asm`.** These bytes are **not** persisted per-file
metadata at all. `execute_disk_IO` computes `DE_sec`/`DE_trk` fresh from `DE_start_sector`
every time it performs an actual FDC transfer (a linear-sector-number → track/sector
conversion, 16 sectors/track), and counts `DE_sec_count` down sector-by-sector as the
transfer loop runs (`disk_IO_loop` exits when `DE_sec_count == 0`). Because these three
bytes physically sit at the tail of the same 32-byte "active header" RAM buffer
(`DE_current_header`) that `copy_active_header`/`copy_header` copies **whole** into the
directory when saving a file, whatever scratch values happen to be sitting there **from
whatever disk operation last ran** get incidentally persisted into the on-disk entry. This
fully explains the previously-puzzling real data: byte 31 (`DE_sec_count`) is always `0`
because the transfer loop's own exit condition guarantees it's zero by the time anything
else runs; bytes 29/30 (`DE_sec`/`DE_trk`) show mostly-constant-with-occasional-different
values because they're leftover CHS state from whichever specific transfer happened to
execute right before each entry got written, not a property of the entry's own file. The
three-candidate speculation from the previous update (self-referential pointer /
fragmentation counter / reserved bytes) is retracted in favor of this sourced explanation.

**New finding — a non-directory-entry lookalike:** the DOS's own boot code (block 1, raw
offset `0x0970`) contains the literal 19-byte string `"AUTORUN         BAS"` — clearly a
hardcoded filename+extension constant the boot code compares against directory entries (to
find and launch an autorun program), **not** an actual directory entry itself: real Z80 code
(`ED 73 ...` = `LD (nn),SP`) follows immediately where a real entry's length/load-address/etc.
fields would be, not struct data. Worth knowing so this string isn't mistaken for a 21st
catalog entry when parsing.

---

## 5. File allocation model

**CONFIRMED (owner research):**
- Each disk **side** is an independent logical volume: own directory track, own free space.
  **Files cannot span two sides.**
- Files are written **sequentially** until a side fills.
- When a file is overwritten by a shorter one, the freed sectors become a reusable gap; the
  DOS **prefers fitting new files into existing gaps** before appending at the end
  (first-fit/best-fit style — which one isn't specified).
- A **`defragment`** command packs a side's files together, presumably consolidating
  scattered gaps into one contiguous free region at the end.

**Design implication:** read-only directory browsing (the M19 host `.dsk` API's "browse"
feature) only needs the fixed 32-byte struct from §4 — no allocation logic required. Write
support (saving a file into a mounted image) would need this gap-reuse/append algorithm
modeled; scoping that as a later concern unless M19 needs write support from the start.

---

## 6. Boot / DOS-load sequence (`getdos`)

The monitor ROM's disk-boot gate (memsize check, then the SLOT1 cartridge header-flags
check) is monitor-ROM behavior, not JWSDOS-specific — see reference doc §5b for that part.
Once gated through, `getdos` (ROM address `0x0E90`, per the owner-supplied disassembly)
performs the actual JWSDOS-aware load:

1. Save the caller's SP; presume failure (`sysdisk_status = 1`, meaning — per the ROM's own
   ambiguous comment — either "no controller/drive/disk/motor-off/door-open" **or**
   "PDOS was read"; flagged as genuinely ambiguous in the source, not resolved here).
2. Copy 4 command templates from ROM (`disk_constants`) to RAM (`disk_transfer` = `0x6070`,
   CONFIRMED address).
3. `disk_init`: IM2, FDC reset (`OUT 0x04→0x90`), a **342 ms** settle delay (`delay_342ms`,
   854,799 T-states — a pure CPU busy-loop; needs no `TimingPolicy` hook since the
   cycle-exact core reproduces it for free), `RETI` (daisy-chain reset signal), **Sense
   Interrupt Status** (`0x08` → 2 result bytes), enable CTC-based interrupts, send
   **SPECIFY** (`03 60 34`).
4. `disk_recall` (**RECALIBRATE**, `07 01`) — seeks to track 0, waits via `HALT` for the
   completion interrupt, reads status.
5. `disk_motor_on`: `OUT 0x0C→0x90` (RESET|MOTOR), another 342 ms settle.
6. For each of 2 tracks: `read_track` — sets `0x94 = 0x01` (RAMSW bank 1, upper 8 KB of
   BANK1) **once**, sends **READ DATA** (`42 01 01 00 01 01 10 0E 00`), then polls **port
   `0x90` bit0** (byte-ready — see reference doc §5d correction) and executes `INI` (byte
   from `0x8D` → `(HL)`, `HL++`) in an unconditional loop terminated by the FDC's own
   result-phase interrupt (routed through CTC ch0, which redirects the polling loop's own
   return address to a status-reading routine rather than resuming it — an ISR technique,
   not a special hardware behavior). Track 1 lands at `0xE000`–`0xEFFF`, track 2 at
   `0xF000`–`0xFFFF` — **8 KB total, entirely within bank 1, no mid-load bank toggle.**
   (`getdos`'s two reads are addressed as DOS track "1" and track "2", landing at raw offsets
   `0x0000` and `0x1000` respectively — resolved in §2: this is a generic, directory-unaware
   2-track load; it happens to land on the same RAM range JWSDOS's own directory buffers
   later occupy, but `getdos` itself has no notion of a directory and doesn't skip anything.)
7. Check the loaded track 1's first byte against `0xF3` ("system disk" signature); if it
   doesn't match, clear `sysdisk_status` to 0. **CONFIRMED (owner, 2026-07-13): `0xF3` is the
   official Philips disk-BASIC signature, not a JWSDOS convention.** The exact check, straight
   from `Disk.asm` — `ld hl,0E000h` / `ld a,0F3h` / `cp (hl)` — compares raw disk offset
   `0x0000` (the very first byte loaded to `0xE000`) against `0xF3`. Two real JWSDOS images
   (`Spel1.dsk`, `jwssytem.dsk`) have `0x20` there instead — confirmed to be JWSDOS 5.0's own
   real first opcode byte (`JR NZ`, per `jwsdos5.0.asm`'s `org 0E000h`), not a bad dump. The
   owner then located a real **`.IMD` image of "Disk BASIC 24K"** (the official Philips
   cartridge+disk product — 16 KB SLOT1 cartridge + 8 KB loaded from disk, exactly the figure
   reconciled earlier in this doc's provenance) and confirmed it **has `0xF3` as the first
   byte at `0xE000`**. This settles it: `0xF3` signs the **official Philips disk-BASIC
   system disk**; JWSDOS is a **third-party, user-group-developed DOS** with no reason to
   carry that byte, and doesn't. Not a bug or an emulator-relevant contradiction — two
   different DOSes, one convention, only one of them follows it.
   **Follow-on point still worth confirming:** `getdos` itself only sets `sysdisk_status`
   (never jumps into the loaded code — see step 8); some other, not-yet-sourced caller reads
   that flag afterward to decide whether to actually launch the loaded code. Since real
   JWSDOS disks legitimately leave `sysdisk_status = 0` here and clearly still work in
   practice, that caller either treats `sysdisk_status` as informational rather than a hard
   gate, or JWSDOS reaches the user through a different path than this automatic
   cartridge-triggered `getdos` flow. Worth sourcing `getdos`'s caller to settle which.
8. Cleanup (always runs): reset CTC ch0 (`03`), FDC off (`00→0x90`), restore caller's SP,
   **restore `0x94 = 0x00`** (bank 0) — so whatever code actually runs the loaded DOS
   extension must itself re-select bank 1 before jumping into it; it isn't left selected.

**CTC wiring, exact values (JWSDOS's usage of the generic mechanism in reference doc §5e):**
ch0 (disk-complete) control word `0xD5` (rising edge), TC `0x01`; ch1 (disk-not-ready)
control word `0xC5` (falling edge), TC `0x01`; both reset via `0x03` when done. `CTC_timer_disk`
(the RAM cell at `0x6020`, ch0's IM2 vector-table slot) is dynamically rewritten between
`empty_handler` and `disk_IO_interrupt` depending on operation phase — a software pattern,
not a new hardware fact.

**Cartridge context (24K disk BASIC — reconciled):** a **16 KB** SLOT1 cartridge whose
header (reference doc §5b, byte at `0x1000`) flags "needs DOS"; `getdos` loads **8 KB**
extra from the 2 DOS tracks (16 KB + 8 KB = 24 KB total, matching the name).

**Command bytes used, exact:**

| Command | ROM name | Opcode | Full bytes sent |
|---|---|---|---|
| SPECIFY | "Specification" | `0x03` | `03 60 34` |
| RECALIBRATE | "Recall" | `0x07` | `07 01` |
| SEEK | "Search" | `0x0F` | `0F 01 01` |
| READ DATA | "Disk IO" (read) | `0x42` | `42 01 01 00 01 01 10 0E 00` |
| WRITE DATA | "Disk IO" (write) | `0x45` | same shape, opcode `0x45` |
| SENSE INTERRUPT STATUS | — | `0x08` | `08` → 2 result bytes |

Byte positions structurally match the standard µPD765 9-byte READ/WRITE DATA parameter
block (drive/unit, cylinder, head, sector, N, EOT, GPL, DTL) — confident in the values and
positions (cross-checked against the ROM's own field comments); **not** independently
verifying the datasheet's MT/MF/SK bit-flag decomposition of the opcode byte from memory —
match dispatch on the exact byte values above, not a reconstructed bit theory.

---

## 7. Open items

1. Side-count field (§3) — dedicated byte parallel to `$FFF`'s track count, or is "SS"/"DS"
   only recoverable by parsing the banner text?
2. **Where does the on-disk label (§3) actually get written?** `jwsdos5.0.asm`'s format/erase
   routine (`disk_erase_directory`/`erase_directory_noask`) zero-fills and re-saves the
   directory, and the in-RAM banner template (`SS_DS_Char`/`track_count_chars`, defaulting to
   `"DS 80Tr drive "`) is clearly the source of the on-disk banner text — but the specific
   code that copies this RAM template into the `$FBF`–`$FFF` disk region wasn't located in
   this pass (a 2980-line file, only sampled — not exhaustively traced). **Owner's lead
   (2026-07-13):** the JWSDOS system disk carries two separate utilities, neither part of
   `jwsdos5.0.asm` — **`Format`** (low-level FDC formatting) and **`JWS Systeem Disk`**
   (writes the two DOS system tracks, including presumably the label, onto an
   already-formatted disk). `JWS Systeem Disk` is the likelier candidate for writing the
   label and the two system tracks — owner is planning to examine it next; see item 5 below,
   which is the same program's write path.
3. **Does anything re-sync `SS_DS_Char`/`number_of_tracks` (§1) from an inserted disk's own
   label?** Not found in this pass. If nothing does, real JWSDOS relies entirely on the
   operator manually matching the format menu to whatever disk is inserted — worth
   double-checking before concluding the emulator's auto-detect-from-label behavior (§3) is
   purely an enhancement rather than also fixing a real usability gap.
4. **Where does side 2's own directory actually live in a raw `.dsk` image?** (§2)
   `dir_side2_prep` reads a physically different disk surface (FDC head = 1); not located in
   `Spel1.dsk`'s raw bytes in this pass — depends on the image's side-interleaving
   convention, which itself isn't confirmed.
5. **Origin of the stale 20-entry directory cluster at raw `0x1000`–`0x17FF`** (§2) — read as
   real, valid, cross-validated directory entries, but not touched by the current build's
   read/save routines and sharing no filenames with the 18-entry active directory.
   **Leading theory, HIGHLY PLAUSIBLE but not yet confirmed (owner, 2026-07-13):** the
   `JWS Systeem Disk` utility (distinct from `Format` — see item 2) writes RAM
   `0xE000`–`0xFFFF` verbatim as the disk's two system tracks, so sectors 1–8 of track 2
   carry whatever was sitting in that RAM region (e.g. a stale directory from a previously
   booted/read disk) at write time, since only the active directory portion (sectors 9–16)
   gets explicitly cleared and rewritten. Supported by a second real image (`jwssytem.dsk`)
   whose track 2 is entirely clean where this one isn't — see §2. Owner's next step is
   reading `JWS Systeem Disk`'s own disassembly to confirm.
6. **Follow-on from the now-resolved `0xF3` signature question (§6 step 7):** does
   `sysdisk_status` actually gate whether the loaded DOS launches, and if so, how does a real
   JWSDOS disk boot despite legitimately failing the `0xF3` check? Needs `getdos`'s
   (unsourced) caller to settle.
7. Load-address 3-way variation (`0x6547`/`0x67BC`/`0x7000`) across `Spel1.dsk`'s active
   directory entries (§4) — observed, unexplained.
8. `sysdisk_status`'s ambiguous initial-value comment (§6 step 1) — presented as-is, not
   resolved.
9. RAM variable addresses beyond `disk_transfer` (`0x6070`, confirmed): `memsize`,
   `disk_status`, `sysdisk_status`, `stacktemp_disk`, `disk_track_num`, `disk_search_track` —
   nice-to-have for `.state`/debugger symbol work, not blocking.

**Resolved since the last revision (moved out of this list):** the SS-80/DS-40 geometry
ambiguity (§1 — byte-confirmed 40-track/DS); the byte-offset reconciliation between the
label and "track 2" (§2 — was an imprecision, label is in the first block); directory-entry
offsets 0–28's field semantics (§4 — fully confirmed via real `DE_*` source symbols);
directory-entry **offsets 29–31's meaning** (§4 — resolved with source-level certainty:
transient FDC-transfer scratch state, not persisted per-file metadata — previously three
open candidates, now answered); the `getdos`-track-2-vs-directory puzzle (§2, §6 — resolved:
`getdos` is simply directory-unaware, not "skipping" anything); the offset-19 field's
identity (§4 — it's `DE_filetype`, not a "creator identifier character"); **the `0xF3`/`0x20`
system-disk-signature discrepancy (§6 step 7) — CONFIRMED as two different DOSes, one
convention: `0xF3` is the official Philips disk-BASIC signature (verified against a real
"Disk BASIC 24K" `.IMD` image), JWSDOS never carried it and was never expected to.**

---

## 8. Provenance log

- **2026-07-13:** disk-boot gate + `memsize` check sourced (owner-supplied `Startup.asm`
  excerpts). → applies to reference doc §5b (not this doc — monitor-ROM behavior).
- **2026-07-13:** full `getdos`/`Disk.asm` sourced (owner-supplied) — command bytes, CTC
  wiring, timing, RAMSW usage, cartridge-size reconciliation (16 KB + 8 KB = 24 KB). →
  this doc §6, and a port-0x90 IN-direction correction flagged for reference doc §5d.
- **2026-07-13:** MAME PR #7577 checked (open, unmerged) as cross-reference for geometry —
  independently corroborates the multi-geometry fact without resolving it. → this doc §1.
- **2026-07-13:** JWSDOS 5.0 multi-geometry support, directory entry format, allocation
  model, and the geometry/system label sourced (owner research). → this doc §1, §3, §4, §5.
- **2026-07-13:** real disk image `Spel1.dsk` (327,680 B, owner-supplied) byte-inspected
  directly (`od`/`strings` hex dumps + programmatic directory-entry parsing/cross-validation
  over all 20 real entries). Confirmed geometry (40 tracks, DS) with two independent
  corroborating lines of evidence; confirmed the on-disk block layout and corrected the
  "directory for side 1" guess to "side 0's own directory"; confirmed the label's exact
  absolute offsets and byte values; confirmed directory-entry offsets 0–28 byte-exact with
  an explicit cross-validation identity holding across all 20 entries, zero exceptions;
  **retracted** the earlier offsets-29–31 inference (cached CHS target + sectors-used count)
  as not matching real data, replaced with an honest three-candidate open item; found and
  ruled out an `"AUTORUN         BAS"` string in boot code as a directory-entry lookalike. →
  this doc §1, §2, §3, §4, §7.
- **2026-07-13:** owner's own manual disassembly of the **JWSDOS 5.0 binary itself**
  (`jwsdos5.0.asm`, real recovered symbol names) sourced — the highest-confidence source in
  this doc for JWSDOS's internal behavior. Major findings: (1) `is_disk_SS`/
  `get_sectors_per_side` read live RAM state (`SS_DS_Char`/`number_of_tracks`, defaulting to
  DS/80-track), **not** the on-disk label — real JWSDOS does not auto-detect geometry from an
  inserted disk; (2) `dir_side1_prep`/`dir_side2_prep` confirm the exact on-disk directory
  location (track-2 sectors 9–16 for side 1's active directory, sectors 1–8 for side 2, via a
  different physical head) — direct byte re-inspection of `Spel1.dsk` against this then
  revealed a **second, different 20-entry directory cluster** at the previously-assumed
  "whole track2 = directory" region, corrected to: sectors 1–8 hold a stale/inactive
  directory snapshot, sectors 9–16 hold the real active one (18 entries, not 20 as previously
  reported — the earlier count had missed this split entirely); (3) `execute_disk_IO`
  **resolves** the offsets-29–31 mystery with certainty — transient FDC-transfer scratch
  state aliasing the tail of the "active header" RAM buffer, not per-file metadata;
  (4) offset 19 is `DE_filetype` (hardcoded `'B'` for Basic saves), not a "creator
  identifier"; (5) confirms a real `AUTORUN.BAS` file entry exists in the active directory,
  distinct from the boot-code string constant found earlier. → this doc §1, §2, §3, §4, §6,
  §7 (multiple corrections/retractions of the previous update's conclusions, detailed inline
  in each section).
- **2026-07-13:** owner proposed a FORMAT-utility theory for the stale directory cluster
  (§2/§7 item 5): a separate FORMAT program writes RAM `0xE000`–`0xFFFF` verbatim as the two
  system tracks, so the non-actively-managed part of that RAM (track-2 sectors 1–8) carries
  whatever was there at format time. A second real image, `jwssytem.dsk` (327,680 B,
  owner-supplied), was byte-inspected to check it: its track 1 + label are byte-for-byte
  identical to `Spel1.dsk`'s, but its entire track 2 is clean (all zero, both halves) —
  consistent with the theory. Marked **highly plausible, not confirmed** per the owner's own
  framing; owner's next step is reading the FORMAT utility's disassembly directly. Separately,
  spotted and flagged (not resolved) a real discrepancy: `getdos`'s own system-disk-signature
  check (`Disk.asm`, raw offset `0x0000` should read `0xF3`) doesn't match either real disk
  image (`0x20` at that offset on both), despite both clearly being valid working system
  disks. → this doc §2, §6, §7.
- **2026-07-13:** owner addendum — the JWSDOS system disk carries **two** separate
  utilities, not one: `Format` (low-level FDC formatting) and `JWS Systeem Disk` (writes the
  DOS system tracks). Re-attributed the system-track-writing theory above from a generic
  "FORMAT utility" to `JWS Systeem Disk` specifically. → this doc §2, §7.
- **2026-07-13:** sharpened the `0xF3`/`0x20` system-disk-signature discrepancy (§6 step 7,
  §7 item 6). Checked `jwsdos5.0.asm` at `org 0E000h`: the real JWSDOS 5.0 binary's own first
  byte is `0x20` (`JR NZ`, the disassembler's own comment flags it "TODO: function of
  this?") — confirming this isn't just the disk dumps disagreeing with the ROM, it's the DOS
  binary itself. **Owner's theory:** `0xF3` likely signs the official Philips system disk;
  JWSDOS is a third-party, user-group-developed DOS with no obligation to match that
  convention — cleanly explains the discrepancy without doubting any source. Marked highly
  plausible, not confirmed (no official Philips system disk inspected yet). New follow-on
  open question: whether `sysdisk_status` actually gates launching the loaded DOS, and if so
  how real JWSDOS disks boot despite failing this check — needs `getdos`'s (unsourced) caller
  to settle. → this doc §6, §7.
- **2026-07-13:** **CONFIRMED.** Owner located a real `.IMD` image of "Disk BASIC 24K" (the
  official Philips cartridge+disk product — 16 KB SLOT1 cartridge + 8 KB loaded from disk,
  matching this doc's earlier-reconciled cartridge-size figure) and verified it has `0xF3` as
  the first byte at `0xE000`. Settles the theory above: `0xF3` is the official Philips
  disk-BASIC signature; JWSDOS is third-party and was never expected to carry it. Moved from
  §7's open items to the resolved list. The `sysdisk_status`-gating follow-on question stays
  open (§7 item 6). → this doc §6, §7.
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

**Generalized raw sector-offset formula — CONFIRMED (2026-07-22, `P2000.Machine` milestone-19
implementation, `DskImage.SectorOffset`), derived from this section's own confirmed byte ranges
and now validated against real fixtures:**

```
raw_offset = head * Tracks * BytesPerTrack
           + cylinder * BytesPerTrack
           + (sector - 1) * BytesPerSector
```

**Side-major, cylinder-minor** — i.e. side/head is the OUTERMOST grouping (all of head 0's
cylinders are contiguous before head 1 begins anywhere), not interleaved per-cylinder. Derived
from this section's own confirmed identity: `(cylinder=1, head=0, sector=9) → raw 0x1800`
(`dir_side1_prep`'s own target) only holds if consecutive cylinders of the SAME head are
contiguous in the file. Validated directly against real `Spel1.dsk` bytes (both DOS-track reads
match exactly) and against three further real images (`jws-sytem.dsk`, `empty-jws.dsk`,
`hires_demo.dsk`) — geometry auto-detect and directory reads all check out; see
`tests/P2000.Machine.Tests/Devices/Fdc/DskImageTests.cs`/`RealFixtureTests.cs`.

**Direct implication for open item 2 below (where does side 2's directory live?) — a strong
candidate answer, but NOT independently verified against real head=1 data yet.** If the formula
holds for head=1 the same way it's confirmed for head=0, side 2's data begins at raw offset
`Tracks * BytesPerTrack` (e.g. `0x28000` for this disk's 40 tracks), and `dir_side2_prep`'s own
target (`DE_start_sector=0x11`/17 = "track-2 sector 1", head=1) would land at raw
`Tracks*BytesPerTrack + 1*BytesPerTrack + 0 = 0x29000` for a 40-track disk. This is a direct
arithmetic consequence of the formula above, not yet cross-checked against actual bytes at that
offset in a real image with real side-2 content — flagging as the strongest lead on open item 2,
not a closed answer.

**Why sectors 1–8 (0x1000–0x17FF) hold a second, different directory — write-SCOPE now
CONFIRMED from `JWS Systeem Disk`'s own disassembly (owner, 2026-07-20); the data-SOURCE half
of the theory below remains open.** `dir_side1_prep` unconditionally targets sector 25
(track-2 sector 9); nothing found in `jwsdos5.0.asm` ever reads or writes sectors 17–24
(track-2 sectors 1–8) as directory data. Yet those bytes are shaped exactly like real
directory entries and list 20 files with **zero filename overlap** with the 18 files in the
active directory. The owner's theory: the JWSDOS system disk carries **two separate
utilities**, not part of the `jwsdos5.0.asm` binary disassembled so far — a **`Format`**
program (low-level FDC formatting — sector/track layout) and a **`JWS Systeem Disk`** program
(writes the DOS system tracks onto an already-formatted disk, turning it into a bootable
system disk). It's this second program the theory concerns.

**CONFIRMED (owner, 2026-07-20, from `JWS Systeem Disk`'s own disassembly): the program
writes a full track 1 (all 16 sectors) plus only 8 sectors of track 2 — sectors 1–8. It does
NOT touch sectors 9–16 of track 2 at all — not written, not cleared, not read.** This refines
(and partly supersedes) the earlier "clears/rewrites the active-directory portion before
writing" framing below — the more precise, disassembly-confirmed mechanism is simpler: sectors
9–16 are entirely outside this program's write path, full stop. Whatever was physically on the
disk there beforehand (typically zero/blank from low-level `Format`, unless the disk had
already been used) is exactly what remains after `JWS Systeem Disk` runs; `Spel1.dsk`'s real
18-file active directory in that range reflects ordinary DOS `save_directory` activity
**after** `JWS Systeem Disk` ran (i.e. files actually copied onto the finished system disk),
not anything `JWS Systeem Disk` itself wrote there.
- **Still OPEN — not confirmed by this pass:** where the data written into sectors 1–8 actually
  comes from. The original theory (**live RAM 0xE000–0xFFFF dumped verbatim, sectors 1–8
  landing on whatever stale directory happened to be sitting in that RAM window at write
  time**) is consistent with the confirmed write-scope above and still the leading candidate,
  but the write-scope finding alone doesn't prove the SOURCE is live RAM specifically, as
  opposed to e.g. a fixed template/leftover buffer baked into the utility's own image. Needs
  the specific instructions that populate sectors 1–8's write buffer, not just the sector
  range they're written to.

**Empirical support for the leading (still not fully confirmed) source theory:** a second real
image, `jwssytem.dsk` (327,680 B, owner-supplied 2026-07-13), lines up with it directly. Its
track 1 + label (`0x0000`–`0x0FFF`) are **byte-for-byte identical** to `Spel1.dsk`'s — both
share the same DOS boot code and the same `"...DS 40Tr drive"` label, consistent with both
having been formatted from the same master. But its entire track 2 (`0x1000`–`0x1FFF`, both
halves) is **all zero** — no stale cluster, no active entries, nothing — exactly what the
live-RAM-source theory predicts if this particular disk happened to be formatted right after a
boot where the relevant RAM genuinely held nothing (e.g. formatted immediately, before any
directory read/write touched that RAM). `Spel1.dsk`'s stale 20-file cluster, by contrast, is
exactly what the theory predicts if some *other* disk (with those 20 files in its own
directory) had been booted or read shortly before `Spel1.dsk` was formatted. Not proof — still
pending the specific write-buffer-population instructions in `JWS Systeem Disk`'s disassembly
— but a real, independent second data point that fits cleanly.

**Corrected — NOT "directory for side 1(?)" and NOT side 2's directory sitting out of
place.** Both clusters above have every entry's side-byte (§4 offset 24) equal to **0** —
this is all side-1 data, one currently-active and one stale. **Side 2's own directory**
(`dir_side2_prep`: sector 17 = track-2 sector 1, same 2048 B length, destination
`DIR_side_2_mem = 0xF800`, but issued with FDC **head = 1** — a physically different disk
surface) lives somewhere else entirely in the raw `.dsk` file, depending on how this image
interleaves the two physical sides. Not located in this pass — **new open item**, see §7.

**CORRECTED (2026-07-22, `P2000.Machine` milestone-19 implementation, direct byte inspection of
`Spel1.dsk`) — the paragraph above is WRONG about the active cluster specifically; flagging
rather than smoothing over it, per this doc's own discipline.** Re-inspecting the real bytes
during implementation found: the STALE cluster (raw `0x1000`–`0x17FF`, 20 entries) does read
side-byte (offset 24) = **0** for every entry, matching the claim above — but the ACTIVE
directory (raw `0x1800`–`0x1FFF`, the 18 real entries) reads side-byte = **1** for every entry,
the opposite of what this section originally claimed. This does not affect anything the
milestone-19 build implemented (`DskImage.ReadDirectory()` reads the active region by its fixed
raw offset, per the confirmed byte ranges above, not by filtering on this byte — so the
implementation is correct regardless of which value is right here), but it's a real discrepancy
in this doc's own narrative that the "both clusters are side-1 data" conclusion was built on.
**Open tension this creates, not resolved here:** if the active directory's own entries
genuinely carry `DE_head=1`, and `dir_side1_prep` (which reads/writes this exact directory) is
confirmed elsewhere in this doc to always operate with `DE_head=0` as an FDC-command parameter,
then either `DE_head` at offset 24 doesn't mean "which physical side this file's data lives on"
in the way assumed (a used/valid flag? something else?), or the active cluster is not what this
section has been calling "side 1's directory" at all. Not guessing further — this needs someone
with direct `jwsdos5.0.asm` access to reconcile; see §7 items 2 and 3, both updated to carry this
forward.

**RESOLVED — partially (2026-07-22, direct read of the owner-supplied `jwsdos5.0.asm` source
itself, not the earlier secondhand disassembly notes).** The "does `DE_head` mean what this doc
assumed" half of the tension above is now settled: **yes, it does.** `dir_side1_prep` sets
`DE_head=0` and `dir_side2_prep` sets `DE_head=1` (source comments: "side 1 (0)" / "side 2 (1)"),
exactly as this doc already had it, and the SAME RAM cell (`DE_head`, `06048h`, offset 24 from
`DE_current_header`) is what `find_room`/`insert_dir_entry` read and write when placing a FILE's
own directory entry — `find_room` tries side 1 first (`DE_head=0`), escalating to side 2
(`DE_head=1`) only if side 1's directory is full and the disk is double-sided; `insert_dir_entry`
then reads that same `DE_head` to choose which in-RAM buffer (`DIR_side_1_mem`, ending `0xF7FF`,
vs `DIR_side_2_mem`, ending `0xFFFF`) receives the new entry. So `DE_head` genuinely is "which
physical side this entry belongs to," consistently, in both its command-parameter and
persisted-field uses — the "maybe it's a used/valid flag" alternative is ruled out.

**But this sharpens the puzzle rather than dissolving it, and a second, real mechanism was found
that at least makes the anomaly plausible rather than inexplicable:** `disk_defragment`
(`crunch_next_file`, source lines ~703–743 — the `defragment` command already noted in §5) walks
every existing file, **deletes its directory entry, then calls `write_file` to re-save it** —
which internally re-runs `find_room` and can assign a **different** `DE_head` than the file had
before, since defragmentation's whole point is repacking files into whatever gaps exist across
BOTH sides at that moment. The routine explicitly detects this: it reads the just-reassigned
`DE_head` after `write_file`/`save_directory`, compares it against the next entry's own recorded
side byte (`(ix+018h)`, the same offset-24 field), and branches on whether the side "swapped."
**This proves a file's `DE_head` is not fixed at original-save-time — ordinary, documented DOS
operation (defragment) can and does reassign which side an existing file's directory entry
reports, independent of when or how it was first saved.** That's a real, sourced mechanism, not
speculation.
**Still not fully closed:** this explains HOW real disks can end up with entries whose `DE_head`
doesn't match a naive "side 1 filled first, in save order" expectation, but it doesn't by itself
explain why `Spel1.dsk` specifically shows ALL 18 active entries reading `DE_head=1` with zero
exceptions — that depends on this particular disk's own operational history (how many
saves/deletes/defragments happened, in what order), which the static DOS source can't reveal.
Treat "DE_head is trustworthy and means physical side" as settled; treat "why this specific
disk's active directory is uniformly side-2-flagged" as an open provenance question about this
one disk image, not a DOS-semantics question — likely unanswerable without more disk images to
compare against.

**`save_directory`'s exact mechanics — CONFIRMED source-level (owner, 2026-07-20, re-read of
`jwsdos5.0.asm` lines 1107–1143), a strong new candidate explanation for the stale cluster:**

```
dir_side1_prep:  DE_filelen=0x0800 (2048B); DE_transfer=0xF000 (DIR_side_1_mem);
                 DE_start_sector=0x19 (25, track-2 sector 9); DE_head=0.
dir_side2_prep:  DE_transfer=0xF800 (DIR_side_2_mem); DE_start_sector=0x11 (17, track-2
                 sector 1); DE_head=1.   (DE_filelen carries over from dir_side1_prep, still
                 0x0800 — dir_side2_prep never resets it, doesn't need to.)

save_directory:  call dir_side1_prep → disk_write_action     ; ALWAYS runs
                 call is_disk_SS → ret z                     ; single-sided disk: stop here
                 call dir_side2_prep → disk_write_action     ; double-sided only
```

`execute_disk_IO` genuinely consumes `DE_head` as a physical FDC parameter (folds it into the
drive number via `xor 0x04`, confirmed at line ~1427–1433) — so `dir_side2_prep`'s write really
does target physical **head 1**, a different surface, not a same-head sector-only distinction.
This is the exact mechanism the earlier "Corrected" note above already inferred; now sourced
to the precise instructions rather than inferred from the comments alone.

**New candidate explanation for the stale 20-entry cluster, combining this with the
already-confirmed `JWS Systeem Disk` write-scope finding (above):** `JWS Systeem Disk` writes a
full track 1 plus track-2 sectors 1–8 as one **blind, sequential, directory-unaware** copy —
by write-scope alone (16 + 8 = 24 sectors = 6144 B), its source RAM range is `0xE000`–`0xF7FF`.
**That range includes the entirety of `DIR_side_1_mem` (`0xF000`–`0xF7FF`) — the SAME RAM
buffer `dir_side1_prep` reads/writes for perfectly ordinary side-1 directory operations, on
WHATEVER disk happens to be in the drive at the time.** Since this buffer is never zeroed
between operations (getdos's own boot-time load into this same RAM range confirms nothing
clears it — §2 above), whatever directory content was sitting there — most plausibly a genuine
side-1 directory read from some *other* disk shortly before `JWS Systeem Disk` ran — gets swept
into the new disk's sectors 17–24 as an incidental side effect of the blind copy, landing at
raw `0x1000`–`0x17FF`. **This would also explain the puzzling "all 20 stale entries have
side-byte 0" observation without needing a separate explanation:** `DIR_side_1_mem` is
STRUCTURALLY a side-1-only buffer (every write path that populates it does so via
`dir_side1_prep`, which always operates in a `DE_head=0` context) — so no matter which disk's
directory was sitting there when `JWS Systeem Disk` ran, it would necessarily be shaped like a
valid, side-byte-0 directory. This is a materially stronger version of the original "live RAM
dump" theory — same shape, now grounded in an exact RAM-buffer identity (`DIR_side_1_mem`
specifically, not just "whatever's in `0xE000`-`0xFFFF`") that mechanistically explains the
side-byte-0 detail the original theory didn't account for.
- **Scope note (2026-07-22 correction, see above): this reasoning is about the STALE cluster
  specifically and is unaffected by the active-cluster side-byte correction above** — the stale
  cluster's side-byte=0 reading stands confirmed; only the active cluster's own side-byte turned
  out to be 1, not 0. Don't extend this "structurally side-1-only" argument to the active
  cluster without re-checking it against that correction first.
- **Still not fully proven** — this is a strong synthesis of two separately-confirmed facts
  (the write-scope finding + this RAM-buffer identity), not a direct disassembly trace of "here
  is the specific prior disk-read that populated `DIR_side_1_mem` before this write." Treat as
  the leading theory, not a closed item.
- **Independent of, and doesn't resolve, where TRUE side-2 (head 1) data lives in a raw `.dsk`
  file** — still open, see §7 item 3. If a real, currently-in-use double-sided disk's genuine
  side-2 directory is written via `dir_side2_prep`'s head-1 path, it must live SOMEWHERE in the
  raw file, and this pass didn't locate it — it is almost certainly NOT the same bytes as the
  stale cluster this theory explains, since those are side-1-shaped, not side-2-shaped.

---

## 3. The geometry / system label

**CONFIRMED (owner research):** near the end of the DOS boot area, JWSDOS embeds a
human-readable banner plus two machine-readable bytes, all **rewritten by the DOS itself
when formatting a system disk** — i.e. this is a real superblock, not something the emulator
has to infer or that the operator has to configure separately.

| Offset | Field |
|---|---|
| `$FBF` | ASCII banner — **CONFIRMED byte-exact against `Spel1.dsk`:** `"JWS DISK SYSTEM.(c)-1986....versie 5.0.NL....DS 40Tr drive "` (the `.` marks non-ASCII display color/position attribute bytes, not literal characters — confirmed to be single bytes, e.g. `0x8C`, `0x04 0x03 0x02`, `0x83`, `0x86`, interleaved between text runs). Doubles as the boot-screen banner and a human-readable geometry record. |
| `$FEF` | **SS/DS indicator — CONFIRMED exact byte position (2026-07-20, direct byte inspection of both real images), a single fixed-offset ASCII character: `'D'` or `'S'` (the first letter of `"DS "`/`"SS "`), always followed by a literal `'S'` at `$FF0`.** Verified byte-identical (`44 53` = `"DS"`) at this exact offset in both `Spel1.dsk` and `jwssytem.dsk` (which share byte-identical track-1+label data). **Closes the former "side-count field" open item (§7) — there is no separate NUMERIC side-count byte, but there IS a reliable single-byte, fixed-offset field**, just as usable for auto-detection as `$FFF`'s track-count byte; no fuzzy text search of the banner is needed. |
| `$FF2`–`$FF3` | Track count as **2-digit ASCII text** (e.g. `"40"`) — human-readable duplicate of `$FFF`'s binary value, part of the same `"...Tr drive "` banner tail. Redundant with `$FFF`; **prefer `$FFF` for parsing** (fixed-width binary, no digit-count ambiguity for 35/80 vs 40) and treat this as display-only. |
| `$FFE` | System drive number — **CONFIRMED**, value `0x01` in `Spel1.dsk`. Not a geometry field, noting separately so it doesn't get conflated with track/side count. |
| `$FFF` | Track count **+1** — **CONFIRMED**, value `0x29` = 41 → 40 tracks; matches "40Tr" in the same image's banner text exactly, and independently matches the directory's own highest-referenced sector (§1, §4). |

All fields verified at their literal absolute offsets in the raw image — i.e. within the
**first** 4096-byte block (see §2's corrected layout). Full confirmed byte dump of the label
region (`Spel1.dsk`, identical in `jwssytem.dsk`), for reference:

```
$FBF "JWS DISK SYSTEM" $FCE<attr> "(c)-1986" $FD7..$FDA<attr×4> "versie 5.0" $FE5<attr>
"NL" $FE8..$FEE<attr×7> "DS" $FF1<space> "40" "Tr" $FF6<space> "drive" $FFC<space>
$FFD=00 $FFE=01(drive#) $FFF=29(=41, track count+1)
```

**Design implication — revised in light of §1's RAM-vs-disk finding.** The label is real,
byte-exact, on-disk data, so an emulator `.dsk` loader **can** read it without adding an
emulator-specific geometry header (keeping the "raw sector dump, no header" convention from
reference doc §3a intact). But it's now clear real JWSDOS itself does **not** read this
label back to auto-configure `SS_DS_Char`/`number_of_tracks` (§1) — so an emulator that
auto-detects geometry from the label would be doing something **more convenient than the
real DOS does**, not simply replicating existing JWSDOS behavior. Worth treating as a
deliberate emulator-side UX improvement (call it out as such in the milestone doc) rather
than "just matching the hardware," since a real P2000T user had to get the format-menu
settings right manually. **Now that `$FEF` (side) is confirmed alongside `$FFF` (track
count), auto-detection is two independent fixed-offset single-byte reads — no banner-text
parsing, no ambiguity — a small, low-risk implementation for the host `.dsk` loader.**

**RESOLVED (2026-07-20, direct byte inspection, closes the former "side-count field" open item):** is there a dedicated
side-count byte parallel to `$FFF`'s track count? Not a separate numeric byte, but
functionally yes — `$FEF` is a reliable, fixed-offset ASCII `'D'`/`'S'` character (see the
table row above), confirmed identically in two independent real images. No banner-text search
needed; parse it exactly like `$FFF`.

**RESOLVED (owner, 2026-07-19 — disassembly of `JWS Systeem Disk` itself; closes the former
"where does the on-disk label get written" open item, moved to §7's resolved list):**
`JWS Systeem Disk` — not `jwsdos5.0.asm`'s own format/erase routine — is confirmed to be the
program that writes this label, and it writes the **correct** track count and SS/DS into the
disk image's text for whatever geometry the operator actually selected at format time. This
matches the in-RAM-template theory already on record here (`SS_DS_Char`/`track_count_chars`,
§1) rather than a hardcoded/copy-pasted banner — the label is live operator-selected geometry,
not a fixed constant baked into every disk regardless of shape. **Scope note — this confirms
the label-writing half only, not §7 item 3's separate "stale directory carried over from live
RAM" theory**, which concerns a different part of the same program's write path (whether it
dumps `0xE000`–`0xFFFF` verbatim including sectors 1–8
of track 2) — don't conflate the two; §7 item 3 stays open pending that specific question.

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
| 24 | 1 B | Head / side | `DE_head` — **confirmed to genuinely mean physical side (0/1), source-validated (§2, 2026-07-22).** `Spel1.dsk`'s real active-directory entries read 1 here, not 0 as originally reported in §2 — not a misidentified field, but a real per-disk value; `defragment` can reassign it on an existing file (§2, §5) |
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
- **CONFIRMED (2026-07-22, `jwsdos5.0.asm` source, `disk_defragment`/`crunch_next_file`):
  defragment can move a file from one side to the other, not just repack it within its current
  side.** Each file is deleted from the directory and re-saved via `write_file` (which re-runs
  `find_room`), so a file that fit on side 1 before may land on side 2 afterward (or vice versa)
  depending on what gaps exist across both sides at that moment — the routine explicitly checks
  for and handles this "side swapped" case. **A file's `DE_head`/side is therefore not a
  permanent property fixed at original save time**; it can change across the disk's ordinary,
  documented lifecycle. See §2 for how this bears on a real observed discrepancy in this disk's
  directory data.

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
   comment — either "no controller/drive/disk/motor-off/door-open" **or** "PDOS was read".
   **The ambiguity is now EXPLAINED, not just flagged (owner, 2026-07-20, re-read of step 7's
   exact branch below): it's inherent in the ROM's own logic, not a disassembly gap** — see
   step 7, which never clears this value on the success path, only on the specific
   "loaded fine, but not the official signature" path.
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
7. Check the loaded track 1's first byte against `0xF3` ("system disk" signature). **Exact
   branch, straight from `Disk.asm` (re-read 2026-07-20 for the precise polarity — corrects an
   imprecise "recognized/not recognized" framing in an earlier pass of this doc):**
   ```
   ld hl,0e000h        ; 1st byte of track 1
   ld a,0f3h
   cp (hl)              ; A(0xF3) - (HL) ; Z set iff byte at 0xE000 == 0xF3
   jr z,disk_interrupts_off   ; MATCH: skip the clear below — sysdisk_status stays 1
   xor a                       ; NO MATCH: clear sysdisk_status to 0
   ld (sysdisk_status),a
   ```
   **So `sysdisk_status` ends at exactly `1` when `0xF3` matches, and exactly `0` when it
   doesn't** — the reverse of what "presume failure, clear on success" would suggest at a
   glance. This is precisely why step 1's initial-value comment is genuinely ambiguous by ROM
   design, not just imprecise writing: value `1` covers BOTH "never got this far" (hardware
   absent/not ready) AND "got here and the signature matched" — the code has no way to tell
   those two apart from `sysdisk_status` alone. Only `0` is unambiguous: "loaded two full
   tracks successfully, but the first byte isn't `0xF3`."

   **`0xF3` is confirmed to be PDOS's own system-disk signature, not a generic "Philips"
   convention — CONFIRMED from two independent, converging sources (2026-07-13 disk-image
   comparison + 2026-07-20 disassembly-comment corroboration):**
   - **Image comparison (2026-07-13):** two real JWSDOS images (`Spel1.dsk`, `jwssytem.dsk`)
     have `0x20` at raw offset `0x0000` instead of `0xF3` — confirmed to be JWSDOS 5.0's own
     real first opcode byte (`JR NZ`, per `jwsdos5.0.asm`'s `org 0E000h`), not a bad dump. A
     real **`.IMD` image of "Disk BASIC 24K"** (the official Philips cartridge+disk product)
     has `0xF3` as its first byte at `0xE000` instead.
   - **Disassembly corroboration (2026-07-20):** `Disk.asm`'s own `disk_constants` table names
     the RAM destination this check reads from directly: `defw 0xe000 ; Transfer adress for
     PDOS (0xE000 in bank 1)`. The original disassembler (independent of the image-comparison
     finding) already identified this exact address as **PDOS's** transfer target — not a
     generic "system disk" destination. Combined with step 1's "OR PDOS was read" comment, the
     ROM's own naming makes it explicit: `getdos` is fundamentally **PDOS's own two-track boot
     convention**, baked into the monitor ROM; JWSDOS is a compatible third-party DOS that
     reuses the same entry point rather than the convention's original owner.
   - **PDOS = "Philips DOS," a real, distinct, official DOS with its own directory system —
     NEW (owner, 2026-07-20, from external documentation research), separate from and
     unrelated to `jwsdos5.0.asm`'s directory format (§4).** The owner is still researching;
     what's confirmed so far is the name and that it loads via the same `getdos` mechanism.
     **Presumed but NOT yet confirmed:** that "Disk BASIC 24K" (the official Philips
     cartridge+disk product, already identified as the `0xF3`-signed image above) is itself a
     PDOS disk. Plausible and consistent with everything found so far, but not independently
     verified — treat as the working assumption, not a settled fact, until the owner's
     research confirms it directly. **PDOS's own directory format is completely unsourced** —
     don't assume it matches `jwsdos5.0.asm`'s `DE_*` struct (§4); that layout is JWSDOS's own,
     not necessarily shared with the official DOS it's compatible with at the boot level only.
   - Not a bug or an emulator-relevant contradiction either way — two different DOSes, one
     boot convention, only one of them (its originator) carries the signature it checks for.
   **`sysdisk_status`-gates-the-launch question — evidence now stronger, still not fully
   resolved:** `getdos` itself only sets `sysdisk_status` (never jumps into the loaded code —
   see step 8); some other, not-yet-sourced caller reads that flag afterward to decide whether
   to actually launch the loaded code. Real JWSDOS disks legitimately end this routine with
   `sysdisk_status = 0` (confirmed exact value now, not just "cleared") and clearly still work
   in practice — **and now that 0 specifically means "loaded fine, just not carrying PDOS's own
   signature" rather than any kind of failure, a hard gate on this value would make JWSDOS
   unbootable outright, which contradicts known reality.** Strengthens, but doesn't fully
   prove, that the caller treats `sysdisk_status` as informational (e.g. a "recognized system
   disk" banner distinction) rather than a hard boot gate. Still worth sourcing `getdos`'s
   caller to settle definitively.
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

1. **Does anything re-sync `SS_DS_Char`/`number_of_tracks` (§1) from an inserted disk's own
   label?** Not found in this pass. If nothing does, real JWSDOS relies entirely on the
   operator manually matching the format menu to whatever disk is inserted — worth
   double-checking before concluding the emulator's auto-detect-from-label behavior (§3) is
   purely an enhancement rather than also fixing a real usability gap.
2. **Where does side 2's own directory actually live in a raw `.dsk` image?** (§2)
   `dir_side2_prep` reads a physically different disk surface (FDC head = 1); not located in
   `Spel1.dsk`'s raw bytes in this pass — depends on the image's side-interleaving
   convention, which itself isn't confirmed. **UPDATE (2026-07-22):** the now-confirmed
   side-major/cylinder-minor raw-offset formula (§2) gives a strong candidate answer —
   `Tracks * BytesPerTrack + BytesPerTrack` (e.g. `0x29000` on a 40-track disk) for
   `dir_side2_prep`'s own target — but this is a direct arithmetic consequence of a formula
   validated only against head=0 data so far, not independently checked against real bytes at
   that offset. **UPDATE (2026-07-22, `jwsdos5.0.asm` source read directly): the "offset-24
   values don't mean what this doc assumed" half of the tangle is resolved — they do mean
   physical side, confirmed from source (§2).** Location is still open on its own terms: the
   formula's head=0 branch is confirmed, its head=1 branch (where side 2 actually sits) is not
   yet checked against real bytes.
3. **Origin of the stale 20-entry directory cluster at raw `0x1000`–`0x17FF`** (§2) — read as
   real, valid, cross-validated directory entries, but not touched by the current build's
   read/save routines and sharing no filenames with the 18-entry active directory.
   **Write SCOPE now CONFIRMED (owner, 2026-07-20, from `JWS Systeem Disk`'s disassembly):**
   the program writes a full track 1 (16 sectors) plus only 8 sectors of track 2 (sectors
   1–8) — sectors 9–16 (where the active directory lives) are not touched at all, not written,
   not cleared. This replaces the earlier "clears/rewrites the active-directory portion"
   framing with a simpler, confirmed one. **Still OPEN, but now a much stronger candidate: the
   data SOURCE for sectors 1–8.** Refined theory (owner write-scope finding + a fresh
   `jwsdos5.0.asm` re-read, both 2026-07-20, see §2): the write-scope's implied source range
   (`0xE000`–`0xF7FF`, from "full track 1 + 8 sectors of track 2") **exactly contains
   `DIR_side_1_mem` (`0xF000`–`0xF7FF`)** — the specific RAM buffer `dir_side1_prep`/
   `save_directory`/`read_directory` use for ordinary side-1 directory operations, confirmed
   never zeroed between disks. `JWS Systeem Disk`'s blind sequential copy would sweep up
   whatever side-1 directory happened to be sitting there (most plausibly from some other disk
   read shortly before), landing it on the new disk's sectors 17–24 — and because that buffer
   is structurally side-1-only by construction, this also explains the previously-unexplained
   detail that all 20 stale entries have side-byte 0. Still not a closed item — this is a
   strong synthesis of two confirmed facts, not a direct trace of the actual write instructions
   populating the buffer at `JWS Systeem Disk` runtime. **Also not resolved by the 2026-07-19
   label-writing finding (§3)** — that confirmed `JWS Systeem Disk` writes the label correctly,
   a separate question from this one. Owner's next step is still reading the rest of
   `JWS Systeem Disk`'s disassembly to confirm this specific mechanism.
   **Separately (2026-07-22, `jwsdos5.0.asm` source): the active directory's own DE_head=1
   discrepancy (§2) is NOT explained by this item's theory** — this item concerns the STALE
   cluster (confirmed side-byte=0, unaffected by the correction) sourced from a blind
   `JWS Systeem Disk` copy, whereas the active cluster's anomaly is now understood via a
   different, unrelated mechanism: ordinary `defragment` operation can reassign a file's side
   during normal DOS use (§2, §5). Keep these two explanations separate — don't conflate them.
4. **Follow-on from the now-precisely-understood `0xF3`/`sysdisk_status` branch (§6 step 7):**
   does `sysdisk_status` actually gate whether the loaded DOS launches, and if so, how does a
   real JWSDOS disk boot despite legitimately ending with `sysdisk_status = 0`? Evidence now
   points toward "informational, not a hard gate" more strongly than before (§6 step 7), but
   still needs `getdos`'s (unsourced) caller to settle definitively.
5. Load-address 3-way variation (`0x6547`/`0x67BC`/`0x7000`) across `Spel1.dsk`'s active
   directory entries (§4) — observed, unexplained.
6. RAM variable addresses beyond `disk_transfer` (`0x6070`, confirmed): `memsize`,
   `disk_status`, `sysdisk_status`, `stacktemp_disk`, `disk_track_num`, `disk_search_track` —
   nice-to-have for `.state`/debugger symbol work, not blocking.
7. **PDOS (Philips DOS) — NEW (owner, 2026-07-20), a real, distinct, official DOS with its
   own directory system, confirmed to exist by name but otherwise unsourced.** Owner is
   researching further. Open sub-questions: (a) is "Disk BASIC 24K" (the `0xF3`-signed image,
   §6 step 7) actually a PDOS disk — presumed, not confirmed; (b) PDOS's own on-disk directory
   format — completely unsourced, do not assume it matches `jwsdos5.0.asm`'s `DE_*` struct
   (§4), which is JWSDOS's own and only boot-level-compatible with PDOS, not necessarily
   directory-format-compatible; (c) whether this project needs to model PDOS as a second,
   separate DOS at all, or whether JWSDOS-only support is sufficient scope — an open scoping
   question for whoever picks this up, not just a research gap.

**Resolved since the last revision (moved out of this list):** **`sysdisk_status`'s ambiguous
initial-value comment (2026-07-20)** — explained, not just flagged: the exact `0xF3` branch
(§6 step 7) never clears it on the match path, so value `1` inherently covers two different
situations (hardware absent, or PDOS signature matched) by ROM design, not by disassembly
imprecision; **the SS/DS indicator's exact
byte offset (2026-07-20)** — `$FEF`, a single fixed ASCII `'D'`/`'S'` character, confirmed
identically in two real images, no banner-text search needed (§3); **where the on-disk geometry
label gets written (2026-07-19)** — `JWS Systeem Disk`, confirmed via its own disassembly, and
confirmed to write the correct track-count/SS-DS text for the operator's actually-selected
format geometry (§3); the SS-80/DS-40 geometry
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
- **2026-07-22 (design-doc maintainer pass, folding in `P2000.Machine` milestone-19 findings):**
  two items carried over from the FDC implementation pass. (1) **CONFIRMED**, new: the
  generalized side-major/cylinder-minor raw sector-offset formula
  (`head*Tracks*BytesPerTrack + cylinder*BytesPerTrack + (sector-1)*BytesPerSector`), derived
  from this doc's own confirmed byte ranges and validated by the machine implementation against
  four real fixtures (`Spel1.dsk`, `jws-sytem.dsk`, `empty-jws.dsk`, `hires_demo.dsk`) — gives a
  strong (not yet independently verified) candidate location for side 2's directory, §7 item 2.
  (2) **CORRECTED**, a real discrepancy, not smoothed over: this doc's §2 claim that "both
  clusters have side-byte 0" is only true for the stale cluster — the active directory's 18
  real entries were found to read side-byte **1**, not 0, during implementation's direct byte
  inspection. Flagged in §2, §4, and §7 items 2–3 as an open tension with the "this is all
  side-1 data" framing; not resolved here, needs `jwsdos5.0.asm` access to reconcile whether
  `DE_head` means what this doc assumed. → this doc §2, §4, §7.
- **2026-07-22 (design-doc maintainer pass, owner supplied `jwsdos5.0.asm` directly): the
  DE_head tension above is validated against the real source, resolved partially.** Confirmed
  from source: `dir_side1_prep`/`dir_side2_prep` set `DE_head=0`/`1` exactly as this doc had it,
  and `find_room`/`insert_dir_entry` read/write that same RAM cell when placing a file's own
  directory entry — `DE_head` genuinely means physical side throughout, ruling out the
  "used/valid flag" alternative raised in the correction above. New mechanism found that makes
  the observed anomaly plausible: `disk_defragment`'s `crunch_next_file` loop (lines ~703–743)
  deletes and re-saves every file via `write_file` (which re-runs `find_room`), explicitly
  detecting when a file's side changes as a result — proving `DE_head` is reassignable during
  ordinary DOS operation, not fixed at original save time. Does NOT fully explain why
  `Spel1.dsk` specifically shows all 18 active entries uniformly on side 2 — that depends on
  this disk's own save/defragment history, unrecoverable from static source alone. → this doc
  §2, §4, §5, §7.
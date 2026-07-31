# P2000T disk formats — device/format guide

(Renamed 2026-07-27 from `JWSDOS-format.md` — this doc grew beyond a single-DOS scope once §6a
added PDOS's own, genuinely distinct on-disk format alongside JWSDOS's; the old name had started
to misdescribe roughly a fifth of the doc's own content. See the §8 provenance entry for the
rename itself and what still needs updating in the actual repo.)

Companion to `docs/P2000T-reference.md` and `docs/MDCR-implementation.md`, same division of
labor as MDCR: **the reference doc keeps the generic µPD765 chip facts** (ports `0x8C`/`0x8D`/
`0x90`, MSR/control-latch bits, command/execute/result phases, semi-DMA, CTC ch0/ch1 roles —
reference doc §5d/§5e). **This doc keeps everything specific to how the P2000T's two real DOSes
actually use that chip and what they write to disk** — the shared boot-load sequence as literally
executed (§6), then each DOS's own on-disk layout, directory format, and allocation model:
**JWSDOS** (§1–§5 — the DOS this doc originally covered exclusively) and **PDOS** (§6a — the
official Philips DOS, a separate product sharing only the physical geometry and `getdos`
boot convention with JWSDOS, §6a's own intro). Reference this doc from the reference doc and
`CLAUDE.md` rather than duplicating its contents there, mirroring how MDCR-implementation.md is
referenced from cassette-related sections.

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

**Flag, not a correction (2026-07-23):** the official Philips "P2000 System T&M Reference
Manual" (owner-supplied, `raw-conversion.md`), Ch2.1, states disk data is recorded "using
frequency modulation" (FM) — in tension with "MFM" above and with the manual's own "double-
density" media label (conventionally implying MFM). Sector geometry (16/256, 35-track base
figure, 4-drive/560k ceiling) is independently confirmed by the same chapter and matches this
doc's figures exactly. See `P2000T-reference.md` §5d for the full discrepancy write-up and the
open action item (check the confirmed READ/WRITE DATA opcode's MF bit) — not re-litigated here
since this doc's own geometry figures are unaffected either way.

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

**FINAL CORRECTED PICTURE (2026-07-31, CC audit, driven by direct execution against real
`Upd765`/`DskImage` code and real `Spel1.dsk` bytes — not disassembly arithmetic).** Everything
below in this section describes how this understanding got here, including a real prior
mislabeling; this block is the settled, current state — cross-reference it first.

```
0x0000–0x0FBE  cyl 0, head 0 — Side-1 DOS boot code, track "1"           (unchanged, invariant
                                                                          under both formulas)
0x0FBF–0x0FFF  cyl 0, head 0 — Geometry/system label (§3)                (unchanged)
0x1000–0x17FF  cyl 0, head 1, sectors 1–8  — DUPLICATE content, byte-for-byte match of
               0x2800–0x2FFF (side 1's real directory) — see "duplicate content" below.
0x1800–0x1FFF  cyl 0, head 1, sectors 9–16 — DUPLICATE content, 2047/2048-byte match of
               0x3000–0x37FF (side 2's real directory, one differing transfer-address byte).
0x2000–0x27FF  cyl 1, head 0, sectors 1–8  — real DOS code: `getdos`'s own confirmed second
               boot-track read target, AND `JWS Systeem Disk`'s own confirmed track-2/
               sectors-1–8 write target (independently converging on the same block).
0x2800–0x2FFF  cyl 1, head 0, sectors 9–16 — **side 1's REAL, genuine directory**
               (`dir_side1_prep`'s actual target, confirmed via direct FDC replay — NOT a
               "stale cluster," see below). Spel1.dsk: 20 real entries ("Fraxxon + scores"…
               "Superlaser"), every one `DE_head=0`, self-consistent.
0x3000–0x37FF  cyl 1, head 1, sectors 1–8  — **side 2's REAL, genuine directory**
               (`dir_side2_prep`'s actual target, confirmed via direct FDC replay). Spel1.dsk:
               18 real entries ("Tralieenspel"…"BABA"), every one `DE_head=1`.
0x3800–0x3FFF  cyl 1, head 1, sectors 9–16 — genuinely blank (all-zero); only 8 of 16 sectors
               of this region are ever written by anything.
```

**The old "stale 20-entry cluster" theory (below, kept as historical record) was a real
mislabeling, not stale data at all.** Zero filename overlap between the two clusters was
correctly observed but wrongly read as evidence of contamination from another disk. It's
exactly what a working double-sided disk's two INDEPENDENT per-side catalogs look like: 20
files on side 1, 18 on side 2, 38 total — close to the owner's own independently-reported "37
files" in `Spel1.dsk`'s real menu (off by one, not chased further, doesn't change the
conclusion). Both real per-side directories, at their confirmed real locations
(`0x2800`/`0x3000`), fully explain the data; no "stale leftover from a different disk" theory is
needed at all, and the elaborate `JWS Systeem Disk`/`DIR_side_1_mem`-RAM-dump theory built below
to explain a "stale cluster" was solving a problem that didn't exist. What DOES still need
explaining (open, not chased further by the owner's own choice): why the `0x1000`–`0x1FFF`
region — cylinder 0's own flip side — holds a near-exact DUPLICATE of both real directories
concatenated (differing by exactly one byte, a transfer-address field, consistent with a stale
RAM snapshot at some write moment, not a new anomaly). The `JWS Systeem Disk` write-scope claim
(full track 1 + only 8 sectors of track 2) is independently CONFIRMED correct from that
program's own disassembly and direct replay — but it describes the `0x2000`–`0x27FF` DOS-code
region, not this directory-duplicate region; see §7 items 2–4 for the full resolution.

**FIXED (2026-07-31) — was a real, confirmed bug: the Floppy Drives window only ever showed one
side's files, on any double-sided JWSDOS disk, and it wasn't reliably even the right side.**
`DskImage.ReadDirectory()`/`EnumerateDirectorySlots()` used to read from exactly one fixed offset,
`DirectoryOffset = 0x1800` — never either real directory location; it was the duplicate-content
region, which only happened to closely match side 2's real content on `Spel1.dsk` specifically.
**Fixed:** directory reads now compute each side's raw offset via the same CHS formula every
other sector read uses (`SectorOffset(cylinder: 1, head, sector: head==0?9:1)`), replacing
`DirectoryOffset` entirely — `0x1800` is no longer read for directory purposes at all. Verified
safe across every real double-sided fixture in the project before landing, not just `Spel1.dsk` —
and it turned out NOT to be a coincidental wash: **three of four real double-sided fixtures
(`jws-sytem.dsk`, `empty-jws.dsk`, `hires_demo.dsk`) had genuine directory content the old
`0x1800` offset was silently missing entirely**, previously reading as having no real directory at
all. `jws-sytem.dsk`'s newly-surfaced 14 real side-1 entries are exactly what a JWSDOS system/
utility disk should list ("JWS Systeem Disk," "Format," "AUTORUN," "Disk-report 2.1," and other
real utility filenames). Single-sided images confirmed a genuine no-op both mathematically (the
formula for cylinder 1/head 0/sector 9 collapses to exactly the old `0x1800` value when
`Sides == 1`) and by full existing test-suite runs on `volorg.dsk`/`diskbasic_1.6uk.dsk`.
`Spel1.dsk`'s `AUTORUN` entry's `TransferAddress` changes from `0x7000` (the `0x1800` duplicate)
to the correct `0x6547` (the real `0x3000` location) — the exact one byte already identified as
differing between the duplicate and the real content, now confirmed to matter for exactly the
field predicted. See §7 item 2a for the closure write-up.

**CONFIRMED, byte-verified against `Spel1.dsk`, cross-checked against `jwsdos5.0.asm`'s
`dir_side1_prep`/`dir_side2_prep` routines — kept as the historical on-disk-layout finding this
section originally recorded, now superseded by the corrected picture above:**

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

**Both raw-offset attributions in the table immediately above turned out to be wrong (not the
underlying `dir_side1_prep`/`dir_side2_prep` facts, just which raw region each one targets) —
2026-07-31, direct FDC replay against real `Spel1.dsk`: `dir_side1_prep`'s real target is raw
`0x2800`-`0x2FFF`, not `0x1800`; `dir_side2_prep`'s real target is raw `0x3000`-`0x37FF`, not
somewhere unlocated. See the corrected picture at the top of this section.**

**CORRECTED (2026-07-30) — the formula below (side-major/cylinder-minor) was WRONG, and this
doc's own 2026-07-22 "CONFIRMED" validation of it was a real, instructive false start: worth
recording plainly rather than quietly overwriting.** The 2026-07-22 validation checked the formula
by searching for known real directory filenames (`Spel1.dsk`'s "Fraxxon"/"Tralieen") at the raw
offsets the formula itself predicted, then declared a match — **circular**: it only proved the
formula was self-consistent with data that had itself been interpreted through that same formula,
not that the formula matched genuine independent ground truth. The circularity was broken the same
day (2026-07-30) by comparing raw disk bytes against a clean, known-good JWSDOS binary reference
(`assets/JWS.bin`, not derived from this project's own fixtures or formula) — that comparison
placed `getdos`'s real second track at raw `0x2000`, not `0x1000`, and the owner's own direct
authority on the monitor ROM (`getdos` loads exactly two PHYSICAL CYLINDERS, both head 0 — the ROM
has no double-sided support at all) sealed cylinder-major/head-minor as correct. **Fixed:**
`DskImage.SectorOffset` and `ImdFormat`'s independently-duplicated copy of the old formula (machine
CLAUDE.md, 2026-07-30 entry). **Confirmed via an actual observed boot: "JWS Dos boots perfectly
now."** See §7 item 9 for the full bug-closure narrative.

**Generalized raw sector-offset formula — CORRECTED (2026-07-30, `P2000.Machine`
`DskImage.SectorOffset` fix), superseding the 2026-07-22 entry below:**

```
raw_offset = cylinder * Sides * BytesPerTrack
           + head * BytesPerTrack
           + (sector - 1) * BytesPerSector
```

**Cylinder-major, head-minor** — a cylinder's two heads are stored back-to-back (all of cylinder
0 before cylinder 1 begins), not all of head 0's cylinders before head 1 starts anywhere.
Single-sided images are unaffected (with `Sides == 1` the head term is always 0, so the two
formulas are numerically identical) — this only changes behavior, and only mattered, for
double-sided images. **Direct consequence for this section's own confirmed byte ranges above: the
raw offsets themselves (`0x0000`, `0x1000`, `0x1800`, `0x2000`, etc.) are unchanged — the same real
bytes are still exactly where they were found — but the CHS (cylinder/head/sector) label attached
to each one changes.** In particular, raw `0x1000`–`0x1FFF` (the "stale 20-entry cluster" and the
"active 18-entry directory," both described just above as "Track '2'," i.e. cylinder 1) is now
understood to be **cylinder 0, head 1** — the flip side of the very same boot track, not a
different cylinder at all. This directly resolves the DE_head tension flagged below: the active
directory's own entries reading `DE_head=1` now matches the corrected physical location cleanly,
rather than contradicting it. **Which specific routine's read command (`dir_side1_prep` vs.
`dir_side2_prep`) actually computes to this exact physical spot is a separate question CC's own fix
entry explicitly left open, not re-verified as part of the geometry fix** — treat the "why does
`dir_side1_prep`'s own head=0 FDC parameter end up here" mechanics as still unresolved (see item 2
below), while treating the raw-offset↔real-file-content mapping itself (§2's byte dump) as
unaffected and correct.

**Open item 2 below (where does side 2's directory live?) — ANSWERED, 2026-07-31, CC audit via
direct FDC replay against real `Spel1.dsk`: raw `0x3000`-`0x37FF` (cylinder 1, head 1), 18 real
entries, every one `DE_head=1`.** See the "FINAL CORRECTED PICTURE" block at the top of this
section for the full layout and §7 item 2 for the closure write-up.

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

**CORRECTED, substantially simplifying the picture (2026-07-30, `DskImage.SectorOffset` geometry
fix — see §2's own correction, cylinder-major/head-minor replacing side-major/cylinder-minor).**
The "open tension" this whole sub-thread was built on — active-directory entries reading
`DE_head=1` while sitting at a raw offset the doc assumed was head-0 territory — is now understood
differently at its root: raw `0x1800` (and the whole `0x1000`–`0x1FFF` range) is, under the
corrected formula, **physically head 1 of cylinder 0**, not head 0 of cylinder 1 as previously
assumed. A `DE_head=1` reading there is therefore no longer an anomaly needing the
defragment-reassignment theory to explain its mere EXISTENCE — it's simply, straightforwardly
correct: that data really is on physical head 1. The defragment mechanism above remains a real,
sourced fact and still plausibly explains why the reading is UNIFORM across all 18 entries (a
disk's operational history could still leave every entry pointing the same way), but it's no
longer load-bearing for the more basic question of why `DE_head=1` shows up there at all.
**ANSWERED (2026-07-31, CC audit, direct FDC replay against real `Spel1.dsk` — not more
disassembly arithmetic):** `dir_side1_prep`'s real target is raw `0x2800`-`0x2FFF` (cylinder 1,
head 0) — the naive "sector 25 → cylinder 1, head 0" division DOES hold, it just lands at
`0x2800`, not `0x1800` as this doc mistakenly assumed while first applying the corrected formula
the day before. `dir_side2_prep`'s real target is raw `0x3000`-`0x37FF` (cylinder 1, head 1). So
raw `0x1800` — where the doc's original byte search actually found the real, currently-active
directory data — is neither routine's real target; it's the still-unexplained "duplicate
content" region (cylinder 0's own flip side), which happens to closely match `dir_side2_prep`'s
real output. **This also means the "stale cluster" at raw `0x1000`-`0x17FF` was never stale at
all** — see the retirement note below and the "FINAL CORRECTED PICTURE" block near the top of
this section for the complete, current understanding.

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

**RETIRED (2026-07-31, CC audit) — the entire "stale 20-entry cluster" investigation above,
including the `JWS Systeem Disk`/`DIR_side_1_mem`-RAM-dump synthesis, was solving a problem that
didn't exist.** Direct FDC replay against real `Spel1.dsk` (not more disassembly arithmetic)
found: the "stale cluster" IS `dir_side1_prep`'s own real, genuine target — it just lives at raw
`0x2800`-`0x2FFF`, not `0x1000`-`0x17FF` as every paragraph above assumed (an artifact of the
same side-major/cylinder-minor formula bug the JWSDOS-boot fix corrected). Its 20 entries, every
one `DE_head=0`, are exactly what side 1's real, currently-active directory looks like — not
stale leftovers from another disk. Symmetrically, `dir_side2_prep`'s real target (raw
`0x3000`-`0x37FF`, 18 entries, every one `DE_head=1`) is side 2's real directory, first located
this pass. **The zero-filename-overlap observation that drove this whole theory was real and
correctly noted — it just means "two independent per-side catalogs," which is exactly what a
working double-sided disk has, not evidence of anything stale or contaminated.** The `JWS Systeem
Disk` write-scope claim above (full track 1 + 8 sectors of track 2) is independently confirmed
correct — it just describes the real DOS-code region at `0x2000`-`0x27FF`, not either directory,
and was never actually the source of directory content at all. What DOES remain genuinely open:
why `0x1000`-`0x1FFF` (cylinder 0's own flip side) holds a near-duplicate of both real
directories concatenated — see the "FINAL CORRECTED PICTURE" block near the top of this section
and §7 item 3 (now rewritten) for the current, accurate state.

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

**RESOLVED (owner + CC, 2026-07-27) — this label is a JWSDOS convention, not a P2000 disk-image
convention, and an emulator's auto-detect must validate it rather than trust it blind.**
Triggered by real testing: the `Basic24k` boot floppy is **PDOS**, a different disk OS entirely
— it has nothing at `$FEF`/`$FFF` at all, so an auto-detect reading those offsets on a PDOS (or
any other non-JWSDOS, or plain data-only) image is just reading whatever sector bytes happen to
occupy that space, not a real label. Compounding this, a raw `.dsk` sector dump carries zero
container metadata of its own, so **file length alone is sometimes genuinely ambiguous** — a
40-track/double-sided image and an 80-track/single-sided image are both exactly 327,680 bytes;
nothing about the bytes themselves can disambiguate that. **The fix, now built (`P2000.Machine`
milestone 20d; reference doc §5d's label-validation RESOLVED block): only trust a read label if
the byte length it implies equals the file's actual length exactly** — otherwise treat the file
as unlabeled and fall back to the drive's configured Capacity/Sides (surfacing a user-facing
mismatch dialog, `P2000.UI` milestone 14e, if that doesn't match either). This single length
check is what makes it safe to read these offsets blind on any file, JWSDOS or not — random
sector bytes forming a combination that ALSO happens to byte-length-match the file is
vanishingly unlikely. **Nothing in this doc's own byte-offset table above needed correcting** —
the label itself, on a genuine JWSDOS disk, is exactly as documented; this note is about not
assuming every `.dsk` file IS a JWSDOS disk just because it CAN be read at these offsets.

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
below is what the DOS uses to search for and match it. **Concrete real cross-check of the
16-sectors/track linear formula (Claude Code, machine/UI milestone 22/15, 2026-07-28):** this
entry's `DE_start_sector`/`DE_end_sector` = 622/632, which the formula maps to track 39 sector
14 through track 40 sector 8 — confirmed rendering correctly in the Disk Drives window's new
Track/Sector column as `T39 S14-T40 S8`, not just verified abstractly.

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
     **CONFIRMED (owner, 2026-07-28, real end-to-end test) — no longer just presumed.** The owner
     booted a real `Basic24k.bin` cartridge + boot floppy and it came up as "Philips Disk BASIC,
     release 1.6 UK" — i.e. the `0xF3`-signed "Disk BASIC 24K" image genuinely is a PDOS disk,
     directly observed, not inferred. **PDOS's own directory format**, once "completely
     unsourced" here, is now the fully-detailed FCB scheme in §6a (track 1, 128 entries, 32-byte
     layout) — sourced from official Dutch-language documentation plus real `volorg.dsk`
     byte-inspection, and does NOT match `jwsdos5.0.asm`'s `DE_*` struct (§4); confirmed as two
     genuinely distinct directory schemes sharing only the physical boot convention, exactly as
     this note originally cautioned.
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

## 6a. PDOS's own disk format — FCB structure, record allocation, and a hard geometry ceiling
(owner-sourced, 2026-07-27, official Dutch-language 24K Disk BASIC documentation) — this
substantially RESOLVES §7 item 7(b) below ("PDOS's own on-disk directory format — completely
unsourced"). Everything in this section is **PDOS's own scheme, confirmed distinct from
JWSDOS's** (§4/§5 above) — same physical sector geometry (16 sectors/track, 256 B/sector) and
the same `getdos` two-track boot convention (§6), but a completely different directory/
allocation model. JWSDOS didn't just clone PDOS at the boot level and stop there — per the
owner's own framing, **JWSDOS is a genuine improvement over PDOS in capability** (wider
geometry support, below), while still being "clumsy in some aspects" (the owner's own
characterization — plausibly pointing at already-documented JWSDOS quirks like the stale
directory cluster left over from `JWS Systeem Disk`'s blind sequential copy, §7 item 3, or the
somewhat ad-hoc write-scope behavior found there; not asserting anything beyond what's already
on record unless the owner wants to specify further).

- **Allocation unit — the "record":** 4 sectors = 1024 bytes (a cluster, in modern terms).
  Capacity: 35 tracks × 4 KB/track = 140 KB; 40 tracks × 4 KB/track = 160 KB — matches figures
  already established elsewhere in this doc/reference doc.
- **Directory — the FCB ("File Control Block"), 32 bytes, one per file:** lives on **track 1
  of a "werkschijf" (data/working disk) ONLY — explicitly NOT the system disk**, whose track 1
  serves the boot-load convention instead (§6) — **RESOLVED (owner, 2026-07-28, official
  Dutch-language 24K Disk BASIC documentation, direct quote): "Het eerste spoor van werkschijven
  (niet van de systeemschijf) bevat de index. Hierin staan de FCB's... van de programma's en
  bestanden"** ("The first track of working disks (not the system disk) contains the index. This
  contains the FCBs... of the programs and files") — upgrades this from inference (previously
  reasoned only from allocation maps never referencing records `00`–`03`) to a directly-quoted
  source fact. **Capacity — RESOLVED (owner, 2026-07-28): 128 entries.** One track is 4096 bytes
  (16 sectors × 256 B); 4096 ÷ 32 B/FCB = 128 slots exactly, filling the whole track with no
  spare region left over for anything else — a clean fit, not a partial-track figure. A directory
  reader can therefore just iterate all 128 fixed slots rather than needing a separate
  end-of-list terminator convention; an unused slot is presumed all-zero (matching the "unused
  allocation-map entry" convention already confirmed below), though no real disk with unused
  slots has been byte-inspected yet to confirm this directly. Layout (1-based byte positions, as
  the source document states them):
  - **Position 1 — RESOLVED (owner, 2026-07-28): a continuation-sequence index, not a flag.**
    `0x00` marks a file's primary (or only) FCB. If a file needs more allocation-map room than
    one FCB's 16 records (16 KB) can describe, additional FCBs are appended to the index with the
    **same filename and extension** and this byte incrementing — `0x01` for the second FCB,
    `0x02` for the third, and so on. Direct owner-stated fact; supersedes the earlier
    "unconfirmed/unlabeled" framing below.
    **Flagged, not sourced (Claude Code, machine milestone 22a, 2026-07-28): how a reader should
    COMBINE multiple FCBs for one file is currently an unconfirmed assumption, not a documented
    fact.** `DskImage.ReadPdosDirectory()` sums each contributing FCB's sector-count byte for a
    combined file length and concatenates their allocation maps in ascending position-1 order —
    a reasonable implementation guess, but no real multi-FCB disk has been found yet to confirm
    or correct it; only exercised by a synthetic test so far.
    **Reconciling with the real-disk `0xF3` finding (still open, but now narrower):** a genuine
    continuation index only ever needs to reach small integers in practice — a file would need
    to exceed 16 KB roughly 243 times over (several megabytes) to legitimately reach `0xF3`/243
    this way, essentially impossible on a 140–160 KB disk. So `0xF3` on `VOLORG.BAS`'s FCB (real-
    disk confirmation below) can't be a continuation index under this scheme — it's still most
    plausibly the owner's separately-floated hypothesis, a distinct system/protected-file flag
    value chosen precisely because normal continuation counting could never reach it, not a
    competing interpretation of the same field. **Not fully confirmed** (still one disk, one file
    pair), but no longer in tension with the continuation-index fact — both can be true of the
    same byte position at different value ranges.
    **Detection-collision consequence, flagged for the disk-directory-view feature (§14 milestone
    TBD, not yet resolved as a design decision):** a system disk's track 1 offset 0 is also
    `0xF3` (§6, the boot signature). Since a working disk's *first* FCB slot could legitimately
    carry an `0xF3` flag value too (if that file happens to occupy slot 1), a bare "byte 0 at
    track 1 == `0xF3`" test cannot safely distinguish "no directory here, this is a system disk"
    from "there is a directory, and its first entry happens to be flagged." Disambiguating needs
    at least one more check — e.g. whether positions 2–9 look like a plausible padded filename
    (printable ASCII/space) and position 16 plus the allocation map look like plausible
    sector/record data — before falling back to "system disk, no directory."
    **Planned owner test (2026-07-28, not yet run):** a concise `VOLORG` manual surfaced a
    "set/reset file protect" menu option — the owner intends to save a file, byte-inspect its
    FCB, toggle protect, and byte-inspect again, which would directly test the "position 1 =
    protected-file flag" hypothesis (rather than just the one-disk `VOLORG`/`VOLINFO` coincidence
    above) by watching which byte(s) actually flip when protection is toggled on a file the owner
    controls. Results pending — will fold in as CONFIRMED/CORRECTED once run.
  - Positions 2–9 (8 bytes): filename, space-padded if shorter than 8 characters (e.g.
    `"MONITORE"`).
  - **Positions 10–12 (3 bytes): extension** (e.g. `"BAS"` in the worked example) — refined from
    "10–15 unlabeled" now that the source document's worked example has been checked byte-by-byte
    against its own stated 1-based position numbering.
  - Positions 13–15 (3 bytes): unlabeled/reserved — `0x00 0x00 0x00` in every worked/confirmed
    example so far (the docx example and both real `volorg.dsk` entries); purpose unconfirmed.
  - **Position 16: sector count** — the file's real length in sectors (e.g. `0x1B` = 27 →
    file is under 27 × 256 = 6912 bytes).
  - **Positions 17–32 (16 bytes) — the Disk Allocation Map:** one byte per **record** number
    used by this file, in order. Since 16 bytes can address at most 16 records × 1 KB = 16 KB,
    **a file over 16 KB gets a second FCB/index entry** (a continuation record).
  - **Worked example, confirms the scheme exactly:** sector count `0x1B` (27) with allocation
    map `[0E, 0F, 10, 11, 12, 13, 14, 00, 00, ...]` — 7 real records (`0x0E`–`0x14`) × 4
    sectors/record = 28 sectors, one more than the 27 actually needed (the last sector of the
    7th record goes unused) — matches the source document's own arithmetic precisely.
  - **CONFIRMED against a real disk (owner-supplied `volorg.dsk`, 143,360 B, SS-35 — a real
    "werkschijf" carrying the `VOLORG`/`VOLINFO` disk-utility programs), independent of the
    source document's own single worked example:** both real FCB entries byte-inspected directly.
    Entry 1 (`VOLORG`, sector count `0x2C`=44) has allocation map `[04,05,06,07,0C,0D,0E,0F,
    10,11,12,00,00,00,00,00]` — 11 records × 4 = 44 sectors, an *exact* fit with zero slack (a
    new case the docx's own example didn't cover). Entry 2 (`VOLINFO`, sector count `0x0E`=14)
    has allocation map `[08,09,0A,0B,00,...]` — 4 records × 4 = 16 sectors, 2 unused, the same
    "slack" shape as the docx worked example. Neither entry's allocation map ever references
    records `00`–`03` — directly confirming, on real data, the "record `0` is always part of
    track 1's own index area" reasoning below (this is also the same point the owner raised
    independently: those four codes are reserved and never spent on file data).
  - **INFERRED → now CONFIRMED on real data:** trailing `0x00` entries in the allocation map past
    the real records are unused padding, not a literal "record 0" reference — both real FCB
    entries above end in `0x00` padding and never once encode an actual `00`–`03` record, matching
    the reasoning that record `0` is permanently reserved for track 1's own index/FCB area and so
    could never legitimately appear as a regular file's data slot.
  - **`CLOSE` is what commits an updated FCB back to the index** — forgetting it in BASIC risks
    the file being partially or entirely unfindable later. A real reliability/usage fact, not an
    emulator behavior question (nothing here currently models file-level DOS semantics).
- **Real physical interleave — CONFIRMED against real disk content (not just sourced from the
  docx table), and directly relevant to the IMD sector-order-map work (machine milestone 21):**
  each track's 16 sectors are grouped into its 4 records in a fixed, non-sequential physical
  order — originally sourced from the source document's own worked table (its record-to-sector
  breakdown is identical for every track, not just the one illustrated): record position 1 =
  physical sectors **{1, 7, 13, 3}** (read in that order, 0-based: `00h, 06h, 0Ch, 02h`);
  position 2 = **{9, 15, 5, 11}** (`08h, 0Eh, 04h, 0Ah`); position 3 = **{2, 8, 14, 4}**
  (`01h, 07h, 0Dh, 03h`); position 4 = **{10, 16, 6, 12}** (`09h, 0Fh, 05h, 0Bh`).
  **Independently re-derived from `volorg.dsk`'s raw bytes, not just transcribed from the
  source table:** `VOLINFO.BAS` (track 3, records 8–11) is a plain-text BASIC help screen — a
  12-item menu, "0 load and run a BASIC program" through "E exit to BASIC command mode." Read
  the track's 16 sectors in plain physical order (1, 2, 3, … 16), the menu items come out
  scrambled — item `E`'s text isn't even findable as a contiguous string, and several sector
  boundaries land mid-word with a chunk of text simply missing (e.g. "BASIC-fil" is directly
  followed by "utes", with "e attrib" — part of "file attributes" — silently absent, because
  that piece of the sentence was physically stored in a different sector position entirely).
  Re-order the *same* 16 sectors per the pattern above — sectors `{1,7,13,3}` first, then
  `{9,15,5,11}`, then `{2,8,14,4}`, then `{10,16,6,12}` — and the menu reads perfectly, items
  `0` through `E` in exact ascending order with no gaps (byte offsets 459, 513, 576, 611, 670,
  727, 770, 809, 866, 901, 940, 1007 in the reassembled stream — cleanly monotonic). This isn't
  a plausible-sounding pattern anymore; it's the only sector ordering that reconstructs
  coherent file content from this real disk. Not currently modeled or produced by this emulator
  (machine milestone 21's IMD writer only emits a plain sequential order for new data,
  explicitly deferring real interleave), but now a confirmed, independently-verified candidate
  for whenever a future milestone models authentic PDOS-formatted disk creation. Not scoped or
  being built now — recorded here so it doesn't need re-deriving later.
- **Track ↔ record-number mapping — a simple formula, not a table worth transcribing in full:**
  track *N*'s four records are numbered `(N-1)×4` through `(N-1)×4 + 3` (hex). Track 1 → records
  `00`–`03`; track 2 → `04`–`07`; …; track 40 → `9C`–`9F`. Total for 40 tracks: records `00`–`9F`
  (160 records, matching 160 KB ÷ 1 KB/record exactly).
- **Hard geometry ceiling — CONFIRMED, and it EXPLAINS the already-known "35 or 40 tracks, no
  80, single-sided only" limit rather than just restating it:** the Disk Allocation Map stores
  each record number as a **single byte** — 0–255, 256 records max. 80 tracks × 4 records/track
  = 320 records, already over budget at 80 tracks regardless of sides; any double-sided variant
  (35 or 40 tracks × 2 sides × 4 records/track = 280 or 320 records) is also over budget. Only
  single-sided 35-track (140 records, `00`–`8B`) or single-sided 40-track (160 records, `00`–
  `9F`) fit within a single byte's range. **This is a hard architectural ceiling in PDOS's own
  addressing scheme, not a preference or a documentation gap** — independently corroborated by
  the source document's own text (disks used with 24K Disk BASIC "have 35 or 40 tracks," no
  80-track figure ever mentioned) and by the owner's own further research into secondary
  sources. **Not in tension with anything already documented about JWSDOS or this emulator**:
  JWSDOS's own allocation model (§5) uses no "record" concept at all and is confirmed to support
  the wider 35/40/80-track, SS/DS range (§1) — a different, more capable DOS-level scheme on
  the same physical hardware, which itself mechanically supports up to 80-track/double-sided
  (confirmed via the M2200/Philips manual research, reference doc §5d) regardless of which DOS
  a given disk happens to be formatted for. Three independent facts, no contradiction: hardware
  ceiling (mechanical) ≥ JWSDOS's ceiling (software) > PDOS's ceiling (software, this section).
- **Applies to:** reference doc §5d (disk geometry / FDC), machine milestone 21 (IMD sector-order
  map — the interleave pattern above is the concrete candidate data for whenever that's picked
  up), this doc §7 item 7 (resolved below).

---

## 7. Open items

1. **Does anything re-sync `SS_DS_Char`/`number_of_tracks` (§1) from an inserted disk's own
   label?** Not found in this pass. If nothing does, real JWSDOS relies entirely on the
   operator manually matching the format menu to whatever disk is inserted — worth
   double-checking before concluding the emulator's auto-detect-from-label behavior (§3) is
   purely an enhancement rather than also fixing a real usability gap.
2. **Where does side 2's own directory actually live in a raw `.dsk` image?** **RESOLVED
   (2026-07-31, CC audit, direct FDC replay against real `Spel1.dsk` — not disassembly
   arithmetic): raw `0x3000`-`0x37FF` (cylinder 1, head 1)** — `dir_side2_prep`'s real,
   confirmed target: 18 real entries, every one `DE_head=1`, self-consistent. This supersedes
   every earlier "strong candidate"/arithmetic-only answer this item carried through
   2026-07-22 and 2026-07-30. **Flagged (Claude Code, UI milestone 15, 2026-07-28) — still
   relevant, now sharper:** no known real fixture has a directory with entries on BOTH sides in
   the SAME on-disk image at the SAME time — `Spel1.dsk` has 20 real side-1 files at `0x2800`
   and 18 real side-2 files at `0x3000`, genuinely on both sides. **RESOLVED as of the item 2a
   fix below:** now exercised end-to-end — `Spel1.dsk` correctly returns all 38 entries split
   across both sides.
2a. **FIXED (2026-07-31) — was a real, confirmed bug: `DskImage.ReadDirectory()`/
   `EnumerateDirectorySlots()` used to read only raw `0x1800`, which was neither real directory
   location** — it was the "duplicate content" region (cylinder 0's own flip side), which only
   happened to closely match side 2's real content on `Spel1.dsk` specifically. **Fixed:**
   directory reads now compute both sides' raw offsets via the same CHS formula every other
   sector read already uses (`SectorOffset(cylinder: 1, head, sector: head==0?9:1)`), entirely
   replacing the old `DirectoryOffset` constant — `0x1800` is no longer read for directory
   purposes at all, resolving the open question this item originally flagged (whether to keep
   `0x1800` alongside a new read, or move to the confirmed real locations — the fix moved fully
   to the real locations, per its own explicit instruction). **This was not a coincidental wash:
   three of four real double-sided fixtures in the project (`jws-sytem.dsk`, `empty-jws.dsk`,
   `hires_demo.dsk`) had genuine directory content the old `0x1800` read was silently missing
   entirely** — all three previously read as having no real directory at all; `jws-sytem.dsk`
   alone has 14 real, well-formed side-1 entries (a genuine JWSDOS utility disk's file list:
   "JWS Systeem Disk," "Format," "AUTORUN," and others), now correctly surfaced.
   `empty-jws.dsk` — despite sharing `jws-sytem.dsk`'s identical boot code/label and its first
   two side-1 entries — is confirmed NOT a byte-identical copy overall; it genuinely has only 2
   entries, checked directly rather than assumed. Single-sided images (`volorg.dsk`,
   `diskbasic_1.6uk.dsk`) confirmed a genuine no-op, both mathematically (the formula collapses
   to the old `0x1800` value exactly when `Sides == 1`) and via unchanged full test-suite runs.
   `Spel1.dsk`'s `AUTORUN` entry's `TransferAddress` correctly changes from `0x7000` (the
   `0x1800` duplicate) to `0x6547` (the real `0x3000` location) — the one byte already identified
   as differing between the duplicate and the real content, confirmed to matter for exactly the
   field predicted. Presentation order (side 1 then side 2) is a UI-layer choice, left open for
   reconsideration. Full `P2000.Machine.Tests`: 605/605 green (was 603).
3. **RESOLVED (2026-07-31, CC audit) — the "stale 20-entry directory cluster at raw
   `0x1000`-`0x17FF`" was never stale at all — it was a real mislabeling of `dir_side1_prep`'s
   own genuine target, which actually lives at raw `0x2800`-`0x2FFF`.** The zero-filename-overlap
   observation that drove this whole investigation (below, kept as historical record) was real
   and correctly noted — it's simply what two independent, healthy per-side catalogs look like
   (20 files on side 1, 18 on side 2), not evidence of contamination from another disk. The `JWS
   Systeem Disk` write-scope claim (full track 1 + only 8 sectors of track 2) is independently
   **CONFIRMED CORRECT** from that program's own disassembly (`docs/jwssysdisk.asm`, owner-
   supplied) and direct replay against a real `DskImage` — but its raw-offset target corrected
   from `0x1000`-`0x17FF` to **`0x2000`-`0x27FF`** (cylinder 1, head 0 — the real DOS-code
   region, matching `getdos`'s own confirmed second boot-track-read target exactly, an
   independent convergence between the reader and the writer). This program was never the
   source of any directory content — the elaborate `DIR_side_1_mem`-RAM-dump synthesis built to
   explain a "stale cluster" (kept below as historical record) was solving a problem that turned
   out not to exist. **What DOES remain genuinely open, not resolved by this pass, deliberately
   not chased further (owner's own call):** why raw `0x1000`-`0x1FFF` (cylinder 0's own flip
   side) holds a near-exact duplicate of BOTH real directories concatenated — its first half
   (`0x1000`-`0x17FF`) matches side 1's real directory (`0x2800`-`0x2FFF`) byte-for-byte; its
   second half (`0x1800`-`0x1FFF`) matches side 2's real directory (`0x3000`-`0x37FF`) except one
   differing byte (a transfer-address field, offset 22 of one entry — consistent with the
   existing "stale RAM snapshot at some write moment" theory, not a new anomaly). This is now
   the sharpest open thread on this whole topic.
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
7. **PDOS (Philips DOS) — sub-item (b) now RESOLVED (owner, 2026-07-27, official Dutch-language
   24K Disk BASIC documentation) — see new §6a for the full FCB/record/geometry-ceiling
   writeup.** Remaining open sub-questions, narrowed: (a) is "Disk BASIC 24K" (the
   `0xF3`-signed image, §6 step 7) actually a PDOS disk — still presumed, not independently
   confirmed, though now a far more plausible presumption given §6a's own source is
   specifically 24K Disk BASIC's documentation; (c) whether this project needs to model PDOS
   as a second, separate DOS at all (directory browsing, write support, etc.), or whether
   JWSDOS-only support is sufficient scope — still an open scoping question for whoever picks
   this up, now unblocked by a real source if the answer turns out to be "yes."

8. ~~New (2026-07-28), raised by the disk-directory-view UI feature: how should format detection
   disambiguate a genuine PDOS system disk from a PDOS working disk whose first FCB slot happens
   to carry the `0xF3` flag value?~~ **RESOLVED (owner, 2026-07-28), IMPLEMENTED (machine
   milestone 22a, 2026-07-28).** Both read identically at "track 1 offset 0 == `0xF3`," so byte 0
   alone can't decide it. Validate the rest of the entry before concluding "system disk": only
   fall back to "no directory, this is a system disk" if that validation fails; if it passes,
   treat track 1 as a working disk's FCB directory (even though its first entry happens to carry
   the flag value) — see §6a's "detection-collision consequence" note for the exact validation
   this implies (plausible padded filename, plausible sector count/allocation map). **Confirmed
   against real data:** `volorg.dsk`'s `VOLORG` FCB (byte 0 = `0xF3`) validates as plausible and
   correctly returns `PdosWorking`; the real "Disk BASIC 24K" system-disk fixture correctly
   returns `PdosSystem`. **The "sane sector count" check, not previously specified this
   precisely:** `ceil(sectorCount / 4) == recordCount` (the real record count from the entry's
   own allocation map) — holds exactly across all three known real/worked FCB examples (the
   docx's 27/7, `VOLORG`'s 44/11, `VOLINFO`'s 14/4). **(A second sub-question — whether the UI's
   PDOS track/sector column should show raw record numbers or convert through the physical
   interleave table — is also RESOLVED, owner, 2026-07-28, IMPLEMENTED (UI milestone 15a):**
   neither: derive a track-only range from **1-based track = `(first/last record ÷ 4) + 1`** —
   **note the `+1`, corrected 2026-07-28 (machine milestone 22a's own findings-log entry): the
   shorthand "record ÷ 4" recorded earlier under-specified this** and would be off by one (e.g.
   record 8 is track 3, not track 2 — confirmed against real `volorg.dsk` data and this doc's own
   §6a interleave finding). Sector count is shown via the existing size column (sector count ×
   256 bytes), not a separate raw figure; no interleave-table conversion needed for display. See
   `P2000.UI` CLAUDE.md milestone 15a.)** **(A third sub-question, raised by the fallback view's
   own implementation — RESOLVED, owner, 2026-07-28, IMPLEMENTED (machine milestone 23, UI
   milestone 16, 2026-07-28):** an all-empty directory region is equally consistent with a blank
   JWSDOS disk or a blank PDOS working disk, so it does NOT default to `Jwsdos` (milestone 22's
   original carve-out, now removed from `IsPlausibleJwsdosDirectory()`) — it falls through to
   `Unknown` like any other unrecognized content, with no new fallthrough logic needed (confirmed,
   not just predicted). The UI shows a distinct **"Clean disk — no data written yet"** message
   rather than the generic "unknown disk contents/structure" wording for the specifically-all-zero
   case, via a new `DskImage.IsDirectoryRegionBlank()` query (no new `DiskDirectoryFormat` value
   needed). **Confirmed against real fixtures:** `jwssytem.dsk`'s own real all-empty track 2 now
   returns `Unknown` (that fixture's own milestone-22 test expectation was flipped, not left
   contradicting); a freshly-created blank disk shows the new message; every real non-empty
   `Jwsdos`/garbage fixture is unaffected. See `P2000.Machine` CLAUDE.md milestone 23 / `P2000.UI`
   CLAUDE.md milestone 16.)**

9. **RESOLVED AND FIXED (2026-07-30) — root cause found and confirmed via an actual observed
   boot: "JWS Dos boots perfectly now."** Three hypotheses investigated in sequence, the first two
   disproven, the third the real cause — kept below in full as the historical record, closing with
   the fix. Originally: **JWSDOS's manual activation path from a plain BASIC prompt:**
   `DEFUSR=5:?USR(0)` → monitor ROM jump-table entry `0x0005` (`cpm_start`, sourced from
   `Startup.asm`'s own `org 0x0000` table) → select bank 1 → `CALL 0xE000`. The "checksum test"
   is real, not a guess: JWSDOS's own `insert_dos_hook` → `checksum_control` sums N bytes from
   `0xE000` (N and the seed read from a 4-byte RAM scratch var, `ramdisk_tmp_storage+1`..`+4`,
   not a constant) and executes `RST 0` ("terminate with reboot," the disassembler's own comment)
   if the sum isn't zero — exactly the reported symptom, literally. **Bonus, independently
   confirms half the owner's own separate M2200 hypothesis:** `jwsdos5.0.asm` defines
   `ramdisk_Track`/`ramdisk_Sector`/`ramdisk_IO` at exactly `0x95`/`0x96`/`0x97`, matching the
   M2200 RAM-disk ports byte-for-byte — real, deliberate M2200 RAM-disk support in JWSDOS 5.0 (see
   this doc's §5c cross-reference). **The checksum itself is now RULED OUT by direct experiment
   (owner, 2026-07-28): patched to always return "pass," the reset to BASIC still happens.** This
   pointed squarely at `init_ramdisk`'s port-95/96/97 probe as the next suspect — exactly the
   owner's own live hypothesis, and a plausible-sounding mechanical story under this project's own
   documented card models (§5/reference doc: homebrew/T-102-class card is raw byte = bank index,
   out-of-range → open bus, so the probe's `17`/`65` writes, IF aliased onto the bank register,
   would push a 6-bank card's index out of range and derail bank 1 via open-bus). **DISPROVEN
   (CC, direct C# source investigation, 2026-07-30):** `PortDispatch.cs` is structurally
   single-exact-port only (fixed 256-entry arrays, no range/mask registration exists anywhere in
   the project) — an over-wide listener spanning `0x94`-`0x97` is architecturally impossible, not
   merely absent; `Machine.cs:163` registers exactly one listener on exactly `0x94`; ports
   `0x95`-`0x97` have zero registered listeners (the M2200 RAM-disk device itself is deferred), so
   `init_ramdisk`'s writes are genuine no-ops and its read returns open-bus `0xFF` — which matches
   neither `17` nor `65`, so its own `ret nz` fires immediately, well before any bank-select write
   could occur. No bank-select write, no stray I/O, no PC corruption; 7 new tests confirm the
   inertness (full `P2000.Machine.Tests`: 600/600 green). See `P2000T-reference.md` §5d for the
   full reasoning — the port-aliasing hypothesis is now provably dead, not just empirically
   unlucky. **Root cause still fully open, and now narrower:** neither the checksum nor
   port-aliasing is the cause, so whatever produces the symptom isn't in this project's port/
   bank-select dispatch at all. What populates `ramdisk_tmp_storage+1`..`+4`, and what actually
   loads JWSDOS's binary into bank 1 in the first place for a plain-BASIC-cartridge manual
   activation, remain open — this session's fresh reading of the newly-supplied `Disk.asm`
   confirms `getdos` itself is NOT that loader (it only auto-runs for a DOS-requesting cartridge,
   and never touches `ramdisk_tmp_storage`), and the now-complete monitor-ROM disassembly
   (`Startup.asm`/`Cassette.asm`/`Printer.asm`/`Disk.asm`/`Symbols.asm`, confirmed byte-identical
   to the real ROM via `P2000ROM.asm`'s own build check) contains no such loader either — it's a
   separate program, most likely on the JWSDOS boot disk itself, still unsupplied/undisassembled.
   **Recommended next step:** use the newly-built per-bank debugger (reference doc §3a, machine
   milestone 24 / UI milestone 17) to directly inspect bank 1 and `ramdisk_tmp_storage` at the
   moment `DEFUSR=5:?USR(0)` runs. **Done (owner, 2026-07-30) — different repro (cold start with
   the JWSDOS disk in drive 1 and a BASIC cartridge, not manual `DEFUSR=5`), same symptom, and a
   materially better mechanism found.** The live bank indicator confirmed bank 1 is selected TWICE
   during a genuine 2-track disk-boot read at cold start — meaning `getdos` (or something with an
   identical read pattern) DOES run here, correcting this entry's own earlier claim that it "only
   auto-runs for a DOS-requesting cartridge" for a plain BASIC cartridge; that gating condition
   needs re-examining. Inspecting bank 1 directly: `0xE000`-`0xEFFF` correctly holds JWSDOS's image;
   **`0xF000`-`0xFFFF` is entirely zero** — an unbroken run of `NOP` until `PC` wraps `0xFFFF`→
   `0x0000` (the monitor's own cold-boot vector), visually indistinguishable from "reboot to BASIC"
   but mechanically nothing like the checksum/`RST 0` or port-aliasing/`RST 38` guesses — no
   checksum, no bank-switch device, no I/O involved at all; bank 1's second half was simply never
   populated. **Re-reading `getdos` (`Disk.asm`) precisely against the owner's own confirmed disk
   geometry (40-track, double-sided, cylinder-major/side-minor layout: T1S1→image `0x0000`,
   T1S2→`0x1000`, T2S1→`0x2000`, T2S2→`0x3000`):** the "Disk IO" read command's head bit and its
   separate explicit side-# byte are BOTH `0` in `disk_constants`, copied once, and **never touched
   again** across the whole two-track loop — only `disk_track_num` (a separate RAM word) advances,
   driving a completely different "Goto Track" Search command (drive#+track# only, no side field)
   that just seeks the head to the next cylinder. Net effect, read directly from the code: both
   reads use side 0; only the cylinder advances. So `getdos` reads (cylinder 1, side 0) →
   `0xE000`-`0xEFFF`, then (cylinder 2, side 0) → `0xF000`-`0xFFFF` — **it never reads side 2 of any
   cylinder.** Under the owner's own layout that's image offset `0x0000` (correct) then `0x2000`
   (T2S1) — never `0x1000` (T1S2). If this boot disk's system image actually spans both SIDES of
   cylinder 1 rather than two side-0 cylinders, `getdos` as literally written could never load it
   correctly — a property of the ROM routine versus this disk's layout, not necessarily an emulator
   bug. **Two things still need checking before concluding that (2026-07-30):** (1) whether the
   emulator's own disk-image (cylinder, head, sector)→byte-offset mapping actually matches the
   owner's stated cylinder-major/side-minor convention — the owner's own explicit ask, not yet
   verified against the C# source; (2) whether the emulator's FDC/disk-command handling honors the
   `getdos` command bytes literally (side `0`, cylinder-only advance) rather than tracking head
   state some other way. See the dedicated investigation prompt and `P2000T-reference.md` §5d for
   the full reasoning.
   - **First check, same day: a false start.** CC's first pass concluded the existing side-major/
     cylinder-minor formula was empirically correct — validated by finding known real directory
     filenames (`Spel1.dsk`'s "Fraxxon"/"Tralieen") at the exact offsets that formula predicts. This
     was **circular**: it only proved the formula was self-consistent with data already interpreted
     through it, not that it matched genuine independent ground truth. Under that (wrong) belief,
     the FDC command-dispatch check (2) came back clean too — `Upd765.DispatchDataCommand` does
     read the head bit fresh from the command bytes every dispatch and seeking genuinely can't
     affect it — and the conclusion drawn was "no bug in either place; the JWSDOS disk's own
     track-2/head-0 content is just genuinely blank on the fixtures on hand, so bank 1's
     `0xF000`-`0xFFFF` being empty is `getdos` faithfully reading real blank content, not a defect."
   - **Overturned the same day by independent ground truth.** The owner supplied a clean,
     known-good JWSDOS binary reference (`assets/JWS.bin`, not derived from this project's own disk
     fixtures or formula) and compared it directly against raw bytes in real disk images
     (`jws-sytem.dsk`, `Spel1.dsk`). The reference's own second-track content matched raw disk
     offset `0x2000`-`0x213F` byte-for-byte — not `0x1000` as the (wrong) formula implied. Combined
     with the owner's own direct authority on the ROM (`getdos` loads exactly two PHYSICAL
     CYLINDERS, both head 0 — no double-sided support exists in the monitor ROM at all), this
     sealed **cylinder-major/head-minor** as the correct formula, not side-major/cylinder-minor.
   - **Fixed:** `DskImage.SectorOffset` (`Devices/Fdc/DskImage.cs`) changed to
     `cylinder * Sides * BytesPerTrack + head * BytesPerTrack + (sector-1) * BytesPerSector`;
     `ImdFormat.Read`/`Write` had independently duplicated the old formula in two places (never
     routed through `DskImage.SectorOffset`) and needed the same fix. Single-sided images are
     unaffected (the head term is always 0 when `Sides == 1`). 5 existing tests that had hardcoded
     raw offsets under the old formula were updated (not weakened) to the corrected offsets; the
     real-fixture regression test was moved off `Spel1.dsk` (whose own `0x2800` region has a
     separately-flagged, deliberately-deferred "duplicate content" oddity) onto `jws-sytem.dsk`'s
     clean match at `0x2000`. Full `P2000.Machine.Tests`: 604/604 green.
   - **Confirmed via an actual observed boot (owner, 2026-07-30, same day): "just tested and JWS
     Dos boots perfectly now."** This closes the bug end to end. See `P2000T-reference.md` §5d and
     §2 above (the formula correction) for the full reasoning and the geometry-formula fix's own
     ripple effects on this doc's CHS interpretations.
   - **Still open, deliberately deferred (owner's own call):** the "duplicate content" puzzle in
     `Spel1.dsk` (the same directory-shaped bytes appearing a second time, shifted by exactly
     `0x1800`, at raw `0x2800`/`0x3000`) — a stale-data theory, not investigated further this pass,
     unrelated to the fix above and doesn't affect JWSDOS booting correctly.

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
- **2026-07-23 (design-doc maintainer pass, official Philips T&M Reference Manual read in full,
  `raw-conversion.md`):** independent, primary-source corroboration of this doc's core geometry
  figures — 16 sectors/track, 256 B/sector, 35-track base geometry, 4-drive/560k ceiling — all
  confirmed directly from the manual's Ch2 "FLEXIBLE DISKS," a different and more authoritative
  source than the previously-cited MAME PR #7577 cross-reference. Also surfaced (not resolved) a
  flagged tension: the manual states disk data uses **frequency modulation (FM)**, not MFM,
  despite calling the media "double-density" (conventionally an MFM-implying label) — in tension
  with this doc's own "MFM encoding" (§1, from `getdos`'s FDC command bytes). Flagged inline in §1;
  full write-up in `P2000T-reference.md` §5d. → this doc §1.
- **2026-07-27 (owner-supplied official Dutch-language 24K Disk BASIC documentation):** PDOS's
  own FCB/directory structure, its "record" (4-sector/1 KB) allocation unit, a fully worked
  example cross-validating the sector-count/allocation-map relationship, a confirmed real
  per-track physical interleave pattern, and the track↔record-number formula, all sourced
  directly — resolving §7 item 7(b)'s "completely unsourced" status. Independently corroborates,
  and explains the mechanism behind, the already-known "35 or 40 tracks, single-sided only"
  ceiling: the allocation map's single-byte record numbering caps addressing at 256 records,
  which only single-sided 35- or 40-track geometries fit within. → this doc §6a, §7 item 7.
- **2026-07-27 (owner-supplied real disk image, `volorg.dsk`, 143,360 B, SS-35 — a real "VOLORG"
  disk-utility werkschijf): direct byte inspection upgrades three §6a items from "sourced from
  a single docx example" to independently CONFIRMED on different, real data.** (1) FCB layout
  confirmed on two real entries (`VOLORG`, exact-fit allocation with zero slack; `VOLINFO`,
  slack-padded allocation) — both match the documented position-16/17–32 scheme exactly, and
  neither ever allocates records `00`–`03`, confirming those are permanently reserved for track
  1's own index area (matches the owner's own reasoning independently). (2) The "trailing `0x00`
  = padding, not a record-0 reference" inference is now confirmed on real data, not just the
  one docx example. (3) **The physical interleave pattern is independently re-derived, not just
  transcribed:** `VOLINFO.BAS`'s own plain-text help menu reads as scrambled/incomplete garbage
  under naive sequential sector order, and reads as a perfect, complete, monotonically-ordered
  12-item menu once reassembled per the documented `{1,7,13,3}/{9,15,5,11}/{2,8,14,4}/
  {10,16,6,12}` pattern — the strongest possible confirmation available short of a real drive.
  Also newly observed: the FCB's position-1 byte is NOT constant (`0xF3` vs `0x00` across the
  two real entries) — the "unlabeled first byte" note is corrected from "0x00 in the worked
  example" to "varies, meaning still unconfirmed." → this doc §6a.
- **2026-07-27 (owner, independent hex-editor inspection of the same `volorg.dsk`):** confirmed
  the sector-reordering finding above independently, using a different method (manual hex-editor
  read) than this doc's own programmatic byte inspection — two independent confirmations of the
  same real disk. Also recalled that `0xF3` is a known Philips system-track signature and
  proposed it may flag position 1 as a system/protected-file marker for files belonging to the
  official Basic-24K product; recorded as a plausible, not-yet-confirmed hypothesis (fits this
  disk's `VOLORG.BAS`=`0xF3`/`VOLINFO.BAS`=`0x00` split cleanly, but needs a disk with more/mixed
  files to test properly). Incidentally surfaced via readable text on the disk: `VOLORG.BAS` is
  credited "WRITTEN BY MAX STERNECK, PHILIPS VIENNA" and interacts directly with the boot
  "SYSTEM DISK" (write-protect-label removal, insertion prompts) — real historical context, not
  currently load-bearing for any design decision. → this doc §6a.
- **2026-07-27 (owner, structural concern): renamed this doc from `JWSDOS-format.md` to
  `P2000T-disk-formats.md`.** With §6a's PDOS content now substantial and confirmed against real
  data, keeping a JWSDOS-branded filename was actively misleading about a fifth of the doc's own
  content — the owner's own objection, and the right call. Title and intro reworded to frame the
  doc as covering both of the P2000T's real DOSes (JWSDOS §1–§5, PDOS §6a) over the shared
  physical/boot layer (§6), rather than "JWSDOS, plus an unrelated PDOS aside." Every
  cross-reference to the old path inside `P2000T-reference.md` (the doc this session maintains)
  has already been updated to match.
- **2026-07-27 (Claude Code): the git mv + reference sweep flagged above is now done.**
  `git mv docs/JWSDOS-format.md docs/P2000T-disk-formats.md` (history preserved). Swept every
  `docs/JWSDOS-format.md` citation in source-code comments and each project's living/forward-
  looking `CLAUDE.md` sections (§13 build-order in `P2000.Machine/CLAUDE.md`, §14 build-order in
  `P2000.UI/CLAUDE.md`) plus `docs/M2200-implementation.md`, updating all to
  `docs/P2000T-disk-formats.md`. Left untouched, per the "historical record, not a live
  reference" rule: dated findings-log entries in both `CLAUDE.md` files, `docs/
  CLAUDE_machine_findings_archive.md`, and `docs/implementation-handoff-2026-07-22.md` (itself a
  dated point-in-time snapshot).
- **2026-07-28 (owner-supplied "PDOS info.docx," official Dutch-language 24K Disk BASIC
  documentation, a second excerpt from the same manual family as the 2026-07-27 source):**
  direct-quote upgrade of the FCB directory's location from inference to sourced fact ("Het
  eerste spoor van werkschijven (niet van de systeemschijf) bevat de index..."), plus the same
  document's own worked FCB hex dump and full track(1–40)-to-record-number table (independently
  confirming the `(N-1)×4`..`(N-1)×4+3` formula already in this doc, transcribed in full in the
  source but not reproduced here since the formula already covers it exactly). → this doc §6a.
- **2026-07-28 (owner, direct statement):** FCB position 1 is a continuation-sequence index —
  `0x00` for a file's primary FCB, `0x01`/`0x02`/… for additional FCBs appended (same name +
  extension) when a file's allocation map can't describe it in one 16-record entry. Resolves
  most of the "position 1 varies" open question from 2026-07-27; the `0xF3` real-disk value
  (`VOLORG.BAS`) is now understood as a distinct flag value outside plausible continuation range,
  not a competing explanation of the same byte — see §6a for the full reconciliation and the
  detection-collision consequence this raises for the disk-directory-view feature. → this doc
  §6a.
- **2026-07-28 (owner, direct statement, refining an initial "64" figure to 128):** the FCB
  directory's capacity is 128 entries — one full track (4096 B) ÷ 32 B/FCB, with no leftover
  space unaccounted for. → this doc §6a.
- **2026-07-28 (owner, planned test, not yet run):** a concise `VOLORG` manual surfaced a
  "set/reset file protect" menu option, letting the owner directly test the position-1
  `0xF3`/protected-file hypothesis (save → inspect FCB → protect → inspect FCB again) instead of
  relying on the one-disk `VOLORG`/`VOLINFO` coincidence. Results pending. → this doc §6a.
- **2026-07-28 (Claude Code, machine milestone 22 / UI milestone 15 — prompt 9 implemented):**
  real-fixture confirmation of the 16-sectors/track linear formula against `Spel1.dsk`'s
  `AUTORUN` entry (`DE_start_sector`/`DE_end_sector` 622/632 → track 39 sector 14 through track
  40 sector 8, confirmed rendering correctly in the new Disk Drives Track/Sector column);
  `volorg.dsk` independently confirmed as a real (not fabricated) "must not false-positive as
  JWSDOS" test case, since it has no JWSDOS label and non-printable-ASCII bytes at JWSDOS's
  directory offset; flagged that no known real fixture has a directory with entries on both
  sides, so the Side column's label logic is only exercised against a synthetic image so far. →
  this doc §4, §7 item 2.
- **2026-07-28 (Claude Code, "Disk I/O error" bugfix investigation): the `{1,7,13,3}/{9,15,5,11}/
  {2,8,14,4}/{10,16,6,12}` physical interleave pattern gets a THIRD independent confirmation,** on
  top of the two above (the source docx table, and the `VOLINFO.BAS` real-data reconstruction) —
  this time from live driver behavior under emulation rather than static analysis. After fixing
  three real `Upd765` FDC bugs (see `P2000T-reference.md` §5d for the full account), Philips Disk
  BASIC's own directory-scan routine was traced issuing sector requests in exactly the sequence
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16` — the documented interleave order, reproduced by the real
  resident driver itself, not by this project's own code re-deriving it. The scan itself now
  completes correctly; the still-open remainder of that bug (LOAD/SAVE still failing afterwards)
  is tracked separately and does not affect this confirmation. → this doc §6a.
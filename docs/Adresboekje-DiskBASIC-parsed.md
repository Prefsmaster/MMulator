# P2000 Adresboekje — Disk BASIC / PDOS section, parsed (pages 35–65)

Source: `Adresboekje.pdf` ("P2000 Adresboekje — BASIC NL / Disk BASIC"), Rob Geutskens, Son,
April 1986. Scanned/photographed booklet, no text layer. This transcription covers the
**Disk BASIC** memory-map section (pages 35–64) and the **BASIC token table** (page 65).
Pages 66–67 ("ROM-routines uit de BASIC-NL-module") cover the 16K Cassette-BASIC/BASIC-NL
ROM specifically, not Disk BASIC/PDOS — per your note that the cassette-BASIC material isn't
useful here, they're omitted.

**Method, so you can judge how much to trust this:** transcribed from the images directly, then
cross-checked address-by-address against a mechanical OCR pass (`tesseract`, 400–600dpi renders
of each page) run separately in this session. The two independent readings agree almost
everywhere; where they didn't, I went with whichever was internally consistent with surrounding
addresses/cross-references. Still — this is 30 pages of dense hex, so please spot-check anything
you're about to act on, especially the page-65 token table (dense columnar hex is exactly where
transcription errors hide). I did not independently verify any of this against real hardware or
the disassembly you're annotating.

Address format: original booklet uses `&Hxxxx` for hex. Kept as-is. `(..)` in the original means
"content varies / not a fixed startup value"; `(00)` etc. means that's the value at cold start.

---

## 🔴 High-value items for the open "Disk I/O error" investigation

Flagging these up front since they're the direct payoff for `cc-bugfix-prompt-9`:

- **`&H6091` — "Flag for Disk I/O error (see `&H69BB`)."** This is almost certainly the actual
  flag BASIC's error dispatcher reads to decide to print "Disk I/O error." Direct, named hit.
- **`&H69BB` is NOT documented in this booklet as its own entry** — it falls inside the range
  `&H6900`–`&H6ED3`, which the booklet glosses only as "Opstartroutine voor DISK BASIC" (Disk
  BASIC's own startup routine — "only used during startup, may be overwritten afterward").
  **Important wrinkle: this whole `&H6200`–`&H8A90` RAM range is the ~8K of the 24K interpreter
  that's loaded from tracks 3, 4, 5 of the system disk at boot (see `&H605D`'s entry and the page
  39 note below) — it is NOT part of the 16K cartridge ROM.** If `Basic24k.bin` in this project's
  assets is only the 16K ROM half, `&H69BB`'s actual code won't be in it — it'd need to come from
  a real system-disk image's tracks 3–5 instead (the project already has `diskbasic_1.6uk.dsk` as
  a real PDOS system-disk fixture, per the reference doc's ms.22b entry). Worth confirming which
  case applies before pointing `Z80.Disassembler` at anything.
- **`&H605D`** — a *different* error, worth not conflating with the one above: the monitor's own
  system-disk check at boot. If `0x00`, Disk BASIC prints **"DISK BASIC LOAD ERROR"** (distinct
  message, distinct trigger — this is "wrong disk in drive A at boot," not the runtime LOAD/SAVE
  failure).
- **PDOS call bridge, fully mapped:**
  - `&H6205`: `JP 6934` — the actual jump BASIC uses to invoke PDOS (only ever jumped to via
    `&H0005` in the monitor).
  - `&H6934`: `JP 696D` — "Roep PDOS aan" (call PDOS).
  - `&H6130`–`&H6174`: helper routines used to talk to PDOS, **reloaded from PDOS itself every
    time PDOS is called** — `&H6130` from `&HEB34` (PDOS 1.4) or `&HE9F5` (PDOS 1.6); `&H6137`
    from `&HEB82` (PDOS 1.4) or `&HEA3A` (PDOS 1.6). Tied to the interrupt vector table at
    `&H6020`/`&H6022` (channel 0 = "disk command complete or CTC interrupt," channel 1 = "disk not
    ready").
  - Register save slots around the PDOS call: `&H693B`/`&H693C` (HL, used by PDOS itself),
    `&H60B8`/`&H60B9` (HL, saved at `&H696D`), `&H60BB` (register C, saved at `&H697C`, moved to A
    after return at `&H69A2`/`&H69C1`), `&H60BC` (register B, apparently unused after saving),
    `&H6096` (last-pressed keycode, temp-stored across the PDOS call, see `&H6971`/`&H6974`).
- **The FDC command block BASIC/the monitor builds for real disk I/O — `&H6070`–`&H6086`** (full
  layout below): drive #, track #, side, sector #, sector count, gap length, etc. — this is the
  same command shape the reference doc's own `Disk.asm`/FDC section documents from the monitor
  side; here it's the RAM staging area BASIC/the monitor use to build it.
- **Disk-BASIC token table entry points (page 65, full table further down) for the tokens most
  relevant to LOAD/SAVE/directory access:**

  | Token | Disk-BASIC code | Disk-BASIC address |
  |---|---|---|
  | LOAD | `C4` | `&H376F` |
  | SAVE | `CB` | `&H3872` |
  | FILES | `C6` | `&H3543` |
  | OPEN | `BF` | `&H33D0` |
  | CLOSE | `C3` | `&H38BD` |
  | MERGE | `C5` | `&H3830` |
  | LOF | `FF B1` | `&H312F` |
  | LSET | `C9` | `&H3965` |
  | LPRINT | `9E` | `&H21A4` |
  | FN | `D3` | `&H7BAA` |
  | FRE | `FF 8F` | `&H4EB8` |

  These addresses are in the `&H2xxx`–`&H4xxx`/`&H7xxx` range, i.e. plausibly inside the
  **16K ROM half** (unlike `&H69BB` above) — worth checking against wherever this project maps
  the cartridge ROM, since if so, these ARE directly disassemblable from `Basic24k.bin` today,
  no system-disk fixture needed.

---

## Memory map, page-by-page

### Video memory (p.35)
- `&H5000`–`&H577F` (00): 1st video memory, 1920 addresses (24 lines × 80 positions). Direct POKE
  writes to screen.
- `&H5780`–`&H57CF` (00): "Scroll" line of the 1st video memory — used e.g. by LIST for the line
  that scrolls up from the bottom.
- `&H57D0`–`&H57FF` (00): last part of 1st video memory, usable for e.g. small tables; writing/
  reading here can cause visible streaks on screen. Cleared by cassette functions.
- `&H5800`–`&H5FFF` (..): 2nd video memory, same layout as 1st (2048 addresses total). On the
  T-model these addresses don't exist at all (no RAM there). On the M-model these hold 4-bit
  "attribute" codes for the 1st video memory (normal/inverse/blink/underline); `&H5800`
  corresponds to `&H5700`, etc.

### Keyboard (p.35–36)
- `&H6000`–`&H600B` (00): keycode buffer, up to 12 pending keys not yet consumed by the
  program/BASIC. Used by the monitor.
- `&H600C` (00): keycode buffer counter (count of codes stored from `&H6000`, max 12).
- `&H600D` (FF): code of the last-pressed key. Not auto-consumed — stays as long as the key is
  held. On release, becomes `FF` (255) at the next keyboard scan (within 20ms).
- `&H600E` (32): repeat rate for a held key. Normally `&H32` (48) — reverted by the monitor if
  changed (see `&H00A3` for T-model / `&H0176` for M-model). **If a Floppy Disk Interface is
  present, this address doubles as the CTC counter.**
- `&H600F` (00): keyboard flag. `00`=no SHIFT/SHIFT LOCK, `01`=SHIFT (bit0), `04`=SHIFT LOCK
  (bit2), `05`=both.

### Counter (p.36)
- `&H6010`/`&H6011` (..): a counter incremented every 20ms, wraps `0`–`&HFFFF`, only while
  interrupts are enabled (i.e. NOT while cassette/disk/printer are active).

### Monitor (p.36)
- `&H6012` (..): monitor flag address, multi-purpose — bit0=1 "repeat" on for held key (fill at
  `&H006C`, test at `&H007A`–`0083`); bit1=1 "numeric" on.

### Screen (p.36)
- `&H6013` (00): cursor shape flag for the application. `00` at T-model boot (inverse block
  cursor visible), `01` at M-model boot. Odd value = cursor invisible (T) / dash-shaped (M). Under
  Disk BASIC, changeable only via `POKE &H6013,X` with X=00 or 01.
- `&H6014`/`&H6015` (00): pointer to where the monitor should display info about actions taken —
  believed unused due to a bug in the relevant monitor routines. Disk BASIC sets this to `0000`
  (see `&H6D07`). If changed on the system disk (e.g. to `&H5000`), "L" appears on-screen for
  SHIFT LOCK and "D" for disk routines executing. Content is an offset; monitor adds register E
  (`&H01C6`). Used by the service module.

### Printer (p.37)
- `&H6016` (01): baud rate the monitor uses to the printer. `Baudrate = 2400/(1+A)`. `00`=2400,
  `01`=1200 (default via `&H034C`), `03`=600, `07`=300 (via `&H0348`), `0F`=150, etc. Bit7=1
  suppresses automatic CR+LF.

### NOP (p.37)
- `&H6017`–`&H601F` (00): unused by Disk BASIC unless you write your own cassette routines
  (should then match BASIC NL's addresses).

### Interrupts (p.37)
Monitor interrupt vector table — two consecutive bytes = jump target address. **The first two
(`&H6130`, `&H6137`) are reloaded every time PDOS is called** (see `&HEB1B`+ in PDOS 1.4, `&HE9DC`
in PDOS 1.6).
- `&H6020`: `30 61` → `JP 6130`. IRQ channel 0: disk command complete, or CTC interrupt.
- `&H6022`: `37 61` → `JP 6137`. IRQ channel 1: disk not ready.
- `&H6024`: `00 00`. IRQ channel 2: communication interrupt (unused).
- `&H6026`: `38 00` → `JP 0038`. IRQ channel 4: keyboard/timer interrupt.
- `&H6028`–`&H602F` (00): unused.

### NOP / cassette record header (p.38)
- `&H6030`–`&H6054` (00): monitor's internal cassette "record header"/"file descriptor," precedes
  every 1024-byte block written to cassette. Unused by Disk BASIC (and `&H6050`–`&H6054`); if you
  write your own cassette routines, these must be populated as the monitor expects.

### Stack pointer (p.38)
- `&H6055` (FB) / `&H6056` (61): monitor saves the old SP here when PDOS is called (via
  `CALL 0005`, which jumps to `&H044F`).

### NOP (p.38)
- `&H6057`–`&H605B` (00): unused.

### Memory (p.38)
- `&H605C` (01): amount of RAM detected at boot. `01`=16K, `02`=32K, `03`=48K+.

### Disk (p.38–39) — boot-time system disk check, "DISK BASIC LOAD ERROR"
- `&H605D` (01): if a Disk Controller + floppy drive with a disk and closed door are present at
  boot, the monitor reads the disk's first two tracks. It then checks whether that was a system
  disk by checking whether `&HF3` is present at `&HE000` of the second memory bank (see
  `&H0EC6`+). If NOT a system disk → this address becomes `00`; in every other case → `01`.
  - `00` = the disk read in is **not** a PDOS system disk.
  - `01` = no Disk Controller / no Disk Drive / no disk in drive A / drive not enabled / door
    open, **or** a PDOS system disk was read in.
  - The monitor reads the first two tracks regardless of content and writes them to `&HE000`–
    `&HFFFF` of the second memory bank either way, then jumps to `&H1010` of the plug-in module.
  - **Disk BASIC's first action at `&H1010` is to check `&H605D`. If it's `00`, "DISK BASIC LOAD
    ERROR" is printed** — i.e. exactly when everything else is fine except the wrong disk sits in
    drive A.
  - The 24K interpreter needs a system disk in drive A to function at all: ~16K lives in ROM
    (cartridge), and the missing ~8K is loaded from **tracks 3, 4, and 5 of the system disk**,
    landing at **`&H6200`–`&H8A90`**. *(This is the range containing `&H69BB` flagged above.)*

### Printer (p.39)
- `&H605E`: `E0 6F` → start address of the first printer-translation table (`&H6FE0`).

### Reserved (p.39)
- `&H6060`–`&H606F` (00): reserved for cassette routines.

### Disk — FDC command staging block (p.39–40)
`&H6070`–`&H6086` are filled by the monitor at boot from `&H0FE8`–`0FFE` (see `&H0E9A`+); Disk
BASIC redoes this lightly (see `&H106F`+) then modifies several fields itself.

| Addr | Init | Meaning |
|---|---|---|
| `&H6070` | `00` | Transfer address (destination). `&H6200` for the missing interpreter chunk, `&HE000` (2nd bank) for PDOS. |
| `&H6071` | `E0` | (high byte of above) |
| `&H6072` | `09` | Command length (always `&H09`) |
| `&H6073` | `42` | Function: `42`=read from disk, `45`=write to disk |
| `&H6074` | `01` | Disk Drive number |
| `&H6075` | `01` | Track number |
| `&H6076` | `00` | Side |
| `&H6077` | `..` | Sector number (`0`–`&H0F`) |
| `&H6078` | `01` | Transmission speed ("baudrate"), always `01` |
| `&H6079` | `10` | Sectors per track, always `&H10` (16) |
| `&H607A` | `0E` | Gap space, always `&H0E` |
| `&H607B` | `00` | Data length |

**Seek command** (p.40):
| Addr | Init | Meaning |
|---|---|---|
| `&H607C` | `03` | command length (always `&H03`) |
| `&H607D` | `0F` | command code (`&H0F`) |
| `&H607E` | `01` | disk drive number |
| `&H607F` | `..` | track number |

**Recall command:**
| Addr | Init | Meaning |
|---|---|---|
| `&H6080` | `02` | command length (always `&H02`) |
| `&H6081` | `07` | command code (`&H07`) |
| `&H6082` | `01` | disk drive number |

**Specify command:**
| Addr | Init | Meaning |
|---|---|---|
| `&H6083` | `03` | command length (always `&H03`) |
| `&H6084` | `03` | command code (`&H03`) |
| `&H6085` | `60` | parameter 1,2 (`&H60`) |
| `&H6086` | `34` | parameter 3,4 (`&H34`) |

- `&H6087`–`&H608D` (..): monitor work addresses.
- `&H608E`/`&H608F` (..): temp SP storage while loading the disk constants from `&H0FE8` to
  `&H6070` (see `&H0E91`/`&H0EDA`).

### Screen (p.40)
- `&H6090` (00): display delay. POKEing 1–255 adds delay when rendering (LIST or RUN); higher =
  slower (see `&H69F0`). Adds on top of the SHIFT-key delay at `&H600F`.

### Disk (p.40) — **the flag**
- **`&H6091` (00): Flag for Disk I/O error (see `&H69BB`).**
- `&H6092` (00): unused.

### Screen (p.40)
- `&H6093` (00): flag for executing `CHR$(4)CHR$(Y)CHR$(X)` (cursor-position escape sequence):
  becomes `01` after `CHR$(4)`, `02` after `CHR$(Y)`, `03` after `CHR$(X)`.
- `&H6094` (00): holds Y (line number) after `CHR$(4)CHR$(Y)`.
- `&H6095` (00): holds X (column) after the full sequence.

### Disk (p.40)
- `&H6096` (00): temp storage of the last-pressed keycode when PDOS is called (see `&H6971`,
  `&H6974`).

### Printer (p.41)
- `&H6097` (00): storage for printer control codes (see `&H6C5B`); what happens to it afterward
  is unclear.

### NOP (p.41)
- `&H6098`–`&H60AF` (00): probably unused.

### Screen (p.41)
- `&H60B0` (27=39dec): physical screen line length. `39`=40-col, `79`(`&H4F`)=80-col. Changeable
  via `POKE &H60B0,X`; values >79 cause cursor overlap on the same line. See also `&H639F`.
- `&H60B1`/`&H60B2` (..): cursor's video-memory address (e.g. `&H5000`).
- `&H60B3` (..): cursor position on the physical line (`PRINT PEEK(&H60B3)`), always between 0 and
  `&H60B0`'s value. Logical-line cursor position is `&H66C3`.
- `&H60B4`/`&H60B5` (..): cursor's memory address — always seems to equal `&H60B1`/`&H60B2`.
- `&H60B6` (00): lowercase→uppercase auto-conversion toggle, switched by SHIFT TAB. `00`=convert
  (BASIC-program mode), `FF`=don't convert (text mode). Any other value breaks SHIFT TAB.

### Disk (p.41)
- `&H60B7` (00): zeroed while loading PDOS (see `&H6D32`); nothing else seems to happen with it.
- `&H60B8`/`&H60B9` (00): temp storage of register HL when PDOS is called (see `&H696D`).
- `&H60BA` (00): probably unused.
- `&H60BB` (00): temp storage of register C when PDOS is called (see `&H697C`); on return from
  PDOS, content can move to register A (`&H69A2`, `&H69C1`).

### (p.42)
- `&H60BC` (00): storage for register B, but apparently nothing further happens with it.
- `&H60BD`–`&H60C5` (00): probably unused (NOP).

### Screen (p.42)
- `&H60C6`/`&H60C7` (00): either stores `&H5000` (see `&H6BD3`) or the difference of HL and BC
  (see `&H6BC9`). Updated whenever the screen scrolls; content otherwise seems unused.
- `&H60C8`–`&H60CA` (00): probably unused (NOP).

### Disk (p.42) — loading the missing interpreter chunk
- `&H60CB` (03): temp storage, lowest track number to read when loading the missing interpreter
  part (see `&H101E`).
- `&H60CC` (06): temp storage, highest track number + 1 (see `&H1023`).
- `&H60CD`/`&H60CE` (00, 62): temp storage, first load address for the missing chunk (=`&H6200` —
  see `&H1019`). Also used when the "Runtime Support?" prompt is answered "Y" (see `&H6E19`+): then
  becomes `07`, `08`, `&HC000` respectively, transferred to the FCB at `&H6070`–`608F`.

### NOP (p.42)
- `&H60CF`–`&H6111` (00): probably unused.

### Unknown (p.42)
- `&H6112` (DD) – `&H611D` (F0): unknown.

### Disk (p.43)
- `&H611E`/`&H611F` (..): temp storage of the SP while loading the missing interpreter part (see
  `&H1061`, `&H10C0`).
- `&H6120`–`&H612F` (..): stack for the CP/M monitor routine (see `&H0453`).

### PDOS reload helper routines (p.43) — **directly tied to the interrupt vectors above**
- `&H6130`: `3E 01` `LD A,01` — put `01` in accumulator.
- `&H6132`: `D3 94` `OUT 94` — switch to 2nd memory bank.
- `&H6134`: `C3 0B EA` `CALL EA0B` — call `&HEA0B` in PDOS.
  - Small helper routines for talking to PDOS. **Reloaded every time PDOS is called**, from
    `&HEB34` (PDOS 1.4) or `&HE9F5` (PDOS 1.6). See interrupt vector `&H6020`.
- `&H6137`–`&H6174`: `F3`...`C9`: more helper routines, likewise reloaded each PDOS call, from
  `&HEB82` (PDOS 1.4) or `&HEA3A` (PDOS 1.6). See interrupt vector `&H6022`.

### Scratch space (p.43)
- `&H6175`–`&H61FF` (00): free for e.g. intermediate results or your own machine-code routines.

### Back to BASIC (p.43)
- `&H6200`: `C3 74 8A` → `JP 8A74`. Returns to BASIC from a machine-code routine with a clean
  stack and cleared buffers. **Any BASIC program in memory IS cleared** (unlike BASIC NL). See
  also `&H6380`.

### NOP (p.43)
- `&H6203`/`&H6204` (00): unused.

### PDOS entry (p.43)
- `&H6205`: `C3 34 69` → **`JP 6934`. This is how PDOS is invoked** (via `&H0005` in the monitor).
  `&H6934` can in principle be replaced with a different jump target, e.g. to call your own
  cassette routines.

### Free for jump addresses (p.43)
- `&H6208`–`&H622F` (00): unused, free for your own jump addresses / short machine-code routines.

### BASIC buffer init (p.44)
- `&H6230`–`&H625F` (00): temporarily holds the first part of the buffer at `&H6280` while BASIC
  starts up (see `&H6E46`) — zeroes the buffer's first 48 bytes. Free for other use afterward.

### NOP (p.44)
- `&H6260`–`&H627F` (00): probably unused.

### File Control Block (FCB) layout — buffer at `&H6280` (p.44–45)
`&H6280`–`&H637F`: 256-byte buffer holding one sector of the disk index during LOAD/SAVE, split
into 8 chunks of 32 bytes (one FCB each, mirroring track-1's index). Also usable by your own
machine code between PDOS calls (contents aren't permanent — overwritten on next PDOS call).

| Offset (from FCB start) | Init | Meaning |
|---|---|---|
| `+0` (`&H6280`) | `00` | file type (usually `00`) |
| `+1`..`+8` (`&H6281`–`88`) | `..` | 8-char name, space-padded |
| `+9`..`+11` (`&H6289`–`8B`) | `42 41 53` | 3-char extension, space-padded (BASIC defaults to `BAS` if none given) |
| `+12` (`&H628C`) | `00` | extent number within the file (usually `00`; `01` for a 2nd FCB entry if file > 16K) |
| `+13` (`&H628D`) | `00` | write-protect code (BASIC can set this) |
| `+14` (`&H628E`) | `00` | probably unused |
| `+15` (`&H628F`) | `..` | length in sectors (256 bytes/sector) |
| `+16`..`+31` (`&H6290`–`629F`) | `..` | **Disk Allocation Map** — up to 16 "record" numbers (4 sectors = 1024 bytes each); real sector order on disk ≠ logical order (see track/sector table below) |

Blocks `&H62A0`–`62BF`, `&H62C0`–`62DF`, etc. repeat the same 32-byte layout for the other 7 FCB
slots.

### Soft start into BASIC (p.45)
- `&H6380`: `C3 88 17` → `JP 1788`. Soft landing into BASIC — any BASIC program in memory is
  **NOT** cleared (contrast with `&H6200`).

### USR table (p.45)
- `&H6383`–`&H6395` (`33 1F` each): jump table for USR0–USR9. Default: `&H1F33`, an error-handling
  routine, until a `DEF USRn` sets a real target.

### Flag addresses (p.45)
- `&H6397` (01): unclear purpose, likely tied to the next address.
- `&H6398` (00): also unclear (see `&H709C`).

### Error code (p.45)
- `&H6399` (..): BASIC error code of the most recently occurred error (program or direct mode).
- `&H639A` (00): NOP.

### Printer (p.46)
- `&H639B` (00): printer column counter — `? LPOS(X)` reads this.

### (L)LIST / (L)PRINT (p.46)
- `&H639C` (00): output indicator, set by the LPRINT routine (`&H21A4`). `00`=screen, `01`=printer.

### Printer (p.46)
- `&H639D` (38): number of 14-space blocks emitted for `LPRINT A,B,C...` — always a multiple of 14
  (`&H0E`); auto-adjusted when `&H639E` changes via `WIDTH LPRINT`.
- `&H639E` (50): max printer-line length before auto CR+LF; settable via `WIDTH LPRINT XX`.

### Screen (p.46–47)
- `&H639F` (FF): max screen-line length (logical). Governs CR+LF vs. continuing the line, in
  concert with cursor position `&H66C3` — CR+LF when `(66C3) > (639F)`.
- `&H63A0` (0E=14dec): controls `PRINT 1,2,3,4`-style column spacing together with `&H63B4` (this
  looks like a doc typo for `&H66C3`, per the described algorithm) — determines whether the next
  value prints 14 columns over or triggers CR+LF, and how many spaces separate printed values.
  Full algorithm and string-printing variant described in the original at length; summary: after
  each comma, cursor position (`&H66C3`) always becomes 0 or a multiple of 14, wrapping to the
  next physical line if the logical line length (`&H639F`) is exceeded.

### Unknown (p.47)
- `&H63A1`–`&H63A4` (00): unknown (`&H63A1` see `&H70B4`; `&H63A2` see `&H1840`).

### CLEAR (p.47–48)
- `&H63A5`/`&H63A6` (..): end of free memory usable by BASIC — set via `CLEAR,&Hxxxx,nnnn`.
  **Key difference from BASIC NL:** under Disk-BASIC, the address `xxxx` marks the boundary, with
  the **stack** reserved downward from it by `nnnn` bytes, and the **string space** below that
  (i.e. string space sits *under* the stack — opposite of BASIC NL, where string space sits
  *above* the stack). String space grows automatically as needed; you get "Out of memory" only
  when it collides with the bottom of the array space. See also `&H66D4`.

### Line number (p.48)
- `&H63A7`/`&H63A8` (FF): currently-executing BASIC line number; `&HFFFF` (-1) at BASIC startup.

### BASIC program start (p.48)
- `&H63A9`/`&H63AA` (4C, 92): start of the BASIC program. With buffers for 3 files: `&H924C`.
  General formula: `&H8A90 + 296 + n*561 + 1` for n file buffers.

### Overflow (p.48)
- `&H63AB`/`&H63AC` (D0, 14): start address of the "Overflow" error message (triggered on
  arithmetic overflow).

### Unknown (p.48)
- `&H63AD`–`&H63B0` (00): unknown.

### Buffers (p.49)
- `&H63B1`/`&H63B2` (90, 8A): end address of the BASIC interpreter = start of the ASCII-file
  buffer (`&H8A90`).
- `&H63B3`/`&H63B4` (90, 8A): start address of the BASIC buffer — 40+256=296 bytes (32 for FCB, 8
  for housekeeping, 256 for one sector) — used when reading ASCII files.
- `&H63B5`–`&H63D2`: start addresses of the file-working buffers (0–15 files selectable at
  startup). Each buffer is 561 (`&H231`) bytes: 1 byte file type, 32-byte FCB, 7 bytes temp
  storage, 2×256 bytes file data, 9 unused bytes. 15 files = 8415 bytes.

  | # files | start address |
  |---|---|
  | 1 | `&H8BB8` |
  | 2 | `&H8DE9` |
  | 3 | `&H901A` |
  | 4 | `&H924B` |
  | 5 | `&H947C` |
  | 6 | `&H96AD` |
  | 7 | `&H98DE` |
  | 8 | `&H9B0F` |
  | 9 | `&H9D40` |
  | 10 | `&H9F71` |
  | 11 | `&HA1A2` |
  | 12 | `&HA3D3` |
  | 13 | `&HA604` |
  | 14 | `&HA835` |
  | 15 | `&HAA66` |

  BASIC program start with 15 files: `&HAA66 + 561 + 1 = &HAC98`.
- `&H63D3` (..): number of reserved buffers (=file count) minus 1.

### Unknown (p.50)
- `&H63D4`–`&H640C` (00): unknown.

### Files (p.50)
- `&H640D` (..): file type.
- `&H640E`–`&H6418` (..): scratch buffer for filename+extension (8+3 chars), space-padded, default
  extension `BAS` applied here before going to the FCB.
- `&H6419` (00): null terminator.

### Unknown (p.50)
- `&H641A`–`&H642D` (00): unknown.
- `&H642E` (3A): unclear; likely marks the start of the next buffer.

### Buffers — tokenized line buffer (p.50)
- `&H642F` (..): holds the first token/character of the last-executed direct-mode line.
- `&H6430`–`&H652D` (..): 254-byte buffer for BASIC lines — program lines are tokenized here as
  typed (see `&H656E`) before being moved into the program; same for EDIT.

### NOP (p.50)
- `&H652E`–`&H656C` (00): probably unused (buffer likely over-provisioned).

### PRINT buffer (p.50)
- `&H656D` (2C=','): unclear purpose, likely marks the start of the next buffer.

### ASCII buffer (p.51)
- `&H656E`–`&H666B` (..): 254-byte ASCII buffer for BASIC lines, used both for tokenizing
  direct-mode input and for EDIT/(L)LIST detokenizing. Viewable via SHIFT+9 on the numeric pad
  ("M") — shows a `!` on screen; editable with the same keys as EDIT mode.

### NOP (p.51)
- `&H666C`–`&H66C2` (00): probably unused.

### Screen (p.51)
- `&H66C3`: cursor position within the **logical** line (0–255, per `&H639F`). Query via
  `PRINT POS(X)`. Physical-line position is at `&H60B3`.

### Variable (p.51)
- `&H66C4` (..): flag for variable lookup. `00`=add to list, `01`=look up address in list.

### FAC type indicator (p.51–52)
- `&H66C5`: type indicator for the Floating-Point Accumulator. Also lands in register A for a
  `?USR(X)` call, and used internally (e.g. computing `SIN(X)`).
  - `02` = 2-byte integer (INT)
  - `03` = string (STR)
  - `04` = single-precision float (SNG)
  - `08` = double-precision float (DBL)

### Flag addresses (p.52)
- `&H66C6` (..): unknown (see `&H1A1F`).
- `&H66C7` (..): unknown (see `&H1A22`).

### BASIC (p.52)
- `&H66C8`/`&H66C9` (..): points to the position in the BASIC line just before the next
  instruction to execute. In direct mode, points into the buffer from `&H642E`.

### Number encoding (p.52)
Disk BASIC stores numbers in tokenized/encoded form in the program (unlike BASIC NL, which uses
ASCII). These addresses stage the encoded value before it's written into the program line (also
used for direct-mode calculations).
- `&H66CA` (..): size code for the number: `11`–`1A` = integers 0–9 (code=`&H11+n`), `0F`=integers
  10–255, `1C`=integers 256–32767, `1D`=single-precision, `1F`=double-precision.
- `&H66CB` (..): number of bytes for the number (`02` int, `04` single, `08` double).
- `&H66CC`–`&H66D3` (..): 8 bytes for the encoded number itself.

### BASIC (p.52–53)
- `&H66D4`/`&H66D5` (..): highest address+1 of string space = first address of the stack.
  Settable via `CLEAR,xxxx,nnnn` → becomes `xxxx` minus `nnnn`. Stack size defaults to 516 bytes at
  start/RESET (first stack address `&HFDFC`). See also `&H63A5`.

### String (p.53)
- `&H66D6`/`&H66D7` (..): pointer to the next valid 3-byte entry in the string table below.
- `&H66D8`–`&H66F5` (..): table of string descriptors (10×3 bytes, 30 total) — used whenever BASIC
  needs to look up a string (concatenation, error messages) in string space or the program.
  Usually starts `04 4E 17` → refers to "Ok" at `&H174E`. Each 3-byte descriptor: length byte, LSB
  of the string's storage address, MSB of the string's storage address.
- `&H66F6`–`&H66F8` (..): staging area for a string descriptor before it's copied into the table
  above.
- `&H66F9`/`&H66FA` (..): address of the last character read in string space (string space lives
  at the top of memory — see `&H63A5`). Reset to `&H66D4`/`&H66D5`'s value (as set by CLEAR) on
  NEW.

### Misc (p.53)
- `&H66FB`/`&H66FC` (..): dual purpose — (1) address of the token currently executing (see
  `&H2526`); (2) formatting flag for `PRINT USING` when converting hex→ASCII (`00`=no formatting
  needed, i.e. strict binary→ASCII) (see `&H7FCA`).

### Unknown (p.53)
- `&H66FD`/`&H66FE` (00): unknown (see `&H4BBC`/`&H4BC7`).

### Pointer (p.54)
- `&H66FF`/`&H6700` (..): end address of the last-executed BASIC command.

### DATA (p.54)
- `&H6701`/`&H6702` (..): line number where the last DATA was read.

### FOR...NEXT (p.54)
- `&H6703` (..): flag, FOR in progress. `00`=no, `01`=yes (sometimes `&H80` — see `&H298E` — or
  `&H64` — see `&H1CD3`).

### INPUT / READ (p.54)
- `&H6704` (00): flag, `00`=INPUT, `01`=READ.

### BASIC (p.54)
- `&H6705`/`&H6706` (..): start address of the next BASIC line to execute. Set to (program start
  − 1) on NEW (`&H924B` with 3 files).

### Unknown (p.54)
- `&H6707` (00): unknown.

### AUTO (p.54)
- `&H6708` (00): flag, AUTO in progress (`00`=no, else = step size).
- `&H6709`/`&H670A` (00): line number for AUTO (filled at `&H18F5`).
- `&H670B` (00): step size for AUTO (filled at `&H2154`).

### Misc (p.54)
- `&H670D`/`&H670E` (..): address of the code in the buffer after `&H642E`, during input phase.
- `&H670F`/`&H6710` (..): SP storage during the execute phase.

### Error handling (p.55)
- `&H6711`/`&H6712` (FF): line number where the last error occurred (see `&H1811`); `&HFFFF` if
  none.

### EDIT and LIST (p.55)
- `&H6713`/`&H6714` (00): line number last EDITed or LISTed. Recallable via `EDIT .` (EDIT + dot).
- `&H6715`/`&H6716` (..): address of the next instruction to execute.

### Error handling (p.55)
- `&H6717`/`&H6718` (00): target line for `ON ERROR GOTO` (see `&H3FD1`).
- `&H6719` (00): flag, error handler invoked. `FF`=an error is being handled, `00`=not (RESUME
  resets to `00`).

### BASIC (p.55)
- `&H671A`/`&H671B` (..): address of the next token to process.
- `&H671C`/`&H671D` (..): last-executed BASIC line number at STOP/END (see `&H1821`).
- `&H671E`/`&H671F` (..): address of the last byte executed before STOP.

### Pointers (p.55)
- `&H6720`/`&H6721` (..): pointer to the start of variable space = end address of the BASIC
  program. Can be raised to reserve room for machine code appended after the program, to be
  written to disk together with it.
- `&H6722`/`&H6723` (..): pointer to start of array space.
- `&H6724`/`&H6725` (..): pointer to end of array space = start of free space for own data /
  machine code = bottom address of string space.

### DATA pointer (p.56)
- `&H6726`/`&H6727` (..): DATA pointer — address of the byte following the last-read DATA
  statement. RESTORE sets this to (program start − 1); `RESTORE n` sets it to the target line's
  start − 1.

### Type declaration table (p.56)
- `&H6728`–`&H6741` (04 each): 26-byte type table, one byte per letter of the alphabet (variable
  name's first letter → default type). `04` (single-precision) for all letters at start/NEW (see
  `&H1CA5`). Changeable via `DEFSTR`/`DEFINT`/`DEFSNG`/`DEFDBL`. `02`=integer, `03`=string,
  `04`=single, `08`=double.

### Unknown (p.56)
- `&H6742`–`&H6745` (00): unknown.

### DEF FN buffer (p.56)
- `&H6746`–`&H67A9` (00): 100-byte buffer for `DEF FN NAME(X,Y,...)` variable names/values — same
  shape as the buffer at `&H67AE`.
- `&H67AA`/`&H67AB` (42, 67): stack pointer marking exactly where this buffer sits on the stack.
- `&H67AC`/`&H67AD` (..): length of the filled portion (counted from `&H67AE`; overflow →
  "Illegal function call").
- `&H67AE`–`&H6812` (..): the actual 100-byte variable buffer. Each variable: type (1 byte), name
  (2 ASCII bytes), value (2/4/8 bytes for int/single/double).

### Unknown (p.57)
- `&H6813`/`&H6814` (..): "start of variable space?" (uncertain per original).
- `&H6815`–`&H6849` (..): unknown.

### Copy protection (p.57)
- `&H684A` (00): if nonzero, the program can't be LISTed; EDIT/POKE/PEEK give "Illegal function
  call." Set to `&HFE` when a program saved with `,P` is loaded. Such a program can run but not be
  altered.

### Unknown (p.57)
- `&H684B`–`&H685B` (..): unknown.

### TRON/TROFF (p.57)
- `&H685C` (00): trace flag. `00`=TROFF, nonzero (`&HAF` when set via TRON)=TRON.

### Temp storage (p.57)
- `&H685D`: scratch for floating-point decode routines — usually holds the last byte shifted out
  of the least-significant-byte position.

### FAC I / FAC II / FAC III (p.57–58)
- `&H685E`–`&H6865`: first Floating-Point Accumulator (FAC I) — calculation results.
- `&H6866`: sign of the number for arithmetic ops.
- `&H6867`: temp storage for double-precision calculations.
- `&H6868`–`&H686F`: FAC II — same shape as FAC I, used when adding two double-precision numbers.
- `&H688B`–`&H6892`: FAC III — same shape as FAC I.

Byte layout across all three FAC buffers:

| Addr (rel.) | Double prec. | Single prec. | Integer |
|---|---|---|---|
| +0 | DP (LSB) | | |
| +1 | DP | | |
| +2 | DP | | |
| +3 | DP | | |
| +4 | DP | SP (LSB) | INT (LSB) |
| +5 | DP | SP | INT (MSB) |
| +6 | DP | SP | |
| +7 | DP (MSB) | SP (MSB) | |

### Unknown (p.58)
- `&H6870` (..): unknown.

### PRINT buffer (p.58)
- `&H6871`–`&H688A` (..): calculation results in ASCII, e.g. `?FRE(0)`'s free-memory figure before
  display.

### Unknown (p.58)
- `&H6893`–`&H689D` (00): unknown.

### INP and OUT (p.58–59)
```
&H689E: CD 6A 2B   CALL 2B6A
&H68A1: 32 A5 68   LD (68A5),A
&H68A4: DB 00      IN 00
&H68A6: C3 E1 28   JP 28E1
```
Used for BASIC's `INP` — `&H68A5` gets the actual port number instead of `00`.

```
&H68A9: CD 54 2B   CALL 2B54
&H68AC: D3 00      OUT 00
&H68AE: C9         RET
```
Used for BASIC's `OUT` — `&H68AD` gets the port number; accumulator content is sent.

```
&H68AF: DB 00      IN 00
&H68B1: AB         XOR E
&H68B2: A0         AND B
&H68B3: CA AF 68   JP Z 68AF
&H68B6: C9         RET
```
Probably used for BASIC's `WAIT`.

### Unused (p.59)
- `&H68B7`–`&H68FF` (00): probably unused.

### BASIC part 2 + jump table (p.59)
- `&H6900` (AF): start of the second part of the Disk BASIC interpreter.
- `&H6909`: `C3 D3 6E` → `JP 6ED3` — continuation of the routine starting at `&H6900`.
- `&H690C`: `00 00 00` — unused.
- `&H690F` (01): flag address.
- `&H6910`: `C3 22 69` → `JP 6922` — see below.
- `&H6913`: `C9 00 00` `RET` — unused jump slot.
- `&H6916`: `C3 D6 69` → `JP 69D6` — put a character on screen.
- `&H6919`: `C3 CB 6C` → `JP 6CCB` — get a character (ASCII).
- `&H691C`: `C3 AD 6C` → `JP 6CAD` — is a key pressed? If not, return.
- `&H691F`: `C3 D6 69` → `JP 69D6` — put a character on screen.
- `&H6922`: `C3 00 6D` → `JP 6D00` — clear screen, ask number of desired files, load "Runtime
  support" if requested.
- `&H6925`: `C9` `RET` — unused.
- `&H6926`: `95 6F` — location of the STOP key in the keycode table.
- `&H6928`: `C9 60 10` `RET` — **if `C9` is changed to `C3`, this becomes a jump address to the
  disk routines in the first ROM** — but this address itself is never called.
- `&H692B`: `C3 22 6C` → `JP 6C22` — print a character on the printer.
- `&H692E`: `C3 77 6E` → `JP 6E77` — call the keyboard-status routine; RET if no key pressed,
  otherwise determine the ASCII code; jumps to `&H405E` if STOP is pressed.
- `&H6931`: `C9` `RET` — unused.
- `&H6932`: `D3 6F` — start of the screen conversion table (`&H6FD3`).
- **`&H6934`: `C3 6D 69` → `JP 696D` — calls PDOS. Only ever jumped to from `&H6205`.**
- `&H6937`: `C9` `RET` — unused.
- `&H6938`: `3D 6F` — start address of the keycode table (`&H6F3D`); this address itself is never
  called.
- `&H693A`: `C9` `RET` — unused.
- `&H693B`/`&H693C` (00): used by PDOS to preserve register HL.
- `&H693D`–`&H696C`: text "PHILIPS DISK BASIC ......".
- `&H69D6` (C5): Disk BASIC startup routine. `&H69D6`–`&H6ED3` only used at startup — may be
  overwritten by your own routines afterward. **(`&H69BB`, referenced by the Disk-I/O-error flag
  at `&H6091`, falls inside this range.)**

### Keycode table (p.60)
- `&H6F3D`–`&H6FCC` (07 at start): keycode table (in RAM) — redefine any key's ASCII code via
  `POKE &H6F3D+n,x` (n=keycode per Table 5 of the P2000T manual, x=desired ASCII code).

### Screen translation table (p.60)
- `&H6FD3`–`&H6FDF` (7B at start): 5 byte-pairs translating ASCII codes before they reach the
  screen (e.g. pound-sign → hash, ½ → apostrophe). Search stops at code `00`. Relocatable if
  `&H6932`/`&H6933` point to the new start.

### Printer translation tables (p.61–62)
Two back-to-back sections: first for printers with no backspace capability (matrix printers),
second for printers that can back up (daisy-wheel/golfball, for combined letter+accent/underline).
- `&H6FE0` (00): first section — starts with a count of byte-pairs (Disk BASIC ships with `00`,
  i.e. no first section by default). Each pair: byte-to-translate, replacement byte.
- Second section starts at `first section start + 2×pair count + 1`; two sub-sections:
  - First sub-section at `&H6FE1`: byte-triples (character, then 2 replacement bytes sent with a
    backspace in between). `06` triples at start. Bit7 set on byte 2 or 3 triggers an escape code
    (`&H1B`=27) sent to the printer first.
  - Second sub-section at `&H6FF4`: same byte-pair structure as the first main section, `&H0C`=12
    pairs.
- `&H700D`–`&H707F` (00): 115 bytes free for your own printer-translation table or extending the
  existing one.

### Hex→decimal (p.62)
- `&H7F9F`: `CD 22 78` — start of a routine converting a hex number in HL to decimal ASCII in the
  PRINT buffer (`&H6874`–`&H6879`).

### End of BASIC (p.62)
- `&H8A8D`: `C3 67 37` → `JP 3767` — last line of the BASIC interpreter.

### Buffers (p.62)
- `&H8A90`–`&H8BB7` (00): 296-byte buffer for reading in programs (32-byte FCB + one 256-byte
  sector).
- `&H8BB8`–`&H8DE8` (00): first file buffer, 561 bytes total: file type byte, 32-byte FCB, 7 bytes
  temp storage (purpose unclear), 512 bytes file data (2 sectors), 9 unused bytes. Current read
  position: `PRINT HEX$(VARPTR(#n))`.
- `&H8DE9`–`&HAC97` (00): room for up to 14 more 561-byte buffers (start addresses: see `&H63B5`
  table above).

---

## Track/sector layout of disks (p.63–64)

24K Disk BASIC disks: 35 or 40 tracks, 16 sectors/track, 256 bytes/sector. Per-track capacity
16×256=4096 bytes (4K); per-disk 35×4K=140K or 40×4K=160K. Track count is determined by the drive
hardware and PDOS (PDOS can be adapted to 40 tracks if the drives support it).

**Records, not raw sectors:** PDOS allocates in units of "records" = 4 sectors = 1024 bytes, even
for files far smaller than that. Sectors within a record are **not** physically contiguous on the
track — logical sector order ≠ physical order. E.g. record 04 = physical sectors 1, 7, 13, 3 (in
that order) of track 2; record 05 = sectors 9, 15, 5, 11 of track 2.

**Track 1 of a working disk (not the system disk) holds the index** — the FCBs of all
programs/files. Each FCB is 32 bytes; example (`MONITOR`/`EBAS`, 27 sectors):
```
00 4D 4F 4E 49 54 4F 52   ..MONITOR
45 42 41 53 00 00 00 1B   EBAS....
0E 0F 10 11 12 13 14 00
00 00 00 00 00 00 00 00
```
- Bytes 2–9: name (space-padded to 8 chars).
- Byte 16: sector count (`&H1B`=27 here → program < 27×256=6912 bytes).
- Bytes 17–32: **Disk Allocation Map** — record numbers. In the example, 8 sectors of track 4, all
  16 of track 5, and 4 of track 6 are used (7×4=28 sectors, one more than strictly needed — the
  last sector of record `&H14` goes unused).
- If a file exceeds 16K, a second FCB "entry" is opened in the index.
- **`CLOSE` writes the updated FCB back into the track-1 index. Forgetting `CLOSE` means the file
  is later unfindable (partially or completely).**

### Track/sector interleave table (p.64)

Logical sector → physical sector mapping (decimal / hex), confirmed identical to this project's
own independently-derived interleave (`1,7,13,3,9,15,5,11,2,8,14,4,10,16` pattern, `P2000T-disk-
formats.md` §6a):

| Logical (dec) | 1 | 7 | 13 | 3 | 9 | 15 | 5 | 11 | 2 | 8 | 14 | 4 | 10 | 16 | 6 | 12 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Logical (hex) | 00 | 06 | 0C | 02 | 08 | 0E | 04 | 0A | 01 | 07 | 0D | 03 | 09 | 0F | 05 | 0B |

The booklet then gives the resulting **absolute** sector-index byte for each (track, logical-group)
combination — tracks 1–40, in 4-column groups matching the interleave groups above (e.g. track 1's
row is `00 01 02 03`, track 2's is `04 05 06 07`, ..., track 40's is `9C 9D 9E 9F`). This is a
derived/expanded view of the same interleave constant above (physical sector index = track×4 +
column, columns ordered per the logical groups) — reproduce from the formula rather than
transcribing all 40 rows verbatim, since it's fully determined by the interleave pattern and track
number.

---

## Disk-BASIC token table (page 65) — Disk-BASIC columns only

Per your note, the Cassette-BASIC (16K) code/address columns are dropped here — they're a
different interpreter (BASIC NL) and not useful for the Disk-BASIC/PDOS investigation. `FF xx`
codes are two-byte (extended) tokens per the original's own convention; `----` in the address
column means "no separate implementation address" (e.g. relational/arithmetic operators like `<`,
`>`, `=`, `+`, `-`, `*`, `/`, `AND`, `OR` — likely inlined into the expression evaluator rather than
being their own subroutine).

| Nr | Token | Code | Address | | Nr | Token | Code | Address |
|---|---|---|---|---|---|---|---|---|
| 1 | AND | F7 | ---- | | 73 | MOD | FC | ---- |
| 2 | ABS | FF 86 | 76AD | | 74 | MKI$ | FF B2 | 3638 |
| 3 | ATN | FF 8E | 8795 | | 75 | MKS$ | FF B3 | 363B |
| 4 | ASC | FF 95 | 4CA4 | | 76 | MKD$ | FF B4 | 363E |
| 5 | AUTO | AB | 212C | | 77 | MID$ | FF 83 | 4D45 |
| 6 | CLOSE | C3 | 38BD | | 78 | NEXT | 83 | 41F1 |
| 7 | CONT | 9A | 40BA | | 79 | NULL | 96 | 40CE |
| 8 | CLEAR | 92 | 4162 | | 80 | NAME | C7 | 337B |
| 9 | CINT | FF 9C | 77C0 | | 81 | NEW | 94 | 3F81 |
| 10 | CSNG | FF 9D | 783A | | 82 | NOT | D5 | 77B9 |
| 11 | CDBL | FF 9E | 7866 | | 83 | OUT | 9D | 68A9 |
| 12 | CVI | FF AB | 3651 | | 84 | ON | 95 | 2097 |
| 13 | CVS | FF AC | 3654 | | 85 | OPEN | BF | 33D0 |
| 14 | CVD | FF AD | 3657 | | 86 | OR | F8 | ---- |
| 15 | COS | FF 8C | 86ED | | 87 | OCT$ | FF 99 | 4A4B |
| 16 | CHR$ | FF 96 | 4CB4 | | 88 | OPTION | BA | 2F1C |
| 17 | CALL | B6 | 3B11 | | 89 | PUT | C2 | 8811 |
| 18 | COMMON | B8 | 1FFE | | 90 | POKE | 99 | 2DA7 |
| 19 | CHAIN | B9 | 3B94 | | 91 | PRINT | 91 | 21AC |
| 20 | CLOAD | AC | 4E7B | | 92 | POS | FF 91 | 28DD |
| 21 | CSAVE | AD | 4E5D | | 93 | PEEK | FF 97 | 2D9D |
| 22 | DATA | 84 | 1FFE | | 94 | READ | 87 | 243E |
| 23 | DIM | 86 | 46C1 | | 95 | RUN | 8A | 1F7E |
| 24 | DEFSTR | AD | 1EE4 | | 96 | RESTORE | 8C | 4043 |
| 25 | DEFINT | AE | 1EE7 | | 97 | RETURN | 8E | 1FE3 |
| 26 | DEFSNG | AF | 1EEA | | 98 | REM | 8F | 2000 |
| 27 | DEFDBL | B0 | 1EED | | 99 | RESUME | A9 | 20E4 |
| 28 | DEF | 98 | 2937 | | 100 | RSET | CA | 3964 |
| 29 | DELETE | AA | 2D63 | | 101 | RIGHT$ | FF 82 | 4D3B |
| 30 | END | 81 | 4063 | | 102 | RND | FF 88 | 8651 |
| 31 | ELSE | A2 | 2000 | | 103 | RENUM | AC | 2DD8 |
| 32 | ERASE | A6 | 411B | | 104 | RESET | CC | 3500 |
| 33 | EDIT | A7 | 42B5 | | 105 | RANDOMIZE | BB | 2F66 |
| 34 | ERROR | A8 | 2121 | | 106 | STOP | 90 | 405E |
| 35 | ERL | D6 | 73D4 | | 107 | SWAP | A5 | 40DD |
| 36 | ERR | D7 | 73D1 | | 108 | SAVE | CB | 3872 |
| 37 | EXP | FF 8B | 859E | | 109 | SPC( | D4 | 7C8E |
| 38 | EOF | FF AF | 3050 | | 110 | STEP | D1 | 7A6C |
| 39 | EQV | FA | ---- | | 111 | SGN | FF 84 | 76C2 |
| 40 | FOR | 82 | 1CD1 | | 112 | SQR | FF 87 | 8541 |
| 41 | FIELD | C0 | 38FB | | 113 | SIN | FF 89 | 86F3 |
| 42 | FILES | C6 | 3543 | | 114 | STR$ | FF 93 | 4A57 |
| 43 | FN | D3 | 7BAA | | 115 | STRING$ | D8 | 754F |
| 44 | FRE | FF 8F | 4EB8 | | 116 | SPACE$ | FF 98 | 4CED |
| 45 | FIX | FF 9F | 78B3 | | 117 | SYSTEM | BD | 34D2 |
| 46 | GOTO | 89 | 1FAC | | 118 | TRON | A3 | 40D7 |
| 47 | GO TO | 89 | 1FAC | | 119 | TROFF | A4 | 40D8 |
| 48 | GOSUB | 8D | 1F94 | | 120 | TAB( | D0 | 783A |
| 49 | GET | C1 | 8812 | | 121 | TO | CE | 77C0 |
| 50 | HEX$ | FF 9A | 4A51 | | 122 | THEN | CF | 7881 |
| 51 | INPUT | 85 | 2374 | | 123 | TAN | FF 8D | 8780 |
| 52 | IF | 8B | 2162 | | 124 | USING | D9 | 75B5 |
| 53 | INSTR | DA | 774C | | 125 | USR | D2 | 7A65 |
| 54 | INT | FF 85 | 78C6 | | 126 | VAL | FF 94 | 4D66 |
| 55 | INP | FF 90 | 689E | | 127 | VARPTR | DC | 796B |
| 56 | IMP | FB | ---- | | 128 | WIDTH | A1 | 2B18 |
| 57 | INKEY$ | DD | 7997 | | 129 | WAIT | 97 | 2AFD |
| 58 | KILL | C8 | 3519 | | 130 | WHILE | B4 | 3A79 |
| 59 | LET | 88 | 202A | | 131 | WEND | B5 | 3A9C |
| 60 | LINE | B1 | 2306 | | 132 | WRITE | B7 | 3E87 |
| 61 | **LOAD** | **C4** | **376F** | | 133 | XOR | F9 | ---- |
| 62 | LSET | C9 | 3965 | | 134 | + | F2 | ---- |
| 63 | LPRINT | 9E | 21A4 | | 135 | - | F3 | ---- |
| 64 | LLIST | 9F | 2B76 | | 136 | * | F4 | ---- |
| 65 | LPOS | FF 9B | 28D7 | | 137 | / | F5 | ---- |
| 66 | LIST | 93 | 2B7B | | 138 | ^ | F6 | ---- |
| 67 | LOG | FF 8A | 7509 | | 139 | ÷ | FD | ---- |
| 68 | LOC | FF B0 | 3117 | | 140 | ' | DB | ---- |
| 69 | LEN | FF 92 | 4C98 | | 141 | > | EF | ---- |
| 70 | LEFT$ | FF 81 | 4D0A | | 142 | = | F0 | ---- |
| 71 | LOF | FF B1 | 312F | | 143 | < | F1 | ---- |
| 72 | MERGE | C5 | 3830 | | | | | |

Bolded LOAD since it's the primary target for the "Disk I/O error" trace.

---

## Not transcribed (out of scope per your note)

- Pages 66–67 ("ROM-routines uit de BASIC-NL-module"): a list of monitor/BASIC-NL ROM routine
  addresses and short behavior notes, for the 16K Cassette-BASIC interpreter specifically. Since
  you said the cassette-BASIC material isn't useful for the Disk BASIC investigation, I left this
  untranscribed — flag if you actually want it (e.g. if some of those routines turn out to be
  shared monitor-level code Disk BASIC also calls).
- The Cassette-BASIC code/address columns in the page-65 token table (dropped per your note).

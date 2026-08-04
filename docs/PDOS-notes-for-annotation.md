# Notes for PDOS_wip.asm annotation

Everything below was learned live-tracing the real emulator (boot → `SYSTEM B` → `RUN"VOLORG"`,
`FILES`, `RESET`) cross-referenced against the disassembly, during the "Disk I/O error"
investigation (project CLAUDE.md's 2026-08-02/03 through 2026-08-04 findings-log entries, Parts
A through I — the investigation is now closed, root cause found and fixed, see §7 and the
CLAUDE.md entry it points to). It's meant to save you re-deriving structure you'll hit again —
not a substitute for your own read of the actual bytes, and not everything here has a byte-exact
address confirmed (flagged where it's an inference from behavior rather than a confirmed
instruction).

Addresses below use whatever label `docs/PDOS_wip.asm` already has where one exists; where it
doesn't (yet), I give the raw hex address and my own working name in *italics*.

---

## 1. The dispatcher

`CPM_entry_point` (`0xE000`) is the single entry point BASIC calls into (via `&H6205 → JP 6934 →
JP 696D`, all in the always-visible monitor RAM `&H6130`-ish region, then `JP 0005h` from there
lands here). On entry, **register A holds the function code** (copied from C by the BASIC-side
wrapper before the call). Two special cases are checked FIRST, by literal ASCII value, not the
numeric function code: `'2'` (0x32, init drive) and `'3'` (0x33, motor off) — both cassette-era
leftovers, reused for disk. Every other value falls through a long `cp nn` / `jr z,label` chain
starting around `le0a6h`. The table below is what I've mapped so far (not exhaustive — I only
traced the codes that came up during `SYSTEM`/`RESET`/`FILES`/`RUN`):

| Code | Meaning (inferred) | Dispatch target |
|---|---|---|
| `0x02` | *(unconfirmed — always classified as an error by the return wrapper, see §5)* | — |
| `0x0A`,`0x0B`,`0x0C` | *(same — always-error classes at the wrapper level, not traced past dispatch)* | — |
| `0x0D` | called by `RESET` (2nd of its 3 calls) | `sub_e2c8h` → `le325h` |
| `0x0E` | **select default drive** — called by both `SYSTEM x` and `RESET`'s 3rd call | falls to `le149h` → `sub_e705h` |
| `0x0F` | verify/compare step during directory scan — **also contains the "is this FCB's extension == 'BAS'?" check that calls `sub_e943h` inline**, see §4 | `le0b4h` area |
| `0x11` | seen during `FILES`/`RESET`'s directory prep | `le0fch` → `sub_e51fh` |
| `0x12` | seen during `FILES` | `le10ah` (falls to `le149h`) |
| `0x13` | seen during directory prep | `le11bh` → `sub_e31bh` |
| `0x14` | recurs throughout every directory scan (`SYSTEM`/`FILES`/`RUN` alike) | `le10ah` area |
| `0x15` | seen once near the start of a scan | `le0f3h` region |
| `0x16` | seen during directory prep | `le124h` → `sub_e2dch` |
| `0x17` | seen during `FILES` | `le0f3h` → `sub_e5f7h` |
| `0x19` | called by `RESET` (1st of its 3 calls) | `le149h` region |
| `0x1A` | **read next directory sector** — the workhorse of every directory scan | `le12dh` (falls to `le149h`) |

`0x18`,`0x1B`-`0x1E` also just fall to `le149h` (i.e. "call `sub_e705h`, then exit") — I never saw
these actually triggered in a real repro, so I don't know what makes BASIC choose them.

## 2. The BASIC-side return wrapper (NOT in `docs/PDOS_wip.asm` — lives in the disk-loaded RAM
chunk, `&H698D`-`&H69D5`)

Every `CALL &H6205` (BASIC's own PDOS invocation) returns through this wrapper before control
goes back to the token handler. It reads a byte the PDOS dispatcher above leaves at `&H60BB`
(this is literally the SAME memory cell as `lf51eh`/`Disk_error_code` from PDOS's own
perspective — PDOS is bank-1 RAM at `0xF51E`, banked into `0xE000`-`0xFFFF`; BASIC reads it back
at the FIXED monitor-RAM address `0x60BB` once bank 0 is restored) and dispatches on it:

- Result `0x02`, `0x0A`, `0x0B`, `0x0C` → **unconditionally** sets `&H6091 = 2` ("Disk I/O
  error" flag) and jumps to the print routine at `&H69BB`, REGARDLESS of whether the underlying
  disk op actually succeeded.
- Result `0x09` → special-cased: only errors if a SECOND check (comparing the SAME byte again)
  isn't `0x10`.
- Result `0x1A` → **leaves `&H6091` completely untouched** — a real "inherit whatever the flag
  already was" no-op. This is what makes `SYSTEM B`'s first call after a failed `RESET` look
  like a "stale retry" — it isn't stale, it's this wrapper genuinely not touching the flag.
- Result `0x0F` → clears a SECOND flag (`&H60D0` bit 0) as well as `&H6091`.
- Anything else → clears `&H6091 = 0` (success).

**Important:** this wrapper's dispatch is on the RESULT byte PDOS leaves behind, NOT the original
function code BASIC sent. `sub_e943h` (§3 below) directly writes `0x02` into this same byte
(`Disk_error_code`/`lf51eh`) — that's the ONLY way `RUN`/`LOAD` end up reporting "Disk I/O
error": nothing in the dispatcher above sets it directly except via `sub_e943h`.

## 3. `sub_e943h` — the actual "report Disk I/O error" routine (`0xE943`)

```
sub_e943h:
    di
    ld a,003h ; reset disk-complete CTC interrupt
    out (CTC_CH0),a
    xor a ; disable FDC/motor entirely
    out (DSKCTRL),a
    ... (sets a couple of other status bits at 0xF525)
    ld a,002h
    call sub_e334h        ; writes 0x02 into Disk_error_code/lf51eh — THIS is what the
                           ; BASIC-side wrapper (§2) reads and turns into "Disk I/O error"
set_bit6_enable_ints:
    ld hl,PDOS_flags
    set 6,(hl)
    ei
    ret
```

**Four call sites, confirmed by address** (search `docs/PDOS_wip.asm` for `call sub_e943h`):
1. `0xE0EE` — inline inside the function-`0x0F` dispatch handler, ONLY when a directory-scan
   FCB's extension field reads exactly `"BAS"` (bytes `0x42 0x41 0x53`). *(Traced: this branch is
   never actually taken during a normal `RUN"VOLORG"` scan — every `0x0F` call in that scan
   returns its own code unchanged, meaning the extension check fails every time for a real .BAS
   file being searched. Worth double-checking this branch's own polarity/offset — it looks
   backwards for a normal case, see the open question in §6.)*
2. `0xE978`/`0xE97D` — the confirmed Part-B path: reached when `busy_wait_for_interrupt`
   (`le95fh`/`le962h`, a hardcoded 65536-iteration delay loop) runs to completion without being
   redirected early by a real disk-operation completion interrupt.
3. `0xEA6B` (`lea6ah`) — a THIRD, distinct path: reached if the interrupt vector installed by
   `Set_time_out_comm_irq`/`Sense_int_and_Specify` (see §4) ever actually fires as-is (i.e.
   nobody overwrote it with a real operation-specific handler first).

## 4. The shared interrupt-vector scratch area (`0x6130`-`0x6174`ish) — genuinely reused for
at least 3 different payloads

This whole region is always-visible monitor RAM (NOT banked), and the interrupt VECTOR TABLE
itself (`0x6020`→ch0, `0x6022`→ch1) always points into it — but WHAT'S ACTUALLY THERE gets
overwritten depending on what PDOS is currently doing:

- **Idle/motor-off timer** (`exit_and_set_disk_off_timer`/`le296h`, called after EVERY PDOS
  command exits): copies a real `irq_code` handler (50 bytes) that decrements two software loop
  counters (`0xFF` then `0x04` — 255×4≈1020 real CTC ticks ≈ 20s at the confirmed ~65,280-T-state
  CTC period) and only THEN sets `PDOS_flags` bit 6 + calls the equivalent of `sub_e943h`'s work.
  This is a "you've been idle a while, turn the motor off" timer, not a disk-error detector.
- **"Default" handler** (`Set_time_out_comm_irq`, called from `Sense_int_and_Specify`): copies a
  DIFFERENT, smaller (69-byte) block whose channel-0 half just does `jp EI_RETI` (a harmless
  no-op ack) and whose channel-1 half jumps straight to `lea6ah` → `sub_e943h` immediately, no
  wait at all. Looks like a "nothing else has claimed this interrupt yet, so treat one as an
  error" baseline, meant to be overwritten by whichever real operation follows.
- **Real read/write completion redirect** (`sub_e8c3h`, called from `sub_e7abh`'s read setup):
  does NOT re-copy the whole block — it only patches the 2-byte JUMP TARGET at `0x6135`-`0x6136`
  (an operand inside whatever's already sitting in the channel-0 slot) to point at a real
  continuation (`le910h`/`le916h`/`le86ah`/`le8f7h` depending on context). When the FDC's real
  completion interrupt fires during `busy_wait_for_interrupt`, this handler POPS the busy-wait's
  own return address off the stack and pushes the continuation instead — a genuine "redirect
  control flow out of the wait loop via the interrupt" trick, confirmed correct in this
  project's own emulation (channel 0 fires and delivers on every real disk operation, every
  time — see the Part B/C findings-log entries).

If you're annotating this region, it's worth tracking WHICH of these last wrote to it before
trusting what a byte dump there means at any given moment — it's genuinely not one fixed
routine.

## 5. FCB layout (track 1, 128 entries, 32 bytes each — already well-documented in
`docs/P2000T-disk-formats.md` §6a, repeating the byte offsets here since they came up constantly)

| Offset | Field |
|---|---|
| `+0` | flag/type byte (`0xF3` = system-disk marker when found at track1/sector1/offset0; otherwise a continuation-sequence index, `0x00` for a file's primary FCB) |
| `+1`..`+8` | filename, space-padded |
| `+9`..`+11` | extension, space-padded |
| `+12` | extent/continuation index |
| `+13` | write-protect flag |
| `+14` | unused |
| `+15` | sector count |
| `+16`..`+31` | allocation map (up to 16 record numbers, 4 sectors/record) |

## 6. Open question as of Part C (RESOLVED by Part I, differently than expected — see §7 below)

**Confirmed (live instruction-level tracing, not just T-state inference):** of the 4
`sub_e943h` call sites in §3, exactly ONE fires for a real `RUN"VOLORG"` attempt —
`channel_time_out` (0xE978), reached from `busy_wait_for_interrupt`'s own natural
65536-iteration timeout. Neither the `0x0F`/"BAS" branch (0xE0EE) nor the third `lea6ah` path
(0xEA6B) ever fires.

**Also confirmed:** PDOS's own dispatcher (`CPM_entry_point`, 0xE000) NEVER receives any
function code beyond `0x0F`/`0x1A`/`0x14` during the whole attempt — a candidate "real
file-data-read trigger" (function `0x39`, `le229h`/0xE229 — converts an `(ix_pointer)` value via
`sub_eb23h` and stores the result into `RW_cmd_track`) is never sent. **Both `0x1A` and `0x14`
route to the exact same handler**, `sub_e705h` (0xE705) → `sub_f2fdh` (0xF2FD) — and
`sub_f2fdh` is itself a SECOND, internal jump table (`lf307h + 2*code`), which is where PDOS's
real FCB-compare/decision logic must live. Its case handlers (a dozen-plus subroutines —
`sub_f068h`, `sub_f09bh`, `sub_f137h`, `sub_f186h`, `sub_f0adh`, `sub_f045h`, `sub_ef3dh`,
`sub_eeadh`, `sub_ef57h`, `sub_ecd5h`, `sub_f24ch`, `sub_f2c2h`, `sub_f2d4h`) are **not yet
disassembled by this investigation** — flagging their existence and rough location (roughly
0xF2FD-0xF428+ in the raw binary) in case it's useful for your own annotation pass, since this
is squarely where the answer to "why doesn't the file read happen" must live: either that
inner jump table's own FCB-match logic never signals success for this fixture's FCB content, or
it does and the decision to proceed to a real read lives entirely on BASIC's own side (a
separate, disk-loaded code region from PDOS's bank-1 driver, per §2 above) and never gets
there. See the project CLAUDE.md's 2026-08-03 "Part C NARROWED FURTHER" entry for the full
write-up, including a real gotcha (PC==0xE943 false-positives from a RET's fetch-increment
timing, and reading the wrong register for the dispatch function code) worth knowing about if
you ever build your own live-tracing tooling against this disassembly.

## 7. Part I (2026-08-04): the real read-issue chain, fully traced — and §6's open question closed (differently than expected)

This closes out §6 above. Short version: **the file read absolutely does happen** — CR genuinely
advances, `sub_e8b3h` genuinely issues a real physical READ DATA for every record, and the
`sub_f2fdh` jump table's dozen-plus handlers (§6, still individually undisassembled) turned out to
be a dead end for this particular question, not the blocker. The actual problem was one level
lower: a race between PDOS's own software polling loop and the real completion interrupt, in a
mechanism that (once found) turned out to be a genuine emulator bug, now fixed — see project
CLAUDE.md's 2026-08-04 "Part I" findings-log entry for the full narrative and the fix itself
(`Upd765.DeferNaturalCompletion`, `src/P2000.Machine/Devices/Fdc/Upd765.cs`). What follows is the
PDOS-side half of that story — genuinely new structural detail about this disassembly, all
byte-exact confirmed (either via embedded operand bytes matching a label's own address, or via
chained arithmetic from a hex-byte-commented anchor elsewhere in the file — not guessed from label
names alone, though the label-naming convention itself turned out to be reliable everywhere it was
checked this way).

### 7.1 The real F_READ → physical-read call chain

```
sub_f137h (0xF137)                    -- F_READ's real handler (reached via lf3a0h -> 0xF3A3's
  |                                        `call sub_f137h`); reads CR/RC via sub_ec39h
  |  CR < RC?
  v
lf170h (0xF170)                       -- calls sub_ec19h, leb9eh (the confirmed-uncapped
  |                                        interleave-index computation, Part F/G), sub_f123h
  v
Seek_to_track (0xE72Ch, per earlier   -- physically seeks; ALSO patches (0x6135) = EI_RETI
  parts) -> send_seek_or_recalibrate     via its own separate `ld (06135h),hl` at 0xE740-E742
  v
sub_e8b3h (0xE8B3)                    -- the REAL physical-read issuer
  |  ld a,00fh; ld (Retry_counter),a
  v
le8b8h (0xE8B8)
  |  call sub_e8c0h
  v
sub_e8c0h (0xE8C0)                    -- `ld hl,le916h` (0x0916) -- confirmed via the embedded
  |                                        operand bytes themselves (`21 16 E9`), not just naming
  v
issue_Disk_read_command (0xE8C3)      -- THIS is what earlier prompts/entries call "sub_e8c3h"
  = issue_Disk_write_command (0xE8C5) -- `ld a,046h` then falls straight in; `ld (RW_cmd_action),a`
  |                                        `ld (06135h),hl` is at EXACTLY 0xE8C8-0xE8CA (3 bytes;
  |                                        confirmed by backward byte-count from the hex-commented
  |                                        `ld a,(lf56bh) ;e8dd` anchor a few instructions later)
  |  ... programs CTC ch1 (0xC5, TC=1 -- an "immediate trigger", NOT the completion signal itself,
  |      see 7.3), sets B=(lf56bh) [always 0x00 -> 256 via INI wraparound], E gets set from
  |      (lf56dh) here but then UNCONDITIONALLY OVERWRITTEN by sub_e8b3h's own `ld e,001h` back in
  |      le8b8h -- E=1 for every real VOLORG-data read, no exceptions seen
  v
sub_e8b3h continues: `ld e,001h; jp le7b8h`
  v
le7b8h (0xE7B8) -- `call read_disk_bytes-0x88c5; jp busy_wait_for_interrupt`
  v
read_disk_bytes (0x6145, unbanked/low RAM -- runs regardless of which bank is selected)
  |  xor a; out (BANK_SWITCH),a         -- selects bank 0 (FDC ports live outside the banked window
  |                                        anyway, but PDOS's OWN bank-1 code isn't reachable while
  |                                        this runs, hence the switch)
  v
dsk_in_loop (0x6148)                  -- the software semi-DMA polling loop, byte layout below
```

### 7.2 `dsk_in_loop`'s exact byte layout (0x6148 onward) — a real gotcha worth flagging

```
0x6148  in a,(DSKCTRL)         ; 2 bytes (DB 90) -- poll the 0x90-bit0 byte-ready flag
0x614A  rra                    ; 1 byte
0x614B  jp nc,dsk_in_loop-0x88c5 ; 3 bytes -- not ready, loop
0x614E  ini                    ; 2 bytes (ED A2) -- ** NOT 1 byte, easy to miscount **
0x6150  jp nz,dsk_in_loop-0x88c5 ; 3 bytes -- B (INI's own down-counter) not yet 0, more bytes
                                             in this sector
0x6153  dec e                  ; 1 byte -- one whole sector done; any more sectors wanted?
0x6154  jp nz,dsk_in_loop-0x88c5 ; 3 bytes -- yes (never happens for VOLORG reads, E=1 always)
0x6157  jp dsk_io_done-0x88c5  ; 3 bytes
```

`INI` being 2 bytes (`ED A2`), not 1, is the kind of thing that throws off manual byte-counting
through this region if you're deriving addresses by hand — flagging it since it directly bit an
earlier pass of this investigation's own tooling (a mis-count here briefly looked like a real
mid-instruction interrupt-timing bug before the arithmetic was redone correctly).

`dsk_io_done` (`0x6157-0x88c5` relocated, i.e. the label right after this block, not yet given a
name in this file) does `ld a,00eh; out (DSKCTRL),a` — **this is PDOS's own EXPLICIT
terminal-count write**, the thing that actually forces most transfers to complete (see 7.3), then
`ld a,001h; out (BANK_SWITCH),a; ret` (back to bank 1, return to `read_disk_bytes`'s own caller,
`le7b8h`'s trailing `jp busy_wait_for_interrupt`).

### 7.3 Why PDOS always requests MORE than it reads, and what that means for completion timing

`DISK_RW_Command`'s static template (`docs/PDOS_wip.asm`, search `db 0x09 ; command length`):

```
db 0x09                    ; length prefix (not a wire byte)
RW_cmd_action:    db 01000110b   ; opcode 0x46 = READ DATA, MF set
RW_cmd_hd_drive_select: db 0x01
RW_cmd_track:     db 0x01        ; C -- irrelevant, addressing is by tracked head position
RW_cmd_head:      db 0x00
RW_cmd_sector:    db 0x10        ; R -- static default; OVERWRITTEN per-read to the real target
                  db 0x01        ; N -- 256 B/sector
                  db 0x10        ; EOT -- ALWAYS 0x10 (16), NEVER overwritten anywhere in this
                                 ;   disassembly. Confirmed by grepping every `ld (RW_cmd_sector)`
                                 ;   site (many) against every reference to the byte right after N
                                 ;   -- none touch it.
                  db 0x0e        ; GPL
                  db 0x00        ; DTL
```

So every real READ DATA command PDOS issues has **R = whichever physical sector the confirmed
interleave table says is next** (1, 7, 13, 3, 9, 15, 5, 11, 2, 8, 14, 4, 10, 16, ... — Part F/G's
own confirmed sequence) and **EOT fixed at 16** — meaning the *nominal* requested window
(`sectorCount = EOT - R + 1`) shrinks every cycle: 16 sectors when R=1, all the way down to
**exactly 1 sector when R=16** (the last sector of the track). But the software's own polling loop
(`dsk_in_loop`, §7.2) only EVER reads one sector (E=1, hardcoded by `sub_e8b3h`) regardless of R.

For every R except 16, this leaves the emulated FDC's own internal transfer buffer bigger than what
the software actually reads — so completion can only come from `dsk_io_done`'s own explicit
terminal-count write, not from the transfer naturally running out of bytes. For R=16, the nominal
window and what the software reads are IDENTICAL (one sector), so the SAME transfer instead
completes via the chip's natural end-of-buffer path — a completion source PDOS's own code never
explicitly triggers or waits for extra settle time on, unlike the TC path. This is precisely the
condition that used to race ahead of PDOS's own return-to-busy-wait bookkeeping in this emulator
(now fixed, see the CLAUDE.md entry) — genuinely useful to know if you're annotating why `RW_cmd_
sector` gets rewritten constantly but the byte right after `N` never does: **that byte
(`EOT`) being permanently 16 is not an oversight in the disassembly, it's PDOS's own real,
consistent behavior** — the "read one sector, using a wide nominal window and a TC or natural
end-of-buffer to actually stop" trick is systematic, not a one-off.

### 7.4 The completion-redirect handler chain (what `le916h` actually does)

`le916h` (0xE916) — reached when CTC channel 0's IM2 vector (permanently pointed at the trampoline
`finished_irq_dest`/`0x6130`, programmed once by `Set_time_out_comm_irq`, §4) fires while
`(0x6135)-(0x6136)` holds `le916h`'s own address (which `issue_Disk_read_command`, §7.1, patches in
before every real read):

```
le916h:
    pop hl              ; DISCARD whatever PC the CPU's own int-ack pushed (the interrupted point)
    ld hl,le8f7h         ; substitute this instead
    [falls into End_RW_action: push hl; ei; reti]   -- so RETI "returns" to le8f7h, not the
                                                          real interrupted point
```

`le8f7h` (0xE8F7) then does the drive-status recheck (`Get_7_disk_status_bytes`/`sub_e937h`),
eventually reaching `le8feh` (0xE8FE) which calls `sub_e96fh` (0xE96F) and does a plain `ret` —
popping whatever's NEXT on the stack below the discarded interrupted-PC. As long as the interrupt
caught the CPU inside `busy_wait_for_interrupt`'s own idle loop (the normal, intended case — see
7.3), that discarded value is a harmless throwaway loop-continuation point, and the stack beneath
it is exactly right for `le8feh`'s own `ret` to land somewhere sensible. If the interrupt instead
catches the CPU still inside `dsk_in_loop` (the bug case, §7.3), the discarded value is
`dsk_in_loop`'s own LIVE resume point (not a throwaway one) — and `le8feh`'s `ret` ends up popping
`read_disk_bytes`'s own real call-return address instead, landing back at `le7b8h`'s trailing
`jp busy_wait_for_interrupt` and re-entering the wait loop completely fresh, waiting for a second
interrupt that (for that operation) will never come.

**Genuinely worth knowing if you're annotating this: `le916h`'s own `pop`/discard technique is only
safe because PDOS's design assumes the interrupt always arrives while the CPU is idling in
`busy_wait_for_interrupt`, never while it's still inside `dsk_in_loop`.** That assumption held for
every case this investigation traced except the track's-last-sector one (now understood to be a
timing artifact of THIS emulator, not necessarily a real hardware possibility — real silicon's own
completion-to-interrupt propagation delay may be what keeps this assumption safe on a real P2000).

### 7.5 `sub_e8b3h` vs `sub_e7abh`'s `try_read_loop` — two different real callers, two different
redirect targets, don't conflate them

`sub_e7abh`'s own `try_read_loop` (used by a DIFFERENT caller than VOLORG's F_READ path — worth
tracing if you hit it elsewhere) sets `ld hl,Disk_Read_ended_irq` (0xE910) directly before calling
`issue_Disk_read_command`, NOT `le916h`. `Disk_Read_ended_irq`'s own body is simpler —
`pop hl; ld hl,Post_read_irq_code; [same End_RW_action tail]` — landing at `Post_read_irq_code`
(`docs/PDOS_wip.asm`'s own existing label) instead of `le8f7h`. Both ultimately do the same kind of
status recheck, but they are NOT the same code path — if you're annotating a call site, check which
of `sub_e8b3h` (→ `le916h`) or `sub_e7abh`'s `try_read_loop` (→ `Disk_Read_ended_irq`) it actually
routes through before assuming the handler chain.

### 7.6 A second PC-fetch-timing artifact class, generalizing the known RET one

The already-known gotcha (§6, and the CLAUDE.md findings log in several places) is that a `RET`'s
own PC can transiently show the address right after itself before the stack-pop completes. The SAME
thing happens for a 2-byte conditional `JR`: `busy_wait_for_interrupt`'s own `jr nz,le962h`
(`0xE96B`-`0xE96C`) can transiently show PC at its own next-sequential address (`0xE96D`) even when
the branch IS taken (looping back) — not just on the genuine, much rarer fallthrough (the loop
actually exhausting). If you ever watch bare `PC==0xE96D` in your own tooling, you'll see roughly
one hit per loop iteration (tens of thousands for a single busy-wait), not the ~1 genuine
exhaustion. The reliable check is the actual zero-condition the fallthrough is gated on (here,
`BC==0`, the value the preceding `or c` tests) — same discipline as checking real `CALL`/`RET`
bytes for the RET-artifact case.

### 7.7 One more open thread, NOT chased in this pass

Fixing the timing bug above also made a plain `RESET` (default drive, the system/boot disk) stop
producing "Disk I/O error" — previously that was analyzed and accepted as CORRECT, INTENTIONAL
behavior (a system disk's track 1 legitimately has no FCB index to read as one). Whether RESET's
own directory-read attempt was ALSO racing this same timing gap (rather than "correctly" failing
for the reason previously assumed) is now genuinely open again — not investigated further this
pass. If you're annotating the RESET path and this matters to you, it's worth re-checking.

## 8. `PDOS_flags`/`FDOS_flags` (`0xF524`) — bit map, confidence-graded

Same variable, both names seen in different parts of the annotation (`ld hl,PDOS_flags` — `21 24
F5` — is a recurring, easy-to-grep pattern if you want to find every touch point yourself).

| Bit | Meaning | Confidence |
|---|---|---|
| 0 | touched right after `(lf589h)` (the "current FCB pointer" cell, Part E) gets updated, and again around a drive-letter-parsing dispatch (`cp 03ah`/`034h`/`035h` = `:`/`4`/`5`, `sub_e52bh` region, `0xE250`-`0xE289`) | seen, meaning NOT confirmed |
| 1 | **CONFIRMED, directly commented in the source itself:** `set 1,(hl)` = "bank 0 = active" (`sub_e53eh`, `0xE53E`); `res 1,(hl)` = "bank 0 NOT active" (`sub_e563h`, `0xE563`, and `sub_e7dch`'s tail, `0xE7D6`). `Test_FDOS_bit1` (`0xE8EE`) reads it with the identical comment ("bit 1 = bank 0 active"). Bank 1 (PDOS's own resident driver) is the DEFAULT/normal state — bit clear. |
| 2, 3 | a TOGGLE PAIR, `sub_e774h` (`0xE774`, confirmed via its own `;e774` byte comment): alternates between "start scanning at sector 1" (bit2 set, bit3 clear) and "start at sector 9" (bit3 set, bit2 clear) on successive calls. Reads as "which half of the 16-sector directory track did I scan last" — sector 1 = first half, sector 9 = second half (an 8-sector split, matching the JWSDOS on-disk convention noted elsewhere in this project's docs, though this is PDOS's own unrelated in-RAM bookkeeping, not a JWSDOS structure). Confirmed mechanism; the higher-level PURPOSE (why halves specifically) not independently re-derived. |
| 4 | toggled by `sub_e547h` (`0xE547`) ONLY when `(Retry_counter+1) == 0x2Bh`; unconditionally cleared elsewhere (`le0a4h` region, gated on the same `0x2B` comparison). Seen, meaning NOT confirmed — possibly tied to a specific retry-count milestone rather than a general-purpose flag. |
| 5 | not seen touched anywhere in this investigation's own traces | unknown |
| 6 | **CONFIRMED, directly commented:** `set 6,(hl)` = "disk not ready flag" / "disk time out" (`set_bit6_enable_ints` inside `sub_e943h`, `0xE943`; also `irq_code`'s own idle-timeout path, §4). Read back at `0xE08C` (`bit 6,(hl)`, right after a DI+RST-adjacent block near the very start of the dispatcher — worth checking if this is an "was the last op an error" gate on entry). |
| 7 | set alongside `(lf589h)` being (re)pointed at the current FCB (`0xE068`, right after `set 0,(hl)` on the DIFFERENT variable `lf58bh`); read back inside the function-`0x0F` dispatch (`0xE0BD`, right after `cp 00fh`) and by `sub_e56bh` (`0xE56B`). Reads as "an FCB is now the active one" / "FCB just primed" — seen, meaning NOT independently confirmed. |

## 9. Jump tables

1. **The main PDOS dispatcher** (`CPM_entry_point`, `0xE000`) — NOT a real jump table, a long
   `cp nn` / `jr z,label` chain starting around `le0a6h`. Fully covered in §1 above; not repeated
   here.
2. **`sub_f2fdh`'s own internal jump table** (`0xF2FD`) — a REAL table, computed as
   `lf307h + 2*code` (the routing code PDOS itself stores at `0xF578` before calling). Confirmed
   structurally (the computed-jump mechanism itself) AND, for the three codes actually exercised by
   a real `RUN"VOLORG"` attempt, functionally — **live-verified, not just read from the table's own
   bytes** (which the disassembler mis-renders as garbage instructions, since nothing marks that
   region as data):

   | Code | Target | Confirmed real handler |
   |---|---|---|
   | `0x0F` (F_OPEN) | `0xF370` | locates/primes a file's working state (`sub_f2d4h`/`sub_f068h`) |
   | `0x14` (F_READ) | `0xF3A0` | `sub_f137h` — the real CR/RC-checking, physical-read-issuing handler, fully traced in §7 above |
   | `0x1A` (F_DMAOFF) | `0xF3CA` | pure buffer-address priming, no disk I/O |

   The table has a dozen-plus OTHER entries (other function codes `sub_e705h`/`sub_e706h` can
   route through) whose targets are still unmapped — `sub_f068h`, `sub_f09bh`, `sub_f186h`,
   `sub_f0adh`, `sub_f045h`, `sub_ef3dh`, `sub_eeadh`, `sub_ef57h`, `sub_ecd5h`, `sub_f24ch`,
   `sub_f2c2h`, `sub_f2d4h` are all real subroutines visible in the raw disassembly near the table
   (roughly `0xF2FD`-`0xF428`+) but none has been individually read/confirmed by this
   investigation — they were a dead end for the specific question Part C/I chased (§6/§7), not
   necessarily uninteresting in general.

## 10. FDC commands PDOS actually issues (exact wire bytes, cross-referenced against `Upd765`'s own trace)

All via `send_disk_command`/`issue_Disk_read_command`/`issue_Disk_write_command` — opcode first,
then parameter bytes, matching what a live `Upd765.Trace` capture shows landing on the wire:

| Command | Bytes | Issued by |
|---|---|---|
| SPECIFY | `03 60 34` | `Sense_int_and_Specify` |
| RECALIBRATE | `07 01` | `Recalibrate_drive` |
| SEEK | `0F <drive/head> <cylinder>` | `Seek_to_track`/`send_seek_or_recalibrate` |
| READ DATA | `46 <HD/US> <C> <H> <R> 01 10 0E 00` | `issue_Disk_read_command` — **R varies per call (the confirmed interleave sequence), EOT is ALWAYS `0x10`(16), never overwritten anywhere in this disassembly — see §7.3 for why that matters** |
| WRITE DATA | `45 ...` (same shape as READ DATA) | `issue_Disk_write_command` |
| SENSE INTERRUPT STATUS | `08` | `Sense_disk_interrupt_status`, and directly inline in a few places (e.g. `sub_e937h`'s own caller chain) |
| SENSE DRIVE STATUS | `02 04 <drive>` | `check_write_enable`-equivalent (this is JWSDOS's own routine name, per `docs/jwsdos5.0.asm` — PDOS's OWN caller of this exact shape hasn't been separately named in this file) |

## 11. Frequently-used low-level subroutines (quick reference)

Addresses marked **confirmed** matched an independent hex-byte-commented anchor elsewhere in the
file (not just the label-naming convention alone, though that convention itself has now been
cross-checked this way many times over and never once been wrong when checked). Addresses marked
*derived* were computed by counting instruction lengths from a confirmed anchor — internally
consistent, not independently re-verified against a second anchor.

| Address | Name | Purpose |
|---|---|---|
| `0xE72C` *(derived)* | `Seek_to_track` | converts 1-based `RW_cmd_track` to 0-based, issues SEEK |
| `0xE739` *(derived)* | `Recalibrate_drive` | issues RECALIBRATE |
| `0xE73D` **(confirmed** via the `pop hl ;e743` comment 6 bytes later) | `send_seek_or_recalibrate` | patches `(0x6135)=EI_RETI` (the no-op redirect — SEEK/RECALIBRATE only need to unblock a `HALT`, no special continuation), sends the command, `HALT`s, then `Sense_disk_interrupt_status` |
| `0xE774` **(confirmed)** | `sub_e774h` | the bit2/bit3 half-track-scan-position toggle (§8), sets `RW_cmd_sector` to 1 or 9 accordingly |
| `0xE7AB` (per this file's own §1 usage) | `sub_e7abh`/`try_read_loop` | a DIFFERENT real-read caller than VOLORG's own F_READ path — uses `Disk_Read_ended_irq`/`Post_read_irq_code`, NOT `le916h`/`le8f7h` (§7.5 — don't conflate the two chains) |
| `0xE8B3` **(confirmed** via naming + surrounding hex comments) | `sub_e8b3h` | THE real physical-read issuer for VOLORG's own F_READ path; hardcodes `E=1` (one sector, always) regardless of the command's own nominal EOT window |
| `0xE8C0` **(confirmed** — `ld hl,le916h` bytes `21 16 E9` match the target's own address directly) | `sub_e8c0h` | sets the read-completion redirect target to `le916h`, falls into `issue_Disk_read_command` |
| `0xE8C3` **(confirmed** via backward byte-count from the `;e8dd` anchor) | `issue_Disk_read_command`/`issue_Disk_write_command` | THE routine earlier prompts/entries call "sub_e8c3h" — patches `(0x6135)-(0x6136)` (the actual JP-target operand of the fixed ISR trampoline at `0x6130`), arms CTC ch1 (immediate-trigger, unrelated to the real completion signal — see §7.3/CLAUDE.md Part B's own "red herring" note), sends the command |
| `0xE8EE` *(derived, consistent with the confirmed anchors either side)* | `Test_FDOS_bit1` | returns NZ if bit 1 (bank 0 active) is set |
| `0xE8F7` **(confirmed** via naming) | `le8f7h` | post-redirect drive-status recheck (§7.4) — reached from `le916h`'s own RETI, NOT a normal call target (watch for the SAME kind of RET/JR-fetch artifact noted in §7.6 if you trace bare `PC==0xE8F7`, since `Test_FDOS_bit1`'s own `ret` at `0xE8F6` sits immediately before it) |
| `0xE916` **(confirmed** three independent ways — naming, embedded operand bytes, forward byte-count from `0xE8F7`) | `le916h` | the real completion-redirect handler for `sub_e8b3h`'s own reads — `pop`s the interrupted PC, substitutes `le8f7h` (§7.4) |
| `0xE91A` *(derived)* | `End_RW_action` | shared tail for every redirect variant — writes DSKCTRL with TC set, resets CTC ch1, pushes the real continuation, `EI`/`RETI` |
| `0xE95F` **(confirmed** via naming, matches the project's own "le95fh/le962h" citation elsewhere) | `busy_wait_for_interrupt` | the hardcoded 65536-iteration idle loop a real operation's interrupt is SUPPOSED to redirect out of early |
| `0xE96F` **(confirmed** via naming) | `sub_e96fh` | re-checks drive match (`sub_e937h`); returns cleanly if matched, otherwise falls into `channel_time_out` |
| `0xE978` **(confirmed** via naming, matches Part C's own citation) | `channel_time_out` | calls `sub_e943h` — reached EITHER from `busy_wait_for_interrupt`'s own natural exhaustion OR from `sub_e96fh`'s own failure fallthrough (§7.6's own note: a bare call-site trace of `sub_e943h` cannot tell these apart, both land here) |
| `0xE943` **(confirmed** via naming) | `sub_e943h` | THE "report Disk I/O error" routine — full body in §3 above |

## Variable names seen so far (yours may differ — listed for cross-reference)

`PDOS_flags`/`FDOS_flags` = `0xF524` (same variable, both names seen in different parts of the
annotation) · `Retry_counter` = `0xF51C` · `Disk_error_code`/`lf51eh` = `0xF51E` (the byte the
BASIC wrapper reads at `0x60BB`) · `RW_cmd_sector` = `0xF53B` · `RW_cmd_track` = `0xF539` ·
`DISK_RW_Command` = the FDC command-block staging area · `dir_buffer` = start of the in-RAM FCB
scratch buffer.

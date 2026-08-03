# Notes for PDOS_wip.asm annotation

Everything below was learned live-tracing the real emulator (boot → `SYSTEM B` → `RUN"VOLORG"`,
`FILES`, `RESET`) cross-referenced against the disassembly, during the "Disk I/O error"
investigation (project CLAUDE.md's 2026-08-02/03 findings-log entries, Parts A/B/C). It's meant
to save you re-deriving structure you'll hit again — not a substitute for your own read of the
actual bytes, and not everything here has a byte-exact address confirmed (flagged where it's an
inference from behavior rather than a confirmed instruction).

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

## 6. Open question this file doesn't answer (Part C, narrowed but still under investigation)

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

## Variable names seen so far (yours may differ — listed for cross-reference)

`PDOS_flags`/`FDOS_flags` = `0xF524` (same variable, both names seen in different parts of the
annotation) · `Retry_counter` = `0xF51C` · `Disk_error_code`/`lf51eh` = `0xF51E` (the byte the
BASIC wrapper reads at `0x60BB`) · `RW_cmd_sector` = `0xF53B` · `RW_cmd_track` = `0xF539` ·
`DISK_RW_Command` = the FDC command-block staging area · `dir_buffer` = start of the in-RAM FCB
scratch buffer.

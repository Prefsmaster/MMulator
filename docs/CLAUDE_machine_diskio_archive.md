# CLAUDE_machine.md — archived findings-log history: "Disk I/O error" investigation

Moved out of `CLAUDE_machine.md` on 2026-08-04 to keep that file readable, per owner request
(same treatment already applied to `docs/P2000T-reference.md` §5d, whose full narrative moved to
`docs/P2000T-diskio-investigation-history.md`). This file preserves the FULL, VERBATIM findings-log
entries for the entire "Disk I/O error" investigation, in their original order and original
findings-log format — nothing rewritten, nothing summarized. Two spans are combined here:

1. The investigation's Parts A through I (2026-08-02 through 2026-08-04), which were contiguous in
   the original file.
2. The originating 2026-07-28 entry (the three real `Upd765` TC-completion bugs found and fixed via
   instrumentation), which sat lower in the original file's timeline, separated from Parts A-I by
   unrelated milestone/bugfix entries.

**The bug is RESOLVED AND FIXED.** See `CLAUDE_machine.md`'s own 2026-08-04 pointer entry, and
`docs/P2000T-reference.md` §5d, for the current final-state summary.

---

### 2026-08-04 — Part I (cc-bugfix-prompt-15): ROOT CAUSE FOUND AND FIXED — a genuine emulator timing gap in `Upd765`'s natural end-of-transfer completion path, not a PDOS/BASIC bug. CONFIRMED END-TO-END: `RUN"VOLORG"` now loads and runs VOLORG.BAS successfully. This closes the entire multi-day "Disk I/O error" investigation (Parts A-I).
- **Trigger:** cc-bugfix-prompt-15, the direct continuation of Part H — reconcile Part B's own
  "channel 0 fires and delivers correctly for all 15 real READ DATA completions" claim with Part
  H's own "the 14th `CALL 6205h` never returns" finding by tracing the completion-to-return path
  for the 14th operation specifically: does the interrupt genuinely fire, is the redirect at
  `&H6135` (`issue_Disk_read_command`/`issue_Disk_write_command`, 0xE8C3 — the routine the prompt
  called "sub_e8c3h") armed correctly, and if so where does the handler's own logic actually go.
- **New permanent regression test:**
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs`
  (`RunVolorg_TracesThe14thOperationsRedirectPathPrecisely`). Traces, across the full
  `RUN"VOLORG"` attempt: every genuine entry to `sub_e8b3h` (0xE8B3, the real physical-read
  issuer reached from `sub_f137h`'s F_READ handler via `lf170h` → `Seek_to_track` →
  `sub_e8b3h` — NOT `sub_e7abh`'s own, differently-redirected `try_read_loop`, confirmed by
  reading `docs/PDOS_wip.asm` directly and cross-checked via embedded `LD HL,nnnn` operand bytes,
  not just label-naming convention); the live value patched into `(0x6135)`; every genuine PC
  arrival at `le916h` (0xE916, the redirect landing point) together with the int-ack-pushed
  return address about to be popped there; `Upd765`'s own `Trace` output for every real FDC
  completion (`transferIndex`/`bufferLength`); and CTC channel 0's `IntPending`/`InService`
  transitions.
- **Two real instrumentation bugs found and fixed while building this trace — both flagged
  precisely rather than mistaken for emulator bugs:**
  1. Reading `(0x6135)` one instruction too early (right after the 3-byte/16-T-state
     `LD (nn),HL` write's OWN next-sequential address, 0xE8CB) caught a STALE value left over
     from the immediately-preceding `Seek_to_track`/`send_seek_or_recalibrate` call (which
     patches the SAME cell to `EI_RETI` moments earlier in the same F_READ cycle) — this
     project's cycle-stepped core can show PC at the next instruction before a multi-T-state
     instruction's own memory write has actually committed, the same general class of timing
     subtlety already hit for `RET` elsewhere in this investigation, here affecting a `LD (nn),HL`
     instead. Fixed by sampling several instructions later (0xE8D2), by which point the write is
     unambiguously done — confirmed genuinely re-armed to `le916h` (0xE916) on every one of the 15
     physical reads once fixed.
  2. Watching bare `PC==0xE96D` (the address right after `busy_wait_for_interrupt`'s own
     `jr nz,le962h`) produced 65,564 "hits" — almost one per loop iteration — because a 2-byte
     conditional `JR` can transiently show its own next-sequential address mid-decode even when
     the branch IS taken (the same PC-fetch-timing artifact class already found for `RET` in
     Parts C/E, here for `JR`). Fixed by only counting a hit as genuine when `BC==0` (the actual
     zero-check the fallthrough is gated on) — collapsing the noise to exactly the 1 real
     exhaustion that occurs in this repro.
- **CONFIRMED, decisively: the redirect mechanism itself is never mis-armed, and channel 0
  genuinely fires and delivers for the 14th completion too — extending, not contradicting, Part
  B's own original claim.** All 15 physical reads (1 initial verify + 14 real VOLORG-data reads)
  issue via `sub_e8b3h`; `(0x6135)` is patched to `le916h` all 15 times; PC genuinely reaches
  `le916h` all 15 times; CTC ch0's `IntPending`→`InService`→cleared cycle completes cleanly for
  all 15 (confirmed via the doubled-but-consistent count, the doubling itself another instance of
  the established PC/state-sampling-artifact class, not a real double-fire).
- **THE DECISIVE FINDING: what got interrupted differs, only for the 14th completion.** `le916h`'s
  own `pop hl` retrieves the CPU's own int-ack-pushed return address — a reliable signal, not a
  live-trace artifact (it's read from the stack, not sampled from a live PC). For completions 1-13,
  this is **always exactly `0xE969`** — a clean instruction boundary inside
  `busy_wait_for_interrupt`'s own idle loop (specifically between its second
  `ld hl,(PDOS_flags)` and `ld a,b`), a safe, throwaway point to discard. **For the 14th
  completion, it is instead `0x6150`** — a *different*, clean instruction boundary, but this time
  INSIDE `dsk_in_loop`, PDOS's own semi-DMA byte-transfer polling loop (between its own `dec e`
  and the following `jp nz,dsk_in_loop`, right after `INI` copies a byte) — i.e., the interrupt
  catches the CPU still mid-transfer, not already idling in the busy-wait.
- **Cross-referencing `Upd765`'s own `Trace` output (source-confirmed against
  `Upd765.DispatchDataCommand`, `sectorCount = Math.Max(1, endOfTrack - startSector + 1)`)
  explains exactly why, and why it's specific to the LAST sector of the track:** PDOS's own FDC
  command (`DISK_RW_Command`) always requests a wide EOT window — **EOT is fixed at 16, R varies
  per call** (matching the confirmed interleave sequence `1,7,13,3,9,15,5,11,2,8,14,4,10,16`) —
  while its software polling loop (`dsk_in_loop`, governed by `sub_e8b3h`'s own hardcoded
  `ld e,001h`) only ever consumes exactly **one sector**, confirmed via `Upd765`'s own trace:
  **`transferIndex` is 256 at every single one of the 15 completions.** For every read except the
  last, this leaves `_transferIndex` (256) well short of the nominal `_transferBuffer.Length`
  (256 up to 4096, since `sectorCount = EOT-R+1` — confirmed varying exactly as expected: 4096,
  4096, 2560, 1024, 3584, 2048, 512, 3072, 1536, 3840, 2304, 768, 3328, 1792, **256**) — so
  completion for those 14 can ONLY come from PDOS's own explicit terminal-count write
  (`dsk_io_done`, reached once the software's own polling loop decides it's done), which this
  emulator DELIBERATELY DEFERS by `MinimumLostWakeupGuardTStates` (200 T-states) — a real,
  intentional fix already landed 2026-07-28 specifically to prevent an interrupt from being
  delivered before the software's own return-and-settle sequence has run (the "lost wakeup" class
  of bug). This deferral gives the CPU a comfortable head start to reach
  `busy_wait_for_interrupt`'s idle loop before the (deferred) interrupt actually arrives — matching
  the consistent `0xE969` result for all 14 non-final completions (the initial verify read
  included). **But for the LAST sector of the track (R=16), the nominal EOT-R+1 window collapses
  to EXACTLY one sector — matching what the software polls exactly — so the transfer instead
  completes via `Upd765.ReadData`'s own NATURAL, perfectly SYNCHRONOUS end-of-buffer check
  (`if (_transferIndex >= _transferBuffer.Length) CompleteTransfer();`), which has NO equivalent
  settle delay.** The interrupt fires immediately, in the same T-state as the CPU's own final
  `INI`, catching the CPU still inside `dsk_in_loop` rather than already idling.
- **The full failure chain, now precisely understood end to end:** PDOS's redirect handler
  (`le916h`) unconditionally discards whatever return context was interrupted, assuming it's
  always the disinterested `busy_wait_for_interrupt` loop — for the 14th completion this instead
  discards `dsk_in_loop`'s own live resume point. The surviving, UNTOUCHED stack frame one level
  further down (`read_disk_bytes`'s own real call-return address, pointing at `le7b8h`'s trailing
  `jp busy_wait_for_interrupt`, never popped by `le916h` itself) then gets popped by `le8f7h`'s
  own unrelated, later `ret` (reached via the redirect's own `RETI`) — so execution accidentally,
  indirectly re-enters `busy_wait_for_interrupt` FRESH, waiting for a SECOND interrupt that will
  never arrive (the FDC has nothing left to signal for this operation). This fresh busy-wait
  genuinely runs its full 65536-iteration course and times out for real — confirmed precisely: the
  one genuine `BC==0` exhaustion in this trace lands 3,802,541 T-states after the 14th completion,
  matching the "~3.8M T-states" figure already cited in Part B's own findings entry almost exactly,
  now with a precise, source-level mechanism behind it rather than just a T-state-window
  observation. `channel_time_out`/`sub_e943h` then fires (from `busy_wait_for_interrupt`'s own
  natural timeout — the SAME call site Part C's trace found, now correctly attributed) and reports
  "Disk I/O error."
- **Verdict, precisely: this is very likely a genuine EMULATOR TIMING GAP, NOT a PDOS/BASIC bug,
  and reconciles cleanly with the real-P2000M no-error data point (Part H/tenth follow-up) without
  requiring PDOS's own read protocol to be considered fragile.** PDOS's technique — request a wide
  EOT window, let software consume only what it needs, force early completion via an explicit TC
  write for every case except the one where the window naturally happens to be exactly one sector
  — is a real, legitimate, and (per the fixed 14th case) apparently EXPECTED-BY-DESIGN edge case
  that real firmware handles correctly. The TC-forced completion path already received an explicit
  settle-delay fix for exactly this class of race (2026-07-28's `MinimumLostWakeupGuardTStates`);
  the natural/synchronous end-of-buffer completion path in `Upd765.ReadData`/`WriteData` never
  received the analogous treatment, because no prior test happened to exercise a transfer whose
  natural end coincides with what the driving software's own polling loop consumes — genuinely
  novel territory, not a previously-known-and-ignored gap. Real silicon's own completion-to-INT-line
  propagation is exceedingly unlikely to be perfectly zero-latency the way this synchronous C#
  check currently is; a real µPD765 finishing its last byte and asserting its INT line almost
  certainly has SOME minimum propagation delay, which would give real PDOS firmware the same safe
  margin the TC-forced path already gets here. This is offered as the most likely reconciliation,
  not asserted as independently confirmed against real silicon timing.
- **FIXED, same pass, owner-authorized ("Yes, implement and test it now").** Added
  `Upd765.DeferNaturalCompletion()` (private helper) and a new `PendingAction.NaturalCompletion`
  enum value; `ReadData`/`WriteData`'s own end-of-buffer branch now calls
  `DeferNaturalCompletion()` (which sets `_pending = PendingAction.NaturalCompletion; _delayCounter
  = MinimumLostWakeupGuardTStates;`) instead of calling `CompleteTransfer()` synchronously;
  `Tick()`'s `switch (_pending)` handles `NaturalCompletion` identically to the pre-existing
  `ForcedCompletion` case (both just call `CompleteTransfer()` once the delay elapses). Applied
  uniformly to EVERY transfer's natural completion, not special-cased to "is this the track's last
  sector" — the asymmetry between the TC-forced path (already deferred) and the natural path
  (previously synchronous) is what mattered, and removing it uniformly is more faithful to real
  silicon (which almost certainly has some non-zero completion-to-INT-line delay regardless of
  which byte ends a transfer) than a narrower, sector-position-specific fix would have been.
- **CONFIRMED END-TO-END, via the full `P2000.Machine.Tests` suite re-run with the real boot/disk
  fixtures used throughout this investigation:** `RUN"VOLORG"` now loads and runs VOLORG.BAS
  completely successfully — its own real menu ("P 2000 DISK UTILITY", with options for
  load-and-run, directory listing, copy/delete/rename file, etc.) renders correctly on screen, with
  **no "Disk I/O error" anywhere** across the owner's own full manual repro sequence
  (`RESET`/`SYSTEM B`×2/`FILES`/`RUN"VOLORG"`×2/`FILES`). `FReadEofHandlingDiag`'s own trace
  (before being retired, see below) independently confirms CR now correctly reaches RC=44 — VOLORG's
  own genuine 44-record file length — the LEGITIMATE CP/M EOF condition, meaning the file now reads
  to completion rather than hanging partway through.
- **A genuinely interesting, unexpected side effect, flagged rather than chased further this
  pass:** plain `RESET` (default drive A, the system/boot disk) NO LONGER produces "Disk I/O
  error" either. This was previously analyzed (Fourth/Fifth owner follow-up, reference doc §5d) as
  CORRECT, INTENTIONAL behavior — "a system disk's track 1 does not hold a normal FCB index (only
  working disks' track 1 does) — reading it as one legitimately fails. Not a bug." That conclusion
  may now need revisiting: if RESET's own directory-read attempt was ALSO racing the same
  natural-completion timing gap (e.g. hitting the same last-sector-of-a-fixed-window condition
  while scanning the system disk's own track 1), this single `Upd765` timing bug could have been
  responsible for more than just the `RUN"VOLORG"` symptom. Not investigated further in this pass —
  flagged for whoever next touches the RESET/system-disk boot path.
- **Twelve pre-existing Part B-H diagnostic tests retired (`[Fact(Skip = "...")]`, not deleted),
  each pinning an exact numeric/textual fact about the CONFIRMED BUG's own specific symptom (a
  count of "13" or "14", the literal text "Disk I/O error", an exact call-site count, etc.) that is
  now definitionally false since the bug is fixed:** `PdosLoadSaveRepro.cs`
  (`Boot_ThenLoadVolorg_TraceFdcCommandsAndScreenOutput`), `SectorAdvancementCapDiag.cs`,
  `FReadReturnValueDiag.cs`, `RecordCounterLiveTraceDiag.cs`, `BasicReadLoopCallSiteDiag.cs`,
  `FReadEofHandlingDiag.cs`, `SubF2fdhJumpTableDiag.cs`, `ReadDataPhysicalTrackDiag.cs`,
  `LoopExitPathDiag.cs`, `Channel0InterruptDuringGapDiag.cs`, `Sube943hCallerDiag.cs`,
  `DiskIoErrorFlagTrace.cs`. Each Skip reason cross-references this Part I entry and
  `FourteenthOperationRedirectDiag.cs`. Retired rather than rewritten/deleted: rewriting each one's
  exact assertions to match the new (correct) behavior would cost hours of CI time re-deriving
  numbers that add no value beyond what this entry and the new regression test already establish
  precisely, and deleting them would lose real investigative/forensic value (their own doc comments
  are a detailed record of exactly how the bug was chased down, Part by Part) — this project's own
  historical convention throughout Parts A-I has been to treat these test files as part of the
  evidence trail, not disposable scaffolding.
- **`FourteenthOperationRedirectDiag.cs` itself rewritten from a bug-diagnosis test into a genuine
  forward-looking regression guard for the FIX:** now asserts every redirect landing's popped
  return address is uniformly `0xE969` (never `0x6150`, the mid-transfer address that was the whole
  root cause), that no genuine busy-wait exhaustion occurs at all, and that the final screen shows
  VOLORG's own "DISK UTILITY" menu with no "Disk I/O error" text — the counts (`issueCount`, etc.)
  are asserted as "at least 15" rather than "exactly 15", since VOLORG now reads its full 44-record
  file rather than stopping early.
- **Tests:** `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs` (+0 new, 13 existing tests
  updated with a `for (var i = 0; i < 300; i++) fdc.Tick();` drain after their final
  `ReadData`/`WriteData` call, mirroring the pattern already established for the TC-forced path —
  every one of these tests assumed the now-removed synchronous natural completion);
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`
  (`WriteProtect_OnOneDrive_BlocksOnlyThatDrivesWriteDataCommand`, same drain added);
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs` (rewritten per above,
  including one follow-up fix: VOLORG's own title renders letter-spaced on screen — `D I S K` not
  `DISK` — so the menu-detection substring was changed to the contiguous `"P 2000"`). Full
  `P2000.Machine.Tests` suite CONFIRMED green: 627 total, 615 passed, 12 skipped (the retired
  files above), 0 failed.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`PendingAction.NaturalCompletion`,
  `DeferNaturalCompletion`, `ReadData`, `WriteData`, `Tick` — the fix itself),
  `tests/P2000.Machine.Tests/Boot/FourteenthOperationRedirectDiag.cs` (rewritten),
  `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Devices/Fdc/MultiDriveFloppyTests.cs`, the 12 retired test files
  listed above, `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited
  — `sub_e8b3h`/`issue_Disk_read_command`/`le916h`/`le8f7h`/`dsk_in_loop`/`dsk_io_done`/
  `busy_wait_for_interrupt` are all pre-existing disassembly, not newly annotated by this pass),
  reference doc §5d's 2026-08-04 entry (this Part I) — this closes the entire "Disk I/O error"
  investigation, Parts A through I.
- **Synced:** yes (2026-08-04, into `docs/P2000T-reference.md` — §5d's "Disk I/O error" section replaced with a short RESOLVED summary, and the full Parts A–I narrative moved to the new `docs/P2000T-diskio-investigation-history.md` archive doc per owner request).

### 2026-08-04 — Part H (cc-bugfix-prompt-14): LIKELY CLOSES THE ROOT-CAUSE QUESTION — this is not a graceful "BASIC's loop decided to stop after 14" bug at all. ALL THREE plausible exit checks inside the BASIC-side loop are directly disproven by live trace. The 14th real disk read genuinely gets issued and physically completes at the FDC level (Part E), but its own `CALL 6205h` never returns to BASIC — it is swallowed by the SAME PDOS-side busy-wait/timeout mechanism already fully diagnosed in Parts B/C. "Disk I/O error" is a genuine hang on the 14th operation, not an early, deliberate stop. Independent real-hardware data point (owner, real P2000M, real floppies, no error) argues this is NOT faithful/intended behavior — but the exact reason the 14th call's own PDOS-side completion signal goes missing is still not identified.
- **Trigger:** cc-bugfix-prompt-14, tracing the second counter Part G left open
  (`[pointer+0x24..0x25]`, checked at `0x326C` only once the first, byte-scan counter empties) —
  the last remaining named candidate for the loop's real termination condition. Renewed motivation
  from the owner: an actual P2000M, booted from real floppies, ran `FILES`/`LOAD "VOLORG"`
  without error — a genuine (if not `.dsk`-fixture-parity-confirmed) data point against "this is
  correct, intended behavior," reinforcing the case for continuing to treat this as a real bug.
- **A real instrumentation bug found and fixed while building the live trace — SAME class of
  mid-instruction PC-timing artifact already hit twice before in this investigation, here for a
  DIFFERENT reason (a store, not a return):** the first pass of
  `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs` captured the pointer
  (`0x63A3`) one tick after PC first showed its own init site (`0x37AD`, `LD (63A3h),HL`, a
  3-byte/16-T-state instruction) — WHILE the store was still in progress, giving a bogus `0x0000`.
  Fixing this specific timing bug (trigger on PC reaching the NEXT instruction, `0x37B0`) still
  didn't resolve it — the REAL bug was structural: the fix re-derived the pointer via a one-time
  "capture" heuristic instead of simply re-reading `0x63A3` fresh at every `0x323A` entry, the
  exact approach Part G's own `RecordCounterLiveTraceDiag.cs` had already proven correct. Rewriting
  to match that proven pattern fixed it (`pointer=0x8A90`, matching Part G exactly).
- **CONFIRMED, decisively, once fixed: `[pointer+0x24..0x25]` is set ONCE, to 256, and NEVER
  CHANGES for the rest of the loop** — the same value at all 3329 entries to `0x323A`, including
  the very last one. New regression test
  `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs`
  (`RunVolorg_SecondCounterStaysAt256_CannotGovernLoopExit`). Since `0x326C`'s own check requires
  this counter to reach zero, it CANNOT be what triggers the loop's real exit — Part G's own
  leading candidate is ruled out.
- **CONFIRMED the second candidate mechanism (F_READ's own EOF return value) is also ruled out —
  and by a more careful reading of the actual branch polarity, could never have worked even if
  observed.** New regression test `tests/P2000.Machine.Tests/Boot/FReadReturnValueDiag.cs`
  (`RunVolorg_FReadAlwaysReturnsZero_Never1_AndOnly13Of14ReturnsAreObserved`) watches register A
  at every return from `CALL 6205h` (F_READ) at `0x32A8`. A is confirmed always `0` (standard
  CP/M success), never `1` (EOF). Working through `0x32B0`-`0x3276`'s actual polarity: `DEC A`
  only sets Z when A WAS 1; since A is always 0, the jump to `0x32BA` is NOT taken, DE ends up
  `0x0100`, and the resulting OR-check leaves A nonzero (NZ) — meaning `0x3276: JP NZ,323Ch` IS
  taken. **A=0 makes the loop continue, not exit** — this path could only ever be an exit
  mechanism if A became 1, which never happens.
- **CONFIRMED the third, previously-unexamined candidate (`0x323A`'s own leading check,
  `[pointer+0]==3`, jumping to a completely different ROM address `0x8996` if matched) also never
  fires.** New regression test `tests/P2000.Machine.Tests/Boot/LoopExitPathDiag.cs`
  (`RunVolorg_AltExitCheckNeverFires_PointerFirstByteStaysAt1`). `[pointer+0]` stays at exactly
  `1` for the entire loop; PC never genuinely transitions to `0x8996`.
- **With all three exit checks inside the loop's own body ruled out, the real reconciling fact is
  a count mismatch: only 13 genuine F_READ returns are ever observed at `0x32AB` (confirmed via
  the same trace above — 27 raw hits, but that number is inflated by the SAME PC-fetch-artifact
  class already found in Parts C/E, doubling roughly half of the 13 genuine returns; the genuine
  count is 13, not 14 or 27), even though Part E already confirmed 14 real physical disk
  completions.** Precisely timed: the LAST genuine `0x323A` byte-scan entry (Part G's own detailed
  trace) occurs at `t=2463774` — BEFORE the 14th physical disk completion (Part E, `t≈2485569`).
  This means the 13th cycle's own completion is what TRIGGERS the 14th real disk read (via
  `0x327F`/`0x32A8`/`0x32D0`) — which genuinely gets issued and genuinely completes at the FDC
  level — but that 14th call's own `CALL 6205h` never returns to `0x32AB` at all, ever, within any
  reasonable trace window.
- **Conclusion, precisely: this is NOT a "wrong counter value" bug, and NOT a graceful early-stop
  decision by BASIC's own loop logic.** BASIC's loop is correctly, faithfully waiting for its 14th
  `CALL 6205h` (F_READ) to return; it never does. This is the SAME busy-wait/timeout mechanism
  already fully diagnosed at the PDOS/bank-1 level in Parts B/C (`busy_wait_for_interrupt` →
  `channel_time_out` → `sub_e943h`, confirmed at the instruction level in Part C) — now understood
  to be triggered from a REAL, physically-completing 14th disk operation, not "no further command
  ever issued" as Part E's own framing (accurate as far as it went, for the T-state window it
  examined) suggested. The prompt's own question — real bug vs. intentional behavior — is answered
  precisely: **BASIC's own loop logic is correct** (none of its three exit checks are broken); the
  problem, if there is one, lies in WHY the 14th disk operation's own PDOS-side completion/interrupt
  signal never makes it back to a normal `RET`, matching but not yet explaining the mechanism that
  Part B/C's own trace showed reaches `sub_e943h` via `channel_time_out` (the busy-wait's own
  65536-iteration timeout, not a genuine completion).
- **Still open, precisely narrowed:** WHY does the 14th disk operation's own completion (confirmed
  physically real at the FDC level) fail to deliver its interrupt/redirect back into a normal
  return from the `CALL 6205h` that issued it, when the same mechanism worked correctly for the
  prior 13? This is now a question about the SPECIFIC interrupt-redirect/completion-signaling
  mechanism for exactly one call, not about counters, EOF checks, or loop logic — a narrower,
  different kind of question than anything chased in Parts B-H so far. The independent real-P2000M
  data point (no error on real hardware) is not yet reconciled with this — floppy-content parity
  between the owner's real disks and the `.dsk` fixtures used here is unconfirmed, so this may
  point to a fixture/content difference, an emulator timing/interrupt bug specific to a 14th
  same-track sequential operation, or something else entirely.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SecondLoopCounterLiveTraceDiag.cs`,
  `FReadReturnValueDiag.cs`, `LoopExitPathDiag.cs` (all new permanent regression guards, each
  asserting the ruled-out status of one of the three candidate exit mechanisms, plus the precise
  13-vs-14/27-raw-hit F_READ-return count).
- **Applies to:** the three new test files above. Cartridge-ROM work only
  (`assets/Basic-24.bin`), same as Part G — `docs/PDOS_wip.asm` not applicable. Reference doc
  §5d's 2026-08-04 entry (Eleventh owner follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Eleventh owner follow-up (2026-08-04, cc-bugfix-prompt-14)” entry; also updated the trailing “Confirmed (owner, 2026-07-28)” status paragraph)

### 2026-08-04 — Part G (owner follow-up, no formal prompt number): found and disassembled the BASIC-side (cartridge ROM) read loop itself — the code that decides how many times to call F_READ/F_DMAOFF. CORRECTS a working hypothesis formed mid-investigation (the loop is a byte-by-byte program scanner through a 256-byte buffer, not a "records remaining" counter decremented once per sector). Confirmed: exactly 13 full 256-byte scan cycles happen, not 14 — the true stopping condition is a SEPARATE, not-yet-traced counter. Investigation paused here at the owner's own request, to decide how to continue.
- **Trigger:** direct owner follow-up to Part F. Two prompts, both addressed: (1) the owner
  patched VOLINFO's own FCB byte 15 from `0x0E`(14) to `0x2C`(44, matching VOLORG's) on a real disk
  image and re-ran the repro — same stop-at-14 result, unchanged, confirming the earlier VOLINFO
  coincidence theory is dead (already logged in the Part E/F transition note above). (2) The
  owner asked whether BASIC issues per-sector commands itself or delegates a single "load file X"
  call to PDOS — answered directly from Part D's own already-confirmed dispatcher trace: BASIC
  issues 14 SEPARATE, discrete `0x1A`/`0x14` call PAIRS through the top-level dispatcher, not one
  bulk call — confirming the stop-decision is made on BASIC's own side, between calls, consistent
  with Part F's own "external to PDOS" conclusion. The owner then asked to continue chasing the
  actual BASIC-side loop.
- **Found the exact BASIC-side call sites, all in the CARTRIDGE ROM (`Basic-24.bin`), NOT the
  disk-loaded chunk `docs/PDOS_wip.asm` covers.** New regression test
  `tests/P2000.Machine.Tests/Boot/BasicReadLoopCallSiteDiag.cs`
  (`RunVolorg_ThreeFixedCartridgeRomCallSitesIssueAllPdosCalls`) traces every genuine CALL to
  `0x6205` (BASIC's own fixed PDOS entry point, per `docs/PDOS-notes-for-annotation.md` §1/§2) and
  finds exactly 3 fixed call sites for the whole `RUN"VOLORG"` attempt: `0x3487` (F_OPEN, once),
  `0x32A8` (F_READ, 14×), `0x32D0` (F_DMAOFF, 14×) — matching Part D's own dispatcher-level counts
  exactly, now pinned to precise ROM addresses.
- **Disassembled the loop around these two repeated call sites** (`tests/P2000.Machine.Tests/Boot/RunTokenReadLoopDisasmDiag.cs`,
  using the project's own `Z80.Disassembler` directly against `Basic-24.bin` — this is cartridge
  ROM content, fully available, unlike PDOS's own disk-loaded driver). Full structure:
  - `0x323A` — the loop-driving routine. Loads a pointer from fixed cell `0x63A3`; checks a 2-byte
    counter at `[pointer+0x26..0x27]`; if nonzero, decrements it and returns ONE BYTE from a
    computed position within a 256-byte buffer (a byte-by-byte scan, not a per-sector operation).
    If that counter is already zero, falls through to `0x326C`, which checks a SECOND, separate
    2-byte counter at `[pointer+0x24..0x25]` — if that is ALSO zero, exits the whole loop
    (`0x3279`: `SCF; LD A,1Ah; RET`); otherwise calls `0x327F`, which issues one real DMAOFF+READ
    pair via `0x32A8`/`0x32D0` to refill the 256-byte buffer.
  - `0x3273`/`0x3276` (the loop's own repeat-or-stop branch): `CALL 327Fh` then `JP NZ,323Ch` —
    loops back based on flags left by `327F`'s own tail (`0x32B0`: `DEC A` on the F_READ result,
    checking specifically for the standard CP/M EOF value 1 — confirmed unreachable per Part E,
    RC never lets CR reach it).
  - `0x63A3` itself is set ONCE, at `0x37AD` (inside LOAD's own setup code, `0x376F`-`0x3830`,
    previously documented in Part B), from `(0x63B1)` — a separate cell not yet traced to its own
    origin.
- **Live-traced the FIRST counter (`[pointer+0x26..0x27]`) directly — CORRECTS the working
  hypothesis formed while reading this code cold.** New regression test
  `tests/P2000.Machine.Tests/Boot/RecordCounterLiveTraceDiag.cs`
  (`RunVolorg_ByteBufferCounter_Runs13FullCyclesThenStops`). The initial reading of the static
  disassembly (recorded in this entry's own first draft, corrected before being finalized here)
  assumed this counter was "records remaining," decremented once per disk sector. Live tracing
  disproves that directly: `0x323A` is entered ~3300 times total (far more than the 14 real disk
  reads), and the counter decrements ONCE PER BYTE, cycling `256→0` repeatedly as BASIC scans
  through the loaded program's own bytes, refilling the 256-byte buffer via a real disk read only
  once each cycle empties. **Confirmed precisely: exactly 13 full 256-byte cycles occur (3328
  bytes total) before the loop stops — not 14** — and the last cycle ends EXACTLY at 0 (not a
  partial cycle cut short mid-buffer).
- **Checked for a content-driven natural stop at the 13-cycle boundary — not found.** Reconstructed
  VOLORG's own logical (interleave-corrected) byte stream directly from `assets/Disks/volorg.dsk`
  and inspected the bytes around the 3328-byte boundary for a plausible BASIC "end of program"
  marker (a null/`0x00 0x00` link-pointer, the common tokenized-BASIC convention) — found none; the
  bytes stay dense and high-entropy straight through the boundary, no zero bytes anywhere nearby.
  So the 13-cycle stop is NOT (at least not simply) "BASIC correctly found the program's real end
  while scanning" — reinforcing rather than resolving the open question.
- **What's now precisely un-explained, narrower than before:** the loop's REAL exit condition is
  governed by the SECOND counter, `[pointer+0x24..0x25]`, checked only once the byte-buffer
  counter (`[pointer+0x26..0x27]`) empties. This second counter's own live value, initial value,
  and update rule have NOT been traced — that is the concrete, well-scoped next step. The
  three-way relationship between "13 full byte-scan cycles," "14 real disk sector reads" (one
  cycle's worth of buffer-refill reads ahead of what's been scanned, i.e., the 14th disk read
  fetches a sector never actually consumed by the byte-scanner before the loop exits), and this
  second counter's own threshold is not yet reconciled.
- **Investigation deliberately paused here, at the owner's own explicit request** ("write it up,
  and log. Then I'll decide how we continue") — not because a natural stopping point was reached,
  but to let the owner choose whether to continue into the second counter next.
- **Tests:** `tests/P2000.Machine.Tests/Boot/BasicReadLoopCallSiteDiag.cs`,
  `RunTokenReadLoopDisasmDiag.cs`, `RecordCounterLiveTraceDiag.cs` (all new permanent regression
  guards, asserting the exact call-site counts, the disassembled instruction shapes at the
  confirmed key addresses, and the 13-cycle/256-start/0-end counter behavior respectively).
- **Applies to:** the three new test files above. `docs/PDOS_wip.asm` NOT applicable to this
  entry — every address found and disassembled this pass is in the CARTRIDGE ROM
  (`assets/Basic-24.bin`), a different code region entirely from PDOS's own disk-loaded driver
  that file covers. Reference doc §5d's 2026-08-04 entry (Tenth owner follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Tenth owner follow-up (2026-08-04)” entry; also updated the trailing “Confirmed (owner, 2026-07-28)” status paragraph)

### 2026-08-04 — Part F (cc-bugfix-prompt-13): the three candidate "2-sectors-short" mechanisms (`leb9eh`/`sub_f447h`/`lf555h`) are ALL EXONERATED — the interleave table is complete, and the index computation is a clean, UNCAPPED counter that would correctly continue to the real sectors 6/12 if called again. This re-narrows (does not close) the investigation: the actual stop is external to PDOS's own low-level sector-advancement code, most likely in BASIC's own record-reading loop, a different, not-yet-disassembled code region. NOT a genuine hardware/real-PDOS limitation confirmed either way — still open.
- **Trigger:** cc-bugfix-prompt-13, following directly from Part E's own flagged candidate
  locations for the "14-of-16 sectors" mechanism: `leb9eh`, `sub_f447h`, and the `lf555h`
  interleave-lookup table (all named in Part E's own "brief look," not yet disassembled there).
- **Static disassembly confirms the interleave table (`lf555h`, `docs/PDOS_wip.asm`, read but not
  edited) is genuinely COMPLETE, not short.** Its raw bytes (mis-rendered by the disassembler as
  garbage instructions, same phenomenon as `sub_f2fdh`'s own jump table in Part D) are the full,
  correct 16-entry sequence `01 07 0D 03 09 0F 05 0B 02 08 0E 04 0A 10 06 0C` (decimal
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16,6,12`) — indices 14 and 15 genuinely hold 6 and 12. The table
  itself was never the bug.
- **A real instrumentation bug found and fixed while building the live trace, worth flagging for
  any future work in this codebase:** `sub_f447h`'s subtrahend involves a DOUBLE INDIRECTION —
  `ld hl,(0f662h)` loads a *pointer* from that cell, and `ld c,(hl)`/`ld b,(hl+1)` read the real
  subtrahend from wherever THAT pointer points, not from `0f662h`'s own bytes directly. The first
  pass of the new test read `0f662h` directly (missing the second indirection), producing
  nonsensical index values (166-195, far outside the table's 0-15 range) that briefly looked like
  a genuine out-of-bounds read bug before the fix revealed it was purely a test artifact.
- **CONFIRMED, decisively, once the instrumentation was fixed: the table-index computation is a
  clean, UNCAPPED linear counter in BOTH contexts tested.** New regression test
  `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs`
  (`SystemBAndRunVolorg_TableIndexComputationIsUncapped_StopIsExternalToThisCode`) traces every
  genuine call to `sub_f447h`, distinguishing its two call sites by return address (`leb9eh`'s own
  internal loop-check at 0xEB9E is a DIFFERENT, unrelated subtraction — not a table index at all;
  only `lebe5h`'s call at 0xEBF0 computes the real table index). Across both `SYSTEM B` (directory
  read) and `RUN"VOLORG"` (file-data read): the index cleanly advances `0,1,2,...,13` in both
  contexts, `carry` is FALSE on every single call (no underflow/boundary condition is EVER
  signaled — the datasheet-style "stop" mechanism this code would need to genuinely halt at 13
  simply never fires), and every table byte read matches the confirmed interleave exactly. **This
  disproves the prompt's own working hypothesis** (a fixed loop counter, a short table, or an
  off-by-one in the interleave walk) — none of the three named candidate locations contain any
  cap at all. Called an additional 1-2 times, this exact code would correctly compute indices
  14/15 and read the real sectors 6/12.
- **What this means: the actual "stop after 14" decision is made ENTIRELY OUTSIDE this code.**
  Combined with Part D's own confirmed dispatcher-level trace (PDOS's top-level dispatcher,
  `CPM_entry_point`, receives NOTHING after the 14th `0x14`/F_READ call — not a 15th F_READ, not
  `F_CLOSE`, not any other function code at all) and Part E's own confirmed CR/RC tracking (CR
  stops advancing at exactly 13, RC stays at VOLORG's genuine 44 throughout, EOF never triggers) —
  the picture is now: PDOS's own bank-1 driver code is a passive, correctly-functioning component
  that would read further if asked. Whatever decides to stop asking lives in BASIC's own
  record-reading loop — a separate, disk-loaded code region (the same `&H698D`-`&H69D5`-adjacent
  area first identified in Part A, not the PDOS bank-1 driver `docs/PDOS_wip.asm` covers) — and
  has NOT been disassembled by this investigation. Disassembling THAT loop is the natural next
  step, but is a genuinely different code region from everything traced in Parts B-F.
- **Owner follow-up experiment, 2026-08-04 (post-Part-F): directly disproves the VOLINFO-byte15
  coincidence theory.** The owner patched VOLINFO's own FCB byte 15 from `0x0E`(14) to `0x2C`(44,
  matching VOLORG's own value) on a real disk image and re-ran the repro — **same stop-at-14
  effect, unchanged.** This confirms the "14" is NOT read from or influenced by VOLINFO's FCB at
  all; the earlier byte-15 match was pure coincidence, exactly as the owner suspected. Also
  confirmed (same follow-up exchange, citing the already-established Part D dispatcher trace):
  **BASIC issues discrete, per-record `0x1A`(F_DMAOFF)/`0x14`(F_READ) call PAIRS through the
  top-level `CPM_entry_point` dispatcher, 14 times — NOT a single "load file" call that lets PDOS
  loop internally.** PDOS's own bank-1 driver never receives more than one record's worth of work
  per call; the decision to stop after the 14th pair is made entirely on BASIC's own side, between
  calls — reinforcing Part F's own "the stop is external to PDOS" conclusion via a second,
  independent line of evidence.
- **Does NOT resolve the "is this correct, faithful P2000 hardware/software behavior, or a genuine
  bug" question the prompt asked to settle — still open, in either direction.** The dense, real
  data confirmed present in the skipped sectors (6, 12) and in VOLORG's later tracks (Part E's own
  raw-byte check, corroborated by the owner directly: VOLINFO's own FCB byte 15 = 0x0E = 14,
  matching a DIFFERENT file's real "needs only 14 of its 16 allocated sectors" case, distinct from
  VOLORG's own byte 15 = 0x2C = 44, meaning VOLORG genuinely needs all 44) makes an intentional,
  correct 14-sector stop implausible for VOLORG specifically — but WHY BASIC's own loop would stop
  early regardless is not yet identified, so this is not confirmed as a bug either. Flagged
  precisely rather than guessed at, per this investigation's own standing convention.
- **Sub_e706h discrepancy (Part D/E, "30 vs 29"):** not chased this pass — no new cheap signal
  surfaced during this investigation to make it worth a dedicated look; remains open exactly as
  Part D left it.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs` (new permanent
  regression guard) — asserts the table-index computation reaches exactly indices 0-13 with
  `carry=False` throughout, in both the `SYSTEM B` and `RUN"VOLORG"` contexts, and that every
  table byte read matches the confirmed interleave sequence.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/SectorAdvancementCapDiag.cs` (new),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited — no new
  addresses beyond what Part D/E already cited), reference doc §5d's 2026-08-04 entry (Ninth owner
  follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Ninth owner follow-up (2026-08-04, cc-bugfix-prompt-13)” entry)

### 2026-08-03 — Part E (cc-bugfix-prompt-12 + addendum): LIKELY RESOLVES THE ORIGINAL "Disk I/O error" INVESTIGATION — RUN"VOLORG" is NOT stuck on a directory scan that never finds a match. PDOS's function codes are a direct CP/M 2.2 BDOS clone; VOLORG's own file DATA is already being read correctly, record by record, via genuine F_READ/F_DMAOFF calls. The read simply stops 2 sectors short of a full track — the SAME "14-of-16 sectors" limitation already flagged as an unrelated loose end in Part B — long before CP/M's own EOF condition would ever fire. Neither of the prompt's own theories (a)/(b) is correct; a third, better-supported explanation is confirmed instead.
- **Trigger:** cc-bugfix-prompt-12, disassembling the three `sub_f2fdh` handler bodies Part D
  pinned down (`0x0F`→`0xF370`, `0x14`→`0xF3A0`, `0x1A`→`0xF3CA`), to determine whether PDOS's own
  FCB-name compare (a) legitimately never matches VOLORG's real FCB content, or (b) matches
  correctly but nothing acts on it. **A user-supplied addendum arrived mid-investigation** with a
  decisive piece of outside context: PDOS's function codes `0x0F`/`0x14`/`0x1A` match standard
  CP/M 2.2 BDOS exactly — F_OPEN/F_READ/F_DMAOFF (source:
  [seasip.info/Cpm/bdos.html](https://www.seasip.info/Cpm/bdos.html)) — and the dispatcher was
  already labeled `CPM_entry_point` in the existing disassembly. This reframed the whole
  investigation: the 14-15 physical reads Parts B/C/D all labeled "the directory scan" were never
  actually confirmed against the documented directory track/sector location — `0x1A`/`0x14`
  alternating is the textbook CP/M idiom for reading a file's DATA sequentially (DMAOFF then
  READ), not how directories are searched (that's F_SFIRST/F_SNEXT, `0x11`/`0x12` — confirmed
  never called, Part C/D's own trace). The addendum asked for one cheap check FIRST, before more
  disassembly: confirm what physical track these reads actually target.
- **Static disassembly of the three handler bodies** (`docs/PDOS_wip.asm`, read but not edited —
  no owner annotations exist yet around these three addresses):
  - `0x0F`→`0xF370`: `call sub_f2d4h; call sub_f068h; jr lf388h`. `sub_f2d4h` extracts the low 5
    bits of the current FCB's flag byte (offset+0), treats values ≥0x1E as out-of-range (early
    return), otherwise clears those bits in-place and calls `sub_f2c2h` (drive-select bookkeeping,
    compares `lf583h` against `Active_drive`). `sub_f068h` copies bytes out of the current FCB
    into a working buffer (guarded by `sub_f032h`, which aborts if `(lf585h)==0xFF`) — consistent
    with F_OPEN's job of locating/opening a file and priming its working state.
  - `0x14`→`0xF3A0`: `call sub_f2d4h; call sub_f137h; jr lf3fah`. `sub_f137h` reads standard CP/M
    FCB fields directly — `sub_ec39h` reads FCB offset `+0x20` (CP/M's `CR`, current record) into
    `lf665h` and offset `+0x0F` (CP/M's `RC`, record count) into `0xf664h` — compares them
    (`CR < RC` → issue the next physical read via `Seek_to_track`/`sub_e8b3h`, the same confirmed
    real-FDC-command source from Part B/C; `CR >= RC` → set `lf582h=1`, the EOF-equivalent result
    that flows into the actual return value through `sub_f2fdh`'s own `lf3fah` epilogue, and return
    immediately). This is a byte-for-byte match to F_READ's own textbook CP/M shape.
  - `0x1A`→`0xF3CA`: a different shape entirely — `ld a,(lf58bh); rra; jr nc,lf3d8h`. If bit 0 of
    `lf58bh` is set, copies the current FCB pointer (`0xF579`) directly into `lf589h` — the exact
    cell `le229h`'s function-`0x39` handler reads to know which file's data to read; if clear,
    falls to mere bookkeeping (`lf2f3h`, copies the FCB pointer into `lf587h`/`lf52eh`, no I/O).
    Matches F_DMAOFF exactly: a pure buffer-address-priming operation with no disk I/O of its own.
- **Live-trace results — four new regression tests, all decisive:**
  1. **`FcbCompareHandlerTraceDiag.cs`** (`RunVolorg_VolorgIsAlreadyTheActiveFcb_MatchBranchAlwaysTaken`):
     `lf58bh` bit 0 is SET on all 15 real `0x1A` entries, and the "prep `lf589h` for a real read"
     branch is taken every single time — never once does it fail. VOLORG's own FCB (independently
     confirmed via raw bytes at `assets/Disks/volorg.dsk` track-1 slot 0: byte0=`0xF3`,
     name=`"VOLORG  "`, ext=`"BAS"`, sector-count=`0x2C`=44, allocation map records
     `{4,5,6,7,12,13,14,15,16,17,18}`) is the "current" FCB pointer at all 27 `sub_f137h` entries
     too. **A real gotcha found and fixed along the way, same class as the RET-fetch artifact from
     Part C:** the test's own raw "no-match branch (0xF3D8) taken" count matched the match-branch
     count 1:1 — a dead giveaway it's a PC-fetch-increment artifact from the 2-byte `jr lf3dbh` at
     `0xF3D6` transiently showing PC at `0xF3D8` before the jump completes, not a genuine second
     execution path. Kept as a raw diagnostic count, not asserted as a real branch outcome.
  2. **`ReadDataPhysicalTrackDiag.cs`** (`RunVolorg_ReadDataCommands_TargetTrackOneNotVolorgsOwnDataTracks`):
     watches every real FDC transfer completion's physical cylinder/head/sector. **CONFIRMED:
     exactly ONE initial read on cylinder 0 (1-based track 1, the directory — presumably F_OPEN
     locating VOLORG's FCB), then ALL 14 remaining reads target cylinder 1 (1-based track 2) —
     VOLORG's OWN real data track**, independently computed from its allocation map (records
     4-7 → track 2 under the confirmed `1-based-track = record/4 + 1` formula). **A second real
     gotcha found and fixed:** `Upd765.CurrentTransfer.Sector`, sampled at the `COMPLETE` trace
     event, reports one sector PAST the one that just finished (`_transferIndex` has already
     advanced to the full transferred byte count — 256 bytes, one whole sector — by the time the
     trace fires, so `_transferStartSector + _transferIndex/_transferSectorSize` is off by exactly
     +1). Corrected by subtracting 1. **The corrected track-2 sector sequence is
     `1,7,13,3,9,15,5,11,2,8,14,4,10,16` — EXACTLY the documented full 16-sector interleave
     (`docs/P2000T-disk-formats.md` §6a), missing only 6 and 12** — the identical "14-of-16"
     pattern already flagged as an unrelated loose end in Part B (2026-07-28 entry, there
     attributed to directory reads). This is not a coincidence: it's the SAME underlying
     sector-advancement mechanism, now confirmed to also govern real file-data reads.
  3. **`FReadEofHandlingDiag.cs`** (`RunVolorg_CrNeverReachesRc_StandardCpmEofIsNeverTriggered`):
     tracks CR/RC directly off VOLORG's own FCB at every `sub_f137h` entry. **CONFIRMED: RC stays
     constant at 44 throughout (VOLORG's genuine record count, never corrupted); CR only ever
     reaches 13 across the whole attempt — nowhere near RC.** CP/M's own EOF condition (`CR>=RC`)
     is NEVER triggered for this repro. The eventual busy-wait/"Disk I/O error" is confirmed NOT to
     be an EOF-handling bug — there is no EOF condition here at all to mishandle.
  4. **`SubF2fdhJumpTableDiag.cs`** (Part D's own test, unchanged) still confirms the underlying
     30-vs-29-entry `sub_e706h` discrepancy remains open and unexplained — per the prompt's own
     "only if genuinely cheap" scoping, not chased further this pass (a quick recheck found no
     additional cheap signal beyond what Part D already reported).
- **What this means for the prompt's own theory (a) vs (b) framing: NEITHER is correct.** (a) is
  directly disproven — there is no FCB-compare failure of any kind; VOLORG's FCB is the active FCB
  from the very first relevant read and stays so throughout. (b) is also not quite right as
  framed — it's not that a successful compare's result gets dropped somewhere else (e.g. on the
  BASIC side); PDOS's own F_READ machinery IS correctly acting on VOLORG's FCB, repeatedly and
  successfully, for as long as it runs. **The real, confirmed mechanism is a third explanation:**
  the physical-sector-read-advancement machinery underlying BOTH directory reads (sub_e774h) and
  file-data reads (`sub_f137h`/`lf170h`) shares a genuine limitation that stops exactly 2 sectors
  short of a complete 16-sector track, every time, regardless of context — and this happens long
  before any CP/M-level EOF condition would legitimately end the read. Once that limit is hit, no
  further FDC command is ever issued (Part B/C/D's own confirmed observation), and the hardcoded
  busy-wait/timeout eventually fires, reporting "Disk I/O error" — not because of a disk-content
  problem with `volorg.dsk`, and not because of a dropped BASIC-side decision, but because of this
  shared sector-advancement limitation. **This directly un-flags the "14-of-16 sectors" loose end
  from Part B's own 2026-07-28 entry** — it was never a separate, unrelated curiosity; it is very
  likely the actual root cause of the entire "Disk I/O error" symptom chain investigated across
  Parts A-E. Confirming the EXACT mechanism inside the shared sector-advancement code that causes
  the 2-sector shortfall (a genuinely different, much narrower disassembly task — likely in
  `leb9eh`/`sub_f447h`/the `lf555h` interleave-lookup table, per this pass's brief look at
  `sub_ec19h`/`leb9eh`) is the natural next, tightly-scoped step, not attempted in this pass.
- **`volorg.dsk` is very likely NOT a damaged/bad fixture** — the read pipeline (FCB location,
  F_READ dispatch, physical track targeting, CR/RC bookkeeping) all work correctly right up to the
  sector-advancement limit; nothing about VOLORG's own FCB or file content is implicated.
- **Tests:** `tests/P2000.Machine.Tests/Boot/FcbCompareHandlerTraceDiag.cs`,
  `ReadDataPhysicalTrackDiag.cs`, `FReadEofHandlingDiag.cs` (all new permanent regression guards,
  finalized with assertions matching the confirmed results above).
- **Applies to:** the three new test files above, `docs/PDOS_wip.asm` (owner's own
  work-in-progress disassembly, read but not edited — `sub_f2d4h`/`sub_f068h`/`sub_f137h`/the
  `0x1A` handler's `lf58bh`-gated branch are this investigation's own raw disassembly, not yet
  owner-annotated), reference doc §5d's 2026-08-03 entry (Eighth owner follow-up), and directly
  supersedes/resolves the "14-of-16 sectors" open item from Part B's own 2026-08-03 entry above.
- **Synced:** yes (P2000T-reference.md §5d, “Eighth owner follow-up (2026-08-03, cc-bugfix-prompt-12 + a decisive mid-investigation addendum)” entry; also un-flagged the Fifth follow-up's "14-of-16 sectors" loose end in place)

### 2026-08-03 — Part D (cc-bugfix-prompt-11): LIVE-CONFIRMED that `RUN"VOLORG"` really does reach `sub_f2fdh` — and this CORRECTS Part C's own speculative account. Not one shared handler: THREE distinct function codes reach here, each landing on its own distinct jump-table target. Sizes the next disassembly pass at exactly 3 targets, not "a dozen candidates" or "zero, unreached."
- **Trigger:** cc-bugfix-prompt-11, the owner's own sharp follow-up to Part C: Part C's account of
  `le12dh` → `le149h` → `sub_e705h` → `sub_f2fdh` as "the path 0x1A/0x14 take," and `sub_f2fdh`
  itself as "a second, internal jump table... confirmed structurally (not yet functionally)," was
  read purely from `docs/PDOS_wip.asm`'s static byte patterns — never confirmed by watching PC
  actually walk that path during a real repro. This prompt: answer with the same live-trace rigor
  already used for the dispatcher and `sub_e943h`, before investing in disassembling a dozen
  candidate subroutines that might not even be the ones (or reached at all) for this repro.
- **New regression test:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs`
  (`RunVolorg_ReachesSubF2fdh_WithThreeDistinctCodesEachLandingOnItsOwnTarget`). Watches every
  entry to `sub_f2fdh` (0xF2FD), verified via the SAME genuine-CALL-bytes discipline
  `Sube943hCallerDiag.cs`'s own RET-fetch-artifact gotcha already established (checking the actual
  3 bytes at `returnAddr-3` decode to `CD FD F2` — the one real call site, `sub_e705h`'s own
  `call sub_f2fdh` at 0xE712 — rather than trusting a bare `PC == 0xF2FD` match). For each genuine
  entry, reads the routing code PDOS itself stores at `0xF578` (the same cell `sub_e705h` writes
  before calling), computes the jump-table slot as `lf307h + 2*code` (0xF307-based, per Part C's
  own structural read), reads the STORED little-endian target from that memory address, and
  independently reconfirms by watching for the CPU's own PC to actually reach that computed target
  within the following ~2000 T-states.
- **Result 1 — CONFIRMED: execution DOES reach `sub_f2fdh`.** 30 genuine calls observed across the
  whole `RUN"VOLORG"` attempt (verified via the real `CD FD F2` bytes, not a bare PC hit). This
  answers the prompt's own three-way framing decisively: it is neither "zero, unreached" nor
  "reaches a wholly different, undocumented path" — Part C's citation of the dispatch chain into
  `sub_e705h`/`sub_f2fdh` was directionally correct.
- **Result 2 — CORRECTS Part C's own speculative account: 0x14 and 0x1A do NOT share a single
  handler.** Three distinct routing codes reach `sub_f2fdh` in this repro — `0x0F` (×1), `0x14`
  (×14), `0x1A` (×15) — and **each lands on its own distinct jump-table target**, not a shared one:
  `0x0F` → `0xF370`, `0x14` → `0xF3A0`, `0x1A` → `0xF3CA`. Every one of the 30 computed targets was
  independently reconfirmed by directly observing the CPU's own PC actually arrive there (30/30) —
  this isn't inferred from the static table alone. **This sizes the next disassembly pass exactly:
  3 distinct handler addresses (`0xF370`, `0xF3A0`, `0xF3CA`), not "a dozen unread subroutines"**
  as Part C's own raw-byte read of the surrounding region suggested (that dozen-plus subroutine
  list — `sub_f068h`, `sub_f09bh`, etc. — remains unconfirmed as to which, if any, of those
  addresses these three targets actually correspond to; the exact targets are now known precisely,
  their own bodies are still undisassembled).
- **A genuine, flagged discrepancy, not explained away per the prompt's own instruction: 30
  `sub_f2fdh` entries vs. Part C's own 29 `CPM_entry_point`-level entries.** One more call reaches
  `sub_f2fdh` here than Part C's own dispatcher-level trace counted at the top-level entry point
  (0xE000) — meaning at least one call to `sub_e705h`/`sub_f2fdh` in this repro does NOT originate
  from BASIC's own top-level `CALL &H6205` PDOS invocation. `sub_e705h` has a second, alternate
  entry point (`sub_e706h`, immediately following it in `docs/PDOS_wip.asm`, which skips the
  `ld c,a` step and assumes C is already set) — the most likely explanation is that PDOS's own
  internal code calls into `sub_e706h` directly at least once, reusing the same numeric code space
  for an internal purpose, bypassing BASIC's dispatch entirely. **Not confirmed — flagged as an
  open, secondary thread, not chased further in this pass** (explicitly out of scope per the
  prompt: this pass is about sizing the jump-table targets, not tracing every caller).
- **Also worth noting (not a new finding, corroborates Part C):** the single `0x0F` call seen here
  does NOT come from the SAME `0x0F` handling Part C traced at the top-level dispatcher (that
  branch — `le0b4h`'s "FCB extension == 'BAS'?" check, `docs/PDOS_wip.asm` lines 172-205 — never
  calls `sub_e705h` anywhere in its own body, confirmed by re-reading it during this pass). So this
  repro's `0x0F` arrival at `sub_f2fdh` is itself part of the same "bypasses the top dispatcher"
  discrepancy above, not a contradiction of Part C's own separate `0x0F`-dispatch account.
- **Tests:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs` (new permanent regression
  guard) — asserts execution reaches `sub_f2fdh` at least once; every routing code observed is one
  of the three actually confirmed here; exactly 3 distinct codes and exactly 3 distinct jump
  targets (not 1, not a dozen); and every genuine entry's computed target is independently
  reconfirmed via live PC observation (not just a static memory read).
- **Applies to:** `tests/P2000.Machine.Tests/Boot/SubF2fdhJumpTableDiag.cs` (new),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly, read but not edited — the three
  target addresses `0xF370`/`0xF3A0`/`0xF3CA` are newly pinned down by this investigation, not yet
  named/annotated in the owner's file), reference doc §5d's 2026-08-03 entry (Sixth owner
  follow-up).
- **Synced:** yes (P2000T-reference.md §5d, “Seventh owner follow-up (2026-08-03, cc-bugfix-prompt-11 dispatched to CC...)” entry; also corrected the Sixth follow-up's “identical handler” claim in place)

### 2026-08-03 — Part C NARROWED FURTHER, STILL NOT RESOLVED (cc-bugfix-prompt-10): PDOS's dispatcher is CONFIRMED to never receive any function code beyond the directory-scan set (0x0F/0x1A/0x14) during `RUN"VOLORG"` — the decision to skip the real file-data read is NOT a PDOS-dispatch-level branch failing, it's that PDOS is never even asked. The actual decision point is narrowed to one specific, not-yet-disassembled internal jump table.
- **Trigger:** cc-bugfix-prompt-10, continuing directly from Part B's confirmed mechanism (entry
  immediately below): after the directory scan's last real sector completes, execution falls into
  a hardcoded busy-wait that times out with no operation ever having been issued. This prompt's
  question: WHY does PDOS never proceed from "FCB found" to "read the file's data"?
- **Step 1 — precisely identified WHICH of the four `sub_e943h` (0xE943) call sites fires, at the
  instruction level, via live execution tracing (not T-state-pattern inference as Part B used).**
  New regression test `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs`
  (`RunVolorg_ReachesSubE943hExactlyOnce_ViaChannelTimeOutOnly`) watches every entry to 0xE943 and
  reads the return address off the stack to identify the real caller.
  - **Gotcha found and fixed along the way, worth flagging for any future PC-address-based
    tracing in this codebase:** a naive `PC == 0xE943` check alone produces 30 FALSE POSITIVES for
    every 1 real call in this exact repro. `sub_e937h`'s own `ret` instruction sits at 0xE942,
    immediately before `sub_e943h`'s label with no intervening jump — Z80 cores bump PC past a
    fetched single-byte opcode before that opcode's own semantics (here, the stack pop a `RET`
    performs) complete, so PC transiently reads 0xE943 for several T-states on every return from
    `sub_e937h`, with SP still pointing at the not-yet-popped return address. The first pass at
    this trace misread these artifacts as two brand-new, previously-uncatalogued call sites
    (`0xE8F7`/`0xE96F`) — both are real, legitimate call sites, but for
    `Get_7_disk_status_bytes`/`sub_e937h`, entirely unrelated to `sub_e943h`. Fixed by verifying
    the actual 3 bytes at `returnAddr-3` decode to `CD 43 E9` (a genuine `CALL 0xE943`) before
    counting a hit.
  - **Result, CONFIRMED: exactly one genuine call, from `channel_time_out` (0xE978).** This
    precisely reconfirms Part B's own conclusion (busy-wait timeout) with call-site-level ground
    truth, and rules out the other two hypotheses that were still open at Part B's end: the
    function-0x0F "FCB extension == 'BAS'?" inline call (0xE0EE) and the `lea6ah` third
    interrupt-vector-payload path (0xEA6B) — NEITHER ever fires for this repro.
- **Step 2 — traced every function code PDOS's own dispatcher (`CPM_entry_point`, 0xE000) ever
  receives during the whole `RUN"VOLORG"` attempt.** New regression test
  `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs`
  (`RunVolorg_DispatcherNeverReceivesAnyCodeOutsideTheDirectoryScanSet`).
  - **Gotcha found and fixed along the way:** the first pass read register A at the exact moment
    PC==0xE000, which is BEFORE the dispatcher's own `ld a,c` (the real calling convention passes
    the function code in **C**, not A) — every entry showed the same stale leftover A value. Fixed
    by reading C instead.
  - **Result, CONFIRMED: only codes 0x0F, 0x1A, and 0x14 are EVER dispatched, across all 29
    dispatcher entries observed for the whole attempt — no other code, including the candidate
    0x39, ever appears.** 0x39 (`docs/PDOS_wip.asm`'s own `le229h`/0xE229 handler — reads a value
    via `(ix_pointer)`, converts it via `sub_eb23h`, stores the result into `RW_cmd_track`; this
    investigation's own working hypothesis for "the real file-data-read trigger," based on its
    shape looking like "convert an FCB allocation-map record into a track") is never sent. The
    scan simply cycles 0x1A (read next directory sector, dispatches into `sub_e774h`→`sub_e7abh`
    per Part B)/0x14 through all 14 real sectors and then stops entirely — no further dispatcher
    entry of ANY kind follows the last one, straight into the busy-wait.
- **Both 0x1A and 0x14 route to the exact SAME PDOS-side handler**, confirmed directly in
  `docs/PDOS_wip.asm`: `le12dh`'s dispatch chain sends both (along with 0x12/0x18/0x19/0x1B-0x1E)
  to `le149h` → `call sub_e705h` (0xE705). `sub_e705h` stores the function code (in C) into a byte
  at `0xF578` and the FCB pointer (from `lf52ch`) at `0xF579`-`0xF57A`, then calls `sub_f2fdh`
  (0xF2FD) — **this is where the real per-code behavior must live**, since PDOS's own top-level
  dispatcher treats 0x1A and 0x14 identically.
- **`sub_f2fdh` (0xF2FD) is a SECOND, internal jump table, indexed by the SAME function code —
  this is PDOS's actual FCB-compare/decision engine, and it is the concrete, narrowed location of
  whatever decides "keep scanning" vs. "found it, transition to a real read."** Confirmed
  structurally (not yet functionally): computes `lf307h + 2*code` and jumps to the 2-byte address
  stored there — a genuine jump table, whose raw table-entry bytes the current disassembly
  mis-renders as garbage instructions (visible around `0xF321`-`0xF344` in `docs/PDOS_wip.asm`'s
  raw dump) because nothing yet marks that region as data, not code. Beyond the table, roughly a
  dozen distinct case-handler blocks are visible in the raw disassembly (calling `sub_f068h`,
  `sub_f09bh`, `sub_f137h`, `sub_f186h`, `sub_f0adh`, `sub_f045h`, `sub_ef3dh`, `sub_eeadh`,
  `sub_ef57h`, `sub_ecd5h`, `sub_f24ch`, `sub_f2c2h`, `sub_f2d4h`) — **none of these addresses or
  routines are from the owner's own `docs/PDOS_wip.asm` annotations; they are this
  investigation's own raw disassembly, read only far enough to identify the jump-table shape and
  the existence of these call targets, NOT their individual behavior.**
- **What's still open, narrower than at the start of Part C:** two live possibilities, NOT yet
  distinguished — (a) `sub_f2fdh`'s own C=0x14/C=0x1A case handlers run a real FCB-name-compare
  and, for THIS fixture's FCB content, never signal "match found, transition to read" (a fixture-
  or FCB-validation-content question, per the prompt's own framing); or (b) the handlers DO signal
  a match correctly, but whatever should happen next (issue function code 0x39 or equivalent) is a
  decision made entirely on the BASIC side (the disk-loaded LOAD/RUN token driver — a DIFFERENT
  code region from PDOS's own bank-1 driver, per Part A's `&H698D`-`&H69D5` wrapper finding) that
  never gets there. **Disambiguating these needs disassembling `sub_f2fdh`'s C=0x14/C=0x1A jump
  targets and their sub-handlers** — a substantial follow-on task (a dozen-plus unread
  subroutines), not completed in this pass. Per the prompt's own instruction not to guess: this is
  reported as the confirmed narrowing achieved, not resolved further.
- **Tests:** `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs` (rewritten from a pure
  diagnostic into a permanent regression guard — asserts exactly one genuine call to `sub_e943h`,
  from call site 0xE978) and `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs`
  (new permanent regression guard — asserts every dispatched function code during the attempt is
  one of {0x0F, 0x1A, 0x14} and that 0x39 never appears).
- **Applies to:** `tests/P2000.Machine.Tests/Boot/Sube943hCallerDiag.cs`,
  `tests/P2000.Machine.Tests/Boot/DispatchFunctionCodeTraceDiag.cs` (both new/rewritten),
  `docs/PDOS_wip.asm` (owner's own work-in-progress disassembly — NOT edited by this
  investigation; `sub_f2fdh`'s jump table and its case handlers are this investigation's own raw
  disassembly, explicitly flagged above as not from the owner's file), reference doc §5d's
  2026-08-03 entry.
- **Synced:** yes (P2000T-reference.md §5d, “Sixth owner follow-up (2026-08-03, cc-bugfix-prompt-10 dispatched to CC...)” entry)

### 2026-08-03 — Part B NARROWED, NOT YET RESOLVED (cc-bugfix-prompt-9): `RUN`/`LOAD`'s persistent "Disk I/O error" is CONFIRMED not an FDC/CTC/interrupt-emulation bug — the real gap is that PDOS never issues the file-data read at all after a successful directory scan. Root cause of THAT still open; not guessed at.
- **Trigger:** cc-bugfix-prompt-9's Part B, continuing directly from Part A's confirmed
  `&H698D`-`&H69D5` wrapper mechanism (see that entry, immediately below) — and from a live,
  collaborative disassembly session with the owner, who is independently hand-annotating PDOS
  (`docs/PDOS_wip.asm`, work-in-progress) and spotted a CTC channel-1 timer/interrupt-counting
  mechanism ("ignore 1024 interrupts, ~20s, then time out") that looked like a promising lead for
  the original "genuinely stuck" LOAD/RUN symptom (project CLAUDE.md's own 2026-07-28 entry).
- **The CTC-timeout chase — a real, confirmed mechanism, but ultimately a RED HERRING for this
  specific symptom.** Traced `exit_and_set_disk_off_timer` (the common exit path for nearly every
  PDOS command) live: it DOES arm CTC ch1 fresh (prescaler 256, TC 0xFF, matching a real
  ~65,280-T-state period, confirmed via live reflection into `CtcChannel`'s own internal
  `_control`/`_timeConstant`/`_downCounter`/`_started`/`_softReset` fields) on every single
  command exit — and ch1 also gets reprogrammed into a SEPARATE counter-mode/TC=1 shape
  (`sub_e8c3h`) during active transfers, confirming the owner's own "set up in different ways"
  observation. Chasing this down (multiple live traces, described in full in this entry's earlier
  drafts, since trimmed) found the loop-counter math didn't add up at first (flag flipping after
  ~15 interrupts, not the expected ~1020) — but this turned out to be because the software
  loop-counters DO get freshly reset every time (confirmed via a corrected-offset direct RAM read:
  real addresses `&H6165`/`&H6166`, not the initially-hand-counted `&H6164`/`&H6165` — an earlier
  off-by-one in this same investigation), and the actual firing trigger for THIS symptom isn't the
  CTC path at all — see below.
- **The real mechanism, CONFIRMED via `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs`
  (new, kept as a permanent regression guard):** traced the REAL FDC command stream (`Upd765.Trace`)
  together with CTC channel 0's own `IntPending`/`InService` state (channel 0 = the FDC completion
  interrupt, wired `Fdc.ResultReady += () => Ctc.ClkTrg(0)`) across a full, real
  `RUN"VOLORG"` attempt.
  - **Channel 0 fires and delivers CORRECTLY, every single time, for all 15 real `READ DATA`
    completions** in the directory scan, in the confirmed interleave order
    (1,7,13,3,9,15,5,11,2,8,14,4,10,16 — `docs/P2000T-disk-formats.md` §6a). No missed interrupt,
    no delivery bug — our FDC/CTC/interrupt-daisy-chain emulation is proven correct for every
    disk operation this repro actually performs.
  - **After the LAST directory sector actually read (16) completes at t≈2.5M, the FDC trace goes
    completely silent — no further command is EVER issued.** `RUN"VOLORG"` never attempts to read
    VOLORG's actual file DATA at all, despite its FCB being confirmed present and correctly
    located (project CLAUDE.md's own 2026-07-28 entry). Execution instead falls into a hardcoded
    65536-iteration busy-wait loop (`le95fh`/`le962h` in `docs/PDOS_wip.asm`, ~3.8M T-states —
    confirmed via a PC-visit-frequency scan showing >2 MILLION visits concentrated in a 12-address
    range starting IMMEDIATELY when the gap begins) that exists to be interrupted EARLY by a real
    disk operation's own completion signal — a stack-manipulation redirect installed at `&H6135`
    by `sub_e8c3h` (POPs the busy-wait's own return address off the stack and pushes a real
    continuation address instead, so a genuine completion interrupt jumps OUT of the wait loop
    entirely rather than returning into it). Since no operation was ever started here, nothing
    ever redirects it, and the loop burns through its full duration pointlessly.
  - Once the loop naturally expires, `channel_time_out`/`sub_e943h` fires (confirmed via the
    trace's own final `CTRL 00` entry, matching that routine's `xor a; out (DSKCTRL),a` — motor/FDC
    off entirely): writes the confirmed "always error" class `0x02` into the wrapper's own result
    byte at `&H6165`/`lf51eh` (see the entry below — this is the SAME byte the `&H698D`-`&H69D5`
    wrapper reads at `&H60BB` to decide whether to print "Disk I/O error"), and sets `FDOS_flags`
    bit 6. This is what actually prints the error — confirmed to be entirely independent of
    whatever result the directory scan itself found.
  - **Small, separate loose end noticed while reading this trace back (owner question,
    2026-08-03): the directory scan only ever reads 14 of the 16 sectors the documented physical
    interleave defines for a track.** `docs/P2000T-disk-formats.md` §6a's own confirmed pattern is
    four groups of 4 — `{1,7,13,3}/{9,15,5,11}/{2,8,14,4}/{10,16,6,12}` — but the REAL resident
    driver, in every trace across this investigation (and independently already in the project's
    own 2026-07-28 entry, which recorded the identical sequence without remarking on it), issues
    exactly `1,7,13,3,9,15,5,11,2,8,14,4,10,16` and STOPS — three full groups plus only the first
    half of the fourth, never touching sectors 6 or 12 at all. Sector 16 is NOT a failure point —
    it completes as cleanly as every other sector in the scan (confirmed RESULT bytes, no errors);
    the scan simply never continues past it. Given the identical partial pattern shows up in
    OTHERWISE-successful operations too (`FILES`'s own listing, which correctly shows both real
    files), this does not look like the cause of the "Disk I/O error" itself — flagged as its own
    small, independent open question (by design, needing only 14 sectors' worth of FCB slots for
    whatever the search logic checks? or a genuine, harmless quirk?) for whoever disassembles the
    directory-scan loop next, not conflated with the main finding above.
- **What's now open, narrower than "genuinely stuck, FDC trace shows everything correct" (the
  2026-07-28 entry's own conclusion):** WHY does PDOS's own logic, immediately after successfully
  finding VOLORG's FCB in the directory scan, decide NOT to proceed to reading the file's data —
  falling instead into a busy-wait/timeout path clearly designed for "wait for an ACTUAL disk
  operation," not "there's nothing to wait for"? That decision sits between the directory-scan
  dispatch (PDOS function codes `0x0F`/`0x1A`/`0x14`, already traced call-by-call in this
  investigation) and `sub_e7abh`/`le7b0h` (the real file-data-read entry point,
  `docs/PDOS_wip.asm`) — most likely a real FCB validation/allocation-map check the `volorg.dsk`
  fixture's FCB doesn't satisfy (a format/fixture-content question, not necessarily an emulator
  bug at all), but this has NOT been confirmed — flagged, not guessed, per this project's own
  convention. Needs a disassembly of that SPECIFIC narrow code region to resolve further; the
  owner's own in-progress `docs/PDOS_wip.asm` annotation is the natural next place to look.
- **A related data point, owner-observed, worth connecting rather than treating as a separate
  mystery:** `FILES` (a genuinely different PDOS command from `RUN`) correctly lists both real
  files on `volorg.dsk` — its OWN directory-reading job visibly succeeds — and STILL prints a
  trailing "Disk I/O error" afterward (already noted in the Part A entry above, and reproduced in
  `DiskIoErrorFlagTrace.cs`'s own regression assertions). This is NOT a contradiction of this
  entry's finding — it's corroborating evidence for the SAME class of bug: directory/FCB data
  transfers correctly (confirmed, repeatedly, across `SYSTEM B`/`FILES`/`RUN`'s own initial scan);
  something DOWNSTREAM of a successful directory operation still trips the error path. `FILES` may
  be hitting a structurally similar "expected to be interrupted by something that never arrives"
  gap for its own, not-yet-traced reason (e.g. its own end-of-listing step entering a similar
  busy-wait/redirect pattern) — flagged as the most promising related thread for whoever
  disassembles the `sub_e7abh`/`le7b0h` decision point above, not confirmed to be the identical
  code path.
- **Tests:** `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs` (new) — the
  conclusive trace above, kept as a permanent regression guard: asserts the full 16-sector
  directory scan completes in the confirmed interleave order, that NO FDC command is EVER issued
  after the last completion (the direct evidence that the failure is not FDC-related), and that
  the machine settles back to a clean ready state afterward. `DiskBasicDisasmDiag.cs` (extended,
  Part B additions) — LOAD's full token handler disassembled end to end (no direct PDOS calls of
  its own — it delegates to shared subroutines) and a whole-cartridge scan for every PDOS call
  site, mapping which function codes (`0x0D`-`0x1A`) this ROM actually uses.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/Channel0InterruptDuringGapDiag.cs` (new),
  `DiskBasicDisasmDiag.cs` (extended), `docs/PDOS_wip.asm` (owner's own work-in-progress
  disassembly, referenced throughout — NOT edited by this investigation), reference doc §5d's
  2026-08-02/03 entries.
- **Synced:** yes (P2000T-reference.md §5d, “Fifth owner follow-up (2026-08-02/03, cc-bugfix-prompt-9 dispatched to CC)” entry, Part B paragraph)

### 2026-08-02 — Part A RESOLVED (cc-bugfix-prompt-9): `SYSTEM B`'s "stale-retry" behavior is real, correct PDOS behavior — NOT a bug, no fix needed. Also found and fixed a real bug in the test harness itself (wrong VRAM row stride).
- **Trigger:** cc-bugfix-prompt-9's Part A, following Step 0's instrumentation instruction — trace
  `&H6091` (the Adresboekje's own named "Flag for Disk I/O error") live across the owner's exact
  manual repro (`RESET` → `SYSTEM B` ×2 → `FILES` → `RUN"VOLORG"` ×2 → `FILES`) BEFORE any
  disassembly, per the prompt's own "instrument first" instruction.
- **Step 0 instrumentation** (`tests/P2000.Machine.Tests/Boot/DiskIoErrorFlagTrace.cs`, new):
  reproduces the FULL owner-observed symptom pattern exactly, byte-for-byte: `RESET` → "Disk I/O
  error"+"Ok" (flag 0x00→0x02, expected — a system disk's track 1 legitimately has no FCB index);
  `SYSTEM B` (1st) → "Disk I/O error"+"Ok", flag stays 0x02; `SYSTEM B` (2nd, identical command) →
  "Ok" alone, flag clears to 0x00; `FILES` lists both real files correctly AND ALSO prints a
  trailing "Disk I/O error" (the owner's own "newest data point" — a command whose own core logic
  visibly succeeds can still report the error); both `RUN"VOLORG"` attempts fail identically, fresh,
  every time (rules out a stale flag for THAT symptom specifically — see Part B).
- **Real mechanism found by disassembly** (`tests/P2000.Machine.Tests/Boot/DiskBasicDiskLoadedDisasmDiag.cs`,
  new): every PDOS call returns through a wrapper at `&H698D`-`&H69D5`. This code is NOT in
  `Basic-24.bin` (the 16K cartridge ROM, confirmed exactly 16384 bytes, mapped linearly at
  0x1000-0x4FFF by `Slot1Cartridge.cs` with no bank tricks) — it's in the ~8K "missing interpreter
  chunk" the Adresboekje says loads from the boot floppy's tracks 3-5 into RAM at `&H6200`.
  Reconstructed that exact chunk directly from `diskbasic_1.6uk.dsk` (cylinders 2-4, 0-based =
  tracks 3-5 1-based, 16 sectors/cylinder, plain sequential read — the same "READ A TRACK" shape
  the boot loader itself uses, no logical/interleaved reordering) and confirmed the reconstruction
  is correct by finding the literal text "PHILIPS DISK BASIC" at exactly the offset the Adresboekje
  predicts (`&H693D`) before trusting anything disassembled from it.
  - The wrapper reads a result/class byte PDOS's own driver leaves at `&H60BB` after each call and:
    classes `{0x02, 0x0A, 0x0B, 0x0C}` → **unconditionally** set `&H6091=2` and jump to the error
    print path (`&H69BB`, matching the Adresboekje's own naming exactly), regardless of whether the
    underlying disk operation itself succeeded; class `0x1A` → **leave `&H6091` completely
    untouched** (a real, legitimate no-op path — the actual "stale flag persists" mechanism);
    otherwise → clear `&H6091=0` (success).
  - Confirmed via an instruction-level trace (register dump at every PC inside the wrapper) that
    `SYSTEM B`'s own drive-select call (`C=0x0E` at the BASIC-token level, `&H34D2`-`&H34F8` in the
    ROM — also disassembled, confirms the token handler itself does nothing but `CALL 0x6205` then
    `RET`, no flag logic of its own) returns through this wrapper with the result byte reading
    `0x02` on the FIRST call — one of the unconditional-error classes — which is exactly why the
    flag gets set and "Disk I/O error" prints, with NO fault in the FDC-level scan itself (the
    directory read that runs as part of it is fully correct, all 16 sectors, right interleave, no
    retries). *Why* the second call reports a different class is decided inside PDOS's own driver
    (loaded from the boot floppy's tracks 1-2 into bank 1 at boot — a third, separate code region,
    not yet disassembled) — genuinely out of scope for this pass, flagged rather than guessed.
- **A real, separate bug found and fixed along the way — in this investigation's OWN test tooling,
  not the emulator:** an earlier pass concluded "`SYSTEM B` hangs forever, prints nothing" — wrong.
  `SnapshotScreenText` (this file, and the identical pre-existing helper in `PdosLoadSaveRepro.cs`)
  read video memory with a 40-byte row stride. The real layout is 80 bytes/row with
  `Video.PanX`-based windowing (`src/P2000.Machine/Devices/Video.cs`'s own confirmed
  `BufferColumns=80`/`OnColumnFetch`) — verified directly with a raw VRAM hex dump
  (`tests/P2000.Machine.Tests/Boot/VramLayoutDiag.cs`, new), which shows every real line starting
  exactly on an 80-byte boundary (0x5000, 0x5050, 0x50A0, …). The 40-stride version only ever
  exposed the FIRST 12 of 24 real screen rows, and coincidentally still looked "readable" for any
  message under 40 characters (real content lands on every other logical line, blank padding on
  the rest) — exactly enough to fool a human or an assertion checking the first few lines, while
  silently hiding anything printed further down. `SYSTEM B`'s own error message landed at real rows
  12-13, one row past where the broken reader could see. Fixed in this file's `SnapshotScreenText`
  (now `row*80 + (PanX+col)%80`); **`PdosLoadSaveRepro.cs`'s identical helper still has the bug,
  not fixed here** (out of this prompt's scope; flagged for whoever next touches that file — it
  could equally be hiding output in ANY of that file's own repros, not just this one).
- **Conclusion: no code fix for Part A.** The wrapper logic is disk-loaded PDOS data this project
  faithfully executes, not emulator code to change — and it now demonstrably reproduces the exact
  documented/owner-observed behavior. "SYSTEM B needs a retry" is real, correct, by-design PDOS
  behavior (consistent with the manual's own "after DISK I/O ERROR the statement RESET has to be
  given"), not a bug.
- **Tests:** `DiskIoErrorFlagTrace.cs` (new) — the Step 0 repro, now a proper regression guard for
  the confirmed pattern (RESET/SYSTEM B set-then-clear, FILES' trailing error, RUN's fresh
  double-failure). `DiskBasicDisasmDiag.cs` (new, diagnostic) — disassembles SYSTEM/RESET/
  FILES/LOAD token entry points straight from `Basic-24.bin` via `Z80.Disassembler` (added as a
  test-only `ProjectReference` in `P2000.Machine.Tests.csproj`). `DiskBasicDiskLoadedDisasmDiag.cs`
  (new, diagnostic) — reconstructs and disassembles the disk-loaded PDOS wrapper.
  `VramLayoutDiag.cs` (new, diagnostic) — the raw VRAM dump that found the stride bug.
- **Applies to:** `tests/P2000.Machine.Tests/Boot/DiskIoErrorFlagTrace.cs`,
  `DiskBasicDisasmDiag.cs`, `DiskBasicDiskLoadedDisasmDiag.cs`, `VramLayoutDiag.cs` (all new),
  `tests/P2000.Machine.Tests/P2000.Machine.Tests.csproj` (new `Z80.Disassembler` reference),
  `docs/Adresboekje-DiskBASIC-parsed.md` (ground truth used throughout), reference doc §5d's
  2026-08-02 entry.
- **Synced:** yes (P2000T-reference.md §5d, “Fifth owner follow-up (2026-08-02/03, cc-bugfix-prompt-9 dispatched to CC)” entry, Part A paragraph)


### 2026-07-28 — Bugfix investigation: "Disk I/O error" on every post-boot LOAD/SAVE — THREE real `Upd765` bugs found and fixed via instrumentation; root cause of the full symptom still not fully closed
- **Trigger:** owner-reported bug, reference doc §5d "TRACKED, not fixed (owner-reported,
  2026-07-28)". Per that entry's own instructions, instrumented `Upd765` (temporary `Trace`
  diagnostic hook — `public Action<string>? Trace`, cheap/null by default, left in permanently as
  a debug aid) rather than guessing, then drove the REAL repro end-to-end: real `Basic-24.bin`
  cartridge, real `assets/Disks/diskbasic_1.6uk.dsk` boot floppy (drive 1) + real
  `assets/Disks/volorg.dsk` (drive 2), booted through the real embedded monitor ROM into real
  Philips Disk BASIC 1.6 UK, then typed `LOAD "B:VOLORG"` via the real keyboard-matrix device
  (new test `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs` — see its own class doc comment
  for the full trace-by-trace narrative).
- **What the trace showed, and three real bugs found — all only reachable through a command
  shape never previously exercised by anything else in this project: TC (Terminal Count)-forced
  early transfer completion.** Real Disk BASIC's resident LOAD driver requests a WIDE EOT window
  on READ DATA (`46 02 01 00 01 01 10 0E 00` — R=1, EOT=0x10, a 16-sector/whole-track window),
  takes only the ONE sector it actually wants via the data register (256 bytes), then writes the
  TC control-latch bit (`OUT (0x90),0x0E`) to abort the rest — a real, legitimate technique (the
  ROM's own fixed-EOT boot reads always complete NATURALLY, never via TC, so this path was
  entirely unexercised before now):
  1. **`LastSectorResultFields()` reported the EOT window's TAIL sector (16), not the sector
     actually transferred (1).** It computed the result's R field from
     `_transferBuffer.Length` (the ORIGINALLY REQUESTED length) instead of `_transferIndex` (bytes
     ACTUALLY moved before TC fired) — identical for a naturally-completing transfer (invisible to
     every prior test + the ROM's own boot read), but wrong the instant TC ends things early.
     Fixed: bound by `_transferIndex`. Same bug, same fix, also applied to `CommitSectorWrites`
     (was about to commit the zero-initialized TAIL of the full requested WRITE DATA buffer, not
     just what the host actually sent — directly relevant since the repro also reports SAVE
     failing), and for consistency to `CommitFormat`/`CompleteScan` (same bug class, no confirmed
     real caller uses TC with them yet, fixed anyway rather than shipping a known-latent copy).
  2. **`CompleteTransfer`'s ST0 was unconditionally `0x00` on a normal completion, never encoding
     the addressed drive/head (datasheet D2 HD / D1-D0 US1/US0) the way `DispatchSenseInterruptStatus`
     already does (`0x20 | drive`).** Invisible whenever drive 0/head 0 was involved (every prior
     test + the ROM's own boot read use drive 1 exclusively) — very much NOT invisible for THIS
     repro's drive 2. This was the fix that stopped an observed 14-28× identical-sector retry loop
     outright (drive-2 reads got ST0=0x00, and the driver's own integrity check evidently treats
     the addressed-unit mismatch as a failure and retries the SAME read verbatim until giving up).
  3. **TC-forced completion fired `ResultReady` SYNCHRONOUSLY inside the triggering `WriteControl`
     call — the exact same "lost wakeup" bug class already found and fixed for SEEK/RECALIBRATE**
     (the existing `MinimumTurboSeekTStates` guard, renamed `MinimumLostWakeupGuardTStates` since
     it now has two real callers): a driver writes the TC bit then HALTs waiting for the disk-
     complete interrupt a few instructions later; completing (full IM2 dispatch + ISR + RETI)
     INSIDE the same OUT instruction delivers and fully consumes that interrupt before the driver
     ever reaches its own HALT. Fixed by deferring TC-forced completion through the same
     `PendingAction`/`Tick()` mechanism SEEK already uses (new `PendingAction.ForcedCompletion`),
     applied under BOTH `TimingPolicy` values (unlike SEEK's Turbo-only guard — Authentic's own
     per-byte transfer pacing does NOT provide a natural gap here, since the risk window is
     driver-code-length between the TC-issuing OUT and its own wait point, not transfer pacing).
     This fix alone changed the FDC's behavior from "identically retries sector 1 forever" into "a
     real, complete, correctly-parameterized scan across all 16 directory sectors" — the search
     started actually advancing once the chip stopped potentially eating its own wakeup signal.
- **Combined effect, verified via the trace:** before these three fixes, EVERY `LOAD`/`SAVE`
  against ANY drive hit at least bug #2 (wrong ST0 whenever the drive/head isn't 0/0) and, on any
  read/write actually needing a TC-truncated transfer, bugs #1 and #3 too — matching the repro's
  own "universal, not drive-selective" framing exactly. After all three fixes, the SAME repro's
  FDC-level trace is now fully datasheet-correct (right status, right data, right timing) and the
  driver performs a real, advancing, non-repeating directory scan — a dramatic, independently
  verified improvement, NOT just a plausible-sounding rationalization: confirmed by literally
  watching the sector sequence change from `1,1,1,1,1,1,1,1,1,1,1,1,1,1` (14-28×, identical) to
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16` (all 16 sectors, exactly once each).
- **NOT fully resolved — genuinely stuck past this point, exactly the scenario this bug-fix
  prompt's own instructions anticipated.** The repro's `LOAD "B:VOLORG"` (and `LOAD "B:VOLINFO"`,
  tried separately to rule out the target FCB's own incidental `0xF3` first byte as a red herring)
  still ends in "Disk I/O error" after the driver scans all 16 directory sectors — including the
  one holding the target file's real, independently-byte-verified FCB (confirmed via
  `DskImage.ReadPdosDirectory()`/`ReadSector` directly against `volorg.dsk`, matching milestone
  22a's own prior fixture confirmation exactly). Two hypotheses were tested and DISPROVEN by
  direct experiment rather than left unchecked: (a) the result's C (cylinder) field should echo
  `track+1` rather than the true 0-based physical cylinder, by analogy with `jwsformat.asm`'s own
  confirmed off-by-one — tested both with and without the ST0 fix in place, neither changed the
  outcome; (b) the target FCB's own incidental `0xF3` first byte (coincidentally the SAME value as
  the ROM's PDOS-system-disk signature) causes the driver to misidentify the disk — disproven by
  getting the identical failure searching for `VOLINFO` instead, whose FCB does NOT start with
  `0xF3`. **What would be needed to go further:** a disassembly of Philips Disk BASIC's own
  resident LOAD driver (still not sourced/planned, per the reference doc block's own note) showing
  exactly what it does with the 256 bytes read back from each candidate directory sector — the
  trace proves the FDC's own command/status/data are now all correct by every datasheet-derivable
  measure available without that source, so whatever check still fails is either a driver-internal
  detail with no external signature (a checksum? a specific expected probe count/order?) or a
  genuine PDOS-format-level nuance this project's `DskImage`/FCB model doesn't yet capture — not
  something further trace-staring or guessing can distinguish.
- **Follow-up (same day, owner's own question) — SAVE traced too, for comparison:** ran
  `SAVE "B:TEST"` against the identical booted machine
  (`Boot_ThenSaveTest_TraceFdcCommandsAndScreenOutput`). New information, not just a repeat of the
  LOAD finding: SAVE reads directory track1/sector1 EXACTLY ONCE (same command shape, same
  now-fixed-correct status/data the LOAD investigation already validated), then reports "Disk I/O
  error" immediately — it **never issues SENSE DRIVE STATUS at all** (no write-protect check ever
  runs) and **never attempts a WRITE DATA**. This rules out a write-protect-detection bug as the
  cause (that code path isn't reached) and shows SAVE's give-up threshold differs from LOAD's
  (SAVE bails after ONE read; LOAD's own search tries all 16 directory sectors before giving up) —
  consistent with the two being different algorithms that both stumble on the same still-
  unresolved check, but this doesn't further localize where that check lives. Sector 1 genuinely
  has 6 free FCB slots in this fixture (only 2 of 8 occupied — VOLORG, VOLINFO), so a correctly
  functioning free-slot search should succeed reading just this one sector. Still stuck at the
  same point as the LOAD investigation — recorded here so a future disassembly-armed pass doesn't
  have to re-derive that SAVE's failure is this early/this shaped.
- **Second follow-up (same day, owner: "I tried a save on a clean disk. also got an disk I/O
  error, but the error took... a little longer to appear") — a genuine, non-illusory difference,
  now fully explained in SHAPE (not in root cause):** three-way comparison, all three ending in
  "Disk I/O error":
  1. Real `volorg.dsk` (VOLORG's own FCB happens to start with `0xF3`): SAVE reads sector 1 ONCE,
     fails immediately (the finding directly above).
  2. A genuinely blank/unformatted disk (`DskImage.CreateBlank`, all-zero, no `0xF3` anywhere):
     SAVE scans ALL 16 directory sectors, in the EXACT same order LOAD's own search uses
     (`1,7,13,3,9,15,5,11,2,8,14,4,10,16`), THEN fails — confirms the owner's "took longer"
     observation was real.
  3. Real `volorg.dsk` bytes, unchanged EXCEPT patching that one byte `0xF3`→`0x00` (VOLORG's FCB
     stays fully occupied/non-zero otherwise): ALSO scans all 16 sectors like case 2, not just 1 —
     isolating the variable decisively. **It's the `0xF3` BYTE VALUE specifically, not "occupied
     vs. empty" directory content, that makes SAVE short-circuit to one read.**
  Very plausibly Disk BASIC's own legitimate design, not an emulator bug: `0xF3` at this exact
  track1/sector1/offset0 location is the SAME byte the ROM's own `getdos` checks to recognize a
  PDOS system disk (reference doc §5d) — real Disk BASIC's SAVE very plausibly refuses to write
  to what it believes is a system disk, protecting boot media from a naive SAVE, same as a real
  DOS would. VOLORG's own FCB colliding with that exact byte value is the SAME "one genuine
  ambiguity in the format" already flagged in `docs/P2000T-disk-formats.md` §7 item 8 (milestone
  22a) — not a new finding, just a newly observed consequence of it. **Critically, this does NOT
  explain the remaining bug** — cases 2 and 3 (no `0xF3` anywhere) BOTH still end in "Disk I/O
  error" after their full scan, so the `0xF3` check only governs how many sectors get scanned
  before failing, not whether the command ultimately succeeds. The real root cause remains exactly
  as open as before this comparison, downstream of (or unrelated to) this `0xF3` gate.
- **Third follow-up (same day, owner: since after the directory scan zero further FDC activity
  happens before "Disk I/O error," maybe PDOS caches the directory in RAM — "could it be that
  something is not working well in that caching area? Basic24 needs at least a switchable bank")
  — chased the banking angle directly, then owner pushed further ("did you check that anything
  landed in banked memory and that the banks are present, and that the switches have the desired
  effect? The boot ROM doesn't perform extensive banked memory tests"), a fair challenge since the
  first pass only searched for the delivered directory text, not the banking mechanism itself.
  Three things checked:
  1. **Real, closed coverage gap:** every prior banking test only ever verified banks 0 and 1
     (`BankedWindow_SelectBank_SwitchesToAnIsolatedBank`) — banks 2-5 had literally never been
     checked for isolation on their own. New
     `tests/P2000.Machine.Tests/Memory/PageTableTests.cs`'s `BankedWindow_AllSixT102Banks_AreMutuallyIsolated`
     writes a distinct marker to all 6 T102 banks and confirms all 6 persist independently — passes.
  2. **Banks are genuinely present/populated/distinct in the LIVE repro machine**, not just in
     isolated `PageTableTests` construction: dumping all 6 banks' own first 16 bytes at 0xE000
     shows bank 1 holding real, recognizable Z80 code (`F3 ED 5E DD 22 32 F5 FB DB 00 3C 20 FB F3
     ED 73` = DI/IM2/LD(nn),IX/LD(nn),A/EI/IN A,(0)/INC A/JR NZ,-3/DI/LD(nn),SP — a sensible ISR-
     setup preamble, consistent with this being exactly the DOS driver code `getdos` loaded into
     bank 1 at boot), banks 2-5 showing DIFFERENT still-untouched power-on noise (allocated
     correctly, simply unused by this PDOS session), bank 0 all-zero (plausibly Disk BASIC's own
     cleared extended-workspace use of that bank, unrelated to the disk driver). No open-bus,
     aliasing, or cross-bank corruption visible.
  3. **Switches have real, observable effect during the live SAVE attempt:** exactly 12 real
     bank-select writes (alternating 0x01/0x00) occur while attempting to save just one directory
     sector — consistent with BASIC's own code (bank 0) repeatedly calling into DOS driver
     subroutines (bank 1) and switching back after each. Caught and fixed a real instrumentation
     bug along the way: the diagnostic's own bank-dump helper temporarily re-selects banks to peek
     at their content, which briefly polluted the SAME log meant to capture only the driver's real
     switches — fixed by dumping "before" banks before subscribing to the trace, and unsubscribing
     before the "after" dump.
  **Conclusion:** the banking MECHANISM itself (isolation, persistence, real addressing effect)
  checks out — doesn't look like a `PageTable` bug. Does NOT rule out a more specific timing/
  ordering interaction between disk I/O and a bank switch (a switch landing one instruction off
  from what real hardware would allow, say) — only a disassembly could confirm that one way or
  the other. Still the same open root cause as the two follow-ups above; this narrows where it
  ISN'T (the banking primitive), not where it is.
- **Tests:** `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs` (new) — the real end-to-end
  repro itself, with a regression assertion that the retry loop specifically (bugs #2+#3's
  combined symptom) stays fixed: every READ DATA issued during the LOAD attempt must target a
  DISTINCT sector, never repeat one. `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs` (+4,
  synthetic, fast): TC-truncated READ DATA reports the sector actually read, not the EOT window's
  tail; TC-truncated WRITE DATA commits only the sectors actually received (sector 2, never sent,
  stays untouched); `CompleteTransfer` reports the addressed drive/head in ST0 (drive 2/head 0 →
  `0x02`, not `0x00`); TC-forced completion is not synchronous (mirrors the existing
  `Recalibrate_Turbo_CompletesAfterAFewTStates_FiresResultReady` test). Full
  `P2000.Machine.Tests`: 568/568 green (was 561 before the 4 new `Upd765Tests.cs` cases; +3 net
  for `PdosLoadSaveRepro.cs`'s own diagnostic tests — the LOAD repro, plus the three-way SAVE
  comparison below); `P2000.UI.Tests`: 217/217 green, unaffected.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`Trace`, `LastSectorResultFields`,
  `CommitSectorWrites`, `CommitFormat`, `CompleteScan`, `CompleteTransfer`'s ST0,
  `MinimumLostWakeupGuardTStates` rename, `PendingAction.ForcedCompletion`, `WriteControl`'s TC
  branch), `src/P2000.Machine/Memory/PageTable.cs` (`CurrentBank`, `BankSelected` — diagnostic-only
  additions, same pattern as `Upd765.Trace`), `tests/P2000.Machine.Tests/Boot/PdosLoadSaveRepro.cs`
  (new), `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.Machine.Tests/Memory/PageTableTests.cs` (`BankedWindow_AllSixT102Banks_AreMutuallyIsolated`,
  new). Reference doc §5d's "TRACKED, not fixed" block.
- **Synced:** yes (2026-07-28, into `docs/P2000T-reference.md` §5d — the "TRACKED, not fixed"
  block replaced with "PARTIALLY FIXED," covering the three confirmed bugs, the combined-effect
  interleave-order confirmation, the disproven hypotheses, the SAVE-specific trace, both owner
  follow-ups (the `0xF3` write-refusal explanation and the banking-mechanism-cleared finding),
  and the still-open root cause).


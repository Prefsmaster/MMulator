# P2000T "Disk I/O error" investigation — full history (archived)

This document is the complete, unabridged provenance record of the multi-day "Disk I/O error"
investigation (Parts A through the closing fix), moved out of `docs/P2000T-reference.md` on
2026-08-04 to keep that document readable. **The bug is RESOLVED AND FIXED** — see
`docs/P2000T-reference.md` §5d for the current, short final-state summary (root cause, fix,
verification, and the couple of durable facts/loose ends worth keeping visible day to day).

Nothing in this document should be treated as the current state of anything — it is a historical
record of the investigation as it actually happened: every dead end, every disproven theory, every
intermediate bug found and fixed along the way, in the order they occurred. Kept in full because
several entries explicitly call out real, separate bugs that were found and fixed as a byproduct
of this investigation (three `Upd765` bugs on 2026-07-28, a VRAM-stride test-harness bug, an
`Upd765.CurrentTransfer.Sector` off-by-one, several PC-fetch-timing artifacts in the project's own
trace tooling) — those fixes are real and permanent even though the narrative that found them is
now archived. Cross-referenced throughout by `CLAUDE_machine.md`'s own findings-log entries
(Parts A-I) and several `cc-bugfix-prompt-N.md` files, all still available for anyone who wants
the full agent-level trace behind any specific finding below.

---

**PARTIALLY FIXED (2026-07-28) — THREE real `Upd765` bugs found via instrumentation and fixed;
the owner's original "Disk I/O error" symptom is NOT fully closed, root cause of what remains is
still open.** Original repro (owner-reported, 2026-07-28): 2 drives configured, 35-track SS, the
sourced `Basic24k.bin` cartridge + boot floppy in drive 1, `volorg.dsk` in drive 2 — both
write-enabled. Boot succeeds cleanly into "Philips Disk BASIC, release 1.6 UK, 27568 bytes
free," but every subsequent `LOAD`/`SAVE` failed with "Disk I/O error," on any drive, regardless
of letter. Per this entry's own prior instruction, `Upd765` was instrumented with a trace hook
(kept in permanently as a debug aid) and driven through the real repro end-to-end (real ROM,
real cartridge, real disk images, real keyboard input) rather than guessed at.

**Three real, confirmed bugs found and fixed — all only reachable through a command shape
nothing had exercised before this: TC (Terminal Count)-forced early transfer completion.** Real
Disk BASIC's LOAD driver requests a wide EOT window on READ DATA, takes only the one sector it
wants, then writes the TC control-latch bit to abort the rest — legitimate real technique, but
the ROM's own fixed-EOT boot reads always complete naturally, so this path was never exercised
before now:
1. **The result phase reported the EOT window's TAIL sector, not the sector actually
   transferred** — computed from the requested length instead of bytes actually moved before TC
   fired. Same bug also affected `WRITE DATA`'s commit (would have written the zero-initialized
   tail of the full requested buffer, not just what was actually sent) and, preventively, the
   FORMAT/scan paths (no confirmed real caller uses TC there yet). All fixed to bound by actual
   bytes transferred.
2. **`CompleteTransfer`'s ST0 was unconditionally `0x00`, never encoding the addressed
   drive/head** the way SENSE INTERRUPT STATUS already did. Invisible for drive 1/head 0 (every
   prior test and the ROM's own boot read use drive 1 exclusively) — broke immediately for drive
   2 (this repro). **This was the fix that stopped an observed 14-28× identical-sector retry
   loop outright** — the driver's own integrity check evidently treats an addressed-unit
   mismatch in ST0 as failure and retries the same read verbatim until giving up.
3. **TC-forced completion fired its result-ready signal SYNCHRONOUSLY inside the triggering
   port write** — the same "lost wakeup" bug class already fixed for SEEK/RECALIBRATE (a driver
   writes TC then HALTs waiting for the completion interrupt; completing it inline delivers and
   consumes that interrupt before the driver ever reaches its own HALT). Fixed by deferring
   TC-forced completion through the same deferred-completion mechanism SEEK already uses, applied
   under both timing policies (unlike SEEK's fast-mode-only guard, since here the risk window is
   driver-code-length, not transfer pacing).

**Combined effect, independently verified, not just argued for:** before these fixes, the FDC
trace showed the directory scan repeating sector 1 forever (`1,1,1,1,1,...`, 14-28×); after all
three fixes, it advances through all 16 directory sectors in the exact confirmed physical
interleave order — `1,7,13,3,9,15,5,11,2,8,14,4,10,16` — matching `docs/P2000T-disk-formats.md`
§6a's interleave finding exactly. **This is now a THIRD independent confirmation of that
interleave pattern** (previously: the source docx's own table, and `VOLINFO.BAS`'s real text
reconstructing correctly under it) — this time from watching a real, unmodified Z80 driver's
actual behavior under emulation, not static disk inspection.

**NOT fully resolved — genuinely stuck past this point.** `LOAD "B:VOLORG"` (and separately
`LOAD "B:VOLINFO"`, to rule out a red herring) still ends in "Disk I/O error" after the driver
correctly scans all 16 directory sectors, including the one holding the target file's real,
independently-verified FCB. Two hypotheses tested and DISPROVEN by direct experiment: (a) the
result's cylinder field should echo `track+1` rather than the true 0-based cylinder, by analogy
with `jwsformat.asm`'s confirmed off-by-one — no change either way; (b) the target FCB's own
incidental `0xF3` first byte (the same value as the system-disk signature) confuses the driver —
disproven, since `VOLINFO` (no `0xF3`) fails identically. **What would be needed to go further:
a disassembly of Philips Disk BASIC's own resident LOAD driver — still not available/planned
(owner, 2026-07-28) — showing what it does with the 256 bytes read back from each candidate
sector.** The FDC's own command/status/data are now correct by every datasheet-derivable measure
available without that source; whatever still fails is either a driver-internal check with no
external signature, or a genuine PDOS-format nuance this project's model doesn't yet capture.

**SAVE traced separately (owner's own follow-up question) — a different failure shape, same
open root cause.** `SAVE "B:TEST"` reads directory sector 1 exactly ONCE (same now-correct
command shape) then fails immediately — it never issues SENSE DRIVE STATUS (no write-protect
check ever runs) and never attempts a WRITE DATA. Rules out a write-protect-detection bug;
shows SAVE's give-up threshold differs from LOAD's (one read vs. LOAD's full 16-sector search).

**Second owner follow-up ("I tried a save on a clean disk, also got Disk I/O error, but it took
longer to appear") — real, explained in SHAPE, not in root cause.** Three-way comparison, all
ending in "Disk I/O error": `volorg.dsk` as-is (VOLORG's FCB starts `0xF3`) — SAVE reads sector 1
once, fails immediately. A genuinely blank disk (all-zero, no `0xF3` anywhere) — SAVE scans all
16 sectors in the same confirmed order, then fails. `volorg.dsk` with just that one `0xF3` byte
patched to `0x00` (rest of the FCB unchanged/occupied) — ALSO scans all 16, isolating that it's
specifically the `0xF3` byte VALUE, not occupied-vs-empty content, that makes SAVE short-circuit.
**Very plausibly legitimate real Disk BASIC behavior, not a bug:** `0xF3` at this exact location
is the same byte `getdos` itself checks for a system disk (§5d) — real Disk BASIC's SAVE quite
plausibly refuses to write to what it believes is a system disk, protecting boot media, exactly
as a real DOS would. This is the same "one genuine ambiguity in the format"
`docs/P2000T-disk-formats.md` §7 item 8 already flagged (milestone 22a) — not a new finding, a
newly-observed consequence of it. **Does not explain the remaining bug** — the blank-disk and
patched-disk cases (no `0xF3` anywhere) still fail after their full scan, so this gate only
governs how many sectors get scanned before failing, not whether the command ultimately
succeeds.

**Third owner follow-up (pushing on a banking hypothesis — "Basic24 needs at least a switchable
bank; did you check anything landed in banked memory and the switches have real effect?") —
chased directly, banking mechanism itself cleared, root cause still open.** A real, previously-
unclosed test gap was found along the way: only banks 0-1 (of 6 real T102 banks) had ever been
tested for mutual isolation — banks 2-5 never had a dedicated test; now added and passing. In
the live repro: bank 1 holds real, recognizable Z80 ISR-setup code at `0xE000` (consistent with
`getdos`'s own driver load target), banks 2-5 show distinct untouched power-on noise (correctly
allocated, simply unused this session), bank 0 is all-zero (plausibly Disk BASIC's own cleared
workspace) — no open-bus, aliasing, or cross-bank corruption. Exactly 12 real bank-select writes
occur during a single SAVE attempt, consistent with BASIC (bank 0) repeatedly calling into the
DOS driver (bank 1) and switching back. **Conclusion: the banking mechanism (isolation,
persistence, real addressing effect) checks out — doesn't look like a `PageTable` bug.** Does
NOT rule out a specific timing/ordering interaction between disk I/O and a bank switch; only a
disassembly could confirm that. Narrows where the root cause ISN'T, not where it is.

**Fourth owner follow-up (2026-08-02) — real BASIC-level ground truth sourced for the first time
(a genuine third-party reference document, not disassembly-only reasoning), plus a live manual
repro that both explains one prior mystery and rules out a hopeful theory for this one.** Two new
primary sources: **"P2000 Adresboekje" (Rob Geutskens, 1986)**, a hardware/software memory-map
booklet with a dedicated Disk BASIC/PDOS section (parsed in full, `&H6000`–`&H8A90` plus the BASIC
token table — see the maintainer's own parsed copy), and **the official Philips "Disk BASIC"
manual's Appendix A/B** (BASIC file commands, disk I/O procedures).
- **Named, direct hit in the Adresboekje: `&H6091` = "Flag for Disk I/O error (see `&H69BB`)."**
  `&H69BB` itself isn't separately documented — it falls inside `&H6900`–`&H6ED3`, glossed only as
  "Disk BASIC's own startup routine." That range sits inside `&H6200`–`&H8A90`, which the same
  booklet identifies as the ~8K of the interpreter **loaded from tracks 3/4/5 of the system disk
  at boot**, not the 16K cartridge ROM — i.e. `&H69BB`'s actual code may not be in `Basic24k.bin`
  at all. The token table separately gives real Disk-BASIC entry points for `LOAD` (`&H376F`),
  `SAVE` (`&H3872`), `FILES` (`&H3543`), `OPEN` (`&H33D0`) — these sit in the lower address range
  plausibly inside the ROM half, so likely ARE reachable directly from `Basic24k.bin`.
- **The official manual's Appendix A.4 (`RESET`) states outright: "After DISK I/O ERROR the
  statement RESET has to be given"** — i.e. this error is DOCUMENTED as a normal, recoverable
  condition tied to disk-administration state, not necessarily an FDC-level fault. Also documents
  `SYSTEM A`/`SYSTEM B` (set default drive) and that `RESET` is required after any diskette
  exchange so PDOS re-reads the new disk's administration into RAM.
- **Owner ran a manual repro directly against this, live, in the emulator (2026-08-02) — real,
  reproducible, and it separates two distinct phenomena that were previously conflated:**
  1. Plain `RESET` (default drive still A, the system/boot disk) → "Disk I/O error." Matches the
     Adresboekje's own explanation exactly: a system disk's track 1 does **not** hold a normal FCB
     index (only working disks' track 1 does) — reading it as one legitimately fails. Not a bug.
  2. `SYSTEM B` (switch default drive to B) — **first attempt: "Disk I/O error"; the exact same
     command run again immediately: "OK."** A real, reproducible "needs a retry" quirk on a
     drive-switch command, most plausibly explained by the `&H6091` flag being left set (stale)
     from the prior failed `RESET` and only clearing after being reported once — i.e. the first
     `SYSTEM B` may be inheriting an unrelated earlier failure rather than genuinely failing itself.
  3. `FILES` (now on drive B) correctly listed both real files (`VOLORG.BAS`, `VOLINFO.BAS`) —
     directory scan continues to check out, consistent with everything established above.
  4. **`RUN"VOLORG"` → "Disk I/O error" — tried a second time immediately, specifically to test
     whether the same "retry clears it" pattern applies here too. It does NOT: identical failure
     both times.** This cleanly rules out "stale drive-switch flag" as the explanation for the
     original LOAD/RUN bug specifically (unlike `SYSTEM B`), while further confirming the failure
     is downstream of a working, correctly-scanned directory and a verified-present FCB — i.e.
     genuinely stuck in the file-transfer path itself, exactly where this entry left off above.
- **Net effect: the `SYSTEM B` stale-retry behavior is a real, separate, minor bug worth its own
  narrow fix** (candidate mechanism: `&H6091` not being cleared/re-evaluated at the right point
  relative to a drive switch) **— but is now confirmed NOT to be the LOAD/RUN root cause.** The
  original mystery is narrowed, not solved: something in the actual data-transfer stage, after FCB
  lookup, still fails, unaffected by drive/administration state being otherwise clean.

**Fifth owner follow-up (2026-08-02/03, cc-bugfix-prompt-9 dispatched to CC) — Part A RESOLVED
(not a bug), Part B significantly NARROWED (still open).** Following directly from the fourth
follow-up above, CC instrumented the owner's exact manual repro live (`&H6091` traced across
`RESET`→`SYSTEM B`×2→`FILES`→`RUN"VOLORG"`×2→`FILES`) before doing any disassembly, then
disassembled two narrow, specific regions to explain what the trace showed.
- **Part A — `SYSTEM B`'s stale-retry quirk: CONFIRMED real, correct PDOS behavior, NOT a bug,
  no fix needed.** Every PDOS call returns through a wrapper at `&H698D`–`&H69D5` — code that
  lives in the ~8K disk-loaded interpreter chunk (`&H6200`–`&H8A90`), not in the 16K cartridge
  ROM, matching the Adresboekje's own account. Reconstructed that chunk directly from
  `diskbasic_1.6uk.dsk` (tracks 3–5, plain sequential read) and confirmed it byte-for-byte via
  the literal string "PHILIPS DISK BASIC" landing exactly where the Adresboekje predicts
  (`&H693D`). The wrapper reads a result/class byte PDOS's driver leaves at `&H60BB`: classes
  `{0x02, 0x0A, 0x0B, 0x0C}` unconditionally set `&H6091=2` and jump to the error-print path
  (`&H69BB`, matching the Adresboekje's naming exactly) regardless of whether the underlying disk
  operation actually succeeded; class `0x1A` leaves `&H6091` completely untouched (the real
  "stale flag persists" mechanism); anything else clears it (success). `SYSTEM B`'s own
  drive-select call returns class `0x02` on its first call — one of the unconditional-error
  classes — which is why the flag gets set and the error prints, with no fault at all in the
  FDC-level directory scan underneath it. *Why* the second call returns a different class is
  decided inside PDOS's own driver code (loaded from boot-floppy tracks 1–2, a third region not
  yet disassembled) — flagged as out of scope for this pass, not guessed at. Consistent with the
  manual's own "after DISK I/O ERROR the statement RESET has to be given."
  - **Bonus: a real bug found and fixed in the investigation's own test tooling (not the
    emulator).** `SnapshotScreenText` used a 40-byte VRAM row stride; the real layout is 80
    bytes/row (`Video.cs`'s own `BufferColumns=80` + `PanX` windowing) — confirmed via a raw VRAM
    hex dump showing every real line starting on an 80-byte boundary. The 40-stride bug only ever
    exposed the first 12 of 24 real rows and happened to still look plausible for short messages,
    which is how an earlier pass wrongly concluded "`SYSTEM B` hangs forever, prints nothing."
    Fixed in the new `DiskIoErrorFlagTrace.cs`'s copy of the helper; **the identical helper in
    `PdosLoadSaveRepro.cs` still has the bug** — explicitly left unfixed (out of scope), flagged
    for whoever next touches that file.
- **Part B — the original LOAD/RUN "Disk I/O error": FDC/CTC/interrupt emulation now PROVEN
  entirely correct; the real gap is narrower and still open.** A full trace
  (`Channel0InterruptDuringGapDiag.cs`, new permanent regression guard) shows CTC channel 0
  (wired to the FDC completion interrupt) firing and delivering correctly for all 15 real READ
  DATA completions in `RUN"VOLORG"`'s directory scan, in the confirmed interleave order — no
  missed interrupt, no delivery bug anywhere in that layer. But after the last directory sector
  completes, the FDC trace goes completely silent: **`RUN"VOLORG"` never attempts to read
  VOLORG's actual file data at all**, despite its FCB being confirmed present and correctly
  located. Execution instead falls into a hardcoded 65536-iteration busy-wait/timeout loop
  (`docs/PDOS_wip.asm`'s `le95fh`/`le962h`) that exists to be interrupted early by a real disk
  operation's own completion (a stack-manipulation redirect at `&H6135` installed by
  `sub_e8c3h`) — since no operation was ever started, nothing redirects it, and it burns through
  its full ~3.8M-T-state duration before `channel_time_out`/`sub_e943h` fires, writes the generic
  "always error" class `0x02` into the same result byte the Part A wrapper reads, and turns off
  the FDC motor. This is what actually prints the error, independent of whatever the directory
  scan itself found.
  - **What's now open (narrower than the 2026-07-28 entry's "genuinely stuck, FDC trace shows
    everything correct"):** WHY does PDOS's own logic, immediately after successfully finding
    VOLORG's FCB, decide NOT to proceed to reading the file's data — falling instead into a
    busy-wait designed for "wait for an actual disk operation," not "there's nothing to wait
    for"? That decision sits between the directory-scan dispatch and `sub_e7abh`/`le7b0h` (the
    real file-data-read entry point) — most likely a real FCB validation/allocation-map check
    the `volorg.dsk` fixture's FCB doesn't satisfy (a possible fixture-content issue, not
    necessarily an emulator bug), but this is flagged, not confirmed. Needs disassembly of that
    specific narrow region; the owner's own in-progress `docs/PDOS_wip.asm` annotation is the
    natural next place to look.
  - **Connects to, rather than contradicts, `FILES`'s own trailing "Disk I/O error"** (noted in
    the fourth follow-up above): `FILES`'s directory read also genuinely succeeds and still ends
    in the same error, suggesting the same class of bug — something downstream of a successful
    directory operation still trips the error path, possibly `FILES`'s own end-of-listing step
    hitting a structurally similar "expected to be interrupted, never is" gap. Not confirmed to
    be the identical code path.
  - **Small, separate loose end, not conflated with the main finding:** the directory scan only
    ever reads 14 of the 16 sectors the confirmed interleave defines per track
    (`1,7,13,3,9,15,5,11,2,8,14,4,10,16`, stopping just before sectors 6/12) — true in both
    failing (`RUN`) and succeeding (`FILES`) cases alike, so it doesn't look like the cause of
    the error itself. Left as its own small open question. **UN-FLAGGED (Eighth owner follow-up
    below) — this was never a separate, unrelated curiosity. It is very likely the actual root
    cause of the entire investigation:** the same 14-of-16 sector-advancement limitation governs
    real file-data reads too, and is what actually stops `RUN"VOLORG"` short, long before any
    CP/M-level EOF condition would legitimately end the read.

**Sixth owner follow-up (2026-08-03, cc-bugfix-prompt-10 dispatched to CC, leaning on the owner's
own growing `docs/PDOS_wip.asm` annotations as primary ground truth) — the "why does PDOS skip
the file-data read" question is narrowed FURTHER, but still NOT resolved.**
- **The dispatcher is never even asked.** A call-site-level trace (new regression test,
  `Sube943hCallerDiag.cs`) confirms exactly one real call to `sub_e943h` for the whole
  `RUN"VOLORG"` attempt, from `channel_time_out` (`0xE978`) — ruling out the two other candidate
  call sites that were still open at the end of the fifth follow-up above. A second trace
  (`DispatchFunctionCodeTraceDiag.cs`) confirms PDOS's own top-level dispatcher
  (`CPM_entry_point`, `0xE000`) NEVER receives any function code beyond `0x0F`/`0x1A`/`0x14`
  across all 29 dispatcher entries in the attempt — in particular, `0x39` (this investigation's
  own working hypothesis for "the real file-data-read trigger," per `docs/PDOS_wip.asm`'s
  `le229h`) is never sent. The directory scan simply cycles `0x1A`/`0x14` through all 14 real
  sectors and stops — no further dispatcher entry of any kind follows, straight into the
  busy-wait. So the failure is not a PDOS-dispatch-level branch going the wrong way; PDOS is
  simply never asked to do anything else.
- **Both `0x1A` and `0x14` route to the identical PDOS-side handler**, confirmed directly in
  `docs/PDOS_wip.asm`: `le12dh`'s dispatch chain sends both (with several other codes) to
  `le149h` → `sub_e705h`, which stashes the function code and FCB pointer and calls `sub_f2fdh`
  (`0xF2FD`) — since PDOS's own top-level dispatcher treats `0x1A`/`0x14` identically, the real
  per-code behavior must live inside `sub_f2fdh`. **CORRECTED below (Seventh owner follow-up) —
  this specific claim ("identical handler") was a static read of the annotated dispatch chain,
  never live-confirmed, and turned out to be wrong: `0x1A` and `0x14` reach `sub_f2fdh` but land
  on two DIFFERENT jump-table targets, not a shared one.**
- **`sub_f2fdh` is a SECOND, internal jump table, indexed by the same function code — this is
  PDOS's actual FCB-compare/decision engine, and the concrete, narrowed location of whatever
  decides "keep scanning" vs. "found it, transition to a real read."** Confirmed structurally
  (computes `lf307h + 2*code`, jumps to the stored address) but NOT yet functionally — the
  table's raw bytes currently mis-disassemble as garbage code (nothing marks that region as
  data yet), and roughly a dozen distinct case-handler subroutines sit beyond it, unread beyond
  confirming they exist. **None of `sub_f2fdh` or its case handlers are in the owner's own
  `docs/PDOS_wip.asm` yet — this is CC's own raw disassembly, explicitly flagged as reaching only
  far enough to find the jump-table shape, not to read any handler's actual behavior.** **Update
  (Seventh owner follow-up below): this "structurally, not yet functionally" caveat was the load-
  bearing one — the jump table's existence was real, but which targets it actually produces for
  this repro was not yet live-confirmed when this entry was written, and the "roughly a dozen"
  scope has since collapsed to exactly 3.**
- **What's still open, narrower than before:** two live possibilities, not yet distinguished — (a)
  `sub_f2fdh`'s own `C=0x14`/`C=0x1A` handlers run a real FCB-name-compare and, for this fixture's
  actual FCB content, never signal "match found, transition to read" (the fixture-/FCB-validation
  theory from the fifth follow-up, now localized to a specific jump table rather than "somewhere
  in PDOS"); or (b) the handlers DO signal a match correctly, but whatever should act on that
  (issuing `0x39` or equivalent) is a decision made on the BASIC side — the disk-loaded LOAD/RUN
  token driver, a different code region from PDOS's own bank-1 driver, per the Part A `&H698D`-
  `&H69D5` wrapper finding — and that decision never gets made. Disambiguating needs disassembling
  `sub_f2fdh`'s `C=0x14`/`C=0x1A` jump targets and their sub-handlers (a dozen-plus unread
  subroutines) — a substantial follow-on task, explicitly not attempted this pass, per this
  project's own "narrow, don't guess" convention. **Both possibilities remain live after the
  Seventh follow-up below — it pins down WHERE to look (3 exact addresses) but not yet WHICH of
  (a)/(b) is true, since none of the 3 handler bodies has been read yet.**

**Seventh owner follow-up (2026-08-03, cc-bugfix-prompt-11 dispatched to CC — the owner's own
direct question, "are the un-disassembled pieces of code even being hit at all?") — live-trace
CONFIRMS `sub_f2fdh` really is reached, but CORRECTS the Sixth follow-up's "identical handler"
claim and re-sizes the remaining work precisely.**
- **Result 1: execution genuinely reaches `sub_f2fdh` — confirmed by real PC observation, not
  inference.** 30 genuine calls across the whole `RUN"VOLORG"` attempt, each verified via the
  actual `CALL sub_f2fdh` bytes (`CD FD F2`) at the return address, applying the same
  don't-trust-a-bare-PC-match discipline the `sub_e943h` trace already established. Answers the
  owner's question directly: this is neither "zero, unreached" nor "a wholly different,
  undocumented path" — the Sixth follow-up's citation of the dispatch chain into
  `sub_e705h`/`sub_f2fdh` was directionally correct.
- **Result 2 — corrects the Sixth follow-up's own claim: `0x14` and `0x1A` do NOT share a
  handler.** Three distinct routing codes actually reach `sub_f2fdh` in this repro — `0x0F` (×1),
  `0x14` (×14), `0x1A` (×15) — and each lands on its OWN distinct jump-table target: `0x0F` →
  `0xF370`, `0x14` → `0xF3A0`, `0x1A` → `0xF3CA`. Every one of the 30 computed targets was
  independently reconfirmed by directly observing the CPU's PC actually arrive there (30/30 —
  not just a static table read). **This sizes the next disassembly pass at exactly 3 handler
  addresses, not "a dozen unread subroutines"** — the dozen-plus candidate subroutine list the
  Sixth follow-up's raw-byte read turned up remains unconfirmed as to which, if any, correspond
  to these three targets; the targets themselves are now known precisely, their bodies are still
  undisassembled.
- **A genuine, flagged discrepancy, not explained away: 30 `sub_f2fdh` entries vs. the Sixth
  follow-up's own 29 top-level dispatcher entries.** One more call reaches `sub_f2fdh` here than
  reaches `CPM_entry_point` (`0xE000`) at the top level — meaning at least one call in this repro
  does NOT originate from BASIC's own top-level `CALL &H6205` PDOS invocation. `sub_e705h` has a
  second, alternate entry point (`sub_e706h`, immediately following it in `docs/PDOS_wip.asm`,
  skipping the `ld c,a` step and assuming C is already set) — most likely explanation: PDOS's own
  internal code calls into `sub_e706h` directly at least once, reusing the same code space for an
  internal purpose, bypassing BASIC's dispatch entirely. Not confirmed — flagged as an open,
  secondary thread, not chased further this pass. Relatedly, this repro's single `0x0F` arrival
  here is confirmed NOT to come from the same `0x0F` branch the Sixth follow-up traced at the
  top-level dispatcher (that branch's own body never calls `sub_e705h`) — so it's part of the same
  "bypasses the top dispatcher" discrepancy, not a contradiction of the Sixth follow-up's separate
  `0x0F` account.
- **Net effect on the open question:** still not resolved — but now precisely scoped. The next
  step is disassembling exactly three handler bodies at `0xF370` (`0x0F`'s target),
  `0xF3A0` (`0x14`'s target), and `0xF3CA` (`0x1A`'s target) to determine whether they run a real
  FCB-compare that legitimately never matches this fixture (theory (a) above) or signal a match
  that something downstream fails to act on (theory (b)) — plus, if convenient, chasing the
  `sub_e706h` direct-entry discrepancy as a secondary thread.

**Eighth owner follow-up (2026-08-03, cc-bugfix-prompt-12 + a decisive mid-investigation
addendum) — LIKELY RESOLVES the original "Disk I/O error" investigation. Neither of the
Seventh follow-up's two theories was correct; a third, better-supported explanation is confirmed
instead.**
- **The decisive new input: PDOS's function codes are a direct CP/M 2.2 BDOS clone, not an
  independent design.** `0x0F`/`0x14`/`0x1A` match standard CP/M 2.2 BDOS exactly — F_OPEN
  (Open File), F_READ (Read next sequential 128-byte record), F_DMAOFF (Set DMA address)
  ([seasip.info/Cpm/bdos.html](https://www.seasip.info/Cpm/bdos.html)) — consistent with the
  dispatcher already being labeled `CPM_entry_point` in the existing disassembly. Real CP/M's own
  directory-search functions (F_SFIRST/F_SNEXT, `0x11`/`0x12`) are confirmed never called here
  (Part C/D). This reframed everything: the 14-15 physical reads every prior entry here labeled
  "the directory scan" were never actually checked against the documented directory track/sector
  location — `0x1A`/`0x14` alternating is the textbook CP/M idiom for reading a file's DATA, not
  for searching a directory.
- **Disassembly of the three handler bodies confirms this directly.** `0x0F`→`0xF370` matches
  F_OPEN's job (locates/primes a file's working state via `sub_f2d4h`/`sub_f068h`).
  `0x14`→`0xF3A0` is a byte-for-byte match to F_READ's own textbook shape: it reads the FCB's real
  CP/M `CR` (current record, FCB offset `+0x20`) and `RC` (record count, offset `+0x0F`) fields
  directly, comparing `CR < RC` (issue the next physical read) vs. `CR >= RC` (real CP/M EOF,
  return immediately). `0x1A`→`0xF3CA` matches F_DMAOFF exactly: a pure buffer-priming operation
  with no disk I/O of its own.
- **Live trace confirms all three theories from the Seventh follow-up are wrong, and pins down
  what's actually happening:**
  - VOLORG's own FCB (confirmed real bytes on `volorg.dsk`: name `"VOLORG  "`, ext `"BAS"`,
    record count `0x2C`=44, allocation records `{4,5,6,7,12,13,14,15,16,17,18}`) is the active FCB
    from the very first relevant read and stays so throughout — **theory (a) (a failing FCB
    compare) is directly disproven.**
  - The physical FDC reads: one initial read on track 1 (the directory — F_OPEN locating
    VOLORG's FCB), then **all 14 remaining reads target track 2 — VOLORG's OWN real data track**,
    independently confirmed from its allocation map. The read pipeline is doing exactly the right
    thing. **Theory (b) (a correct match nothing acts on) is also not right** — PDOS's F_READ
    machinery is genuinely, repeatedly, successfully acting on VOLORG's real data.
  - Tracked directly off VOLORG's FCB: **`RC` stays constant at 44 throughout (never corrupted);
    `CR` only ever reaches 13 — nowhere near 44.** CP/M's own EOF condition (`CR>=RC`) is
    confirmed to NEVER fire in this repro — there is no EOF condition here to mishandle at all.
  - **The real, confirmed mechanism:** the physical-sector-read-advancement machinery underlying
    BOTH directory reads and file-data reads shares a genuine limitation that stops exactly 2
    sectors short of a complete 16-sector track, every time, regardless of context — and this
    happens long before any CP/M-level EOF condition would legitimately end the read. Once that
    limit is hit, no further FDC command is ever issued (consistent with every prior part's own
    observation), and the hardcoded busy-wait/timeout eventually fires, producing "Disk I/O
    error" — not a disk-content problem with `volorg.dsk`, not a dropped BASIC-side decision, but
    this shared sector-advancement limitation.
- **This directly UN-FLAGS the "14-of-16 sectors" loose end from the Fifth follow-up above (Part
  B, 2026-07-28's own original observation) — it was never a separate, unrelated curiosity; it is
  very likely the actual root cause of the entire "Disk I/O error" symptom chain across this whole
  investigation.**
- **`volorg.dsk` is very likely NOT a damaged/bad fixture** — the read pipeline (FCB location,
  F_READ dispatch, physical track targeting, `CR`/`RC` bookkeeping) all work correctly right up to
  the sector-advancement limit; nothing about VOLORG's own FCB or file content is implicated. Worth
  telling the owner directly: the P2500/CP/M image search that prompted this whole reframing may no
  longer be needed to rule out fixture damage — though independent confirmation is still welcome.
  A related, real bug was also found and fixed in the investigation's OWN test tooling along the
  way: `Upd765.CurrentTransfer.Sector`, sampled at a transfer's `COMPLETE` event, was reporting one
  sector past the one that actually just finished (an off-by-one in how the sample point relates
  to the already-advanced transfer-byte-count) — corrected in the new trace, not a change to
  `Upd765` itself.
- **What's NOT yet resolved:** the EXACT mechanism inside the shared sector-advancement code that
  causes the 2-sector shortfall (candidate location: `leb9eh`/`sub_f447h`/the `lf555h`
  interleave-lookup table, per a brief look this pass) — a genuinely different, much narrower
  disassembly task than anything attempted so far, not started this pass. Also still open,
  unchanged: the 30-vs-29 `sub_e706h` direct-entry discrepancy from the Seventh follow-up (a quick
  recheck this pass found no additional cheap signal).

**Ninth owner follow-up (2026-08-04, cc-bugfix-prompt-13) — the three candidate "2-sectors-short"
mechanisms are ALL EXONERATED; the investigation re-narrows rather than closes.**
- **The `lf555h` interleave table is confirmed COMPLETE, not short** — its raw bytes (read
  correctly this time, not mis-rendered as garbage instructions) are the full 16-entry sequence
  `1,7,13,3,9,15,5,11,2,8,14,4,10,16,6,12`; indices 14/15 genuinely hold the "missing" sectors 6
  and 12.
- **The table-index computation (`sub_f447h`, called from `lebe5h`/`0xEBF0` — NOT `leb9eh`'s own
  internal check at `0xEB9E`, which is an unrelated subtraction) is a clean, UNCAPPED linear
  counter in both the `SYSTEM B` and `RUN"VOLORG"` contexts.** Live-traced: it cleanly advances
  `0,1,2,...,13`, carry is FALSE on every call (no boundary/underflow condition ever fires), and
  every table byte read matches the confirmed interleave. Called 1-2 more times, this exact code
  would correctly compute indices 14/15 and read the real sectors 6/12. **None of the three named
  candidates contain any cap at all — this disproves Part E's own working hypothesis for where
  the limit lives.**
- **What this means: the "stop after 14" decision is made entirely OUTSIDE PDOS's own bank-1
  driver code.** Combined with Part D's dispatcher trace (nothing beyond the 14th `0x14`/F_READ
  call ever reaches PDOS — not a 15th read, not F_CLOSE, nothing) and Part E's CR/RC tracking
  (CR stops at 13, RC stays at VOLORG's genuine 44, EOF never triggers): PDOS's driver is a
  passive, correctly-functioning component that would read further if asked. Whatever decides to
  stop asking lives in BASIC's own record-reading loop — a different, disk-loaded code region
  from PDOS's own bank-1 driver (`docs/PDOS_wip.asm`) — not yet disassembled as of this entry.
- **Owner follow-up experiment, same day: directly disproves a "VOLINFO's FCB byte 15" coincidence
  theory.** The owner patched VOLINFO's own FCB byte 15 from `0x0E`(14) to `0x2C`(44, matching
  VOLORG's) on a real disk image and re-ran the repro — same stop-at-14 effect, unchanged. The "14"
  is not read from or influenced by VOLINFO's FCB at all. Also confirmed: **BASIC issues 14
  discrete, per-record `0x1A`/`0x14` call PAIRS through the top-level dispatcher — not one bulk
  "load the file" call that lets PDOS loop internally.** The stop decision is made on BASIC's own
  side, between individual calls, reinforcing the "external to PDOS" conclusion via a second,
  independent line of evidence.
- **Does NOT resolve whether this is correct, faithful P2000 behavior or a genuine bug — still
  open in either direction as of this entry.** VOLINFO's own real FCB byte 15 = 14 (a different
  file that genuinely only needs 14 of its 16 allocated sectors) vs. VOLORG's own byte 15 = 44
  (needs far more) makes an intentional 14-sector stop implausible for VOLORG specifically, but
  why BASIC's loop would stop early regardless is not yet identified.
- **A real instrumentation bug found and fixed along the way, not an emulator bug:** the new
  trace's first pass read the wrong memory cell for `sub_f447h`'s subtrahend (missing a double
  pointer indirection — `(0xF662)` holds a pointer, not the value itself), producing nonsensical
  index values that briefly looked like a genuine out-of-bounds bug before the fix revealed it was
  a test artifact.

**Owner real-hardware corroboration (2026-08-04) — a genuine P2000M, real floppies, no errors.**
The owner booted an actual P2000M into Disk BASIC and ran `FILES` and `LOAD "VOLORG"` from real
floppy disks (not the `.dsk` image fixtures — content parity between the two is NOT confirmed).
**Both completed with no errors, as expected on working hardware.** This doesn't settle the
open question above on its own (the physical floppy's actual byte content may differ from
`volorg.dsk`), but it's a real, independent data point weighing against "this 14-sector stop is
correct, faithful P2000 behavior" — genuine hardware running the equivalent operation is not
observed to fail the same way. **Owner-observed follow-up detail: watching the real drive LED
during `FILES`, it flashes repeatedly rather than staying lit continuously** — consistent with,
and independent physical corroboration of, Part G's own disassembly-derived conclusion that
BASIC issues discrete, separate per-sector operations rather than one bulk multi-sector transfer.
Real hardware genuinely behaves the discrete way the ROM disassembly says it should; this isn't
an artifact of how the emulator's own trace tooling observes things.

**Tenth owner follow-up (2026-08-04) — found and disassembled BASIC's own read loop (cartridge
ROM, not PDOS); corrects a mid-investigation working hypothesis; re-narrows the open question
further and pauses there at the owner's request.**
- **The three fixed call sites in `Basic-24.bin` that issue every PDOS call for this repro are now
  pinned down precisely:** `0x3487` (F_OPEN, once), `0x32A8` (F_READ, 14×), `0x32D0` (F_DMAOFF,
  14×) — matching Part D's dispatcher-level counts exactly, now at real ROM addresses.
  Disassembled directly from `Basic-24.bin` via the project's own `Z80.Disassembler` (this is
  cartridge ROM content, fully available — unlike PDOS's disk-loaded driver).
- **The loop structure around the two repeated call sites:** `0x323A` loads a pointer from fixed
  cell `0x63A3`, checks a 2-byte counter at `[pointer+0x26..0x27]` — if nonzero, decrements it and
  returns ONE BYTE from a computed position in a 256-byte buffer (a byte-by-byte program scanner,
  not a per-sector operation). Once that counter hits zero, falls through to check a SECOND,
  separate 2-byte counter at `[pointer+0x24..0x25]` — if that is also zero, the loop exits
  (`0x3279`); otherwise it calls `0x327F`, which issues one real DMAOFF+READ pair to refill the
  256-byte buffer. `0x63A3` itself is set once, inside LOAD's own setup code (`0x376F`-`0x3830`,
  previously documented in Part B).
- **Live-tracing the first counter CORRECTS a working hypothesis formed while reading the code
  cold.** It is NOT "records remaining, decremented once per sector" as first assumed — it's a
  byte-level scan counter, entered ~3300 times for the whole attempt (far more than 14). Confirmed
  precisely: **exactly 13 full 256-byte scan cycles occur (3328 bytes total) before the loop stops
  — not 14 —** and the last cycle ends exactly at 0, not cut short mid-buffer. Reconstructing
  VOLORG's own real byte stream from `volorg.dsk` found no plausible "end of program" marker (no
  null link-pointer, the usual tokenized-BASIC convention) anywhere near that 3328-byte boundary —
  so this isn't simply "BASIC correctly found the program's real end."
- **What's now precisely un-explained, narrower than before:** the loop's real exit condition is
  governed by the SECOND counter (`[pointer+0x24..0x25]`), checked only once the byte-scan counter
  empties — its live value, initial value, and update rule have not yet been traced. The exact
  relationship between "13 full byte-scan cycles," "14 real disk sector reads" (the 14th disk read
  fetches a sector never actually consumed by the byte-scanner before the loop exits), and this
  second counter's own threshold is not yet reconciled.
- **Investigation deliberately paused here, at the owner's own explicit request, to decide how to
  continue** — not because a natural stopping point was reached.

**Eleventh owner follow-up (2026-08-04, cc-bugfix-prompt-14) — LIKELY CLOSES the root-cause
question, though not the investigation itself: this is not BASIC's loop gracefully deciding to
stop after 14. It's a genuine hang on the 14th operation.**
- **All three candidate exit mechanisms inside BASIC's own loop are directly disproven by live
  trace:** the second counter (`[pointer+0x24..0x25]`, Part G's own leading candidate) is set
  once, to 256, and never changes for the whole loop — it cannot be what triggers the exit.
  F_READ's own EOF return value (`A=1`) never occurs — A is confirmed always `0` (success) — and
  working through the actual branch polarity shows `A=0` makes the loop continue, not exit, so
  this path could never have been the mechanism even if EOF had fired. A third,
  previously-unexamined leading check inside `0x323A` itself (`[pointer+0]==3`, jumping to a
  different ROM address) also never fires — that byte stays `1` throughout.
- **The real reconciling fact: only 13 genuine F_READ returns are ever observed, even though 14
  real physical disk completions were already confirmed (Part E).** Precisely timed: the last
  genuine byte-scan loop entry happens BEFORE the 14th physical disk completion — meaning the
  13th cycle's own completion is what triggers the 14th real disk read (genuinely issued,
  genuinely completes at the FDC level), but that 14th call's own `CALL 6205h` never returns to
  BASIC, ever, within any reasonable trace window.
- **Conclusion: BASIC's own loop logic is correct — none of its three exit checks are broken.**
  BASIC is correctly, faithfully waiting for its 14th F_READ call to return; it never does. This
  is the SAME busy-wait/timeout mechanism already fully diagnosed at the PDOS/bank-1 level in
  Parts B/C (`busy_wait_for_interrupt` → `channel_time_out` → `sub_e943h`) — now understood to be
  triggered from a REAL, physically-completing 14th disk operation, not "no further command ever
  issued" as Part E's framing suggested (accurate for the T-state window it examined, incomplete
  for the full picture). "Disk I/O error" is a genuine hang on one specific real operation, not an
  early, deliberate stop.
- **Still open, now narrowed to a specific kind of question:** WHY does the 14th disk operation's
  own completion (confirmed physically real at the FDC level) fail to deliver its
  interrupt/redirect back into a normal return from the `CALL 6205h` that issued it, when the
  identical mechanism worked correctly for the prior 13? This is now about the specific
  interrupt-redirect/completion-signaling path for exactly one call, not about counters, EOF
  checks, or loop logic. Not yet reconciled with the real-P2000M no-error result — floppy-content
  parity with the `.dsk` fixtures is unconfirmed, so this could point to a fixture/content
  difference, an emulator timing/interrupt bug specific to a 14th same-track sequential operation,
  or something else entirely.

**Owner follow-up (2026-08-04) — CORRECTION to this entry's own first draft (twice over: which
disk was tested, AND how far the conclusion reaches). The second disk tested is the SYSTEM/boot
disk itself (the one whose tracks 3-5 get loaded as PDOS's disk-loaded driver chunk, and whose
track 1 is scanned at `RESET`/boot) — NOT a `volorg.dsk`-equivalent data disk. A second,
independently-sourced boot disk (`.imd` format, QWERTZ keymap, evidently a different
regional/market system-disk variant/pressing from `diskbasic_1.6uk.dsk`, the fixture this whole
investigation's PDOS disassembly was reconstructed from since Part A) reproduces the IDENTICAL
"Disk I/O error" symptom pattern.** **What this rules out, precisely, per the owner's own
correction: that the BOOT/system disk specifically is a faulty/corrupted fixture** — two boot
disks from evidently different origins (a different keyboard-layout variant implies a different
regional pressing, not just a copy of the same file) both trigger the same failure pattern,
meaningful corroboration that the driver code disassembled from `diskbasic_1.6uk.dsk` in Parts
A/E/F isn't an artifact of one damaged system-disk copy. **What it does NOT rule out: `volorg.dsk`
itself (the data disk holding VOLORG.BAS, the actual file `RUN`/`LOAD` targets throughout this
investigation) being a bad or unrepresentative fixture — that remains exactly as open as before.**
The two disks play different roles in the repro (one supplies the driver code that runs, the
other supplies the file data that code reads) and testing one says nothing about the other. Also
doesn't by itself distinguish "genuine emulator bug" from "a characteristic shared by how these
system disks are formatted that the emulator mishandles regardless of which specific disk" —
consistent with the eleventh follow-up's own finding that PDOS's read pipeline and BASIC's loop
logic are both independently confirmed correct.

**Twelfth entry (2026-08-04, cc-bugfix-prompt-15) — ROOT CAUSE FOUND AND FIXED. This closes the
entire multi-day "Disk I/O error" investigation, Parts A through I.**
- **The question this pass had to reconcile:** Part B's own original trace said channel 0 (the
  FDC completion interrupt) fires and delivers correctly for all 15 real completions in this
  repro, including the 14th. Part H found the 14th `CALL 6205h` never returns. Both can't be the
  full picture — this pass traced the completion-to-return path for the 14th operation
  specifically to find out why.
- **CONFIRMED: the redirect mechanism (the stack-manipulation redirect at `&H6135`, installed by
  `sub_e8c3h`/`issue_Disk_read_command`) is never mis-armed, and channel 0 genuinely fires and
  delivers for the 14th completion too** — extending, not contradicting, Part B's original claim.
  All 15 physical reads issue via `sub_e8b3h`; `(0x6135)` is patched to the redirect landing point
  (`le916h`) all 15 times; PC genuinely reaches `le916h` all 15 times; CTC channel 0's interrupt
  cycle completes cleanly for all 15.
- **THE DECISIVE FINDING: what got interrupted differs, only for the 14th completion.** `le916h`'s
  own `pop hl` retrieves the CPU's int-ack-pushed return address. For completions 1-13, this is
  always exactly `0xE969` — a safe, throwaway point inside `busy_wait_for_interrupt`'s own idle
  loop. **For the 14th completion, it is instead `0x6150`** — a different, but equally clean,
  instruction boundary, this time INSIDE `dsk_in_loop`, PDOS's own semi-DMA byte-transfer polling
  loop — i.e. the interrupt catches the CPU still mid-transfer, not already idling in the
  busy-wait.
- **Why, precisely, and why only for the last sector of the track:** PDOS's own FDC command always
  requests a wide EOT window (EOT fixed at 16, R varies per call) while its software polling loop
  only ever consumes exactly one sector. For every read except the last, this leaves the transfer
  well short of its nominal buffer length, so completion can only come from PDOS's own explicit
  terminal-count (TC) write — which this emulator deliberately defers by
  `MinimumLostWakeupGuardTStates` (200 T-states, a real, intentional fix already landed
  2026-07-28 to prevent exactly this class of "lost wakeup" race). That deferral gives the CPU a
  comfortable head start to reach the busy-wait's idle loop before the interrupt arrives — which
  is why completions 1-13 land safely at `0xE969`. **But for the LAST sector of the track (R=16),
  the EOT window collapses to exactly one sector — matching what the software polls exactly — so
  the transfer instead completes via `Upd765.ReadData`'s own NATURAL, perfectly SYNCHRONOUS
  end-of-buffer check, which had NO equivalent settle delay.** The interrupt fires immediately, in
  the same T-state as the CPU's own final byte-transfer instruction, catching the CPU still inside
  `dsk_in_loop` rather than already idling.
- **The full failure chain, end to end:** PDOS's redirect handler unconditionally discards
  whatever return context was interrupted, assuming it's always the disinterested busy-wait loop
  — for the 14th completion this instead discards `dsk_in_loop`'s own live resume point. A
  surviving, untouched stack frame one level further down then gets popped by unrelated later
  code, so execution accidentally, indirectly re-enters `busy_wait_for_interrupt` fresh, waiting
  for a SECOND interrupt that will never arrive (the FDC has nothing left to signal). This fresh
  busy-wait genuinely runs its full 65536-iteration course and times out for real — confirmed to
  land 3,802,541 T-states after the 14th completion, matching Part B's own original "~3.8M
  T-states" figure almost exactly, now with a precise mechanism behind it. `channel_time_out`/
  `sub_e943h` then fires and reports "Disk I/O error."
- **Verdict: a genuine emulator timing gap, not a PDOS/BASIC bug** — and this reconciles cleanly
  with the real-P2000M no-error result without requiring PDOS's own read protocol to be considered
  fragile. PDOS's technique (wide EOT window, software consumes only what it needs, forced early
  completion via TC for every case except the one where the window naturally happens to be exactly
  one sector) is real, legitimate design that real firmware handles correctly. The TC-forced path
  already got its settle-delay fix in 2026-07-28; the natural/synchronous completion path in
  `Upd765.ReadData`/`WriteData` never got the analogous treatment, because no prior test happened
  to exercise a transfer whose natural end coincides with what the driving software's own polling
  loop consumes. Real silicon's own completion-to-interrupt propagation is very unlikely to be
  perfectly zero-latency the way the emulator's synchronous check was — real µPD765 hardware
  finishing its last byte almost certainly has some minimum propagation delay, giving real PDOS
  firmware the same safe margin the TC-forced path already had here.
- **FIXED, same pass, owner-authorized.** `Upd765.DeferNaturalCompletion()` (new) makes
  `ReadData`/`WriteData`'s end-of-buffer branch defer completion the same way the TC-forced path
  already does, applied uniformly to every transfer's natural completion (not special-cased to
  "is this the track's last sector") — more faithful to real silicon, which almost certainly has
  some non-zero completion delay regardless of which byte ends a transfer.
- **CONFIRMED END-TO-END:** the full `P2000.Machine.Tests` suite (627 total) re-run with the real
  boot/disk fixtures used throughout this investigation: 615 passed, 12 skipped, 0 failed. The 12
  skipped are pre-existing Part B-H diagnostic tests, each retired (not deleted or rewritten) —
  each pinned an exact numeric/textual fact about the confirmed bug's own specific symptom that is
  now definitionally false since the bug is fixed. Retiring rather than rewriting preserves their
  own doc comments as a genuine record of exactly how the bug was chased down, part by part.
  `RUN"VOLORG"` now loads and runs VOLORG.BAS completely successfully — its own real "P 2000 DISK
  UTILITY" menu renders correctly, with no "Disk I/O error" anywhere across the owner's full
  manual repro sequence (`RESET`/`SYSTEM B`×2/`FILES`/`RUN"VOLORG"`×2/`FILES`). CR now correctly
  reaches VOLORG's genuine RC=44 — the legitimate CP/M EOF condition — meaning the file reads to
  completion rather than hanging partway through.
- **An unexpected, not-yet-chased side effect:** plain `RESET` on the system disk also no longer
  produces "Disk I/O error." This was previously analyzed (Fourth/Fifth follow-ups above) as
  correct, intentional behavior — a system disk's track 1 legitimately has no FCB index. That
  conclusion may need revisiting: if RESET's own directory-read was ALSO racing this same
  natural-completion timing gap, this single `Upd765` fix could have resolved more than just the
  `RUN"VOLORG"` symptom. Not investigated further — flagged for whoever next touches the
  RESET/system-disk boot path.
- **Applies to:** `src/P2000.Machine/Devices/Fdc/Upd765.cs` (the fix itself — `PendingAction.
  NaturalCompletion`, `DeferNaturalCompletion`, `ReadData`, `WriteData`, `Tick`); the 13 existing
  `Upd765Tests.cs`/`MultiDriveFloppyTests.cs` tests updated with a post-transfer tick-drain to
  match the now-deferred natural completion; `FourteenthOperationRedirectDiag.cs` (new permanent
  regression guard, rewritten from a bug-diagnosis test into a forward-looking guard asserting the
  redirect always lands at `0xE969`, never `0x6150`); the 12 retired diagnostic tests listed above;
  `docs/PDOS_wip.asm` (read throughout this pass, not edited — every address cited above was
  pre-existing disassembly).

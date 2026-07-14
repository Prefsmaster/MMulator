# Hand-over — P2000T emulator: drafting the FDC milestone

*Paste this into the new chat as the first message, and attach the files listed at the bottom.*

## What you're doing in the new chat
Acting as the **design-doc maintainer** for a cycle-exact Philips P2000T (Z80) emulator. The
immediate task: **draft the FDC (floppy disk controller) milestone** for `P2000.Machine`, grounded
in the reference doc. You are NOT writing emulator code — "Claude Code" implements against your
specs and appends findings; you maintain the design docs. **The reference doc is the source of
truth.**

## Project state (as of 2026-07-11)
- **T-baseline complete** in both `P2000.Machine` (headless, cycle-exact) and `P2000.UI` (Avalonia
  MVVM: display, audio, cassette, full debugger). Architecture: Z80.Core ✓, Z80.Disassembler ✓,
  P2000.Machine ✓ (T), P2000.UI ✓ (T).
- **Machine milestone 17 (Z80 CTC + IM2 daisy chain + Lock interlock) — BUILT & synced.**
- **Machine milestone 18 (tape turbo — ROM-trap fast load/save) — BUILT & synced.**
- **Next up: the FDC**, which is **milestone 19** (then floppy+RAM board integration + PTC-96K ≈
  M20, SLOT2 cards ≈ M21).

## Working disciplines — KEEP THESE (they matter)
- **Line endings, exact:** `src/P2000.Machine/CLAUDE.md` is **CRLF**; the reference doc, UI
  CLAUDE.md, and MDCR guide are **LF**. Preserve on every edit and verify. For the CRLF machine
  file, edit via a Python pass opening with `newline=""` and `\r\n`-joined anchors — NOT plain
  str_replace with `\n`.
- **Divergence caution (this has bitten twice):** the human's merges sometimes drop prose edits
  while keeping findings-log edits (or vice-versa). Before editing, diff the uploaded file against
  your last output; re-apply anything silently lost; flag it. *(Most recent instance: the UI file
  came back with findings sync-flags intact but §3.2/§5/§9 prose reverted — repaired 2026-07-11.)*
- **Findings sync pass:** Claude Code appends findings to a CLAUDE.md's log marked `Synced: no`.
  Triage each: **design/hardware truth → reference doc** (or MDCR guide); **pure implementation
  lessons stay in the log**, marked synced-as-implementation-only. Flip flags to `Synced: yes` with
  the target noted. Don't rewrite historical findings — add dated CORRECTED notes.
- **Standalone-chip pattern (DECISION, applies to the FDC):** model real chips as board-agnostic
  units (`SAA5050`, `Z80Ctc`), each with its own class + unit tests; the **owning board wires them**
  (CLK/TRG, INT routing). **Defer the multi-board framework** until a second consumer is real. So
  the FDC = a standalone **`Upd765` chip** the extension board instantiates and wires (its INT →
  CTC ch0). Do NOT build a generic card framework yet.
- **`.state` version discipline:** any device-block format change bumps `CurrentVersion`/`MinVersion`
  **at build time** and rejects older files (learned from a v1→v2 silent-misload bug; currently at
  **v3** after the CTC/Lock block). The FDC will add a block → bump to v4.
- **Source, don't invent:** hardware facts come from the P2000 service manual, the project's own
  monitor-ROM disassembly (authoritative for what the ROM actually pokes), MAME, and real disk
  images — never guessed. Flag contradictions rather than smoothing them.

## FDC — what's already CONFIRMED (in reference doc §5d)
- **Chip: µPD765, semi-DMA, software-polled.** Command / execute / result phases; MFM; 2 drives
  (US0,1 → DRISEL1/2); write-protect + track-00 sensing; INT raised at the result phase.
- **Ports (ROM disassembly — authoritative):**
  - `0x8C` `DSKIO1` (IN) — FDC main status; **bit 7 = RDY**.
  - `0x8D` `DSKSTAT` (IN/OUT) — data/status; **bit 2 = REQ** (data request).
  - `0x90` `DSKCTRL` (OUT) control latch: **bit0 ENABLE** (1 = r/w registers), **bit1 Count** (TC),
    **bit2 RESET** (1 = FDC reset), **bit3 MOTOR** (1 = on), **bit4 SELDIS** (1 = select disabled —
    **P2C2 disk board ONLY**). *(The service manual described bit0/bit2 differently; the ROM wins.)*
- **Interrupt wiring: the FDC has NO direct CPU INT line — its INT feeds CTC ch0** (IM2-vectored,
  vector base `0x6020` → ch0 = `0x6020`). This is why the **CTC (milestone 17) had to come first**;
  it's done, so the seam is ready.
- **Semi-DMA:** during a read/write execute phase, the driver **polls the request bit** and services
  each byte transfer itself — model the polled per-byte handshake, not autonomous DMA.
- **Seams already built to plug into:** the interrupt aggregator + IM2 daisy chain (register the
  FDC/its ch0 path), the slot model (`IMemorySlot`/`IIoSlot`, milestone 12), the port dispatch
  fan-out, `TimingPolicy` (authentic/turbo — reuse the cassette pattern), and the host-image API
  pattern (mount/eject/create/write-protect — cassette's `.cas` API is the template for `.dsk`).

## FDC — what STILL needs sourcing (bring these to the new chat)
- The **monitor-ROM FDC driver disassembly**: the **presence-probe sequence** (reset →
  interrupt → Sense-Interrupt-Status is the expected pattern — confirm) and the **command set /
  sequences** the ROM actually issues (SPECIFY, RECALIBRATE, SEEK, READ/WRITE DATA, SENSE INT
  STATUS, READ ID, FORMAT). This is the analogue of the CTC probe you already sourced.
- **Disk geometry**: single-sided, 35-track is the expectation — **confirm sector size / count /
  interleave from real JWSDOS disk images** (the test corpus).
- **Host `.dsk` image format**: which container (raw sector dump? JWSDOS layout?) and the
  mount/eject/write-protect/create API.
- **MAME PR #7577** corroborates chip/ports/DMA/INT if you want a second source.
- Note `TimingPolicy` for the FDC is a **chip-timing policy (authentic vs turbo), NO ROM trap
  needed** — unlike the cassette, the FDC is register-level so turbo just relaxes command timing.

## Open flags to carry over (not FDC-blocking, but live)
- **Divergence:** verify the machine + reference files you're carrying didn't also lose recent
  prose edits in a partial merge (diff against the maintainer's last outputs).
- **RAM-total reconciliation:** the original 2-bank extension board = 48 KB max addressable, yet
  the variant table lists **T/102 = 80 KB** and **PTC-96K = 96 KB** — those don't fit the original
  board (homebrew wider-`0x94` banking is a separate axis). Resolve when the floppy+RAM board /
  PTC-96K milestone (M20) is drafted.
- **Port `0x94` is card-specific:** original Philips board = 1-bit `RAMSW` flip-flop (D0 toggles
  BANK1's two 8 KB halves); homebrew cards decode more bits for more banks. Milestone-2's page
  table needs the original board added as the 1-bit default card (flagged, not yet done).

## Milestone shape to aim for (FDC = M19)
Standalone **`Upd765` chip** (its own class + tests) the extension board owns and wires (INT →
CTC ch0). Register interface (MSR/data via the `0x8C`/`0x8D` ports + `0x90` control latch),
command/execute/result state machine, the ROM's command subset, **presence probe** (so a
board-absent machine falls back cleanly — the same "absent = silent" discipline as the CTC),
semi-DMA polled transfers, `TimingPolicy` authentic/turbo, host `.dsk` mount/eject API, `.state`
block (bump to **v4**). Tests: presence probe both ways, each command's result bytes, a real JWSDOS
image boots (ROM detects disk → DOS loads). End with `→ commit`.

---

## Files to attach to the new chat
**Essential (2):**
- `docs/P2000T-reference.md` — the source of truth. Has §5c (slot/bus model), **§5d (FDC —
  confirmed ports, control bits, semi-DMA, INT-via-CTC-ch0)**, §5e (interrupts/CTC), §5 memory.
- `src/P2000.Machine/CLAUDE.md` — the machine contract + milestone list (17 & 18 done; FDC = 19
  gets drafted here). **CRLF — preserve.**

**Helpful (bring if handy):**
- `docs/MDCR-implementation.md` — the cassette device design is the **pattern template** for the
  FDC device (TimingPolicy authentic/turbo, host-image mount/eject API). Reference-only.

**Plus the external FDC sources** listed under "what still needs sourcing" above (ROM FDC-driver
disassembly, a JWSDOS `.dsk` image or two, optionally MAME PR #7577). These aren't repo files but
they're what unblocks the milestone's remaining unknowns.

**Not needed for the FDC machine milestone:** `src/P2000.UI/CLAUDE.md` (the UI disk mount/eject
surfacing is already specced and is a later, separate concern).

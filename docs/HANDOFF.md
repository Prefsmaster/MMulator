# P2000T Emulator — Project Handoff / Status

Snapshot for resuming in a fresh chat (or by a new Claude Code session). The **docs are the
source of truth** — this note is a map + current status, not a replacement for them.

_Last updated: 2026-07-05._

---

## The documents (all in the repo)

| File | Role |
|------|------|
| `docs/P2000T-reference.md` | **THE hardware + design source of truth.** Everything confirmed about the machine. |
| `CLAUDE.md` (repo root) | Slim solution map: projects, dependency direction, `Z80Tables` rule, global conventions. |
| `src/Z80.Core/CLAUDE.md` | Z80 core contract (DONE, v1.0.0). |
| `src/Z80.Disassembler/CLAUDE.md` | Disassembler contract (DONE). |
| `src/P2000.Machine/CLAUDE.md` | Machine-layer contract + milestone list + findings log §17. |
| `docs/SAA5050-implementation.md` | Video/teletext device guide. |
| `docs/MDCR-implementation.md` | Cassette device guide. |
| `docs/CLAUDE-addendum-interrupts.md` | Z80 int-ack / daisy-chain readiness notes. |

Hierarchical CLAUDE.md loading: root + nearest project file. The reference doc is **read on
demand** (not auto-loaded).

---

## Build status

**DONE + validated:**
- **Z80.Core** — v1.0.0. Full instruction set (all prefixes), interrupts, SingleStepTests
  (1604 opcode files) + ZEXALL/ZEXDOC green. Cycle-stepped, bus-exposed, `ulong` pin mask,
  synchronous `Step()`, `Z80Tables` shared.
- **Z80.Disassembler** — spec-complete. Parallel x/y/z/p/q decoder over `Z80Tables`, golden +
  1604 conformance green. Symbol injection stubbed (interface ready).
- **P2000.Machine** — milestones 1–11 + 9a done (all `synced: yes` in §17). Page table, port
  dispatch + CPOUT/CPRIN fan-out, video (SAA5050, 640×480, fields/comb), interrupt aggregator
  (IM1 RST38 video tick), boot (bare + BASIC), keyboard (matrix + ghosting), cassette
  (authentic phase-bitstream + CSAVE + `.cas` save + TimingPolicy), contention (Z80-priority
  single-cell, DRAM-scope `addr>=0x5000`), config+state serialization (`.cfg` / `.state`).

**NOT started:**
- **P2000.UI** (Avalonia) — the whole windowed front-end. **Has no CLAUDE.md yet** — the one
  major spec still to write. Design is captured in reference doc §3a (display-as-main-window,
  4 display modes incl. comb, config/keyboard/debugger/cassette-deck windows, memory-watch +
  the special VRAM/pan-rectangle window). Machine is headless and produces framebuffers +
  snapshots for the UI to observe.

---

## Confirmed-from-disassembly wins (recent)

The owner's monitor-ROM disassembly resolved the last hardware unknowns:
- **Scroll/pan register = I/O port `0x30`**, write 0–40 (0=base, 40=2nd screen right). >40
  undefined (deferred). Reference doc §5 memory/video.
- **WEN sense = bit set means PROTECTED** (1=protected, 0=writable). Old `MdcrDevice.cs` was
  inverted; corrected.
- **`.cas` block header IS on tape** (32 B header + 1024 data per block); ZOEK/CLOAD-by-name
  read it. `LoadCasImage` corrected to encode it.
- **Contention scope = DRAM only (`addr>=0x5000`)** — ROM/cartridge are separate chips off the
  DRAM bus, can't collide. (Refined §4.)
- CSAVE routines: `cas_Write`/`write_block`/`cas_block_write` (Cassette.asm) — turbo trap targets.
- Disk ports `DSKIO1`=0x8C, `DSKSTAT`=0x8D, `DSKCTRL`=0x90; CTC serves disk/comms/keyboard
  (for deferred FDC/CTC work).

---

## Open items (none block a running T)

- **Contention scope — FIX PENDING:** milestone-10 implemented `IsDramAddress = addr >= 0x5000`
  (whole DRAM). Owner corrected this to **VRAM-only: `>= 0x5000 && < 0x5800`** — VRAM is a
  separate 2K×8 chip, so main-RAM access can't glitch the display (reference doc §4). Change the
  contention check in `VideoFetchUnit`/`PageTable` accordingly. (Only widen back if the schematic
  shows a single DRAM controller across all chips — unlikely.)
- **Scroll register > 40** — undefined; owner to investigate. Clamp/wrap placeholder for now.
- **Cassette WEN/reverse-direction** — WEN resolved; reverse-direction bit mapping still behind
  a toggle (`ReverseDataBitMapping`, default true), confirm when read-while-reversing observable.
- **Turbo ROM-trap addresses** — TimingPolicy infra exists; actual `cas_Write`/`write_block`
  trap addresses to be sourced from Cassette.asm and logged.
- **SHIFT/CODE matrix positions** — for the keyboard mapping table.
- **Deferred hardware (M phase):** P2000M (different video-memory sharing), CTC (Z8430) + IM2
  daisy chain + Lock interlock, FDC/floppy (µPD765, ports known), hires overlay, SLOT2 cards,
  80-column mode, printer. Seams built; not implemented.
- **PTC-96K RAM variant** — addressing scheme unconfirmed; deferred with floppy.

---

## Workflow notes

- **Findings loop:** Claude Code appends to `src/P2000.Machine/CLAUDE.md` §17 during each
  milestone; the human periodically syncs entries into `docs/P2000T-reference.md` and marks them
  `Synced: yes`. All current entries (through milestone 11) are synced.
- **Divergence caution:** when the design chat edits the machine CLAUDE.md body AND Claude Code
  appends findings, merge by hand — the chat copy is canonical for the body, Claude Code's for
  the findings. Push the merged copy back so Claude Code isn't building from a stale spec.
- **Commit discipline:** each milestone = tests green, then a conventional commit whose body
  records what was built + findings.

---

## Suggested next steps

1. **Write `src/P2000.UI/CLAUDE.md`** — the last unwritten project spec. All design is in
   reference doc §3a; grounded like the others. This unblocks the windowed emulator.
2. **RUN validation end-to-end** — boot bare → insert a real `.cas` (Ghosthunt) → auto-load 'P'
   → correct colours (validates the 160–255 swap + contention + cassette together). Needs the UI
   or a headless framebuffer-dump test.
3. Owner to pin down: scroll >40, SHIFT/CODE positions, turbo trap addresses (all minor).

# CLAUDE.md - Z80 Cycle-Stepped Core (.NET)

This file is the standing contract for this repository. Read it fully at the start of
every session. It defines the architecture, the hard rules, and the validation gates.
When in doubt, prefer correctness and the explicit design decisions below over cleverness.

---

## 1. What this project is

A **cycle-stepped, T-state-accurate, bus-exposing Z80 CPU emulator** written in C#,
intended as the core of a future cycle-exact Philips P2000T emulator. **This build
delivers the CPU core and its test harness ONLY.** No P2000T hardware, no video, no
machine wiring. The core must remain a pure Z80 that knows nothing about any host machine
- it only drives and samples a bus.

The reason for cycle-stepping: the eventual machine needs to detect bus contention
between the CPU and a video circuit on a per-T-state basis. That is only possible if the
CPU exposes its address/data/control pins **every T-state**, including mid-instruction
(when MREQ drops, when the refresh address appears during M1). An instruction-stepped
core that only reports "this opcode took N cycles" cannot support this and is explicitly
rejected.

---

## 2. Locked design decisions (do NOT revisit without being asked)

1. **Pin state = a single packed 64-bit `ulong` mask** (floooh `z80.h` style). All bus
   and control lines live in one `ulong`. This is the core's primary interface type.
2. **Scope of THIS build = Z80 core + test harness only.** No machine, no devices.
3. **Execution model = synchronous `Step()`; host inspects pins, services the bus, writes
   back.** No callbacks, no delegates, no events for memory/IO. One `Step()` advances the
   CPU by **one T-state** and returns the new pin mask. The host examines the mask; if a
   memory/IO read is active it places the data byte into the data-bus bits; if a write is
   active it consumes them; then it calls `Step()` again with the (possibly modified) mask.
4. **Instruction implementation = a big switch driven by a resumable micro-step counter**,
   with each opcode composed from a small set of **standard machine-cycle templates**
   (see §5). Microcode advances one T-state per `Step()`.

These four are settled. Build to them.

---

## 3. The pin mask layout (authoritative)

One `ulong`. Bit positions (match floooh `z80.h` so external references line up):

| Bits  | Meaning                         |
|-------|---------------------------------|
| 0-15  | Address bus A0-A15              |
| 16-23 | Data bus D0-D7                  |
| 24    | M1   (opcode fetch / int ack)   |
| 25    | MREQ (memory request)           |
| 26    | IORQ (I/O request)              |
| 27    | RD   (read)                     |
| 28    | WR   (write)                    |
| 29    | RFSH (refresh)                  |
| 30    | HALT                            |
| 31    | WAIT (input to CPU)             |
| 32    | INT  (input to CPU)             |
| 33    | NMI  (input to CPU)             |
| 34    | RESET (input to CPU)            |
| 35    | BUSRQ (bus request, input)      |
| 36    | BUSAK (bus acknowledge, output) |

Provide:
- A `Pins` static class with `const ulong` masks for each control line and helpers:
  `GetAddress(ulong)`, `SetAddress(ulong, ushort)`, `GetData(ulong)`, `SetData(ulong, byte)`.
- All control lines are **active-high in our representation** (bit set = asserted), even
  though the real chip is active-low. Document this once; keep it consistent everywhere.
- Model WAIT, BUSRQ/BUSAK from the start even though nothing uses them yet. Retrofitting
  bus arbitration later is painful; the plumbing is cheap now.

---

## 4. The `Step()` contract (host-facing API)

```csharp
public sealed class Z80
{
    public ulong Pins;            // current pin state (or pass in/out of Step)
    public ref Registers Reg;     // register file access for harness/debug

    // Advance exactly one T-state. Returns the new pin mask.
    public ulong Step(ulong pins);

    public void Reset();          // assert reset behaviour
}
```

Host loop shape (this is what the test harness and the future machine both do):

```csharp
ulong pins = cpu.Step(pins);
if ((pins & Pins.MREQ) != 0 && (pins & Pins.RD) != 0)
    pins = Pins.SetData(pins, memory[Pins.GetAddress(pins)]);   // service read
else if ((pins & Pins.MREQ) != 0 && (pins & Pins.WR) != 0)
    memory[Pins.GetAddress(pins)] = Pins.GetData(pins);          // service write
// IORQ handled analogously
```

Rules:
- **One `Step()` == one T-state.** Never advance more than one T-state per call.
- The CPU sets address + control pins on the appropriate tick and samples the data bus on
  the correct internal tick; the host must have the byte present by then. Follow the real
  3-T memory-read timing (address+MREQ+RD asserted on the first tick of the read M-cycle;
  data sampled on the tick the real chip samples it).
- The CPU must be fully resumable mid-instruction: all state needed to continue lives in
  fields (micro-step counter, latched operands, WZ, etc.), never on the C# call stack.

---

## 5. Machine-cycle templates (how to keep the big switch tractable)

Do NOT write unique per-T-state code for all ~1300 opcodes. Compose every opcode from a
small set of **standard machine cycles**, each a fixed T-state sequence with defined pin
behaviour. This is the key to a manageable cycle-stepped core.

| Template | T-states | Pin behaviour summary                                            |
|----------|----------|------------------------------------------------------------------|
| **M1** (opcode fetch) | 4 | T1: addr=PC, M1 asserted, PC++. T2: MREQ+RD pulse (1 T-state; stretched by Tw while WAIT is asserted). T3: opcode sampled off the data bus; addr=IR refresh address (R *before* increment), RFSH asserted, data bus echoes the sampled opcode; R=(R&0x80)\|((R+1)&0x7F). T4: addr=IR refresh address held, RFSH asserted, no other signals. |
| **MR** (memory read)  | 3 | T1: addr set, no signals. T2: MREQ+RD pulse (1 T-state; data still null this tick; stretched by Tw while WAIT is asserted). T3: data now present on the bus, no signals. |
| **MW** (memory write) | 3 | T1: addr set, no signals. T2: MREQ+WR pulse *with the data already driven this same tick* (stretched by Tw). T3: nothing — no data, no signals. |
| **IOR** (I/O read)    | 4 | T1: addr set, no signals. T2: addr held, no signals (automatic wait T-state). T3: IORQ+RD pulse, data still null (stretched by Tw). T4: data now present, no signals. |
| **IOW** (I/O write)   | 4 | T1: addr set, no signals. T2: addr held, no signals (automatic wait T-state). T3: IORQ+WR pulse *with the data already driven this same tick* (stretched by Tw). T4: nothing. |
| **Internal**          | n | no bus activity; burns n T-states (e.g. ADD HL,ss internal time).|
| **INT ack**           | 6 | M1+IORQ asserted, 2 wait T-states; mode-dependent follow-up.     |

All five rows above are now confirmed against SingleStepTests/z80's actual JSON cycle
data (default "simplified memory access T-states" config), not just the Z80 manual's
prose: every pulse (MREQ/IORQ + RD/WR) lands one T-state later than a naive 2-T-pulse
reading would suggest, and — the detail most likely to bite a reimplementation — **reads
show their data one T-state *after* the pulse, but writes drive their data in the *same*
T-state as the pulse**, since the CPU doesn't need to wait on a peripheral to drive a
write. M1's refresh address uses the *pre*-increment R. The suite's `cycles` entries only
encode address/data/RD/WR/MREQ/IORQ — M1 and RFSH have no bit of their own in the test
data, so drive them per real silicon (M1 high for all 4 M1 T-states, RFSH high for M1's
T3-T4) but treat them as unverified by this particular suite. INT ack timing is confirmed
by unit tests in `InterruptTests.cs` (milestone 7) — T-state counts match the Z80 manual:
NMI=11T, IM1=13T, IM2=19T.

An instruction = a sequence of these templates. The micro-step counter indexes the
current T-state within the current template; the opcode (plus prefix state) selects the
template sequence.

Strongly consider a **small code generator** (a `tools/` script or a T4 template) that
emits the opcode→template-sequence tables, exactly as floooh generates z80.h from a
decoder script. Hand-write the template engine and the ALU; generate the dispatch tables.
If generating, commit BOTH the generator and its output, and never hand-edit the output.

---

## 6. Z80 correctness details that WILL be tested

These are the things SingleStepTests and ZEXALL specifically catch. Get them right early.

- **R register:** low 7 bits increment per M1 fetch; bit 7 preserved. Each prefix byte is
  its own M1 and increments R. Tested directly.
- **WZ / MEMPTR** internal register: affects undocumented flag bits 3/5 in `BIT n,(HL)`,
  and is updated by many instructions. SingleStepTests checks `wz`. Implement it.
- **Undocumented flags (bits 3 & 5, "YF"/"XF"):** set from result/operand as documented in
  "The Undocumented Z80 Documented" (Sean Young). ZEXALL depends on these.
  - **CP r / CP n** is the one arithmetic op where YF/XF come from the *operand*, not the
    result (every other ALU op takes them from the result byte). Implemented and tested in
    `Alu.Cp8` — confirmed by a unit test showing it diverges from plain `Sub8` on inputs
    where the operand's and the result's bits 3/5 differ.
  - **SCF / CCF**: confirmed exactly against all 1000 cases of `37.json`/`3f.json` while
    wiring up milestone 5 — Patrik Rak's Q-dependent rule is real and is exactly: when the
    *incoming* Q (flags as left by whichever instruction last touched them) equals the
    *incoming* F, Y/X come from A alone; otherwise Y/X come from `(F | A)`. Implemented in
    `Alu.Scf`/`Alu.Ccf` (both take `q` as an explicit third parameter — pure functions,
    no hidden state) and in `Z80.Dispatch`, which captures `_incomingQ` *before* the
    per-instruction default of clearing Q, since by the time an opcode handler runs,
    `Reg.Q` has already been reset for this instruction and no longer holds the prior
    instruction's value.
- **Prefixes:** CB, ED, DD, FD, DDCB, FDCB.
  - DD/FD swap HL→IX/IY and (HL)→(IX+d)/(IY+d); they also expose IXH/IXL/IYH/IYL for
    register ops (undocumented). DD/FD are each a full M1 and stack/chain correctly
    (DD DD ... only the last takes effect; each consumes an M1).
  - **Mixed-operand quirk (DD/FD):** when a single LD r,r' accesses (HL)/(IX+d) on one
    side AND H/L on the other, H/L stays as the *real* H/L register, not IXH/IXL.
    H/L→IXH/IXL substitution only applies when *both* operands are pure registers (neither
    is the (HL)/(IX+d) slot).
  - **Prefix-byte Q-reset (DD/FD/CB/ED):** every prefix M1 resets Q to 0 — confirmed
    against `dd 37.json`: when initial Q==F, DD+SCF produces a different Y/X result than
    plain SCF, because the DD byte resets Q to 0 so the subsequent SCF sees Q≠F and uses
    the `(F|A)` branch instead of the `A`-only branch.
  - **Quadrant-11 opcode collision in DD/FD dispatch:** opcodes 0xE1/0xE3/0xE5/0xE9/0xF9
    have z/y bit-field values that collide with quadrant-00 cases in the decoder (e.g.
    PUSH IX 0xE5 has z=5,y=4, matching DEC IXH). The quadrant-11 group must be checked
    *before* the z-field switch in both the Dispatch and Execute phases.
  - **DDCB/FDCB:** displacement byte and CB-table opcode byte are *both* fetched via plain
    MR (not M1 — R does not increment for either byte). Timing confirmed against
    `dd cb __ 06/40/86.json`: rotate/RES/SET = 23T
    (M1+M1+MR(d)+MR(op)+Internal(2)+MR(value)+Internal(1)+MW); BIT = 20T (same without
    the trailing MW). The Internal(2) *between* the opcode fetch and the value read is easy
    to miss. Undocumented dual-write: for z≠6 the result is also stored in register r[z].
  - **DDCB/FDCB transient-flag decay:** `EiPending` and `LastWasLdAIR` must be explicitly
    reset when prefix bytes are detected in RunFetch, because Dispatch() is never called
    for any of the four prefix bytes in a DDCB/FDCB sequence — the standard per-opcode
    reset in Dispatch() never runs.
- **Block instructions** (LDIR/LDDR/CPIR/CPDR/INIR/OTIR etc.): correct repeat timing
  (extra 5 T-states when repeating) and undocumented flag behaviour.
  - LDI/LDD/CPI/CPD and the INI/IND/OUTI/OUTD non-repeat forms: undocumented flags fully
    confirmed against their opcode files (`ed a0/a8/a1/a9/a2/aa/a3/ab.json`) — LDI/LDD's
    Y/X come from bits 1/3 of `A + transferred byte`; CPI/CPD's from bits 1/3 of
    `(A-(HL)) - H`; INI/IND/OUTI/OUTD's S/Z/Y/X come from B *after* decrement, N from bit
    7 of the transferred byte, H/C from `(value + k) > 0xFF` (k = `(C±1)&0xFF` for IN,
    `L` *after* it moves for OUT), P/V from `parity((k&7) ^ B_after)`. OUTI/OUTD's port
    address uses B *after* decrement (`(B-1)<<8|C`); INI/IND's port uses B *before*.
  - **LDIR/LDDR/CPIR/CPDR**, repeat-continuation iteration only: Y/X stop following the
    single-iteration formula above and instead come directly from bits 5/3 of PC's high
    byte (no shift — found by brute-force bit-correlating every flag bit against every
    candidate register byte across 400+ real cases of `ed b0.json`, after the plain
    LDI-style formula matched 0% of repeat cases despite matching 100% of LDI's own).
    Implemented in `Z80.OverrideRepeatYX`, applied after PC is rolled back by 2. All other
    flags (S/Z/H/N/P/C) keep following the base op's normal formula. CPIR additionally
    stops repeating early on a match (`Z=1`), not just when BC reaches 0.
  - **INIR/INDR/OTIR/OTDR**, repeat-continuation iteration only: Y/X follow the same
    PC-high-byte rule as above. H and P/V resisted brute-force bit-correlation entirely
    (every tried operand substitution and PCH-XOR hybrid mismatched ~75% of cases) until
    the user pointed at MAME's source (`src/devices/cpu/z80/z80.cpp`,
    `z80_device::block_io_interrupted_flags()`), which gave the right shape: with `B`
    already decremented and the base op's carry/parity already computed,
    `H = 0` unless carry, in which case `H = ((B&0xF)==0)` when the transferred byte's
    bit 7 is set else `((B&0xF)==0xF)`; the same carry/bit7 branch picks a 3-bit `pvRaw`
    (`(B∓1)&7` or `B&7`), and final P/V is the *old* parity flag combined with
    `parity(pvRaw)`. One transcription point flipped from the source: MAME's C++ reads as
    an XOR of old-parity and new-parity; only the XNOR (equivalence) matched real hardware
    (100% across all four opcodes' repeat-continuation cases, ~4000 cases total) — kept as
    XNOR per the data over the literal source reading. Implemented in
    `Z80.OverrideIoRepeatFlags`.
- **Interrupts (milestone 7, implemented in `Interrupts.cs`):**
  - **NMI:** 11T = 5T aborted M1 (M1 no-MREQ; T3 RFSH/R++; T5 internal SP--) + 3T MW
    PCH + 3T MW PCL. IFF2←IFF1, IFF1←0 at aborted-M1 T1. PC←0x0066, WZ←0x0066.
    Edge-triggered (`_prevNmi` tracks previous NMI pin level; latch set only on 0→1).
  - **INT ack M-cycle:** 6T with M1+IORQ asserted together (the int-ack signature — not
    M1+MREQ like a normal fetch). T1: M1 no-MREQ, IFF1/IFF2←0. T2: M1+IORQ auto-wait-1.
    T3: M1+IORQ auto-wait-2 (WAIT-stretchable). T4: M1+IORQ, ack byte sampled from data
    bus into `_latchLo`. T5: RFSH, R++. T6: RFSH done → `DispatchIntMode` (synchronous,
    no extra T-state). IM0: ack byte dispatched as opcode. IM1: 1T internal (SP--) +
    3T MW PCH + 3T MW PCL, PC←0x0038 (13T total). IM2: 1T internal (SP--) + 3T MW PCH
    + 3T MW PCL + 3T MR vecLo + 3T MR vecHi, PC←vector (19T total); vector address is
    `(I<<8)|(ack&0xFE)` (low bit forced 0). **The 1T internal** between ack T6 and push
    is a genuine separate Step() call — easy to miss, confirmed by the 13T/19T count tests.
  - INT sampled only at instruction boundary; EI has a one-instruction delay (`EiPending`
    flag) before interrupts re-enable. DI clears IFF1/IFF2 immediately.
  - **Prefix-boundary inhibition:** interrupts (both NMI and INT) are gated on
    `_prefix == Prefix.None` in RunFetch T1 — they are NOT taken between a prefix byte
    (DD/FD/CB/ED) and its following opcode. The real Z80 defers to the end of the
    prefixed instruction.
  - **NOP prefix-clear bug (found during milestone 7):** `DispatchZ0` for NOP originally
    returned bare `pins` without calling `FinishInstruction`, leaving `_prefix = DD` set
    after DD+NOP. The subsequent T1 would then see `_prefix != None` and skip the
    interrupt check indefinitely. Fixed: NOP now calls `FinishInstruction`.
  - **HALT prefix-clear:** `DispatchHalt` also clears `_prefix = Prefix.None` explicitly,
    so that `DD HALT` (unusual but valid) does not trap interrupts in the halted loop.
- **HALT:** PC stays at HALT_addr+1 (the M1 that fetched HALT already incremented it);
  the halted loop re-fetches M1 from `_haltAddr` without advancing PC. HALT pin stays
  asserted throughout. NMI/INT wake the CPU: `_halted` is cleared before `EnterInterrupt`
  so the interrupt sequence pushes the correct return address (HALT_addr+1).
- **RESET:** PC=0, I=R=0, IFF1=IFF2=0, IM0, SP and AF behaviour per spec.

---

## 7. Validation gates (REQUIRED - the project is not "done" until both pass)

### 7a. SingleStepTests z80 (PRIMARY - per-cycle bus assertion)
- Source: https://github.com/SingleStepTests/z80 (JSON, one file per opcode, ~1000 cases each).
- Each case has `initial` and `final` CPU+RAM state, plus a **`cycles` array describing
  per-T-state bus activity** (address, data, active control pins). **Confirm the exact
  cycle/pin encoding from the repo README** and map it to our pin mask.
- The harness MUST assert, for every case:
  1. final register state (incl. WZ, R, Q, IFF1/2, IM), and
  2. final RAM, and
  3. **the per-cycle bus activity** - address, data, and which control pins are active on
     every T-state. This is the assertion that proves cycle/bus accuracy; do not skip it.
- Run a representative subset in CI on every commit; full suite as a nightly/full gate.
- **Build the harness FIRST, before implementing opcodes.** Implement opcode 0x00 (NOP)
  end-to-end through the harness, then expand opcode by opcode. The suite drives the
  implementation, not the other way around.

### 7b. ZEXALL / ZEXDOC (instruction/flag correctness)
- Frank Cringle's exerciser (amended by J.G. Harston). ZEXDOC = documented flags;
  ZEXALL = all incl. undocumented.
- Run as a minimal CP/M harness: load the `.com` at 0x0100, implement BDOS calls at
  0x0005 (function 2 = print char in E; function 9 = print `$`-terminated string at DE),
  warm-boot at 0x0000 ends the run. Stream output; a clean run prints all-OK with no
  "ERROR" / mismatched CRC lines.
- ZEXDOC should pass before ZEXALL. ZEXALL passing means undocumented behaviour is right.

A core passing BOTH gates is trustworthy enough to build the machine/contention layer on.

---

## 8. Project layout

```
/CLAUDE.md
/Z80.sln
/src/
  Z80.Core/                 # the library - ZERO external dependencies
    Z80.cs                  # CPU: state + Step()
    Pins.cs                 # pin mask consts + get/set helpers
    Registers.cs            # AF/BC/DE/HL + primes, IX/IY, SP, PC, I, R, WZ, IFF, IM, Q
    Alu.cs                  # 8/16-bit ALU + flag computation (incl. YF/XF)
    MachineCycles.cs        # M1/MR/MW/IOR/IOW/Internal templates
    Decode/                 # opcode→microcode tables (generated + generator if used)
/tools/
  gen/                      # optional decoder/table generator (commit generator + output)
/tests/
  Z80.Tests/                # xUnit: unit tests + SingleStepTests runner
    SingleStepTests/        # JSON runner + (vendored or submoduled) test data
  Z80.Conformance/          # console runner for ZEXDOC/ZEXALL (long-running)
```

- **Core target framework:** current .NET LTS, cross-platform (Windows/macOS/Linux).
  Default to `net8.0`; bump only if asked. Core must have **no NuGet dependencies**.
- Tests may use xUnit + System.Text.Json.
- The core must compile and pass on all three OSes (no platform APIs, no unsafe unless
  justified and benchmarked).

---

## 9. Coding conventions

- Correctness and debuggability first. Performance second - the eventual target is 2.5 MHz,
  trivially within reach, so do NOT sacrifice clarity for micro-optimisation. If a hot path
  later needs `[MethodImpl(AggressiveInlining)]` or struct tweaks, do it with a benchmark,
  not on spec.
- Keep the pin mask manipulation behind `Pins` helpers; avoid scattering magic bit shifts.
- No P2000T or machine-specific constants anywhere in `Z80.Core`. If you're tempted to add
  one, it belongs in a future project, not here.
- Deterministic: no threads, no clocks, no `DateTime`, no randomness in the core. The host
  owns timing. `Step()` is pure w.r.t. wall-clock.
- Public API small and stable: `Z80`, `Pins`, `Registers`. Everything else internal.
- xML-doc the public API and every machine-cycle template with its T-state table.
- Conventional commits; each commit should keep CI green (SingleStepTests subset passing).

---

## 10. Build & test commands

```bash
dotnet build Z80.sln -c Release
dotnet test tests/Z80.Tests          # unit + SingleStepTests subset
dotnet run --project tests/Z80.Conformance -- zexdoc   # CP/M ZEXDOC run
dotnet run --project tests/Z80.Conformance -- zexall   # CP/M ZEXALL run
```

(Adjust once the harness exists; keep these documented and current in this file.)

---

## 11. Build order (milestones)

1. ✅ Solution skeleton + `Pins` + `Registers` + empty `Z80.Step()` returning idle pins.
2. ✅ **SingleStepTests harness** wired to a flat 64K byte array, able to load a JSON file,
   run a case by stepping, service the bus, and assert final state + per-cycle bus log.
3. ✅ **M1 fetch template + NOP (0x00)** passing its SingleStepTests file end-to-end.
4. ✅ ALU + flags (incl. YF/XF) with unit tests.
5. ✅ Main (unprefixed) opcode set via the machine-cycle templates → pass all base-page tests.
6. ✅ CB prefix; then ED; then DD/FD; then DDCB/FDCB. Pass each page's tests before moving on.
7. ✅ Interrupts (NMI, IM0/1/2), EI delay, HALT semantics. 18 new tests; 1662 total passing.
8. ✅ Full test suite green: all SingleStepTests JSON opcode files + all unit tests (ALU, interrupt, etc.).
   Coverage: 1604 JSON files (252 base + 256 CB + 80 ED + 252 DD + 252 FD + 256 DDCB +
   256 FDCB) + 58 unit tests (AluTests + InterruptTests) = 1662 total, all passing.
   ED page covers only the 80 files the suite provides (40-7F documented + A0-BB block
   instructions); undefined ED opcodes have no JSON file and are not tested.
9. CP/M harness → ZEXDOC pass → ZEXALL pass.
10. Tag a release of `Z80.Core`. Hand-off point for the machine layer (future project).

Do not move to the next milestone while the current milestone's tests are red.

---

## 12. References

- floooh cycle-stepped Z80 design (THE architectural model for our tick/pin approach):
  https://floooh.github.io/2021/12/17/cycle-stepped-z80.html - and code:
  https://github.com/floooh/chips (`z80.h`).
- SingleStepTests z80: https://github.com/SingleStepTests/z80
- "The Undocumented Z80 Documented" (Sean Young) - flags, WZ, undocumented opcodes.
- Z80 User Manual (Zilog) - official M-cycle / T-state timing.
- Dotneteer/spectrum-dotnet-engine - C# reference for per-T-state stepping (study only;
  its contention is wait-state based, opposite polarity to our future video model).
- Konamiman/Z80dotNet - instruction-behaviour cross-check oracle (NOT our architecture).

---

## 13. When to ask the human

Ask before: changing any of the four locked decisions in §2; adding a core dependency;
deviating from the pin-bit layout in §3; or relaxing a validation gate in §7. For ordinary
implementation choices within these rules, proceed and keep CI green.

---

## 14. Addendum — Interrupt-acknowledge & daisy-chain readiness

This refines the interrupt requirements in §6 so that future **Z80-family peripherals
(CTC, SIO/DART, PIO)** that ride the **IM2 daisy chain** have what they need — without
putting any peripheral or machine logic into the core. The core stays a pure, bus-exposed
Z80. These are clarifications, not changes to the four locked decisions in §2.

### Why this matters now

Even though THIS build is the CPU core only (no peripherals), the eventual machine wires
Z80-family chips onto the interrupt-acknowledge + RETI mechanism. If the core models
interrupts as a purely internal affair, those peripherals can't work later and a retrofit
is invasive. The good news: a cycle-stepped, bus-exposing core satisfies all of this
naturally — these notes just make the requirements explicit so they aren't lost.

### What the core MUST do

**1. Model a real interrupt-acknowledge M-cycle.** When the CPU accepts a maskable INT
(INT asserted, IFF1 set, at an instruction boundary, honoring the EI one-instruction
delay):
- Begin a **special M1 cycle with M1 AND IORQ asserted together** (this is the int-ack
  signature; in a normal opcode fetch M1 is high with MREQ, never with IORQ). The int-ack
  M1 cycle carries **2 automatically inserted wait T-states** (longer than a normal M1).
- During that cycle the CPU **reads a byte from the data bus** — it does NOT synthesize
  the vector/instruction internally. The host (a peripheral, later) drives the bus; the
  core samples it. Expose the pins each T-state so that byte can be supplied.

Then per mode:
- **IM0:** execute the instruction read from the bus during ack (typically an `RST`).
- **IM1:** ignore the bus byte; restart at the **fixed hardware vector `0x0038`**
  (`RST 38h`). The ack cycle still happens (M1+IORQ, wait states); the sampled byte is
  discarded. *(P2000T base machine: the 50 Hz video tick uses this IM1 / `0x0038` path.)*
- **IM2:** form a table address as `(I << 8) | (busByte & 0xFE)`, fetch the 16-bit ISR
  address from there (two memory reads), and jump to it. The low bit of the bus byte is
  forced to 0. This is the path the CTC/SIO/PIO use; the **vector comes from the bus**.

**2. Keep opcode fetches snoopable on the bus.** Daisy-chained peripherals watch the
instruction stream for **`RETI` (`ED 4D`)** to clear their in-service latch and re-enable
lower-priority interrupts. (They also see **`RETN` (`ED 45`)**.) The core does nothing
special for this — but **every opcode fetch, including the `ED` prefix and the second
byte, must appear on the bus** exactly as the real chip drives it, so a future peripheral
can detect the sequence. A cycle-stepped core already exposes this; just don't "optimize
away" any fetch.

**3. NMI** (unchanged, restated for completeness):
- Edge-triggered, not maskable, takes priority over INT at the instruction boundary.
- Saves IFF1 → IFF2, clears IFF1, pushes PC, jumps to **`0x0066`**, 11 T-states.
- `RETN` restores IFF1 from IFF2. *(P2000T uses NMI for the soft-reset button.)*

**4. IFF / timing behaviour the daisy chain depends on:**
- INT sampled **only at instruction boundaries**; **EI** enables interrupts only AFTER
  the instruction FOLLOWING `EI` (the one-instruction delay) — get this exactly right,
  ISRs rely on it.
- `DI` clears IFF1/IFF2 immediately.
- Accepting INT clears IFF1 (and IFF2) so the ISR runs with interrupts disabled until `EI`.

### What the core must NOT do (peripheral / machine-layer concerns — out of scope here)

- **No daisy-chain priority logic, no IEI/IEO.**
- **No RETI/RETN snooping or in-service tracking.**
- **No vector generation.** The core READS the vector from the bus; it never invents one.
- **No CTC/SIO/PIO/interrupt-aggregator code.** None of it belongs in `Z80.Core`.

The core's entire interrupt responsibility: correct acceptance timing, the int-ack
M-cycle with M1+IORQ and the bus read, correct IM0/IM1/IM2 behaviour, NMI, and the
IFF/EI-delay semantics — with all bus activity visible each T-state.

> **Note — IM2 daisy chain is an OPTIONAL machine-layer module, not M-only.** The chain
> is required whenever ANY Z80-family peripheral is mounted (floppy interface,
> serial/CTC/SIO, PIO, other expansion cards) — that can happen on the **T** (with the
> floppy/expansion cards) as well as the **M**. So it is optional *per configuration*, not
> deferred to a machine. Build it as an opt-in `DaisyChain` component in the machine layer
> that mounted peripherals register into; absent any such peripheral (bare T) it simply
> isn't instantiated and the only INT source is the 50 Hz video tick (IM1 / `0x0038`). The
> CORE requirements above are exactly what that future module needs — nothing in the core
> changes whether the chain is present or not.

### Validation notes

- **SingleStepTests is opcode-centric** and may not exercise the asynchronous int-ack
  sequence directly. Add **targeted unit tests** for: INT accepted in IM0/IM1/IM2 (assert
  the M1+IORQ ack cycle, the wait states, the bus read, and the resulting PC), the EI
  one-instruction delay, NMI entry/`RETN`, and `RETI` executing as a normal return (its
  snoop semantics are peripheral-side, but the opcode itself must behave like `RET`).
- `EI; RET`-style delay and `RETI`/`RETN` opcodes ARE in the instruction set the suite
  covers — make sure those pass as ordinary instructions in addition to the
  interrupt-flow unit tests above.

### One-line summary for the core author

> The core never generates or interprets a vector beyond IM1's fixed `0x0038` and IM2's
> table lookup; it **reads the int-ack byte from the bus**, performs the **M1+IORQ ack
> cycle with correct wait states**, and keeps **every opcode fetch visible** — that is all
> the daisy chain will ever need from it.


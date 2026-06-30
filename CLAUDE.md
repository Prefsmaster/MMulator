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
T3-T4) but treat them as unverified by this particular suite. INT ack above is still
unverified prose — confirm it the same way when interrupts are implemented (milestone 7).

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
  - DDCB/FDCB: displacement byte comes **before** the final opcode byte; addressing and
    timing differ from CB.
- **Block instructions** (LDIR/LDDR/CPIR/CPDR/INIR/OTIR etc.): correct repeat timing
  (extra 5 T-states when repeating) and undocumented flag behaviour.
- **Interrupts:**
  - NMI: 11 T-states, pushes PC, jumps to 0x0066, IFF1→IFF2 saved, IFF1 cleared.
  - INT modes 0/1/2 with correct acknowledge timing. IM2 uses I register + bus vector.
  - INT sampled only at instruction boundary; EI has a one-instruction delay (the EI
    backlog) before interrupts are enabled.
- **HALT:** PC does NOT advance past the HALT; the CPU executes NOPs (still doing M1
  fetches/refresh) until an interrupt. (Known historical bug source - verify PC behaviour.)
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

1. Solution skeleton + `Pins` + `Registers` + empty `Z80.Step()` returning idle pins.
2. **SingleStepTests harness** wired to a flat 64K byte array, able to load a JSON file,
   run a case by stepping, service the bus, and assert final state + per-cycle bus log.
3. **M1 fetch template + NOP (0x00)** passing its SingleStepTests file end-to-end.
4. ALU + flags (incl. YF/XF) with unit tests.
5. Main (unprefixed) opcode set via the machine-cycle templates → pass all base-page tests.
6. CB prefix; then ED; then DD/FD; then DDCB/FDCB. Pass each page's tests before moving on.
7. Interrupts (NMI, IM0/1/2), EI delay, HALT semantics. Pass relevant tests.
8. Full SingleStepTests suite green.
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

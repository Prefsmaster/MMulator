# Addendum to CLAUDE.md §6 — Interrupt-acknowledge & daisy-chain readiness

Drop this alongside `CLAUDE.md` (or append it under §6). It refines the interrupt
requirements on the Z80 core so that future **Z80-family peripherals (CTC, SIO/DART, PIO)**
that ride the **IM2 daisy chain** have what they need — without putting any peripheral or
machine logic into the core. The core stays a pure, bus-exposed Z80.

These are clarifications, not changes to the four locked decisions in §2.

---

## Why this matters now

Even though THIS build is the CPU core only (no peripherals), the eventual machine wires
Z80-family chips onto the interrupt-acknowledge + RETI mechanism. If the core models
interrupts as a purely internal affair, those peripherals can't work later and a retrofit
is invasive. The good news: a cycle-stepped, bus-exposing core satisfies all of this
naturally — these notes just make the requirements explicit so they aren't lost.

---

## What the core MUST do

### 1. Model a real interrupt-acknowledge M-cycle
When the CPU accepts a maskable INT (INT asserted, IFF1 set, at an instruction boundary,
honoring the EI one-instruction delay):
- Begin a **special M1 cycle with M1 AND IORQ asserted together** (this is the int-ack
  signature; in a normal opcode fetch M1 is high with MREQ, never with IORQ). The int-ack
  M1 cycle carries **2 automatically inserted wait T-states** (longer than a normal M1).
- During that cycle the CPU **reads a byte from the data bus** — it does NOT synthesize the
  vector/instruction internally. The host (a peripheral, later) drives the bus; the core
  samples it. Expose the pins each T-state so that byte can be supplied.

Then per mode:
- **IM0:** execute the instruction read from the bus during ack (typically an `RST`).
- **IM1:** ignore the bus byte; restart at the **fixed hardware vector `0x0038`**
  (`RST 38h`). The ack cycle still happens (M1+IORQ, wait states); the sampled byte is
  discarded. *(P2000T base machine: the 50 Hz video tick uses this IM1 / `0x0038` path.)*
- **IM2:** form a table address as `(I << 8) | (busByte & 0xFE)`, fetch the 16-bit ISR
  address from there (two memory reads), and jump to it. The low bit of the bus byte is
  forced to 0. This is the path the CTC/SIO/PIO use; the **vector comes from the bus**.

### 2. Keep opcode fetches snoopable on the bus
Daisy-chained peripherals watch the instruction stream for **`RETI` (`ED 4D`)** to clear
their in-service latch and re-enable lower-priority interrupts. (They also see
**`RETN` (`ED 45`)**.) The core does nothing special for this — but **every opcode fetch,
including the `ED` prefix and the second byte, must appear on the bus** exactly as the
real chip drives it, so a future peripheral can detect the sequence. A cycle-stepped core
already exposes this; just don't "optimize away" any fetch.

### 3. NMI (unchanged, restated for completeness)
- Edge-triggered, not maskable, takes priority over INT at the instruction boundary.
- Saves IFF1 → IFF2, clears IFF1, pushes PC, jumps to **`0x0066`**, 11 T-states.
- `RETN` restores IFF1 from IFF2. *(P2000T uses NMI for the soft-reset button.)*

### 4. IFF / timing behaviour the daisy chain depends on
- INT sampled **only at instruction boundaries**; **EI** enables interrupts only AFTER the
  instruction FOLLOWING `EI` (the one-instruction delay) — get this exactly right, ISRs
  rely on it.
- `DI` clears IFF1/IFF2 immediately.
- Accepting INT clears IFF1 (and IFF2) so the ISR runs with interrupts disabled until `EI`.

---

## What the core must NOT do (peripheral / machine-layer concerns — out of scope here)

- **No daisy-chain priority logic, no IEI/IEO.**
- **No RETI/RETN snooping or in-service tracking.**
- **No vector generation.** The core READS the vector from the bus; it never invents one.
- **No CTC/SIO/PIO/interrupt-aggregator code.** None of it belongs in `Z80.Core`.

The core's entire interrupt responsibility: correct acceptance timing, the int-ack M-cycle
with M1+IORQ and the bus read, correct IM0/IM1/IM2 behaviour, NMI, and the IFF/EI-delay
semantics — with all bus activity visible each T-state.

> **Note — IM2 daisy chain is an OPTIONAL machine-layer module, not M-only.** The chain is
> required whenever ANY Z80-family peripheral is mounted (floppy interface, serial/CTC/SIO,
> PIO, other expansion cards) — that can happen on the **T** (with the floppy/expansion
> cards) as well as the **M**. So it is optional *per configuration*, not deferred to a
> machine. Build it as an opt-in `DaisyChain` component in the machine layer that mounted
> peripherals register into; absent any such peripheral (bare T) it simply isn't
> instantiated and the only INT source is the 50 Hz video tick (IM1 / `0x0038`). The CORE
> requirements above are exactly what that future module needs — nothing in the core
> changes whether the chain is present or not.

---

## Validation notes

- **SingleStepTests is opcode-centric** and may not exercise the asynchronous int-ack
  sequence directly. Add **targeted unit tests** for: INT accepted in IM0/IM1/IM2 (assert
  the M1+IORQ ack cycle, the wait states, the bus read, and the resulting PC), the EI
  one-instruction delay, NMI entry/`RETN`, and `RETI` executing as a normal return (its
  snoop semantics are peripheral-side, but the opcode itself must behave like `RET`).
- `EI; RET`-style delay and `RETI`/`RETN` opcodes ARE in the instruction set the suite
  covers — make sure those pass as ordinary instructions in addition to the interrupt-flow
  unit tests above.

---

## One-line summary for the core author

> The core never generates or interprets a vector beyond IM1's fixed `0x0038` and IM2's
> table lookup; it **reads the int-ack byte from the bus**, performs the **M1+IORQ ack
> cycle with correct wait states**, and keeps **every opcode fetch visible** — that is all
> the daisy chain will ever need from it.

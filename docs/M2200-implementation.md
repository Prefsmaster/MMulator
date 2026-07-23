# M2200-implementation.md — the M2200 multi-function extension board

Device guide for the **M2200**, a real third-party multi-function board for the internal
extension slot (reference doc §5c "Daisy-chaining on the M" / §3.8.1 on a T). Mirrors the split
already used for the cassette (`docs/MDCR-implementation.md` holds the device detail; the
reference doc keeps a summary + pointer, not a duplicate). Read this together with reference doc
§5c (slot placement, corrected 2026-07-23 to internal-slot, NOT SLOT2) and §5d (the FDC this board
shares).

**Sources:**
1. Owner-supplied M2200 manual **port map excerpts** (2026-07-21) cross-checked against the
   project's own already-confirmed FDC/bank-switch port assignments (reference doc §5d).
2. **The full M2200 "Technical Manual rel. 1.01" (Miniware BV, Dutch, orig. Baexem juli 1985 —
   owner-supplied 2026-07-23)** — the actual technical manual for the board, superseding item 1
   as the primary source. Covers memory map/bank-switch, RAM disk, Centronics, interrupt
   structure, both CTCs, the Z80 SIO, the RTC (full register set), the FDC, board-level I/O
   decoding, connector pinouts, and the parts list. Translated from Dutch by the maintainer;
   flagged inline anywhere a translation could plausibly read two ways. Not yet cross-checked
   against a physical board — this is Miniware's own documentation, which is about as
   authoritative as a source gets for this board, but it is a single source, and (per its own
   foreword) the manual invites error reports, so isolated internal inconsistencies are possible
   and one is flagged below (§2.1) rather than silently resolved.

---

## 1. What this board is

M2200 is a **multi-function** board: one physical card bundling several independent capabilities
behind one connector. Plugs into the internal extension slot (full Z80 bus — memory + I/O, §5c);
on an M it sits behind the video board's downstream connector, not in front of it. **Not SLOT2** —
an earlier documentation pass mis-placed it there; corrected per the owner citing the Field
Service Manual's §3.8.1/§3.8.11 connector pinouts directly (reference doc §5c). Miniware's own
manual calls it "het Multifunktiebord" and documents both an **old** and a **new** hardware
revision — the new revision is what this board's own manual is written for, and is the revision
whose facts are CONFIRMED below unless a difference from the old revision is called out
explicitly (§2.2, §3.1).

Six features live on this one board:

| Feature | Port(s) | Status | Chip / mechanism |
|---------|---------|--------|-------------------|
| RAM bank-switch | `0x94` | **shared** with the plain floppy+RAM board (port), board-specific behavior confirmed for M2200 (§2.2) | 3-bit register, 6 banks × 8 KB |
| FDC | `0x8C`/`0x8D`/`0x90` | **shared** port assignment with the plain floppy+RAM board | µPD765 (later units) or **µPD7265** (first ~100 units) — §2.1 |
| RAM disk | `0x95`/`0x96`/`0x97` | new to this board | port-driven track/sector/data — NOT an FDC media variant |
| RTC | `0x9C`/`0x9D` | new to this board | **Hitachi HD146818** (MC146818-family), battery-backed, indexed register file — CONFIRMED §3.1 |
| Serial | `0x84`-`0x87` | new to this board | **Zilog Z8440 (Z80-SIO/0)**, two channels (RS232 + RS422) |
| Centronics | `0x98`-`0x9B` | new to this board | parallel printer interface |

A **seventh** device, not in the original six-feature table because it wasn't known about until
the full manual arrived: **a second Z8430 CTC, at ports `0x80`-`0x83`**, dedicated mostly to baud
rate generation for the serial interface (§3.5) — this is genuinely new to the project's model,
not a re-labeling of anything already documented. See §3.5 and §4's daisy-chain note.

The two "shared" rows are the interesting modularity result: M2200 doesn't reimplement the FDC or
the bank-switch register, it happens to expose them at the identical port addresses the plain
board already uses (§2 below) — though "identical ports" turns out to mean identical ports, not
necessarily identical chip or identical register-value semantics behind them (§2.1, §2.2). The
other four (plus the newly-found CTC2) are net-new capabilities this board adds beyond what the
plain floppy+RAM board offers.

---

## 2. Shared features (reuse, don't reimplement — with caveats now attached)

### 2.1 FDC — `0x8C` status/command, `0x8D` data, `0x90`-`0x93` control/DMA-req

**Chip identity — NOW MORE COMPLICATED THAN "shared," CONFIRMED (M2200 manual, ch.9):** "De
eerste serie van 100 stuks is op de markt gebracht met als floppycontroller de µPD 7265. De
latere met het type µPD 765" — **the first ~100 M2200 units shipped with a µPD7265** (Sony-
compatible recording format, aimed at 3.5″ media), **later units with the µPD765** (IBM-
compatible recording format, aimed at 5.25″/8″ media) already documented for the plain board
(reference doc §5d, machine CLAUDE.md milestone 19). The manual states both work "op dezelfde
manier" (the same way) as the Philips extension board's controller — i.e. command-set-compatible
at the level this manual describes — but they are **different physical chips with different
native recording formats**, not two names for the same part. **Scope decision, don't build
speculatively:** the project's existing FDC device (`Upd765`, M19) models the µPD765 case, which
covers later M2200 units and the plain board. **A µPD7265-equipped early M2200 is explicitly OUT
OF SCOPE** until/unless the owner wants it modeled — flag rather than silently assume every M2200
is a µPD765 board.

**Control register (`0x90`-`0x93`, only 4 LSBs meaningful) — CONFIRMED bit layout, matches the
project's existing model with one framing difference to flag:**

| Bit | Manual's own words | Matches existing reference doc §5d model? |
|---|---|---|
| D0 | `0` = DMA-Acknowledge, `1` = Chip Select | Roughly the existing `ENABLE` bit, but framed as a 2-way *mode* select (which function this same line performs) rather than a simple enable — same physical bit, sharper description. |
| D1 | `1` = Terminal Count | Matches existing `Count (TC)` bit exactly. |
| D2 | `1` = Reset Controller | Matches existing `RESET` bit exactly — and matches the independently-confirmed real-ROM presence probe (`OUT (0x90),0x04` asserts reset, reference doc §5d/§13.19). |
| D3 | `1` = Motor on, `0` = Motor off | Matches existing `MOTOR` bit exactly. |

Bit 4 (`SELDIS`, flagged in the reference doc as "P2C2 board only") is consistent with this
manual's own statement that M2200 uses only the 4 LSBs — M2200 doesn't populate it, as expected.

**Flag, don't silently resolve — an apparent internal inconsistency in the manual's own worked
example:** its bit table above states D2=1 asserts reset (matching the independently-confirmed
ROM behavior). But the manual's own "Software Reset controller" example writes `0x00` commented
"Reset" (D2=0) followed by `0x05` commented "haal reset weg" / "remove reset" (D2=1) — the
opposite polarity from its own table and from the ROM-confirmed behavior. **Trust the table +
the ROM disassembly (D2=1 asserts reset) over this one worked example's comments** — the
project's own already-implemented `Upd765.Reset()` behavior (§13.19) needs no change from this;
this is flagged purely so a future reader doesn't "fix" the confirmed behavior to match a
probably-mistranscribed 1985 example. Also worth carrying forward: **"Herstellen Reset toestand"
(restoring from the reset state)** — at power-on the control register starts in its reset state
and software must write `0x05` (D0=1 Chip Select, D2=1) to bring the chip to a normal operating
state — this is a **board-level power-on sequencing fact**, not obviously already covered by the
existing presence-probe modeling; worth checking `Upd765`/`InternalExtensionBoard`'s own
power-on sequence against it.

**Clock — CONFIRMED, explains an existing caveat:** the FDC chip is fed **4 MHz**, derived from
an **8 MHz crystal (K2)** through the data-separator chip (FDC9229, IC115), not fed 8 MHz
directly. The manual flags this **twice** as a potential source of **formatting problems** ("Er
kunnen echter problemen ontstaan omdat de controller geen 8 MHz klok krijgt toegevoerd") — the
standard FORMAT TRACK N/SC/GPL parameter table it reproduces (a generic µPD765 datasheet table,
not board-specific) is explicitly only valid **"voor een 8 MHz klok"** (for an 8 MHz clock), and
no 4-MHz-adjusted table is given. **Open item, relevant to the create-blank-disk milestone
(machine CLAUDE.md M20):** if/when the emulator ever needs to model FORMAT TRACK timing
authentically, the datasheet's stock GPL values may not be what the real board's software
actually used — flag rather than assume the generic table applies unmodified.

**Connector — CONFIRMED, resolves the drive-count question raised in machine CLAUDE.md M20:**
the 34-pin floppy interface connector (§5 below) carries **`DRISEL0`, `DRISEL1`, `DRISEL2`,
`DRISEL3`** — four physical drive-select lines, not two. Hardware description (ch.10.6): "IC 139
decodeert US0 en US1, die gebruikt worden voor het adresseren van de drives" — an external 2-to-4
decoder (IC139) turns the µPD765's own two native unit-select pins (US0/US1) into four individual
per-drive select lines, one per physical connector position. **This means M2200's own floppy
connector physically supports up to 4 drives** — a materially different (larger) number than the
"2 drives" figure the reference doc's FDC section previously stated. **RESOLVED (owner, 2026-07-23):
the plain, single-purpose Philips floppy+RAM board also supports 4 drives.** The "2 drives" figure
traced back to a poor-quality Field Service Manual scan (a transcription error, not a genuine
2-drive board) — a separate, official Philips-authored P2000 manual clearly states the expansion
board supports up to 4 drives, and this lines up with M2200's own design intent as a drop-in
replacement (with extras) for the official Philips memory/FDC card. Reference doc §5d now carries
this correction. **Also confirmed:** the US0/US1 decoder (and so drive addressing generally) **is
only active when the "motor-on" signal is asserted** — a real hardware gate, not just a software
convention, on top of the already-known
"wait ~0.5 s after motor-on before read/write" software rule.

**Upgraded from owner-statement to directly-verified primary source (2026-07-23):** the maintainer
has now personally read the official Philips "P2000 System T&M Reference Manual" (144-page scan,
`raw-conversion.md`) rather than relying only on the owner's report of it. Its Chapter 2 confirms
verbatim: *"Two disk drive units can be supported by all P2000 models, and in some cases two more
may be connected, allowing a total of four disks to be in use at one time, with a capacity of 560k
bytes"* — independently corroborating the 4-drive figure from a source the maintainer has now
verified directly, not just cited at owner's word. This also confirms the 35-track/16-sector/
256-byte/140k-per-disk geometry the plain board and M2200 share. See reference doc §5d for the
full citation and additional corroborated detail (extension-board signal names, port-map
cross-check).

**Extension Board signal names — also corroborated (2026-07-23), reference doc §5d:** the same
manual's Appendix A ("Extension Board Signal Listing") names the low-level FDC glue signals this
board's port assignment sits behind — `FCDK`/`FCDR`/`FCINT` (data-acknowledge/data-request/
interrupt handshake between the FDC chip and the rest of the board), `MOTORON`, `STEP`/`DIRECT`,
`TRACK00`, `WPROT`, `INDEX`, `WCK` (4 MHz) — all consistent with, and not previously named
alongside, the port-level behavior already documented above for M2200's shared FDC ports. No new
information for M2200 specifically (the plain board and M2200 share this same FDC port
assignment, §2.1 intro), but useful signal-level vocabulary for anyone implementing the FDC-to-
board glue logic.

**A drive-timeout watchdog exists on this board:** IC118 monitors the drive's index signal to
detect whether the drive is "vergrendeld" (locked/latched — i.e. a disk is inserted and the door
closed). If no index pulse arrives (no disk, or door open) during a data transfer, **an interrupt
fires after ~1 second** specifically to keep the FDC from hanging the computer; the documented
recovery is to reset and reinitialize the controller.

**RESOLVED, OUT OF SCOPE for the plain-board M20 milestone (owner decision, 2026-07-23) — deferred
indefinitely to a future M2200-specific milestone, not modeled as a device anywhere yet.** This
chip is sourced only from this manual — never independently confirmed to exist on the plain
floppy+RAM board (unlike the drive-count/motor-line facts above, which DID get cross-confirmed for
both boards). `P2000.Machine` CLAUDE.md §13.20 instead widens the existing "unmounted drive is a
no-op" rule (reference doc §5d) to also cover a configured-but-empty drive and a mid-transfer
eject — both resolve as an instant, harmless no-op, same as an absent drive, with no watchdog
timer or door-state device built. Revisit modeling IC118 for real only once an M2200-specific
milestone exists to build it against — this manual remains the primary source when that happens.

### 2.2 RAM bank-switch (`RAMSW`) — `0x94` — bit-width now CONFIRMED for this board

**Value semantics — CONFIRMED (M2200 manual §2.2): 3 bits used, 6 banks.** "De bankswitch is
ondergebracht in poort &H94 (write only). Hiervan worden de drie minst significante bits
gebruikt. Daar er echter maar 6 mogelijkheden zijn (0 tot 5), zal wanneer men 6 of 7 kiest bank 0
geselecteerd worden. Bij het opstarten wordt bank 0 geselecteerd." — three LSBs of a write-only
port, six valid values (0-5) selecting six 8 KB banks at `0xE000`-`0xFFFF`, values 6/7 alias to
bank 0, and bank 0 is the power-on default. Hardware-description confirms the mechanism (ch.10.2):
a PROM (IC144) decodes the bank number latched in a set of D-flops (IC142, using the same 3 LSBs)
and an adder (IC145) folds the decoded offset into the address bus.

**This is the "homebrew wider decode" case the reference doc already anticipated (§5 memory,
"Homebrew / third-party RAM cards — MORE bits of `0x94` → MORE banks") — not a contradiction of
the separately-sourced, service-manual-confirmed original Philips board's 1-bit `RAMSW` toggle.**
Those are two different physical boards behaving two different (compatible-at-the-port-level)
ways; M2200 is the first board this project has an exact bit-count/bank-count for on the
"homebrew" side of that split. **Resolves reference doc §5's open item 3** ("`0x94` decode width
— card-specific") for M2200 specifically.

**Corroborates T/102's 80 KB total, without over-claiming board identity:** base memory (§3 below)
is a contiguous 32 KB (`0x6000`-`0xDFFF`) plus 6 × 8 KB = 48 KB banked at `0xE000`-`0xFFFF` = **80
KB exactly**, matching the reference doc's `T/102` variant total. This is the first source this
project has that gives an exact bank-count/bit-width for an 80-KB-class board, and the numbers
land exactly on T/102 — **strong corroborating evidence for what a T/102-class bank-switch
mechanism looks like, not proof that a Philips-branded T/102 unit and Miniware's M2200 are the
literal same hardware.** Reference doc §5's "T/102... flag until the board that reaches them is
documented" open item can be updated to reflect this corroboration, without claiming full
identity.

**A second, orthogonal axis — address-decode WIDTH, also confirmed, do not conflate with the
bit-width finding above:** per the manual's own "Veranderingen op het nieuwe Multifunktiebord"
section, **the OLD multifunctiebord and the (separate) Philips Extension Board both accept ANY
address between `0x94` and `0x97`** as equivalent to `0x94` for bank-switching purposes (a coarse/
sloppy address decode). **The NEW multifunctiebord (this manual) narrows that decode to exactly
`0x94`**, freeing `0x95`-`0x97` for the RAM disk registers (§3.2). This is about which *addresses*
trigger the bank switch, not how many *data bits* of the value matter — both facts can be true of
the same board simultaneously (a coarsely-decoded address range, and a 3-bit value once you write
there), and there is no real conflict with the already-established 1-bit-original-board fact,
since that described the Philips-official board via a different, service-manual source.

---

## 3. New features (M2200-only, not present on the plain board)

### 3.1 Real-time clock (RTC) — `0x9C` select, `0x9D` data — now fully CONFIRMED via primary source

**Chip identity — CONFIRMED twice over: Hitachi HD146818** (owner statement, 2026-07-23; parts
list IC157 "HD146818", M2200 manual, cross-confirming). MC146818(A)-family, the same RTC used as
the IBM PC/AT's CMOS clock. **The manual's own RTC chapter (ch.8) independently confirms the
generic register map already added here last pass, upgrading it from "standard datasheet,
unconfirmed against this board" to CONFIRMED:** 64 bytes total, first 14 = time/date/alarm, other
50 = free general-purpose — verbatim match to the datasheet-derived table already in this doc.

**Access — CONFIRMED: 6-bit address.** "Men heeft toegang tot deze registers door... het adres (6
bits) van het gewenste register naar poort &H9C te schrijven" — 6 bits = exactly the 0-63 range
needed to address all 64 bytes. `0x9D` reads/writes the selected byte.

**Register A (`0x0A`) — CONFIRMED bit layout:**

| Bits | Meaning |
|---|---|
| 7 | `UIP` (Update In Progress), read-only — don't read time/date/alarm while set. |
| 6:4 | Crystal-select code. **New board: `0`,`1`,`0`. Old board: `0`,`0`,`0`.** Confirms/explains the "RTC base frequency changed from 4.194304 MHz to 32768 Hz" note already carried in machine CLAUDE.md — this is the exact register-level encoding of that change. |
| 3:0 | Periodic-interrupt rate select — 16 codes, manual gives the **full timing table for both crystal frequencies** (new-board 32.768 kHz: 3.90625 ms down to 500 ms across the 16 codes; old-board 4.194304 MHz: 30.517 µs down to 500 ms) — see the manual for the full table if periodic-interrupt timing is ever modeled precisely. |

**Register B (`0x0B`) — CONFIRMED bit layout:**

| Bit | Meaning |
|---|---|
| 7 | `SET` — 1 halts the update cycle so software can safely write time/date/alarm without a mid-write update. |
| 6 | `PIE` (Periodic Interrupt Enable) — routes to **CTC1 channel 2** (§4). Cleared on computer reset. |
| 5 | `AIE` (Alarm Interrupt Enable) — routes to **CTC1 channel 2**. |
| 4 | `UIE` (Update-ended Interrupt Enable) — routes to **CTC1 channel 2**; fires once/second when the clock updates. |
| 3 | Square Wave Enable — **the pin this would drive is not wired out on this board** ("niet naar buiten gevoerd") — value doesn't matter for M2200. |
| 2 | `DM` (Data Mode): `0` = BCD, `1` = binary. Changing it requires reprogramming all time/date/alarm fields. |
| 1 | `24/12`: `0` = 12-hour, `1` = 24-hour. Same reprogram-on-change caveat. |
| 0 | Daylight Savings Enable — **must be 0**; not implemented on this board. |

**Register C (`0x0C`) — CONFIRMED, read-only, read-clears:** bit7 `IRQF = PF·PIE + AF·AIE +
UF·UIE` (asserts the RTC's IRQ pin, wired to **CTC1 channel 2**'s trigger input); bit6 `PF`
(periodic flag); bit5 `AF` (alarm flag); bit4 `UF` (update-ended flag); bits 3:0 unused, always 0.
Reading register C clears `IRQF`/`PF`/`AF`/`UF` and deasserts IRQ — matches the read-clears model
already documented last pass.

**Register D (`0x0D`) — CONFIRMED, read-only:** bit7 `VRT` (Valid RAM and Time) — clears to 0 when
the backup battery has discharged too far and gets disconnected (below), meaning all RTC RAM
content is lost; bits 6:0 unused, always 0.

**Daisy-chain wiring — CONFIRMED, resolves the previously-open "does it raise an interrupt"
item:** the RTC's IRQ pin feeds **CTC1 channel 2**'s trigger input directly (ch.8: "Voor het geven
van interrupts is de RTC via kanaal 2 van CTC1 in een Daisy Chain opgenomen"). This exactly
matches the interrupt vector table already confirmed via ROM disassembly (machine CLAUDE.md
§13.17: IM2 vector base `0x6020`, RTC at `0x6024-25`) — **independent cross-confirmation from a
second source of the exact same CTC1-channel-2-is-RTC assignment.**

**Battery protection circuit — new fact, not previously documented:** the backup battery (B1,
3.6 V) is monitored by a zener + resistor pair (V9/R4); if it discharges too far, a comparator
(IC158) cuts the RTC chip's own supply pin to prevent over-discharge damage — this is the
mechanism behind Register D's `VRT` bit going to 0. The battery auto-recharges whenever the
computer is powered from the mains. **Crystal:** 32.768 kHz (K1), trimmable via C2.

**Open items, narrowed further:**
- Whether M2200's actual driving software (BASIC NL module, or the board's own example programs)
  runs the chip in BCD or binary mode and 12- or 24-hour format is still a software-convention
  question, not a chip fact — the manual's own worked example (bijlage 7, a BASIC clock-display
  program) wasn't transcribed here in full; check it if an exact default is needed.
- Whether M2200 deviates from the stock 64-byte map (century byte, repurposed general-purpose
  bytes) remains open — no evidence of a deviation found, but not exhaustively ruled out either.

### 3.2 RAM disk — `0x95` track, `0x96` sector, `0x97` data — geometry now CONFIRMED

**Capacity:** 64 KB or 256 KB, a purchase-time hardware option (parts list: 8× `HM4864` [64Kbit×1]
for the 64 KB version, 8× `HM50256` [256Kbit×1] for the 256 KB version — same board position,
different DRAM density, pin-compatible).

**Geometry — CONFIRMED:** sector size is **256 bytes** (same as the real floppy, matching
`docs/JWSDOS-format.md`'s conventions, though this is a genuinely separate device — §3.2 below).
Maximum **16 sectors/track** (only the 4 LSBs of the sector register are used, regardless of
capacity). Track register width depends on capacity: **4 LSBs for the 64 KB version, 6 LSBs for
the 256 KB version** (4 bits × 16 sectors × 256 B = 64 KB exactly; 6 bits × 16 sectors × 256 B =
256 KB exactly — both check out arithmetically). Access pattern: load the track register (`0x95`),
then the sector register (`0x96`), then stream bytes through the data register (`0x97`) via
`OTIR`/`INIR` — a byte counter auto-increments and resets when the sector register is reloaded.

**Persistence — PARTIALLY resolved, more narrowly than "battery-backed like the RTC":** the manual
states an explicit design provision keeps RAM disk contents intact **across a reset-button press**
specifically ("Als extra is op de RAM disk een voorziening aangebracht die ervoor zorgt dat de
informatie behouden blijft wanneer de resetknop ingedrukt wordt... vooral bruikbaar wanneer een
programma zichzelf 'ophangt'"), framed as a deliberate extra feature (implying that without it,
something about a reset-button press WOULD disturb it — hardware description ch.10.3 explains
why: pressing reset hands DRAM refresh over to a dedicated counter, IC161, buffered/tri-stated via
IC162, specifically so refresh continues uninterrupted through the reset pulse). **This is a
narrower guarantee than the RTC's non-volatility** — it says nothing about surviving a full
power-off, and the RAM disk has no battery backup mentioned anywhere in this manual (unlike the
RTC's explicit battery). **Modeling implication:** RAM disk contents should probably survive a
**warm reset** (matches the already-established "reset ≠ reinitialize" pattern, but for a
different underlying reason than the RTC — refresh continuity, not battery backup), while a
**cold reset / fresh power-on** most likely still applies the standard garbage-fill treatment —
this latter half is inference from the manual's own framing, not a sentence it states directly, so
treat it as probable, not CONFIRMED.

**Open items, narrowed:**
- DOS-level directory/allocation format layered on top by software (if any) — still unknown, this
  manual doesn't cover it (RAM disk is explicitly software/OS-managed, per ch.3: "Het is echter
  niet zo dat de RAM disk ook door de floppy controller bestuurd wordt. Dit moet door de software
  of door het Operating System gebeuren").
- Cold-reset persistence — inferred probable-not, per above, not explicitly stated.

### 3.3 Serial — Zilog Z8440 (Z80-SIO/0), `0x84`/`0x85` (RS232 ch.A), `0x86`/`0x87` (RS422 ch.B)

**Chip identity — CONFIRMED:** parts list IC120 = `Z8440` (the Z80-SIO/0 part number), matching
the manual's own "Voor de seriële interface is de Z80/SIO-0 gebruikt." Two simultaneously-active
channels: channel A → RS232 line driver (separate TX/RX/handshake lines), channel B → RS422
"network interface" (single differential pair, both directions, needs external termination at
each end — a genuinely different electrical interface from channel A, not just a second RS232
port). This is a materially different chip from the SLOT2 Serial card's plain UART (reference doc
§5c) — **do not share a device class between the two.**

**Register model — CONFIRMED, standard Z80 SIO shape:** 8 write registers (`WR0`-`WR7`, though
`WR6`/`WR7` are meaningless here — SDLC-only, and this board is hardwired async-only) and 3 read
registers (`RR0`-`RR2`) per channel, selected by first writing the target register number (bits
0-2) to `WR0`, then accessing the command/data port again for that register. Channel A has higher
daisy-chain priority than channel B. `WR2`/`RR2` (interrupt vector) exist **only on channel B** —
the vector is shared across both channels.

**Interrupt wiring — CONFIRMED, resolves the previously-open item:** the SIO **does** participate
directly in the IM2 daisy chain (§4) — it is not purely polled. Vectored-interrupt mode (`WR1`
bit2=1) uses a fixed sub-vector encoding (bits 1-3 of the vector byte set per the SIO's own
priority-encoded cause: which channel, Tx-empty vs Rx-available vs special condition) layered onto
the common base vector in `WR2`.

**Interrupt causes, per channel, CONFIRMED (`WR1`):** Rx interrupt on every character / on first
character of a message only / on a special condition (parity error, framing error, overrun,
break) — the third option only in combination with one of the first two. Tx interrupt when the
transmit buffer empties (only fires again after loading and then re-emptying the buffer). Errors
latch until an explicit Error Reset command (`WR0` bits 4-5), except Framing Error, which clears
itself once the byte that caused it is read.

**Rx has a 3-byte buffer** — a 4th character arriving before the 1st is read overwrites the 3rd
and sets Overrun in `RR1` bit5. Once read, a character can't be re-read. Parity error is `RR1`
bit4, Framing error `RR1` bit6, Break Detect `RR0` bit7 (not necessarily an error — can be sender-
initiated or a dropped connection).

**Baud rate generation — CONFIRMED, this is CTC2's main job (§3.5):** RS232 Rx baud from **CTC2
channel 0**, RS232 Tx baud from **CTC2 channel 1** (independently settable — the board explicitly
supports split Rx/Tx rates, e.g. Videotex's 1200/75). RS422's baud is normally fixed, derived
directly from the 2.5 MHz system clock at a chosen sample-rate divisor (16/32/64 → 156250 / 78125
/ 39062.5 baud fixed options; 1 is not a valid divisor for Rx) — **or**, if a board jumper is
moved, RS422 baud can instead be set via **CTC2 channel 2** the same way RS232's is. The manual
gives a full CTC-time-constant-to-baud-rate table (both counter-mode ÷1 and timer-mode ÷16
derivations) — see the manual directly if exact baud timing constants are ever needed for
emulation; not transcribed byte-for-byte here since it's a generic divisor table, not a hardware
quirk.

**Character framing detail worth carrying forward:** for non-8-bit characters, the unused high
bits of a read byte carry parity/stop-bit information rather than being don't-care — e.g. the
manual's own Viditel example (7 data bits, even parity, 1 stop bit) puts the parity bit in bit 7
of the byte read back from the data register; software must mask it explicitly.

**Open items, narrowed:**
- Host-side transport target for each channel (RS232 pass-through vs. TCP vs. file sink) — still
  an emulator design question, this manual naturally has nothing to say about it.
- Exact interrupt-vector sub-encoding bit assignment (`WR1` bits 1-3, "fig.4" in the manual) — the
  manual illustrates this as an image, not transcribed in text form here; consult the manual
  directly (or the Zilog Z80 SIO datasheet it's copied from — a link to the datasheet PDF is in
  the manual itself) if implementing vectored-interrupt sub-cause dispatch precisely.

### 3.4 Centronics — `0x98` data, `0x99` status, `0x9A` strobe-on, `0x9B` strobe-off

**Status register (`0x99`) bits — CONFIRMED:** bit4 Error (negative logic), bit3 Printer On, bit2
Paper Out, bit1 Busy, bit0 ACK; bits 5-7 always 0.

Strobe ports remain **access-triggered, not data-dependent** (already documented) — hardware
description confirms the mechanism: an SR-flip-flop (IC132) is set by addressing `0x9A` and reset
by addressing `0x9B`, with two gates paralleled for drive strength. Data (`0x98`, write-only) and
status (`0x99`, read-only) are ordinary registers.

The natural host-side sink is "bytes become a file," mirroring the cassette's "Save as `.cas`"
pattern — no printer emulation required, just capture the byte stream. The manual's own example
software is a **printer spooler**: buffered on interrupt, so the host program can keep running
while printing proceeds in the background — worth mirroring in spirit if/when a host-side capture
UI is built (a queued/buffered write rather than a blocking one).

**Open item, unchanged:** whether this shares its register shape with a possible future SLOT2
Centronics card is still unconfirmed (SLOT2 Centronics ports not yet sourced).

### 3.5 NEW — second Z80 CTC (Zilog Z8430), `0x80`-`0x83` — not previously documented anywhere in this project

**This device was unknown to the project before the full manual arrived.** The reference doc and
machine CLAUDE.md's CTC milestone (17) model exactly **one** `Z80Ctc` (ports `0x88`-`0x8B`,
"CTC1" in this manual's own terms). **M2200 has a second, physically separate Z8430 CTC chip**
(parts list: IC113 = CTC1 at `0x88`-`8B`, IC121 = a **second** `Z8430-CTC` at `0x80`-`83`) — I/O
decode table confirms the ports: `CTCB`/CTC2 = `0x80`-`0x83`.

**Role — CONFIRMED:** CTC1 (already modeled) is the interrupt controller for floppy/floppy-error/
RTC/keyboard (unchanged from what's already documented). **CTC2 is dedicated almost entirely to
baud-rate generation** for the serial interface (§3.3): channel 0 = RS232 Rx baud, channel 1 =
RS232 Tx baud, channel 2 = optional RS422 baud (jumper-selected) or otherwise free, channel 3's
trigger input is wired to **channel 2's own output** (a pulse each time channel 2 reaches zero) —
allowing channel 3 to be chained off channel 2 either in counter mode (decrements once per channel
-2-rollover) or in a triggered timer mode, which the manual documents as a technique for running
**a second, independent machine-language program on interrupt basis**, entirely separate from the
CTC1-driven interrupt structure. **Channels used for baud-rate generation must not be programmed
in interrupt mode** (the manual states this as a hard rule, not a suggestion).

**Daisy-chain position — CONFIRMED (figure 1 in the manual): CTC1 → SIO → CTC2, left to right,
highest to lowest priority** — i.e. CTC1's 4 channels are all higher priority than the SIO's 2
channels, which are all higher priority than CTC2's 4 channels. Ten total daisy-chain positions:
CTC1 ch.0-3, SIO ch.A/ch.B, CTC2 ch.0-3. This directly extends (does not contradict) the
already-confirmed CTC1 vector table (machine CLAUDE.md §13.17, IM2 base `0x6020`).

**Channel Control Register — CONFIRMED bit layout (applies to both CTC1 and CTC2, same chip
family, standard Z80 CTC shape):**

| Bit | Meaning |
|---|---|
| D0 | `1` = D1-D7 apply to this channel individually (normal channel-control write). `0` = this is an interrupt-vector write instead — D3-D7 become the vector's high bits, D1/D2 are forced per channel number (ch0=`00`, ch1=`01`, ch2=`10`, ch3=`11`), D0 of the vector itself is always 0 (a vector is 2 bytes). |
| D1 | Reset — `1` stops the counter/timer immediately. |
| D2 | Load Time Constant — `1` means the next byte written is the time constant. |
| D3 | Timer-mode start behavior — `0` starts immediately on time-constant load, `1` waits for the trigger input. |
| D4 | Counter-mode trigger edge — `0` = falling edge, `1` = rising edge. |
| D5 | Timer-mode prescaler — `0` = ÷16, `1` = ÷256. |
| D6 | Mode select — `0` = Timer mode (system-clock-derived, prescaler applies), `1` = Counter mode (external trigger, no prescaler). |
| D1 (of Channel Control Register, separate from the reset bit above — used only in interrupt-enable) | Interrupt Enable when D0=1: a channel reaching 0 fires an interrupt if this bit is set. |

(Note: the manual numbers the Interrupt Enable bit as "D1" in its own prose, distinct from the
Reset bit also called "D1" in the vector-vs-channel-write disambiguation above — this reflects the
chip's own overloaded bit numbering across its two write-word shapes, not a transcription error;
consult the manual's own figure 5 for the disambiguated bit-by-bit picture if implementing this
from scratch.) **This did not change anything already confirmed for CTC1** (machine CLAUDE.md
§13.17's control-word bit layout already matches) — it newly confirms the **same** layout applies
to CTC2, which is the useful new fact.

**Time constant range — CONFIRMED:** 1-256 (0x00 read as 256). Timer-mode period = prescaler ×
0.4 µs (the system clock's period) — so ÷16 → 6.4 µs/count, ÷256 → 102.4 µs/count. **One CTC2-
specific timing quirk, CONFIRMED, don't apply to CTC1:** "In countermode geldt voor de kanalen 0
tot en met 2 van CTC2 een extra deelfactor van 2 voor de systeemklok" — channels 0-2 of CTC2
specifically have an **extra ÷2 factor** applied to the system clock in counter mode (relevant to
the baud-rate table above); this is called out as specific to CTC2's ch.0-2, not a general CTC
property.

**Open items:**
- Whether this project's device model should be one parameterized `Z80Ctc` class instantiated
  twice (matching the "board-agnostic chip, board wires it" decision already made for CTC1 —
  machine CLAUDE.md §13.17) is a design question for whoever picks up M2200 modeling, not a
  hardware fact — flagged here for that future work, not resolved.

---

## 4. Interrupt architecture — the full daisy chain, CONFIRMED

Combining §3.1/§3.3/§3.5 above, the M2200's complete IM2 daisy chain, highest to lowest priority:

**CTC1 ch0 (floppy controller) → CTC1 ch1 (floppy controller error detection) → CTC1 ch2 (RTC) →
CTC1 ch3 (keyboard scan) → SIO ch.A (RS232) → SIO ch.B (RS422) → CTC2 ch0 (RS232 Rx baud) → CTC2
ch1 (RS232 Tx baud) → CTC2 ch2 (RS422 baud / free) → CTC2 ch3 (chained off ch2, or free).**

CTC1's own four assignments and IM2 vector table (`0x6020`-`0x6027`) exactly match what machine
CLAUDE.md §13.17 already has confirmed from ROM disassembly — an independent second-source
cross-confirmation, not new information for CTC1 itself. **What's new is everything from the SIO
onward** — the SIO's daisy-chain participation and CTC2's existence, role, and position were not
previously documented anywhere in this project. Any future M2200-specific milestone work should
account for a 10-member chain, not the 4-member one the plain floppy+RAM board needs.

---

## 5. Hardware / board-level facts

### 5.1 I/O address decode (full board port map, CONFIRMED)

A PROM (IC107, type 82S123) decodes I/O addresses `0x80`-`0x9F` into blocks; further chips
(IC100/102/155/160) split some blocks into individual per-register signals. All signals are
negative logic. Full block map:

| Signal | Port(s) | Signal | Port(s) |
|---|---|---|---|
| `CTCB` (CTC2) | `0x80`-`0x83` | `MMTS` (RAM disk track) | `0x95` |
| `SIO` | `0x84`-`0x87` | `EMSS` (RAM disk sector) | `0x96` |
| `CTC1` | `0x88`-`0x8B` | `EMDS` (RAM disk data) | `0x97` |
| `FDC` (status/data) | `0x8C`-`0x8D` | `CDO` (Centronics data out) | `0x98` |
| `WRP`/`RDP` (FDC control, write/read pulses) | `0x90`-`0x93` | `CDI` (Centronics status in) | `0x99` |
| `MMBS` (memory bank switch) | `0x94` | `CDN` (Centronics strobe on) | `0x9A` |
| | | `CSF` (Centronics strobe off) | `0x9B` |
| | | `RTCR` (RTC register-select) | `0x9C` |
| | | `RTCD` (RTC data) | `0x9D` |

Note the FDC's control register block (`WRP`/`RDP`) is decoded as the **whole `0x90`-`0x93`
range**, not just `0x90` — consistent with the coarse block-level decoding already noted for the
old bank-switch address range (§2.2); only the write/read *pulse*, not the exact low address
bits, matters within that block. **Board-level caution, confirmed:** even unused ports within
`0x80`-`0x9F` still activate the bus transceiver on access — you cannot repurpose an apparently-
unused port in this range for something else without side effects.

### 5.2 Connectors

**25-pin Sub-D (Centronics + RS232 + RS422, shared connector):** full pinout table available in
the manual (data lines D0-D7, TxD/RxD/RTS/CTS/DCD/DTR/Ground for RS232, TRxD ×2 for RS422,
STROBE/ACKN/BUSY/paper-out/printer-on/ERROR for Centronics, +5V on pin 1 for RS422). Transcribe in
full if/when the connector needs modeling in detail; the port-level register facts above are the
part that matters for emulation.

**34-pin floppy interface connector (Shugart-style, only even pins carry signals, odd = GND) —
CONFIRMED, this is the source of the 4-drive finding (§2.1):**

| Pin | Signal | Pin | Signal |
|---|---|---|---|
| 2 | NC | 20 | STEP* |
| 4 | NC | 22 | WRITE DATA* |
| 6 | `DRISEL0`* | 24 | WRGATE* |
| 8 | INDEX* | 26 | TRACK00* |
| 10 | `DRISEL1`* | 28 | WRPROT* |
| 12 | `DRISEL2`* | 30 | READ DATA* |
| 14 | `DRISEL3`* | 32 | HEADSEL* |
| 16 | MOTORON* | 34 | NC |
| 18 | DIRECTION | | |

(`*` = negative logic.) Standard Shugart/IBM-style 34-pin layout, four drive-selects, single
shared MOTORON line (not per-drive) — **this answers the earlier machine-layer open item about
whether MOTOR is a single shared line or per-drive independent (M20, §13.20): on M2200 it is a
single shared line**, gated by the drive-select decoder per §2.1. `WRPROT` and `TRACK00` are
per-selected-drive sense lines (read back only for whichever drive is currently addressed), not
per-drive independent state the controller tracks simultaneously for all drives — worth checking
against how `Upd765`'s per-drive state (M19's `_cylinder[drive]` array) is structured, since the
real hardware only ever "sees" one drive's WRPROT/TRACK00 at a time (whichever is selected), which
may already be how the emulator models it, or may need reconciling.

### 5.3 Parts list highlights (full lists in the manual, ch.11.4-11.6)

Confirms/cross-confirms chip identities used throughout this doc: IC112 FDC (µPD7265 early units /
µPD765 later, §2.1), IC113 + IC121 both `Z8430-CTC` (CTC1 + CTC2, §3.5), IC115 `FDC9229` (data
separator), IC120 `Z8440-SIO` (§3.3), IC157 `HD146818` (RTC, §3.1), IC126/127 `LM7805`/`LM7912`
(±voltage regulators). Crystals: K1 = 32.768 kHz (RTC), K2 = 8 MHz (FDC, dropped to 4 MHz via the
data separator, §2.1). Backup battery B1 = 3.6 V. RAM: 8× `HM4864` (64 Kbit×1, main 64 KB
expansion, always populated) plus a second bank of 8× `HM4864` or `HM50256` (256 Kbit×1) for the
64 KB or 256 KB RAM disk option respectively (§3.2).

---

## 6. Design consequences

- **Board = manifest of features, not a monolithic class.** M2200 is best modeled as an assembly
  of **seven** independent feature devices now (six original + CTC2, §3.5), each with its own
  `IIoSlot`/`IDevice` registration, rather than one bespoke `M2200Board` class that reimplements
  FDC/bank-switch/CTC logic a second time. The plain floppy+RAM board and M2200 both instantiate
  the same FDC and (compatible) bank-switch feature classes; only the board-assembly step differs
  (which features it lists, and — new — M2200 lists a *second* CTC instance the plain board
  doesn't need).
- **"Shared" ports don't automatically mean "shared implementation."** The FDC's chip identity
  turned out to be revision-dependent (µPD7265 vs µPD765, §2.1) and the bank-switch register's
  bit-width/address-decode width both turned out to be board-specific facts requiring their own
  sourcing (§2.2) even though the port numbers matched from the start. Keep treating "same port"
  and "same behavior" as two separate claims needing two separate confirmations, per feature.
- **Port-base parameterization already earns its keep once, and should stay a habit regardless** —
  unchanged from before, now reinforced by CTC2 needing the exact same `Z80Ctc` class as CTC1 at a
  different port base, which only works cleanly if port base was already a constructor parameter.
- **No address collisions** across the full board: `0x80`-`0x9D` is now completely accounted for
  (§5.1's table), SLOT2's address space (`0x40`/`0x41`/`0x61`/`0x62`) remains disjoint.

---

## 7. Open items (collected, post-manual)

**Resolved by the full manual (kept here for traceability, not re-flagged elsewhere):**
- RAM bank-switch bit-width for M2200 — 3 bits, 6 banks (§2.2).
- RTC index → field mapping, and full Register A/B/C/D bit layout — CONFIRMED (§3.1).
- RTC alarm/interrupt wiring — CTC1 channel 2 (§3.1/§4).
- RAM disk geometry — 256 B/sector, 16 sectors max, 4- or 6-bit track register by capacity
  (§3.2).
- Z80 SIO control-word model and interrupt-wiring/daisy-chain participation (§3.3/§4).
- Existence of CTC2 — entirely new, not previously known to the project (§3.5).

**Explicitly deferred, not just open (owner decision, 2026-07-23):**
- The IC118 drive-timeout watchdog (§2.1) — deferred indefinitely to a future M2200-specific
  milestone; not modeled at all in `P2000.Machine` CLAUDE.md §13.20's plain-board scope, which
  instead widens the existing "unmounted drive is a no-op" rule to cover the same triggering
  conditions (empty configured drive, mid-transfer eject) without a watchdog device or door state.
  Re-open this only when an M2200-specific milestone exists.

**Still open, narrowed:**
- FDC chip variant per specific M2200 unit (µPD7265 vs µPD765) — a per-board fact the owner would
  need to state for their own hardware, if physical-board fidelity to a specific unit ever
  matters; the project's own emulation scope is µPD765-only for now.
- ~~Whether the plain, single-purpose Philips floppy+RAM board's own connector matches M2200's~~
  — **RESOLVED (owner, 2026-07-23; independently verified by the maintainer 2026-07-23 via direct
  reading of the manual): yes, 4 drives on both boards, 560k total** — the earlier "2 drives"
  figure traced to a poor Field Service Manual scan; the official Philips "P2000 System T&M
  Reference Manual" (now read in full, `raw-conversion.md`) confirms 4 drives / 560k directly in
  its own Chapter 2, consistent with M2200's design intent as a drop-in replacement for the
  official card (§2.1).
- FORMAT TRACK timing parameters under the board's actual 4 MHz FDC clock (vs. the generic 8-MHz-
  clock datasheet table the manual itself reproduces) — flagged, not resolved.
- RAM disk cold-reset (full power-cycle) persistence — inferred probable-not, not stated directly.
- RAM disk DOS-level directory/allocation format, if any — unknown.
- Serial host-side transport target per channel (RS232 vs RS422) — an emulator design choice, not
  a hardware fact this manual could resolve.
- Whether M2200's Centronics register shape matches a future SLOT2 Centronics card — still
  unconfirmed (SLOT2 Centronics ports not yet sourced).
- CTC2 device-modeling approach (reuse `Z80Ctc` at a second port base vs. something else) — a
  design question for future M2200 implementation work, not attempted here.
- ~~Cross-reference for later reconciliation~~ — **DONE (2026-07-23/26):** the 4-drive connector,
  second CTC, T/102 bank-count corroboration, and the shared (not per-drive) motor line were
  reconciled into machine-layer milestone 20 and UI milestone 14 (`P2000.Machine`/`P2000.UI`
  CLAUDE.md) in a follow-up pass — see those docs directly rather than this note for current
  milestone text.

---

## 8. Provenance

- **2026-07-21** — owner-supplied port-map excerpts (chat-pasted), M2200 manual origin unstated at
  the time. Established the six-feature table, the shared-port observations, and most of the
  first-pass open items.
- **2026-07-23** — owner states the RTC chip is a Hitachi HD146818; integrated with the standard
  MC146818-family register map (datasheet-derived, not yet cross-checked against M2200's own
  manual at that point).
- **2026-07-23** — owner supplies the full M2200 "Technical Manual rel. 1.01" (Miniware BV, Dutch,
  1985; Word conversion "Steenderen, juli 2026"). This pass: confirmed/expanded the RTC register
  set in full, resolved the bank-switch bit-width, resolved RAM disk geometry, resolved the Serial
  interface's control model and daisy-chain participation, discovered the previously-unknown CTC2,
  discovered the FDC chip-variant history and the 4-drive connector, and flagged one apparent
  internal inconsistency in the manual's own FDC-reset worked example (§2.1). Translated from
  Dutch by the maintainer — flagged inline wherever a translation could plausibly read two ways.
- **2026-07-23** — owner reports checking a separate, official Philips-authored P2000 manual,
  which clearly states the expansion board supports up to 4 drives — resolving the "does the
  plain board match M2200's connector" open item in favor of yes, both 4 drives. Owner also
  identifies the earlier "2 drives" figure's likely root cause: a poor-quality Field Service
  Manual scan that didn't transcribe cleanly. Not yet independently reviewed by the maintainer
  against the new manual itself (owner-reported, no document uploaded for this specific point).
- **2026-07-23** — owner supplies the official Philips "P2000 System T&M Reference Manual"
  (144-page scanned PDF, no OCR, 1982, 12NC 5103 991 30421) — the manual referenced but not
  uploaded in the 2026-07-23 entry above. Maintainer personally read and transcribed all 144
  pages (`raw-conversion.md`, this docs folder) rather than relying on the owner's paraphrase.
  Independently confirmed: the 4-drive/560k figure (Chapter 2, directly, closing the "not yet
  independently reviewed" caveat from 2026-07-23), the 35-track/16-sector/256-byte disk geometry,
  the extension-board FDC glue-signal names (Appendix A), the full port map (Appendix B — CTC at
  `0x88`-`0x8B`, FDC at `0x8C`-`0x8D`/`0x90`-`0x93`, matching this project's existing figures
  exactly), and the CTC IM2 vector table (`0x6020`/`22`/`24`/`26`, in the manual's own body text,
  not just an appendix — a third independent source for a fact already confirmed twice). No new
  M2200-specific facts beyond the drive-count/geometry corroboration above — this manual describes
  the plain Philips board, not M2200, so it can corroborate shared features (FDC, drive count) but
  says nothing about M2200's own new features (RTC, CTC2, Serial, RAM disk, Centronics).
- **2026-07-23 (later same day)** — owner decision on the IC118 drive-timeout watchdog (§2.1,
  §7): not modeled for the plain-board `P2000.Machine` CLAUDE.md §13.20 milestone, deferred
  indefinitely to a future M2200-specific milestone. Reasoning: IC118 is sourced only from this
  manual, unlike the drive-count/motor-line facts that got cross-confirmed for the plain board
  too — and it's a real-world-only edge case (physical door/missing-media condition) not worth a
  second device for, in contrast to the cassette's deliberately real-world-accurate phase-bitstream
  model (`docs/MDCR-implementation.md`), where that fidelity is the actual point of the device.
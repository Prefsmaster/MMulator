# M2200-implementation.md — the M2200 multi-function extension board

Device guide for the **M2200**, a real third-party multi-function board for the internal
extension slot (reference doc §5c "Daisy-chaining on the M" / §3.8.1 on a T). Mirrors the split
already used for the cassette (`docs/MDCR-implementation.md` holds the device detail; the
reference doc keeps a summary + pointer, not a duplicate). Read this together with reference doc
§5c (slot placement, corrected 2026-07-24 to internal-slot, NOT SLOT2) and §5d (the FDC this board
shares).

**Source:** owner-supplied M2200 manual (port map, 2026-07-21) cross-checked against the
project's own already-confirmed FDC/bank-switch port assignments (reference doc §5d). Not yet
cross-checked against a physical board or a second independent source — treat "AFAIK"-flagged
items below as probable, not confirmed.

---

## 1. What this board is

M2200 is a **multi-function** board: one physical card bundling several independent capabilities
behind one connector. Plugs into the internal extension slot (full Z80 bus — memory + I/O, §5c);
on an M it sits behind the video board's downstream connector, not in front of it. **Not SLOT2** —
an earlier documentation pass mis-placed it there; corrected per the owner citing the Field
Service Manual's §3.8.1/§3.8.11 connector pinouts directly (reference doc §5c).

Six features live on this one board:

| Feature | Port(s) | Status | Chip / mechanism |
|---------|---------|--------|-------------------|
| RAM bank-switch | `0x94` | **shared** with the plain floppy+RAM board | `RAMSW`-style flip-flop or wider decode (open item, §3.2) |
| FDC | `0x8C`/`0x8D`/`0x90` | **shared** with the plain floppy+RAM board | µPD765, per reference doc §5d |
| RAM disk | `0x95`/`0x96`/`0x97` | new to this board | port-driven track/sector/data — NOT an FDC media variant |
| RTC | `0x9C`/`0x9D` | new to this board | battery-backed, indexed register file |
| Serial | `0x84`-`0x87` | new to this board | Z80 SIO, two channels (RS232 + RS422) |
| Centronics | `0x98`-`0x9B` | new to this board | parallel printer interface |

The two "shared" rows are the interesting modularity result: M2200 doesn't reimplement the FDC or
the bank-switch register, it happens to expose them at the identical port addresses the plain
board already uses (§2 below). The other four are net-new capabilities this board adds beyond
what the plain floppy+RAM board offers.

---

## 2. Shared features (reuse, don't reimplement)

### 2.1 FDC (µPD765) — `0x8C` status, `0x8D` data, `0x90` control/DMA-req

Identical port assignment to the plain floppy+RAM board's FDC (reference doc §5d: `0x8C` =
`DSKIO1`/MSR, `0x8D` = data/status, `0x90` = `DSKCTRL` control-latch OUT / byte-ready-flag IN).
**Owner's own words, verbatim:** "AFAIK, the FDC registers are the same as for the now
implemented version." Read as: very likely a literal drop-in of whatever FDC device class lands
for the plain board (machine CLAUDE.md milestone 19), but the equivalence is the owner's
recollection, not a manual cross-check yet — confirm against the M2200 manual's FDC register
descriptions (not just the port numbers) before assuming byte-for-byte identical behavior,
particularly around the `0x90` IN/OUT split (reference doc §5d already flags this as an inferred
mechanism, not a fully documented one, even for the plain board).

### 2.2 RAM bank-switch (`RAMSW`) — `0x94`

Same port address as the original Philips floppy+RAM board's bank flip-flop (reference doc §5
memory / §5d). **Open item:** address parity does not by itself confirm bit-width parity. The
reference doc already distinguishes the original board's **1-bit** `RAMSW` (D0 only, two 16 KB
banks) from **homebrew cards** that decode more bits of `0x94` for more banks. Which of these
M2200 implements is not established by the information gathered so far — check the M2200 manual's
own description of what values are meaningful at `0x94` and how many banks result before assuming
either the narrow or wide behavior.

---

## 3. New features (M2200-only, not present on the plain board)

### 3.1 Real-time clock (RTC) — `0x9C` select, `0x9D` data

Indexed-register-file pattern: write an index to `0x9C` to select a sub-register (time/date/alarm
fields, plus whatever status the chip exposes), then read or write that sub-register's value
through `0x9D`. This is the standard shape for RTC chips of this era (cf. a PC's CMOS RTC,
already used as the analogy in reference doc §5c).

**Non-volatility (already flagged in reference doc §5c / machine CLAUDE.md §17, restated here for
this device specifically):** the battery-backed contents must survive a cold reset. `Reset()` on
this device must **preserve/restore** the RTC's memory instead of applying the non-zero
garbage-fill treatment every other populated RAM region gets on cold boot. This is the one device
on this board where "reset" does not mean "reinitialize."

**Open items:**
- The index → field mapping at `0x9C` (which index addresses seconds/minutes/hours/day/month/
  year/alarm/status) is not yet sourced from the manual excerpts gathered in this conversation.
- Alarm behavior (does it raise an interrupt? through which line — is it wired into the IM2
  daisy chain, or does it only latch a status bit the ROM/driver software polls?) is unconfirmed.

### 3.2 RAM disk — `0x95` track, `0x96` sector, `0x97` data

**Port-driven, not an FDC media variant** (owner-confirmed directly: "RAM disk is port driven") —
a genuinely separate device from the FDC above, even though both are "disk-shaped." The
track-select / sector-select / data-port triple is the classic simple-disk-controller register
pattern; the CHS→linear-offset addressing math likely overlaps conceptually with the `.dsk`
geometry handling already documented in `docs/JWSDOS-format.md`, but the bus-level register
interface has nothing in common with the µPD765's command/execution/result-phase model — do not
route RAM disk accesses through the FDC device.

**Does not collide with the plain board's FDC.** An earlier pass of this documentation loosely
described "the official memory expansion/FDC cartridge" as living in "94h-97h" — that phrasing
turns out to refer to the bank-switch/RAM-expansion side of the board, not the FDC, which lives
at `0x8C`/`0x8D`/`0x90` (§2.1 above). So the plain board's FDC and M2200's RAM disk occupy
disjoint address ranges; there's no real hardware conflict between them, only a naming ambiguity
in how the port range was described before this pass.

**Open items:**
- Backing-store size/geometry (how many tracks/sectors, what each sector's byte size is) not yet
  sourced.
- Persistence-on-reset semantics: is RAM disk contents backed by the same battery-backed memory
  as the RTC (non-volatile, needs `Reset()` preservation like §3.1) or by ordinary volatile RAM
  (gets the standard non-zero garbage-fill treatment on cold boot, same as main RAM)? Not
  established — don't assume either way without checking the manual.
- Whether any DOS-level directory/allocation format is layered on top by software (same category
  of question `docs/JWSDOS-format.md` answers for real floppies) is unknown.

### 3.3 Serial — Z80 SIO, `0x84`/`0x85` (RS232), `0x86`/`0x87` (RS422)

A real **Z80 SIO** with two channels — channel A wired to an RS232 line driver, channel B to
RS422. This is a materially different chip from the SLOT2 Serial card's plain UART (reference
doc §5c: SLOT2 Serial = data `0x40` / control `0x41` / handshake `0x61` / DIP switches `0x62`) —
**do not share a device class between the two**, even though both are conceptually "a serial
port." The SIO's programming model (per-channel write-register selects, closer in spirit to how
the CTC's control-word scheme works than to a plain UART's single control byte) is more involved
than the SLOT2 card's UART and needs its own control-word decode, separate from that card.

Because it's an SIO — the same chip family as the CTC already in this project — it plausibly
wants **IM2 daisy-chain participation** the way CTC channels do (reference doc §5d/§5e), rather
than being purely polled like a simple UART might be. Whether it actually does, and where it
would sit in daisy-chain priority relative to the CTC's four channels, is unconfirmed.

**Open items:**
- Exact SIO control-word / write-register-select sequence the driving software uses (mirrors the
  CTC's "exact control words confirmed from disassembly" treatment in reference doc §5d — no
  equivalent disassembly-sourced sequence exists yet for this chip).
- Interrupt wiring: does either channel raise INT, and through what path (SLOT2's comms hook is
  CTC ch2, §5d — M2200 is a different slot, so this is not automatically the same hook)?
- Host-side transport target for each channel — same open design question already flagged for the
  SLOT2 Serial card: is "the other end of the cable" a pass-through to a real host serial device,
  a TCP socket, or a file/pipe sink? Needs an answer per channel (RS232 vs RS422 may reasonably
  want different answers, e.g. RS232 as a host pass-through and RS422 as a file sink, or vice
  versa) — not yet decided.

### 3.4 Centronics — `0x98` data, `0x99` status, `0x9A` strobe-on, `0x9B` strobe-off

A parallel printer interface. The strobe ports are **access-triggered, not data-dependent** — a
read or a write to `0x9A`/`0x9B` has the same effect regardless of which direction or what byte
value is involved; the port address alone is the signal. Data (`0x98`) and status (`0x99`) behave
as ordinary registers.

The natural host-side sink is "bytes become a file," mirroring the cassette's existing "Save as
`.cas`" pattern (reference doc §3a/§7 — an always-available host action, no protocol modeling
needed) — no printer emulation required, just capture the byte stream.

**Open item:** whether this shares its register shape with a possible future SLOT2 Centronics
card is unconfirmed — SLOT2 Centronics port numbers haven't been sourced yet (reference doc §5c
flags this as the one open cross-card question). If the shapes do turn out to match, the
Centronics device class built here is a candidate for reuse on a SLOT2 card later; if not, treat
them as unrelated devices that happen to speak the same real-world protocol.

---

## 4. Design consequences

- **Board = manifest of features, not a monolithic class.** M2200 is best modeled as an assembly
  of six independent feature devices (two reused, four new), each with its own `IIoSlot`/`IDevice`
  registration, rather than one bespoke `M2200Board` class that reimplements FDC and bank-switch
  logic a second time. The plain floppy+RAM board and M2200 both instantiate the same FDC and
  bank-switch feature classes; only the board-assembly step differs (which features it lists).
- **Port-base parameterization already earns its keep once, and should stay a habit regardless.**
  FDC and bank-switch happen to agree on addresses between the plain board and M2200 — that's a
  fortunate coincidence for these two specific boards, not a guarantee a future board will honor.
  Keep port bases as constructor parameters on every feature device even where today's boards
  agree, rather than hardcoding the addresses that happen to work now.
- **No address collisions** between M2200's four new features, M2200's two shared features, or
  SLOT2's address space (`0x40`/`0x41`/`0x61`/`0x62`) — everything above sits in the `0x84`-`0x9D`
  band cleanly.

---

## 5. Open items (collected)

- FDC register-level equivalence with the plain board — owner-flagged "AFAIK," not yet confirmed
  against the M2200 manual's own FDC section.
- RAM bank-switch bit-width — 1-bit original-board semantics vs. wider homebrew decode — not
  established for M2200 specifically.
- RTC index → field mapping (which `0x9C` index addresses which time/date/alarm/status field).
- RTC alarm/interrupt behavior.
- RAM disk geometry (track/sector counts, sector size).
- RAM disk persistence semantics on cold reset (non-volatile like the RTC, or ordinary volatile
  RAM).
- RAM disk DOS-level directory/allocation format, if any.
- Z80 SIO control-word/init sequence for both channels.
- Z80 SIO interrupt wiring and IM2 daisy-chain priority, if any.
- Serial host-side transport target, per channel (RS232 and RS422 may differ).
- Centronics host-side sink target (file capture assumed by analogy with cassette save-as, not
  yet decided as such).
- Whether M2200's Centronics register shape matches a future SLOT2 Centronics card.

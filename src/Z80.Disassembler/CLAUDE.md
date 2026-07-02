# CLAUDE_disassembler.md — Z80 Disassembler (for the debugger)

> **Placement:** this file belongs at `src/Z80.Disassembler/CLAUDE.md` (rename from
> `CLAUDE_disassembler.md`). Claude Code loads it together with the root `CLAUDE.md`. Global
> conventions (target framework, determinism, thread boundary, commits, dependency direction)
> live in the **root** `CLAUDE.md` and are NOT repeated here — this file is disassembler-specific
> only. **Dependency rule (from root §2/§3):** this project depends on `Z80.Core` and is
> **READ-ONLY** toward it — consume `Z80.Core.Z80Tables` and the public API; never modify the
> core, never redefine the operand orderings with local literals.

Spec for building a Z80 disassembler that powers the debugger's "disassembly around PC"
view. Read this fully before starting. It targets the **completed cycle-stepped core** in
`src/Z80.Core` (tagged **v1.0.0** — all opcodes incl. CB/ED/DD/FD/DDCB/FDCB, interrupts,
ZEXALL/ZEXDOC passing; see `CLAUDE.md`). The disassembler is a **separate, read-only
component**; it does not modify the core's behaviour.

**Core status (from CLAUDE.md §11): the full instruction set is DONE.** So the disassembler
must cover the ENTIRE Z80 set from the outset — all base opcodes plus all six prefix pages
(CB, ED, DD, FD, DDCB, FDCB) including undocumented forms (IXH/IXL/IYH/IYL, SLL, DDCB/FDCB
dual-writes). There is no "unprefixed first, prefixes later" staging on the core side; the
only staging is the disassembler's own build order (§12).

---

## 1. Goal & the one hard requirement

Produce, for any address, the mnemonic + operands + instruction length, so the debugger
can render a live disassembly around PC.

**Hard requirement: the disassembler must never disagree with the core about what an opcode
is or how long it is.** A debugger that shows a different instruction than the core executes
is worse than none. This is guaranteed structurally (§3) and enforced by a conformance test
(§6) — not by hoping.

---

## 2. Read the core FIRST — do not assume encodings

The core is **algorithmically decoded** using the canonical Z80 x/y/z/p/q bit-field scheme
(Christian Dinu, "Decoding Z80 Opcodes"), NOT a 256-entry table. Confirm this by reading the
dispatch files in `src/Z80.Core` — the quadrant dispatch/execute methods (e.g.
`DispatchQuadrant11`/`ExecuteQuadrant11`, plus the Quadrant00/01/10 equivalents, and the
z-sub-dispatchers like `DispatchZ1Quadrant11`, `DispatchZ3`, `DispatchZ5`). You will see the
pattern:
- `x = _opcode >> 6`         (quadrant, 0–3)
- `z = _opcode & 7`
- `y = (_opcode >> 3) & 7`
- `p = (_opcode >> 4) & 3`
- `q = (_opcode >> 3) & 1`

Prefix state is tracked in a `_prefix` field (`Prefix.None`/DD/FD/CB/ED); DDCB/FDCB is the
CB page under an active DD/FD. Mirror the core's prefix handling in `RunFetch`/`Dispatch`.

**Before writing any disassembly, extract the core's ACTUAL operand mappings — do not invent
orderings.** Find in the core how these are defined and reuse the SAME orderings:
- condition codes — the `TestCondition((_opcode>>3)&7)` mapping (`cc[]`)
- 16-bit register pairs — the SP-variant mapping (`rp[]`)
- 16-bit register pairs, AF variant — `Get16Af`/`Set16Af` (`rp2[]`, used by PUSH/POP)
- 8-bit registers — the `r[]` mapping (B,C,D,E,H,L,(HL),A order), incl. the DD/FD IXH/IXL
  substitution rules (§9) and the mixed-operand quirk
- ALU ops — the `ApplyAluOp((_opcode>>3)&7, …)` mapping (`alu[]`)
- rotate/shift ops — the CB `rot[]` mapping (RLC..SRL incl. SLL)
- interrupt modes — the ED IM mapping (`im[]`)
- block instructions — the ED block-op mapping (`Z80.OverrideRepeatYX` /
  `OverrideIoRepeatFlags` name the block ops handled)

If these live as scattered literals in the core, **refactor them into one shared static
class `Z80Tables` in `Z80.Core`** (see §4) and make the core reference it, so the core and
disassembler read identical orderings. If the maintainer prefers no core change, instead
mirror them in the disassembler but add an assertion test that the mirrored orderings match
the core's observed behaviour. Prefer the shared class.

---

## 3. Architecture: a PARALLEL x/y/z/p/q decoder (mirror the core's shape)

Do NOT build a flat 256-case mnemonic table, and do NOT scrape mnemonics out of the core's
execute methods — **there are no mnemonic strings in the core** (they exist only in
comments). Instead write the disassembler as a **second visitor over the same bit-field
decomposition**, structurally mirroring the core's dispatch:
- One method per quadrant (x=0,1,2,3), each branching on z, then q, then p/y — the same
  nesting you see in the core's `Dispatch*`/`Execute*` methods.
- Where the core calls `ExecuteRetCc`, the disassembler emits `"RET " + cc[y]`. Same
  decision tree, different output (text instead of behaviour).
- Because both walk identical bit fields using identical operand tables, they cannot
  disagree about instruction identity by construction.

This keeps the disassembler compact (a handful of methods, not 256 cases) and structurally
locked to the core.

### Output type
```csharp
public readonly record struct DisasmLine(
    ushort Address,      // where this instruction starts
    int Length,          // total bytes consumed (opcode + prefixes + operands)
    byte[] Bytes,        // the raw bytes, for the "raw bytes" column
    string Text);        // rendered mnemonic + operands, symbols applied
```

### Decode entry point
```csharp
// Pure function over a memory-read delegate. NEVER touches the live core.
public DisasmLine Decode(ushort addr, Func<ushort,byte> readByte);
```
`readByte` reads from a machine-state SNAPSHOT (see §5), not the running machine.

---

## 4. Shared tables (`Z80Tables`)

Create `Z80.Core/Z80Tables.cs` (or confirm an equivalent exists) holding the operand
orderings as the single source of truth, e.g.:
```csharp
public static class Z80Tables
{
    public static readonly string[] R    = {"B","C","D","E","H","L","(HL)","A"};
    public static readonly string[] Rp   = {"BC","DE","HL","SP"};
    public static readonly string[] Rp2  = {"BC","DE","HL","AF"};
    public static readonly string[] Cc   = {"NZ","Z","NC","C","PO","PE","P","M"};
    public static readonly string[] Alu  = {"ADD A,","ADC A,","SUB ","SBC A,","AND ","XOR ","OR ","CP "};
    public static readonly string[] Rot  = {"RLC","RRC","RL","RR","SLA","SRA","SLL","SRL"};
    public static readonly string[] Im   = {"0","0/1","1","2","0","0/1","1","2"};
    // block-ops table for ED (LDI/LDD/LDIR/... , CPI/..., INI/..., OUTI/...)
}
```
**IMPORTANT:** the string labels above are illustrative for the DISASSEMBLER. The *orderings*
(index → meaning) MUST match the core's actual `cc`/`rp`/`rp2`/`alu`/`r` decode. Verify each
ordering against the core before committing — if the core's `Set16Af` maps p=3→AF, then
`Rp2[3]` must be "AF", etc. The core uses these tables for BEHAVIOUR; the disassembler uses
them for TEXT. One ordering, two consumers.

---

## 5. Threading / data source (observer side only)

Per `CLAUDE.md` §3 and the reference doc §3a, the debugger is an **observer**:
- The disassembler reads from a **memory snapshot + PC** captured from machine state at a
  break/step. It NEVER reads the live running core or mutates anything.
- `Decode` is a pure function of (address, readByte). No side effects, no core coupling.
- This makes it trivially testable and race-free.

---

## 6. Backward context around PC (the alignment problem)

Z80 instructions are 1–4 bytes (more with prefixes), so there is **no fixed alignment** and
you cannot decode backwards unambiguously.
- **Forward from PC is exact** — decode from PC onward for as many lines as the pane shows.
  The line AT PC and everything after MUST be correct.
- **Backward context is a heuristic ("sync to PC"):** to show ~N lines before PC, start from
  an anchor `PC - K` (try K ≈ 8–16 bytes), decode forward accumulating instruction
  boundaries; if a boundary lands exactly on PC, accept that anchor's alignment; if decoding
  overshoots PC (a boundary crosses it), increment the anchor and retry. Pick the alignment
  whose boundaries hit PC.
- A mis-decoded leading line or two after a data block is tolerable; the PC line is not.
- **Better anchors when available:** for addresses in the monitor ROM, the project's own
  documented ROM disassembly provides ground-truth instruction boundaries and named entry
  points — use those as reliable anchors instead of the heuristic. Fall back to the heuristic
  for RAM/cartridge code. (Make the anchor source pluggable.)

---

## 7. Symbol resolution

Annotate addresses and ports with names, from a pluggable symbol source:
- Port names from the confirmed I/O map: `OUT (0x10),A` → comment/label `CPOUT`;
  `IN A,(0x20)` → `CPRIN`; keyboard rows 0x00–0x09; bank register 0x94; etc.
- ROM routine names / labels from the project's monitor disassembly for `CALL`/`JP` targets
  (e.g. `CALL 0x0038`).
- Symbol lookup is a `Func<ushort,string?>` (address→name) + a port-name map, both injected.
  With no symbol source, render bare hex.
- Keep raw hex always visible (§8) even when a symbol is shown.

---

## 8. Rendering

- Format: `AAAA: BB BB BB   MNEMONIC operands   ; symbol`
  e.g. `1234: 21 00 60   LD HL,6000h` and `0038: CD 38 00   CALL 0038h ; (ROM entry)`.
- Show the **raw bytes** column (encoding matters for a hardware debugger; makes mis-decodes
  visible; shows which prefix/undocumented form was used).
- Hex style: consistent (e.g. `nnnnh` or `0xnnnn` — pick one, match the project's convention).
- Signed displacements for `(IX+d)`/`(IY+d)` and `JR`/`DJNZ` targets: render the effective
  target address for jumps (`JR 1234h`) AND/OR the displacement; show `(IX+05h)` / `(IX-03h)`.
- Undocumented forms (once milestone 6 lands): render IXH/IXL/IYH/IYL, SLL, the DDCB/FDCB
  `(IX+d)` bit ops, etc. — the algorithmic decode produces these naturally.

---

## 9. Prefixes — ALL implemented in the core; disassemble the confirmed forms

The core implements **every prefix page** (CB, ED, DD, FD, DDCB, FDCB) and its undocumented
behaviour is confirmed against SingleStepTests (CLAUDE.md §6). The disassembler must match
what the core actually decodes. Specific forms the core confirms — reproduce them exactly:
- **DD/FD:** swap HL→IX/IY and (HL)→(IX+d)/(IY+d); expose **IXH/IXL/IYH/IYL** for pure
  register ops. Render these undocumented halves. DD/FD chain — only the last takes effect;
  each is its own M1 (so each is one prefix byte of length).
  - **Mixed-operand quirk:** in `LD r,r'` where one side is (IX+d) and the other is H/L, the
    H/L side stays the REAL H/L, not IXH/IXL. Substitution to IXH/IXL applies only when
    *both* operands are pure registers. The disassembler must render `LD (IX+d),H` (real H),
    not `LD (IX+d),IXH`.
  - **Quadrant-11 collision:** 0xE1/E3/E5/E9/F9 under DD/FD (POP/EX(SP)/PUSH/JP(IX)/LD SP,IX)
    must be recognised BEFORE the z-field switch, same as the core's dispatch. Mirror that
    ordering so you don't mis-decode PUSH IX as a DEC-IXH-style form.
- **DDCB/FDCB:** the **displacement byte comes BEFORE the final CB-table opcode byte**
  (order: DD/FD, CB, d, op). Render `RLC (IX+d)`, `BIT n,(IX+d)`, `RES/SET n,(IX+d)`.
  - **Undocumented dual-write:** for the CB op's z≠6, the result is ALSO stored into
    register r[z] — render the documented form (e.g. `RLC (IX+d),B` style, or annotate the
    dual target per the project's chosen convention; pick one and be consistent).
- **ED:** block ops (LDI/LDD/LDIR/LDDR/CPI…/INI…/OUTI…), IM 0/1/2, `RETI`(ED 4D)/`RETN`(ED
  45), `LD A,I`/`LD A,R`, `RRD`/`RLD`, `NEG`, `IN r,(C)`/`OUT (C),r`, 16-bit `ADC/SBC HL`.
  Undefined ED opcodes (no SingleStepTests file exists for them) behave as NOP-likes in the
  core — render them as `NOP?`/`DB EDh,nnh` per convention; don't invent mnemonics.
- **CB:** rot/shift (RLC/RRC/RL/RR/SLA/SRA/**SLL**/SRL) + BIT/RES/SET n,r. SLL is the
  undocumented one — include it.
- **Length note:** in DDCB/FDCB the displacement and CB-op bytes are fetched via plain MR
  (R does not increment for them), but for the DISASSEMBLER only the **byte count** matters
  (4 bytes: prefix, CB, d, op). The conformance test (§10) still just compares total length.

---

## 10. Conformance test (REQUIRED — this is the anti-drift guarantee)

Because the core never DECLARES instruction length (it just advances PC through `MemRead`
calls during execution), prove agreement by counting:
- For every opcode (base + all six prefix pages — the core runs them all):
  1. Ask the disassembler for its declared `Length`.
  2. Run the SAME opcode through the CORE and count how many **PC-advancing fetches** it
     performs. The core's `Step()`/bus contract (CLAUDE.md §4) exposes every fetch on the
     pins; count reads where the fetched address == the pre-step PC and PC advanced. The
     existing SingleStepTests harness in `tests/Z80.Tests` already services the bus per
     opcode — reuse its infrastructure rather than re-instrumenting.
  3. **Assert disassembler `Length` == core's consumed byte count.**
- Cross-check against the **SingleStepTests** JSON already vendored in the repo: each case's
  `cycles` array shows the bytes fetched at PC; the disassembler's length must match. The
  core already passes all 1604 opcode files, so this data is a trustworthy oracle.
- Any mismatch fails CI. This is the single source of truth for "the debugger doesn't lie" —
  it verifies BEHAVIOUR agreement, stronger than comparing declared tables.
- Note the DDCB/FDCB length subtlety (§9): those are 4 bytes even though the displacement and
  CB-op are fetched via MR (not M1). Count total PC-advancing byte fetches, not M1s.

Also add straightforward **golden-output unit tests**: a handful of hand-verified
`(bytes → text)` cases per quadrant and per prefix, including signed displacements, symbol
substitution, and at least a few undocumented forms.

---

## 11. Project layout

```
src/
  Z80.Core/
    Z80Tables.cs           # shared operand orderings (core + disassembler)
  Z80.Disassembler/        # new, references Z80.Core (read-only; no core mutation)
    Disassembler.cs        # Decode(addr, readByte) -> DisasmLine; parallel x/y/z/p/q
    SyncToPc.cs            # backward-context alignment heuristic + anchor plug-in
    Symbols.cs            # symbol/port-name injection
tests/
  Z80.Disassembler.Tests/
    ConformanceTests.cs    # length == core-consumed-bytes over all opcodes (+ SingleStepTests)
    GoldenTests.cs         # hand-verified bytes->text cases
```

---

## 12. Build order

The core is complete (v1.0.0), so the disassembler covers everything — the staging below is
purely the disassembler's own convenience, and the conformance test (§10) can run against the
full opcode set from the start.

1. Read the core's Quadrant00/01/10/11 dispatch + prefix handling in `RunFetch`/`Dispatch`;
   extract/confirm the operand orderings into `Z80Tables` (refactor core to use it).
2. Implement the unprefixed disassembler (quadrants 0–3) as a parallel decoder + `DisasmLine`.
3. Add CB, then ED, then DD/FD, then DDCB/FDCB — including the undocumented forms the core
   confirms (§9). All prefix pages are in scope now (the core already runs them).
4. Golden tests per page (§10) as each is added.
5. Conformance test: disassembler length vs core PC-fetch count, over the **full** opcode set
   (base + all prefixes) + SingleStepTests cross-check. Green before proceeding.
6. `SyncToPc` backward-context + ROM-anchor plug-in.
7. Symbol/port-name injection + rendering polish (raw bytes, signed displacements, IXH/IXL).

---

## 13. When to ask the human

Ask before: changing the core's public behaviour; introducing a mnemonic ordering that
can't be verified against the core; or relaxing the §10 conformance assertion. For rendering
style choices within these rules, proceed.

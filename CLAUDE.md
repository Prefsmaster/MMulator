# CLAUDE.md — MMulator solution (root)

Solution-wide contract. This file is loaded in **every** session regardless of which project
you're working on, so it is deliberately short: a project map plus the handful of genuinely
global rules. **Each project has its own `CLAUDE.md` with its specific contract — read the
one for the project you are touching.** Claude Code loads CLAUDE.md hierarchically (root +
the nearest project file); the closer file wins on any conflict.

---

## 1. What this solution is

A cycle-exact emulator of the Philips **P2000T** (and later **P2000M**) 8-bit microcomputer,
built in .NET as a layered set of projects. Fidelity goal: cycle-exact, bus-accurate,
including the T's CPU-vs-video contention glitches. The full machine architecture and
hardware reference lives at **`docs/P2000T-reference.md`** (repo root) — the confirmed memory
map, I/O ports, slot pin-outs, interrupt architecture, contention model, and device details.
It is **read on demand** (not auto-loaded); open it for machine-layer hardware work. It is the
clean **source of truth** — do not edit it from within a project; findings during
implementation go in the relevant project's CLAUDE.md and the human syncs them back.

---

## 2. Project map & dependency direction

```
src/
  Z80.Core/           # cycle-stepped, T-state-accurate, bus-exposing Z80 CPU. DONE (v1.0.0).
  Z80.Disassembler/   # read-only Z80 disassembler for the debugger. Depends on Z80.Core.
  (future) P2000.Machine/  # bus, memory page table, devices, slots, interrupts. Depends on Z80.Core.
  (future) P2000.UI/       # Avalonia UI: display, config, keyboard, debugger. Depends on Machine (+ Disassembler).
tests/
  Z80.Tests/          # SingleStepTests runner + unit tests for the core.
  Z80.Conformance/    # CP/M ZEXDOC/ZEXALL runner.
  (future) *.Tests per project.
```

**Dependency rules (do not violate the direction):**
- `Z80.Core` depends on **nothing** (zero NuGet, no machine specifics — see its own CLAUDE.md).
- `Z80.Disassembler` depends on `Z80.Core` and is **READ-ONLY** toward it: it consumes the
  core's decode tables and public API; it never modifies core behaviour.
- The future machine layer depends on `Z80.Core`; the UI depends on the machine (and the
  disassembler for the debugger view). Nothing lower ever depends on something higher.
- **No P2000T/machine-specific constants in `Z80.Core` or `Z80.Disassembler`.** Both are
  pure-Z80; machine specifics belong in `P2000.Machine`/`P2000.UI`.

---

## 3. Shared source of truth: `Z80Tables`

The Z80 operand orderings (condition codes `cc`, register pairs `rp`/`rp2`, 8-bit regs `r`,
ALU ops `alu`, rotates `rot`, interrupt modes `im`, block ops) live **once**, in
`Z80.Core` (`Z80Tables`). Both the core (for behaviour) and the disassembler (for text)
consume the SAME orderings. The disassembler must never redefine or mirror them with its own
literals — index→meaning drift between core and disassembler is exactly the bug that makes a
debugger lie. See each project's CLAUDE.md for its side of this rule.

---

## 4. Global conventions (apply to every project)

- **Target framework:** current .NET LTS, cross-platform (Windows/macOS/Linux). Default
  `net8.0`; bump only if asked. `Z80.Core` has **zero NuGet dependencies**; other projects
  keep dependencies minimal and justified.
- **No platform APIs / no `unsafe`** unless justified and benchmarked.
- **Determinism:** the core and machine emulation are deterministic — no threads, clocks,
  `DateTime`, or randomness in emulation code. The host/UI owns wall-clock timing.
- **Thread boundary (spans projects):** emulation runs on its own thread and produces
  completed frames/state snapshots; UI and debugger are **observers** that read snapshots and
  never mutate the live core/machine. Keep this boundary intact in every project that touches
  running state.
- **Correctness & debuggability first, performance second** (target is 2.5 MHz — trivially in
  reach). Optimise only with a benchmark, never on spec.
- **Conventional commits.** Every commit keeps CI green.
- Public APIs small and stable; everything else `internal`. XML-doc public surfaces.

---

## 5. Build & test (whole solution)

```bash
dotnet build MMulator.sln -c Release
dotnet test                                   # all *.Tests projects
dotnet run --project tests/Z80.Conformance -- zexdoc   # core CP/M exercisers
dotnet run --project tests/Z80.Conformance -- zexall
```
(Per-project test/run commands live in each project's CLAUDE.md; keep both current.)

---

## 6. Where to read next

- Working on the **CPU** → `src/Z80.Core/CLAUDE.md` (the core's full contract; v1.0.0, whole
  instruction set incl. all prefixes + interrupts, ZEXALL/ZEXDOC passing).
- Working on the **disassembler** → `src/Z80.Disassembler/CLAUDE.md`.
- Working on the **machine/UI** (future) → that project's CLAUDE.md + the P2000T reference doc.

---

## 7. When to ask the human

Ask before: changing a project's public API in a way that ripples to dependents; adding a
dependency to `Z80.Core`; violating the dependency direction in §2; or relaxing any project's
validation gates. Ordinary in-project choices: proceed and keep CI green.

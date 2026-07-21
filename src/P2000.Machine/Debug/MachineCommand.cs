namespace P2000.Machine.Debug;

/// <summary>
/// Discriminated union of commands that can be queued via <see cref="Machine.Enqueue"/>
/// and applied at the next instruction boundary (project CLAUDE.md §3b.3, milestone 15).
/// Every mutation from a client is a queued command applied at a safe boundary —
/// symmetric with frame-boundary keyboard input (root CLAUDE.md thread-boundary rule).
/// </summary>
public abstract record MachineCommand;

// ── Execution control ──────────────────────────────────────────────────────────────────

/// <summary>Resume execution (clears IsPaused). No-op if already running.</summary>
public sealed record RunCommand : MachineCommand;

/// <summary>Pause at the next instruction boundary.</summary>
public sealed record PauseCommand : MachineCommand;

/// <summary>Warm reset: CPU and all devices reset; RAM contents are preserved.</summary>
public sealed record WarmResetCommand : MachineCommand;

/// <summary>Cold reset: CPU and all devices reset plus all RAM refilled with deterministic
/// non-zero "garbage" (project CLAUDE.md §17, 2026-07-21/22 finding — real volatile RAM
/// doesn't power up to zero). <paramref name="RamSeed"/> is optional: <c>null</c> falls back to
/// <see cref="MachineConfig.RamSeed"/>, then to <see cref="Memory.PageTable.DefaultRamSeed"/> —
/// the same null-means-default convention as <see cref="MachineConfig.MonitorRomPath"/>.
/// <see cref="P2000.UI"/> generates a genuinely random seed per real user-triggered cold reset
/// and passes it here so each one gets fresh unpredictable content, while a bare
/// <c>new ColdResetCommand()</c> (what any test gets) stays fully deterministic.</summary>
public sealed record ColdResetCommand(ulong? RamSeed = null) : MachineCommand;

// ── Single-instruction stepping ────────────────────────────────────────────────────────

/// <summary>Execute exactly one instruction then pause at the next boundary.</summary>
public sealed record SingleStepCommand : MachineCommand;

/// <summary>
/// Step over a subroutine call: if the instruction at PC is a CALL / RST / DJNZ /
/// block instruction (LDIR etc.), sets a temporary exec breakpoint at PC + instruction
/// length and runs; otherwise single-steps. Pauses when the landing breakpoint is hit.
/// </summary>
public sealed record StepOverCommand : MachineCommand;

/// <summary>
/// Step out of the current call frame: reads the return address from the top of stack
/// ([SP] | [SP+1] &lt;&lt; 8) and sets a temporary exec breakpoint there, then runs.
/// </summary>
public sealed record StepOutCommand : MachineCommand;

// ── Run to position ────────────────────────────────────────────────────────────────────

/// <summary>
/// Run until the first instruction boundary at or past T-state
/// <see cref="FieldTState"/> within the current 50 Hz field.
/// </summary>
public sealed record RunToCycleCommand(int FieldTState) : MachineCommand;

/// <summary>
/// Run until the first instruction boundary at or past the start of raster line
/// <see cref="Line"/> (0-based; 160 T-states/line). Equivalent to
/// <see cref="RunToCycleCommand"/> with <c>FieldTState = Line × 160</c>.
/// </summary>
public sealed record RunToScanlineCommand(int Line) : MachineCommand;

// ── Register / memory mutation ─────────────────────────────────────────────────────────

/// <summary>Set the CPU program counter. Applied at the next instruction boundary.</summary>
public sealed record SetPcCommand(ushort Address) : MachineCommand;

/// <summary>
/// Write a single byte to memory. Applied at the next instruction boundary.
/// Raises <see cref="Machine.NonReplayableAction"/> — a mid-run memory mutation
/// breaks cycle-exact replay for this session (project CLAUDE.md §3b.3).
/// </summary>
public sealed record MemoryWriteCommand(ushort Address, byte Value) : MachineCommand;

/// <summary>
/// Write a byte array to memory starting at <see cref="StartAddress"/>.
/// Raises <see cref="Machine.NonReplayableAction"/> (same caveat as
/// <see cref="MemoryWriteCommand"/>).
/// </summary>
public sealed record LoadImageCommand(ushort StartAddress, byte[] Data) : MachineCommand;

// ── Breakpoint CRUD ────────────────────────────────────────────────────────────────────

/// <summary>Add an execute breakpoint at <see cref="Address"/>.</summary>
public sealed record AddExecBreakpointCommand(ushort Address) : MachineCommand;

/// <summary>Add a memory-read watchpoint at <see cref="Address"/>.</summary>
public sealed record AddMemReadBreakpointCommand(ushort Address) : MachineCommand;

/// <summary>Add a memory-write watchpoint at <see cref="Address"/>.</summary>
public sealed record AddMemWriteBreakpointCommand(ushort Address) : MachineCommand;

/// <summary>Add a memory-access watchpoint (read or write) at <see cref="Address"/>.</summary>
public sealed record AddMemAccessBreakpointCommand(ushort Address) : MachineCommand;

/// <summary>Add an I/O-read watchpoint on <see cref="Port"/>.</summary>
public sealed record AddIoReadBreakpointCommand(byte Port) : MachineCommand;

/// <summary>Add an I/O-write watchpoint on <see cref="Port"/>.</summary>
public sealed record AddIoWriteBreakpointCommand(byte Port) : MachineCommand;

/// <summary>Remove the breakpoint with <see cref="Id"/>.</summary>
public sealed record RemoveBreakpointCommand(int Id) : MachineCommand;

/// <summary>Remove all breakpoints.</summary>
public sealed record ClearBreakpointsCommand : MachineCommand;

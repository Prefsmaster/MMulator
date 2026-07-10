namespace P2000.Machine.Debug;

/// <summary>
/// Classifies what kind of access a breakpoint watches for (project CLAUDE.md §3b.2).
/// </summary>
public enum BreakpointKind
{
    /// <summary>Fires before the instruction at the watched address executes (PC match at an
    /// instruction boundary).</summary>
    Exec,

    /// <summary>Fires when the CPU reads from the watched memory address (includes
    /// instruction-fetch reads — any MREQ+RD).</summary>
    MemRead,

    /// <summary>Fires when the CPU writes to the watched memory address.</summary>
    MemWrite,

    /// <summary>Fires on either a read or a write to the watched memory address.</summary>
    MemAccess,

    /// <summary>Fires when the CPU reads from the watched I/O port (IORQ+RD, excluding
    /// interrupt-acknowledge cycles).</summary>
    IoRead,

    /// <summary>Fires when the CPU writes to the watched I/O port.</summary>
    IoWrite,

    /// <summary>Fires when execution pauses for a non-breakpoint reason: single-step
    /// completed, step-over of a non-call completed, run-to-cycle/scanline reached, or
    /// a <see cref="P2000.Machine.Debug.PauseCommand"/> was processed. <c>BreakpointId</c>
    /// is -1 (no real breakpoint). Used by observer UIs to take a snapshot after stepping.</summary>
    Step,
}

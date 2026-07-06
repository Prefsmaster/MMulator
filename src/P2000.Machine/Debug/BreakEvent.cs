namespace P2000.Machine.Debug;

/// <summary>
/// Describes a breakpoint hit delivered via <see cref="Machine.BreakHit"/>
/// (project CLAUDE.md §3b.2). Always raised at an instruction boundary.
/// </summary>
public readonly struct BreakEvent
{
    internal BreakEvent(BreakpointKind kind, ushort address, int breakpointId)
    {
        Kind        = kind;
        Address     = address;
        BreakpointId = breakpointId;
    }

    /// <summary>What kind of access triggered the breakpoint.</summary>
    public BreakpointKind Kind { get; }

    /// <summary>
    /// The address associated with the hit:
    /// <list type="bullet">
    ///   <item><description><see cref="BreakpointKind.Exec"/>: the PC of the instruction that
    ///   is about to execute.</description></item>
    ///   <item><description><see cref="BreakpointKind.MemRead"/>, <see cref="BreakpointKind.MemWrite"/>,
    ///   <see cref="BreakpointKind.MemAccess"/>: the memory address that was accessed.</description></item>
    ///   <item><description><see cref="BreakpointKind.IoRead"/>, <see cref="BreakpointKind.IoWrite"/>:
    ///   the I/O port address (low byte).</description></item>
    /// </list>
    /// </summary>
    public ushort Address { get; }

    /// <summary>The ID returned by the <see cref="BreakpointStore.Add*"/> call that registered
    /// the breakpoint that fired.</summary>
    public int BreakpointId { get; }
}

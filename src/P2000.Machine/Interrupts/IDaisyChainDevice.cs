namespace P2000.Machine.Interrupts;

/// <summary>
/// One source on the IM2 vectored-interrupt daisy chain (project CLAUDE.md §8 / §13
/// milestone 17; reference doc §5e). Real Z80-family peripherals chain IEI→IEO in silicon;
/// this interface models one link. Granularity varies by device: each Z80-CTC channel is its
/// own link (they chain internally inside the real chip, ch0 highest — reference doc §5d),
/// while a future SLOT2 card registers as a single link behind the CTC's four channels.
/// </summary>
public interface IDaisyChainDevice
{
    /// <summary>True when this source has an unacknowledged interrupt request.</summary>
    bool IntPending { get; }

    /// <summary>True from <see cref="Acknowledge"/> until <see cref="ClearInService"/> (RETI).
    /// A higher-priority in-service device blocks (IEO low) every lower-priority device in the
    /// chain from being acknowledged at all, mirroring the real IEI/IEO cascade.</summary>
    bool InService { get; }

    /// <summary>Called when this device wins int-ack arbitration: clears its pending request,
    /// sets <see cref="InService"/>, and returns its vector byte for the data bus.</summary>
    byte Acknowledge();

    /// <summary>Called when this is the highest-priority in-service device in the chain during
    /// a RETI (ED 4D) snoop — clears <see cref="InService"/> so lower-priority sources unblock.</summary>
    void ClearInService();
}

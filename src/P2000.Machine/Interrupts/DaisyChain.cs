namespace P2000.Machine.Interrupts;

/// <summary>
/// IM2 vectored-interrupt daisy chain (project CLAUDE.md §8 / §13 milestone 17; reference doc
/// §5e). An OPTIONAL, config-driven module: <see cref="InterruptAggregator"/> only holds one
/// when a peripheral needing IM2 is mounted (currently: the floppy+RAM board's
/// <see cref="Devices.Ctc.Z80Ctc"/>) — a bare T never instantiates it. Devices register in
/// priority order (highest first) at machine-assembly time; registration order IS priority,
/// exactly like the real IEI/IEO chain (reference doc §5d: CTC ch0 &gt; … &gt; ch3, with any
/// SLOT2 card registering behind them).
/// </summary>
public sealed class DaisyChain
{
    private readonly List<IDaisyChainDevice> _devices = new();

    /// <summary>Adds <paramref name="device"/> at the end of the chain — lowest priority so
    /// far. Call in priority order (highest first) at machine-assembly time.</summary>
    public void Register(IDaisyChainDevice device) => _devices.Add(device);

    /// <summary>The highest-priority device with a pending request, or <c>null</c> if none —
    /// honouring the IEI/IEO block: a higher-priority in-service device masks every
    /// lower-priority device's request from being seen at all (mirrors real silicon; a real
    /// RETI must clear it before a lower-priority source can be acknowledged).</summary>
    private IDaisyChainDevice? HighestPriorityPending()
    {
        foreach (var d in _devices)
        {
            if (d.InService) return null; // this device's IEO is low: blocks itself + everything lower
            if (d.IntPending) return d;
        }
        return null;
    }

    /// <summary>True when some device's request can currently reach the CPU's INT line.</summary>
    public bool IntPending => HighestPriorityPending() != null;

    /// <summary>Acknowledges the highest-priority pending device, returning its vector byte.
    /// Callers should check <see cref="IntPending"/> first; if nothing is actually pending this
    /// returns 0xFF (passive pull-up), matching <see cref="InterruptAggregator"/>'s IM1 default.</summary>
    public byte Acknowledge() => HighestPriorityPending()?.Acknowledge() ?? 0xFF;

    /// <summary>Snoops a RETI (ED 4D) opcode fetch: clears the highest-priority in-service
    /// device, mirroring how a real RETI propagates down the chain until it reaches the device
    /// that is actually in service.</summary>
    public void OnReti()
    {
        foreach (var d in _devices)
        {
            if (d.InService)
            {
                d.ClearInService();
                return;
            }
        }
    }
}

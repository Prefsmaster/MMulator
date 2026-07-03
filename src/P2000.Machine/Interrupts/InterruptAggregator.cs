using P2000.Machine.State;

namespace P2000.Machine.Interrupts;

/// <summary>
/// Wired-OR INT aggregator (project CLAUDE.md §8). In the T-first build the only source is
/// the video 50 Hz VBLANK (<see cref="Video.FieldComplete"/>); future sources (CTC
/// channel-3 when a CTC is present, IM2 daisy-chain peripherals) register by calling
/// <see cref="RaiseInt"/> from their own <c>FieldComplete</c>-equivalent handlers.
///
/// The INT line is kept asserted in the machine's pin word (<see cref="IntPending"/> = true)
/// until the CPU acknowledges it via the int-ack M-cycle (M1+IORQ, detected by
/// <see cref="Machine.Tick"/>). For IM1 the CPU ignores the bus byte entirely; the aggregator
/// returns 0xFF (passive pull-up) from <see cref="Acknowledge"/> anyway so that a future IM2
/// source can replace it with a real vector byte without changing the machine's ack path.
/// </summary>
public sealed class InterruptAggregator : IDevice
{
    private bool _intPending;

    /// <summary>True when at least one INT source is pending. The machine asserts
    /// <see cref="Z80.Core.Pins.INT"/> into its pin word while this is true.</summary>
    public bool IntPending => _intPending;

    /// <summary>Raises a maskable interrupt request. Any registered source calls this (e.g.
    /// <c>Video.FieldComplete += Interrupts.RaiseInt</c>). Multiple concurrent sources are
    /// fine — the wired-OR means any one keeps the line asserted.</summary>
    public void RaiseInt() => _intPending = true;

    /// <summary>Called by the machine when it detects an int-ack bus cycle (M1+IORQ asserted
    /// together without RD/WR). Clears the pending latch and returns the vector byte to drive
    /// onto the data bus. For IM1 the CPU ignores the byte; for a future IM2 daisy-chain
    /// source this is where the winning peripheral's vector would be returned instead.</summary>
    /// <returns>Vector byte for the data bus (0xFF = passive pull-up, correct for IM1).</returns>
    public byte Acknowledge()
    {
        _intPending = false;
        return 0xFF;
    }

    public void Reset() => _intPending = false;

    public void SaveState(IStateWriter writer) => writer.WriteBool(_intPending);

    public void LoadState(IStateReader reader) => _intPending = reader.ReadBool();
}

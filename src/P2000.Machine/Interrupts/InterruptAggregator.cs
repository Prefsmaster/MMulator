using P2000.Machine.State;

namespace P2000.Machine.Interrupts;

/// <summary>
/// Wired-OR INT and NMI aggregator (project CLAUDE.md §8). In the T-first build the only
/// INT source is the video 50 Hz VBLANK (<see cref="Video.FieldComplete"/>); future sources
/// (CTC channel-3, IM2 daisy-chain peripherals) register by calling <see cref="RaiseInt"/>.
///
/// INT: the line is kept asserted in the machine's pin word (<see cref="IntPending"/> = true)
/// until the CPU acknowledges it via the int-ack M-cycle (M1+IORQ, detected by
/// <see cref="Machine.Tick"/>). For IM1 the CPU ignores the bus byte; the aggregator returns
/// 0xFF (passive pull-up) from <see cref="Acknowledge"/> so that a future IM2 source can
/// supply a real vector without changing the ack path.
///
/// NMI: edge-triggered. <see cref="RaiseNmi"/> sets the latch; <see cref="Machine.Tick"/>
/// asserts <see cref="Z80.Core.Pins.NMI"/> for exactly one T-state then calls
/// <see cref="ClearNmi"/> so the Z80 core sees a single 0→1 rising edge per request.
/// Current NMI sources for the P2000T: front-panel soft-reset button and SLOT1 pin 1A
/// (reference doc §5c/§5e). Neither fires in the T-first build; the seam is ready.
/// </summary>
public sealed class InterruptAggregator : IDevice
{
    private bool _intPending;
    private bool _nmiPending;

    // ---- INT (maskable) -----------------------------------------------------------

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

    // ---- NMI (non-maskable) -------------------------------------------------------

    /// <summary>True when an NMI request is waiting to be driven onto the pin.
    /// <see cref="Machine.Tick"/> asserts <see cref="Z80.Core.Pins.NMI"/> for one T-state
    /// then calls <see cref="ClearNmi"/> (edge-triggered — one rising edge per request).
    /// </summary>
    public bool NmiPending => _nmiPending;

    /// <summary>Requests a non-maskable interrupt. Callers: front-panel soft-reset button,
    /// SLOT1 pin 1A (reference doc §5c/§5e). No NMI source fires in the T-first build.</summary>
    public void RaiseNmi() => _nmiPending = true;

    /// <summary>Called by the machine immediately after asserting the NMI pin high for one
    /// T-state, so the latch is consumed and subsequent ticks return the pin to low.</summary>
    public void ClearNmi() => _nmiPending = false;

    // ---- IDevice ------------------------------------------------------------------

    public void Reset()
    {
        _intPending = false;
        _nmiPending = false;
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteBool(_intPending);
        writer.WriteBool(_nmiPending);
    }

    public void LoadState(IStateReader reader)
    {
        _intPending = reader.ReadBool();
        _nmiPending = reader.ReadBool();
    }
}

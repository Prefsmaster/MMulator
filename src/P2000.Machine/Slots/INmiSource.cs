namespace P2000.Machine.Slots;

/// <summary>
/// Optional capability for devices that can pull the Z80's NMI line low (reference doc §5c,
/// §5e). Current NMI sources for the P2000T: the front-panel soft-reset button and SLOT1
/// pin 1A (a cartridge that drives NMI). SLOT2 has no NMI.
///
/// NMI is edge-triggered in the Z80 core: the core latches a pending NMI on a 0→1 rising
/// edge of the NMI pin. <see cref="Machine"/> drives the pin high for exactly one T-state
/// and calls <see cref="ClearNmi"/> immediately after, giving the core exactly one rising
/// edge per request.
/// </summary>
public interface INmiSource
{
    bool NmiPending { get; }

    /// <summary>Called by the machine immediately after asserting the NMI pin high, so the
    /// request is consumed and subsequent ticks return the pin to low.</summary>
    void ClearNmi();
}

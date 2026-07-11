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
///
/// IM2: <see cref="AttachDaisyChain"/> lets the owning board (currently only the floppy+RAM
/// board's <see cref="P2000.Machine.Devices.Ctc.Z80Ctc"/>, project CLAUDE.md §13 milestone 17) plug a
/// <see cref="DaisyChain"/> in at machine-assembly time; a bare T never attaches one. When
/// attached, its pending sources OR into <see cref="IntPending"/> and win vector arbitration
/// in <see cref="Acknowledge"/> ahead of the plain IM1 pull-up.
///
/// Lock (internal-slot pin 35, reference doc §5e): "disables interrupt generation if extension
/// board is used" — asserted whenever the floppy+RAM board occupies the internal slot, it
/// suppresses the onboard video 50 Hz INT so only the board's CTC drives INT (via IM2). Gates
/// only the maskable video INT for now, not NMI — the front-panel reset button and SLOT1 NMI
/// have no logical tie to the internal-slot board (documented default; reference doc §5e flags
/// this as the one still-open item, resolvable during implementation — see project CLAUDE.md
/// §17 finding).
/// </summary>
public sealed class InterruptAggregator : IDevice
{
    private bool _intPending;
    private bool _nmiPending;
    private bool _lockAsserted;
    private DaisyChain? _daisyChain;

    // ---- INT (maskable) -----------------------------------------------------------

    /// <summary>True when at least one INT source can currently reach the CPU's INT pin: the
    /// video 50 Hz VBLANK (unless <see cref="LockAsserted"/> suppresses it) OR'd with the
    /// attached <see cref="DaisyChain"/>'s pending sources, if any.</summary>
    public bool IntPending => (_intPending && !_lockAsserted) || (_daisyChain?.IntPending ?? false);

    /// <summary>Raises a maskable interrupt request. Any registered source calls this (e.g.
    /// <c>Video.FieldComplete += Interrupts.RaiseInt</c>). Multiple concurrent sources are
    /// fine — the wired-OR means any one keeps the line asserted. Subject to
    /// <see cref="LockAsserted"/> gating at the <see cref="IntPending"/>/<see cref="Acknowledge"/>
    /// level (the video device itself stays unaware of Lock).</summary>
    public void RaiseInt() => _intPending = true;

    /// <summary>True when the internal-slot floppy+RAM board's Lock line is asserted
    /// (reference doc §5e) — suppresses the onboard video 50 Hz INT so only the board's CTC
    /// drives INT. Fixed at machine-assembly time (topology, not runtime state); unaffected by
    /// <see cref="Reset"/> (locked decision §2.3).</summary>
    public bool LockAsserted => _lockAsserted;

    /// <summary>Sets <see cref="LockAsserted"/>. Called once by <see cref="Machine"/> at
    /// assembly time from <see cref="MachineConfig.Board"/>; never mutated afterwards.</summary>
    public void SetLock(bool asserted) => _lockAsserted = asserted;

    /// <summary>Attaches the machine's IM2 <see cref="DaisyChain"/> (called once by
    /// <see cref="Machine"/> at assembly time when a peripheral needing IM2 is mounted). A bare
    /// T never calls this, matching reference doc §5e ("a bare T with no such card never
    /// instantiates it").</summary>
    public void AttachDaisyChain(DaisyChain chain) => _daisyChain = chain;

    /// <summary>Called by the machine when it detects an int-ack bus cycle (M1+IORQ asserted
    /// together without RD/WR). If the attached <see cref="DaisyChain"/> has a pending source,
    /// it wins arbitration and supplies its real IM2 vector; otherwise the video latch is
    /// cleared and the passive IM1 pull-up byte is returned.</summary>
    /// <returns>Vector byte for the data bus (0xFF = passive pull-up, correct for IM1).</returns>
    public byte Acknowledge()
    {
        if (_daisyChain is { IntPending: true })
            return _daisyChain.Acknowledge();

        _intPending = false;
        return 0xFF;
    }

    /// <summary>Called by the machine on a RETI (ED 4D) opcode-fetch snoop (reference doc §5e:
    /// "the emulated daisy chain must snoop it"). No-op when no daisy chain is attached.</summary>
    public void OnReti() => _daisyChain?.OnReti();

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
        writer.WriteBool(_lockAsserted);
    }

    public void LoadState(IStateReader reader)
    {
        _intPending = reader.ReadBool();
        _nmiPending = reader.ReadBool();
        _lockAsserted = reader.ReadBool();
    }
}

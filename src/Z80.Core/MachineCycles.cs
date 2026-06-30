using PinBits = Z80.Core.Pins;

namespace Z80.Core;

/// <summary>
/// Reusable, resumable machine-cycle templates (CLAUDE.md §5), each driven by the
/// shared <c>_tstate</c> counter and confirmed against SingleStepTests/z80's real
/// JSON cycle data (see CLAUDE.md §5's notes). Every method advances exactly one
/// T-state per call and returns <c>true</c> once the cycle has fully completed
/// (resetting <c>_tstate</c> to 0 so the next cycle starts clean).
/// </summary>
public sealed partial class Z80
{
    /// <summary>MR: T1 addr only. T2 MREQ+RD pulse (data still null this tick;
    /// stretched by Tw while WAIT is asserted). T3 data now present, no signals.</summary>
    private bool MemRead(ref ulong pins, ushort addr, out byte data)
    {
        switch (_tstate)
        {
            case 0:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ);
                _tstate = 1;
                data = 0;
                return false;

            case 1:
                pins = PinBits.SetAddress(pins, addr);
                pins |= PinBits.MREQ | PinBits.RD;
                data = 0;
                if ((pins & PinBits.WAIT) != 0)
                    return false; // Tw: repeat T2 until WAIT is released.
                _tstate = 2;
                return false;

            case 2:
                data = PinBits.GetData(pins);
                pins = PinBits.SetAddress(pins, addr);
                pins = PinBits.SetData(pins, data);
                pins &= ~(PinBits.MREQ | PinBits.RD);
                _tstate = 0;
                return true;

            default:
                throw new InvalidOperationException($"Unreachable MR T-state {_tstate}.");
        }
    }

    /// <summary>MW: T1 addr only. T2 MREQ+WR pulse with the data already driven
    /// this same tick (stretched by Tw). T3 nothing.</summary>
    private bool MemWrite(ref ulong pins, ushort addr, byte data)
    {
        switch (_tstate)
        {
            case 0:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ);
                _tstate = 1;
                return false;

            case 1:
                pins = PinBits.SetAddress(pins, addr);
                pins = PinBits.SetData(pins, data);
                pins |= PinBits.MREQ | PinBits.WR;
                if ((pins & PinBits.WAIT) != 0)
                    return false; // Tw: repeat T2 until WAIT is released.
                _tstate = 2;
                return false;

            case 2:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.MREQ | PinBits.WR);
                _tstate = 0;
                return true;

            default:
                throw new InvalidOperationException($"Unreachable MW T-state {_tstate}.");
        }
    }

    /// <summary>IOR: T1 addr only. T2 addr held, no signals (automatic wait
    /// T-state). T3 IORQ+RD pulse, data still null (stretched by Tw). T4 data
    /// now present, no signals.</summary>
    private bool IoRead(ref ulong pins, ushort addr, out byte data)
    {
        switch (_tstate)
        {
            case 0:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ);
                _tstate = 1;
                data = 0;
                return false;

            case 1:
                pins = PinBits.SetAddress(pins, addr);
                data = 0;
                _tstate = 2;
                return false;

            case 2:
                pins = PinBits.SetAddress(pins, addr);
                pins |= PinBits.IORQ | PinBits.RD;
                data = 0;
                if ((pins & PinBits.WAIT) != 0)
                    return false; // Tw: repeat T3 until WAIT is released.
                _tstate = 3;
                return false;

            case 3:
                data = PinBits.GetData(pins);
                pins = PinBits.SetAddress(pins, addr);
                pins = PinBits.SetData(pins, data);
                pins &= ~(PinBits.IORQ | PinBits.RD);
                _tstate = 0;
                return true;

            default:
                throw new InvalidOperationException($"Unreachable IOR T-state {_tstate}.");
        }
    }

    /// <summary>IOW: T1 addr only. T2 addr held, no signals (automatic wait
    /// T-state). T3 IORQ+WR pulse with the data already driven this same tick
    /// (stretched by Tw). T4 nothing.</summary>
    private bool IoWrite(ref ulong pins, ushort addr, byte data)
    {
        switch (_tstate)
        {
            case 0:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ);
                _tstate = 1;
                return false;

            case 1:
                pins = PinBits.SetAddress(pins, addr);
                _tstate = 2;
                return false;

            case 2:
                pins = PinBits.SetAddress(pins, addr);
                pins = PinBits.SetData(pins, data);
                pins |= PinBits.IORQ | PinBits.WR;
                if ((pins & PinBits.WAIT) != 0)
                    return false; // Tw: repeat T3 until WAIT is released.
                _tstate = 3;
                return false;

            case 3:
                pins = PinBits.SetAddress(pins, addr);
                pins &= ~(PinBits.IORQ | PinBits.WR);
                _tstate = 0;
                return true;

            default:
                throw new InvalidOperationException($"Unreachable IOW T-state {_tstate}.");
        }
    }

    /// <summary>Internal: burns <paramref name="count"/> T-states with no bus
    /// activity (the address bus is left as the host last set it, matching the
    /// "last value held" convention SingleStepTests uses for disconnected ticks).</summary>
    private bool InternalCycle(ulong pins, int count)
    {
        _tstate++;
        if (_tstate < count)
            return false;
        _tstate = 0;
        return true;
    }
}

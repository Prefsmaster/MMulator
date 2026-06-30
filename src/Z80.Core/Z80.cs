using PinBits = Z80.Core.Pins;

namespace Z80.Core;

/// <summary>
/// Cycle-stepped Z80 CPU core. <see cref="Step"/> advances the CPU by exactly one
/// T-state and returns the new pin mask; the host inspects the mask, services any
/// active memory/IO request, and calls <see cref="Step"/> again. All continuation
/// state lives in fields so execution can resume mid-instruction across calls.
/// </summary>
public sealed class Z80
{
    private Registers _reg;
    public ref Registers Reg => ref _reg;

    /// <summary>Mirrors the most recently returned pin mask. The authoritative
    /// protocol is the <see cref="Step"/> parameter/return value; this field is a
    /// convenience for harness/debug inspection between calls.</summary>
    public ulong Pins;

    // M1 fetch sub-state: which T-state (0-3) of the current opcode fetch we're on.
    private int _tstate;
    private ushort _fetchAddr;
    private ushort _refreshAddr;
    private byte _opcode;

    public Z80()
    {
        Reset();
    }

    /// <summary>Asserts RESET behaviour: PC=0, I=R=0, IFF1=IFF2=0, IM=0. SP and AF
    /// are left unaffected, per the Zilog spec.</summary>
    public void Reset()
    {
        _reg.PC = 0;
        _reg.I = 0;
        _reg.R = 0;
        _reg.IFF1 = false;
        _reg.IFF2 = false;
        _reg.IM = 0;
        _tstate = 0;
        Pins = 0;
    }

    public ulong Step(ulong pins)
    {
        pins = RunFetch(pins);
        Pins = pins;
        return pins;
    }

    /// <summary>
    /// The M1 (opcode fetch) machine-cycle template, 4 T-states:
    /// T1: address=PC, M1 asserted, PC incremented internally.
    /// T2: MREQ+RD pulse (one T-state, per the SingleStepTests "simplified memory
    ///     access" convention); stretched by Tw wait states while WAIT is asserted.
    /// T3: opcode latched from the data bus; address moves to the I:R refresh
    ///     address; R's low 7 bits increment; data bus echoes the latched opcode
    ///     (matches the bus-capacitance artifact the test data records).
    /// T4: refresh address held, no further bus activity.
    /// </summary>
    private ulong RunFetch(ulong pins)
    {
        switch (_tstate)
        {
            case 0: // T1
                _fetchAddr = _reg.PC;
                _reg.PC++;
                pins = PinBits.SetAddress(pins, _fetchAddr);
                pins |= PinBits.M1;
                pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                _tstate = 1;
                return pins;

            case 1: // T2 (+ Tw wait states)
                pins = PinBits.SetAddress(pins, _fetchAddr);
                pins |= PinBits.M1 | PinBits.MREQ | PinBits.RD;
                pins &= ~PinBits.RFSH;
                if ((pins & PinBits.WAIT) != 0)
                    return pins; // Tw: repeat T2 until WAIT is released.
                _tstate = 2;
                return pins;

            case 2: // T3
                _opcode = PinBits.GetData(pins);
                _refreshAddr = _reg.RefreshAddress;
                pins = PinBits.SetAddress(pins, _refreshAddr);
                pins = PinBits.SetData(pins, _opcode);
                _reg.BumpR();
                pins &= ~(PinBits.M1 | PinBits.MREQ | PinBits.RD);
                pins |= PinBits.RFSH;
                _tstate = 3;
                return pins;

            case 3: // T4
                pins = PinBits.SetAddress(pins, _refreshAddr);
                pins &= ~(PinBits.M1 | PinBits.MREQ | PinBits.RD);
                pins |= PinBits.RFSH;
                _tstate = 0;
                return Dispatch(pins);

            default:
                throw new InvalidOperationException($"Unreachable fetch T-state {_tstate}.");
        }
    }

    /// <summary>Executes the just-fetched opcode. Only NOP (0x00) is implemented
    /// so far; every other opcode is added milestone by milestone per CLAUDE.md §11.
    /// Q, the EI-delay latch, and the LD A,I/R marker all decay to their default
    /// (cleared) state after any instruction that doesn't explicitly set them.</summary>
    private ulong Dispatch(ulong pins)
    {
        _reg.Q = 0;
        _reg.EiPending = false;
        _reg.LastWasLdAIR = false;

        switch (_opcode)
        {
            case 0x00: // NOP
                return pins;

            default:
                throw new NotImplementedException($"Opcode 0x{_opcode:X2} is not implemented yet.");
        }
    }
}

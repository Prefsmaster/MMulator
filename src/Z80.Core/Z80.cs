using PinBits = Z80.Core.Pins;

namespace Z80.Core;

/// <summary>
/// Cycle-stepped Z80 CPU core. <see cref="Step"/> advances the CPU by exactly one
/// T-state and returns the new pin mask; the host inspects the mask, services any
/// active memory/IO request, and calls <see cref="Step"/> again. All continuation
/// state lives in fields so execution can resume mid-instruction across calls.
/// </summary>
public sealed partial class Z80
{
    private Registers _reg;
    public ref Registers Reg => ref _reg;

    /// <summary>Mirrors the most recently returned pin mask. The authoritative
    /// protocol is the <see cref="Step"/> parameter/return value; this field is a
    /// convenience for harness/debug inspection between calls.</summary>
    public ulong Pins;

    private enum Phase { Fetch, Execute }
    private Phase _phase;

    // T-state sub-counter, shared by the M1 template and every machine-cycle
    // helper in MachineCycles.cs. Always 0 at the start of a new cycle.
    private int _tstate;

    // Which step of the current opcode's post-fetch execution we're on (0-based).
    // Only meaningful while _phase == Execute; opcodes needing zero extra machine
    // cycles never enter Execute at all.
    private int _step;

    private ushort _fetchAddr;
    private ushort _refreshAddr;
    private byte _opcode;

    // General-purpose scratch latches for multi-byte operand/address fetches
    // within a single instruction (e.g. the low/high bytes of an LD (nn),A
    // address). Deliberately separate from Registers.WZ: WZ is only ever
    // assigned the specific value SingleStepTests confirms for a given opcode,
    // never used as incidental scratch space.
    private byte _latchLo;
    private byte _latchHi;
    private sbyte _displacement;

    /// <summary>Holds a resolved 16-bit (nn) address across an instruction's
    /// remaining cycles once _latchLo/_latchHi have been re-used for a second
    /// pair of bytes (e.g. LD HL,(nn)'s value bytes, fetched after nn itself).</summary>
    private ushort _addrLatch;

    /// <summary>Q as it was at the start of the current instruction, captured
    /// before Dispatch() resets _reg.Q to its default-cleared state. Only SCF
    /// and CCF read this (Patrik Rak's Q-dependent Y/X flag quirk).</summary>
    private byte _incomingQ;

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
        _phase = Phase.Fetch;
        _tstate = 0;
        _step = 0;
        Pins = 0;
    }

    public ulong Step(ulong pins)
    {
        pins = _phase == Phase.Fetch ? RunFetch(pins) : RunExecute(pins);
        Pins = pins;
        return pins;
    }

    /// <summary>
    /// The M1 (opcode fetch) machine-cycle template, 4 T-states:
    /// T1: address=PC, M1 asserted, PC incremented internally.
    /// T2: MREQ+RD pulse (one T-state, per the SingleStepTests "simplified memory
    ///     access" convention); stretched by Tw wait states while WAIT is asserted.
    /// T3: opcode latched from the data bus; address moves to the I:R refresh
    ///     address (R *before* increment); R's low 7 bits increment; data bus
    ///     echoes the latched opcode (matches the bus-capacitance artifact the
    ///     test data records).
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
                _step = 0;
                return Dispatch(pins);

            default:
                throw new InvalidOperationException($"Unreachable fetch T-state {_tstate}.");
        }
    }

    /// <summary>Finishes the current instruction: returns to the Fetch phase so
    /// the next Step() call begins a fresh M1.</summary>
    private ulong FinishInstruction(ulong pins)
    {
        _phase = Phase.Fetch;
        _tstate = 0;
        _step = 0;
        return pins;
    }

    /// <summary>Begins the Execute phase for opcodes that need at least one more
    /// machine cycle beyond the M1 fetch. The next Step() call runs T1 of that
    /// cycle via <see cref="RunExecute"/>.</summary>
    private ulong EnterExecute(ulong pins)
    {
        _phase = Phase.Execute;
        _tstate = 0;
        _step = 0;
        return pins;
    }
}

using PinBits = Z80.Core.Pins;

namespace Z80.Core;

/// <summary>
/// Interrupt handling: NMI and maskable INT (IM0 / IM1 / IM2).
///
/// Machine-cycle timing (Z80 CPU Technical Manual + MAME / floooh z80.h):
///
/// NMI (11 T-states):
///   M1ₐₗₜ (5T): T1 M1 no-MREQ addr=PC; T2 M1 held; T3 RFSH R++;
///               T4 RFSH; T5 internal (SP--).
///   MW PCH (3T): write PC-high to (SP), SP--.
///   MW PCL (3T): write PC-low to (SP).  PC ← 0x0066.
///   IFF2 ← IFF1, IFF1 ← 0 (at T1).
///
/// INT acknowledge M-cycle (6T, shared by all IM variants):
///   T1 M1 no-MREQ addr=PC; IFF1=IFF2=0.
///   T2 M1+IORQ auto-wait-1.
///   T3 M1+IORQ auto-wait-2 (WAIT-stretchable).
///   T4 M1+IORQ data sampled into _latchLo.
///   T5 RFSH R++.  T6 RFSH done → DispatchIntMode.
///
/// IM0 (variable):
///   After ack T6: ack byte dispatched as opcode (no second M1 fetch).
///   A RST ack byte: +7T (1T internal + 3T MW + 3T MW) → total 13T.
/// IM1 (13T = 6T ack + 1T internal + 3T MW PCH + 3T MW PCL):
///   After ack T6: one internal T (SP--), then push PC, jump to 0x0038.
/// IM2 (19T = 6T ack + 1T internal + 3T MW PCH + 3T MW PCL + 3T MR lo + 3T MR hi):
///   After ack T6: one internal T (SP--), push PC, read ISR address, jump.
///
/// The "internal T-state" after the ack is a genuine separate Step() call;
/// DispatchIntMode only sets up mode-specific state synchronously inside T6,
/// it does NOT consume a T-state of its own.
/// </summary>
public sealed partial class Z80
{
    private ulong RunInterrupt(ulong pins) => _intType switch
    {
        IntType.Nmi => RunNmi(pins),
        IntType.Int => RunIntAck(pins),
        _           => throw new InvalidOperationException($"Unknown IntType {_intType}."),
    };

    // ---- NMI (11 T-states) -------------------------------------------------------

    /// <summary>
    /// NMI sequence.
    /// Step 0: 5-T aborted M1 (inner sub-switch on _tstate 0-4).
    /// Step 1: MW — push PCH, SP--.
    /// Step 2: MW — push PCL → PC ← 0x0066.
    /// </summary>
    private ulong RunNmi(ulong pins)
    {
        switch (_step)
        {
            case 0: // Aborted M1 (5 T-states, managed via _tstate)
            {
                switch (_tstate)
                {
                    case 0: // T1: M1, addr = PC (return address), no MREQ
                        // Save and clear IFFs at the earliest moment.
                        _reg.IFF2 = _reg.IFF1;
                        _reg.IFF1 = false;
                        pins = PinBits.SetAddress(pins, _reg.PC);
                        pins |= PinBits.M1;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ | PinBits.RFSH);
                        _tstate = 1;
                        return pins;

                    case 1: // T2: M1 held, no bus cycle
                        pins = PinBits.SetAddress(pins, _reg.PC);
                        pins |= PinBits.M1;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                        _tstate = 2;
                        return pins;

                    case 2: // T3: RFSH, R bumped
                        _refreshAddr = _reg.RefreshAddress;
                        _reg.BumpR();
                        pins = PinBits.SetAddress(pins, _refreshAddr);
                        pins |= PinBits.RFSH;
                        pins &= ~(PinBits.M1 | PinBits.MREQ | PinBits.RD);
                        _tstate = 3;
                        return pins;

                    case 3: // T4: RFSH held
                        pins = PinBits.SetAddress(pins, _refreshAddr);
                        pins |= PinBits.RFSH;
                        pins &= ~(PinBits.M1 | PinBits.MREQ | PinBits.RD);
                        _tstate = 4;
                        return pins;

                    case 4: // T5: internal — clear RFSH, decrement SP
                        pins &= ~(PinBits.RFSH | PinBits.M1);
                        _reg.SP--;
                        _tstate = 0;
                        _step = 1;
                        return pins;

                    default:
                        throw new InvalidOperationException($"RunNmi step 0 tstate {_tstate} unreachable.");
                }
            }

            case 1: // MW: push PCH
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                _reg.SP--;
                _step = 2;
                return pins;

            case 2: // MW: push PCL → jump to NMI vector
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                _reg.PC = 0x0066;
                _reg.WZ = 0x0066;
                return FinishInterrupt(pins);

            default:
                throw new InvalidOperationException($"RunNmi step {_step} unreachable.");
        }
    }

    // ---- INT acknowledge M-cycle + mode dispatch (IM0 / IM1 / IM2) ---------------

    /// <summary>
    /// INT sequence. Step 0 runs the 6-T int-ack M-cycle (inner sub-switch on
    /// _tstate 0-5). At T1 IFF1/IFF2 are cleared. At T6 completion _latchLo
    /// holds the ack byte and <see cref="DispatchIntMode"/> is called to set up
    /// mode-specific state synchronously (consuming no extra T-state).
    /// Steps 1+ execute the IM-specific push/read/jump.
    /// </summary>
    private ulong RunIntAck(ulong pins)
    {
        switch (_step)
        {
            case 0: // INT-ack M-cycle (6 T-states)
            {
                switch (_tstate)
                {
                    case 0: // T1: M1 no-MREQ; IFF1/IFF2 cleared (CPU has accepted the INT)
                        _reg.IFF1 = false;
                        _reg.IFF2 = false;
                        pins = PinBits.SetAddress(pins, _reg.PC);
                        pins |= PinBits.M1;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.WR | PinBits.IORQ | PinBits.RFSH | PinBits.HALT);
                        _tstate = 1;
                        return pins;

                    case 1: // T2: M1 + IORQ (auto wait 1)
                        pins |= PinBits.M1 | PinBits.IORQ;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                        _tstate = 2;
                        return pins;

                    case 2: // T3: M1 + IORQ (auto wait 2, WAIT-stretchable)
                        pins |= PinBits.M1 | PinBits.IORQ;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                        if ((pins & PinBits.WAIT) != 0) return pins; // Tw: hold T3
                        _tstate = 3;
                        return pins;

                    case 3: // T4: M1 + IORQ, sample ack byte from data bus
                        _latchLo = PinBits.GetData(pins);
                        pins |= PinBits.M1 | PinBits.IORQ;
                        pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                        _tstate = 4;
                        return pins;

                    case 4: // T5: RFSH, R bumped, M1+IORQ deasserted
                        _refreshAddr = _reg.RefreshAddress;
                        _reg.BumpR();
                        pins = PinBits.SetAddress(pins, _refreshAddr);
                        pins |= PinBits.RFSH;
                        pins &= ~(PinBits.M1 | PinBits.IORQ | PinBits.MREQ | PinBits.RD);
                        _tstate = 5;
                        return pins;

                    case 5: // T6: RFSH held → ack complete; call DispatchIntMode synchronously
                        pins = PinBits.SetAddress(pins, _refreshAddr);
                        pins &= ~(PinBits.M1 | PinBits.IORQ | PinBits.RFSH);
                        _tstate = 0;
                        // DispatchIntMode sets up mode state and advances _step; it does NOT
                        // consume an extra T-state — the next Step() is the internal T-state (IM1/2)
                        // or the first Execute cycle of the ack-byte instruction (IM0).
                        return DispatchIntMode(pins);

                    default:
                        throw new InvalidOperationException($"RunIntAck step 0 tstate {_tstate} unreachable.");
                }
            }

            // Steps 1+ are IM-specific (IM1 / IM2).  IM0 re-uses the normal Execute machinery
            // so RunIntAck's default branch is never reached for IM0.
            default:
                return ExecuteIntMode(pins);
        }
    }

    /// <summary>
    /// Called synchronously inside the T6 Step() call — no T-state is consumed here.
    /// Sets up mode-specific state and advances <c>_step</c> to 1 so the next
    /// Step() starts the internal T-state (IM1/IM2) or the first Execute cycle (IM0).
    /// </summary>
    private ulong DispatchIntMode(ulong pins)
    {
        switch (_reg.IM)
        {
            case 0:
                // IM0: treat the ack byte as a complete single-byte instruction.
                // Dispatch() sets _phase to Execute (or Fetch for zero-cycle ops),
                // so subsequent Step() calls run the instruction's Execute phase.
                // R was already bumped by the int-ack RFSH; no second M1 increment.
                _opcode = _latchLo;
                _prefix = Prefix.None;
                return Dispatch(pins); // phase transitions inside Dispatch

            case 1:
                // IM1: fixed vector 0x0038. Step 1 is a 1-T internal cycle (SP--).
                _step = 1;
                return pins;

            case 2:
                // IM2: vector address = (I << 8) | (ack & 0xFE). Step 1 is a 1-T internal (SP--).
                _addrLatch = (ushort)((_reg.I << 8) | (_latchLo & 0xFE));
                _step = 1;
                return pins;

            default:
                throw new InvalidOperationException($"DispatchIntMode: unknown IM {_reg.IM}.");
        }
    }

    /// <summary>
    /// IM1 / IM2 push+jump sequence, entered after the int-ack DispatchIntMode
    /// has set <c>_step = 1</c>.
    ///
    /// IM1 steps: 1=internal(SP--) 2=MW PCH  3=MW PCL → PC=0x0038.
    /// IM2 steps: 1=internal(SP--) 2=MW PCH  3=MW PCL  4=MR vecLo  5=MR vecHi → PC.
    /// </summary>
    private ulong ExecuteIntMode(ulong pins)
    {
        if (_reg.IM == 1)
        {
            switch (_step)
            {
                case 1: // 1T internal: SP--
                    if (!InternalCycle(pins, 1)) return pins;
                    _reg.SP--;
                    _step = 2;
                    return pins;

                case 2: // MW: push PCH
                    if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                    _reg.SP--;
                    _step = 3;
                    return pins;

                case 3: // MW: push PCL → jump to IM1 vector
                    if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                    _reg.PC = 0x0038;
                    _reg.WZ = 0x0038;
                    return FinishInterrupt(pins);

                default:
                    throw new InvalidOperationException($"ExecuteIntMode IM1 step {_step} unreachable.");
            }
        }
        else // IM2
        {
            switch (_step)
            {
                case 1: // 1T internal: SP--
                    if (!InternalCycle(pins, 1)) return pins;
                    _reg.SP--;
                    _step = 2;
                    return pins;

                case 2: // MW: push PCH
                    if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                    _reg.SP--;
                    _step = 3;
                    return pins;

                case 3: // MW: push PCL
                    if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                    _step = 4;
                    return pins;

                case 4: // MR: read low byte of ISR address from vector table
                    if (!MemRead(ref pins, _addrLatch, out _latchLo)) return pins;
                    _step = 5;
                    return pins;

                case 5: // MR: read high byte → set PC
                    if (!MemRead(ref pins, (ushort)(_addrLatch + 1), out _latchHi)) return pins;
                    _reg.PC = (ushort)((_latchHi << 8) | _latchLo);
                    _reg.WZ = _reg.PC;
                    return FinishInterrupt(pins);

                default:
                    throw new InvalidOperationException($"ExecuteIntMode IM2 step {_step} unreachable.");
            }
        }
    }

    // ---- Shared helpers ----------------------------------------------------------

    private ulong FinishInterrupt(ulong pins)
    {
        _phase = Phase.Fetch;
        _tstate = 0;
        _step = 0;
        _prefix = Prefix.None;
        return pins;
    }
}

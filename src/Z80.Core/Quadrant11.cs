namespace Z80.Core;

/// <summary>
/// Opcodes 0xC0-0xFF: conditional/unconditional control flow (RET/JP/CALL/RST),
/// stack ops (PUSH/POP), exchanges, I/O, DI/EI, and ALU A,n. Decoded by the same
/// z/y/p/q bit fields as Quadrant00. The four prefix bytes (CB/DD/ED/FD) throw
/// NotImplementedException here — they're milestone 6, not this one.
/// </summary>
public sealed partial class Z80
{
    private ulong DispatchQuadrant11(ulong pins)
    {
        switch (_opcode & 7)
        {
            case 0: return EnterExecute(pins); // RET cc
            case 1: return DispatchZ1Quadrant11(pins);
            case 2: return EnterExecute(pins); // JP cc,nn
            case 3: return DispatchZ3(pins);
            case 4: return EnterExecute(pins); // CALL cc,nn
            case 5: return DispatchZ5(pins);
            case 6: return EnterExecute(pins); // ALU A,n
            case 7: return EnterExecute(pins); // RST
            default: throw new InvalidOperationException();
        }
    }

    private ulong ExecuteQuadrant11(ulong pins)
    {
        switch (_opcode & 7)
        {
            case 0: return ExecuteRetCc(pins);
            case 1: return ExecuteZ1Quadrant11(pins);
            case 2: return ExecuteJpCc(pins);
            case 3: return ExecuteZ3(pins);
            case 4: return ExecuteCallCc(pins);
            case 5: return ExecuteZ5(pins);
            case 6: return ExecuteAluN(pins);
            case 7: return ExecuteRst(pins);
            default: throw new InvalidOperationException();
        }
    }

    // ---- z=0: RET cc ------------------------------------------------------------

    private ulong ExecuteRetCc(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                if (!TestCondition((_opcode >> 3) & 7)) return FinishInstruction(pins);
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.SP, out _latchLo)) return pins;
                _reg.SP++;
                _step = 2;
                return pins;

            case 2:
                if (!MemRead(ref pins, _reg.SP, out _latchHi)) return pins;
                _reg.SP++;
                _reg.PC = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=1: POP rp2 (q=0) / RET, EXX, JP (HL), LD SP,HL (q=1) ---------------

    private ulong DispatchZ1Quadrant11(ulong pins)
    {
        var q = (_opcode >> 3) & 1;
        if (q == 1)
        {
            switch ((_opcode >> 4) & 3)
            {
                case 1: // EXX
                    (_reg.BC, _reg.BC_) = (_reg.BC_, _reg.BC);
                    (_reg.DE, _reg.DE_) = (_reg.DE_, _reg.DE);
                    (_reg.HL, _reg.HL_) = (_reg.HL_, _reg.HL);
                    return FinishInstruction(pins);
                case 2: // JP (HL): copies HL into PC directly, no memory access at all.
                    _reg.PC = _reg.HL;
                    return FinishInstruction(pins);
            }
        }
        return EnterExecute(pins); // POP rp2, RET, LD SP,HL
    }

    private ulong ExecuteZ1Quadrant11(ulong pins)
    {
        var q = (_opcode >> 3) & 1;
        var p = (_opcode >> 4) & 3;
        if (q == 0) return ExecutePop(pins, p);
        return p switch
        {
            0 => ExecuteRet(pins),
            3 => ExecuteLdSpHl(pins),
            _ => throw new InvalidOperationException(),
        };
    }

    private ulong ExecuteRet(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.SP, out _latchLo)) return pins;
                _reg.SP++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.SP, out _latchHi)) return pins;
                _reg.SP++;
                _reg.PC = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteLdSpHl(ulong pins)
    {
        if (!InternalCycle(pins, 2)) return pins;
        _reg.SP = _reg.HL;
        return FinishInstruction(pins);
    }

    private ulong ExecutePop(ulong pins, int p)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.SP, out _latchLo)) return pins;
                _reg.SP++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.SP, out _latchHi)) return pins;
                _reg.SP++;
                Set16Af(p, (ushort)((_latchHi << 8) | _latchLo));
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=2: JP cc,nn ------------------------------------------------------------

    private ulong ExecuteJpCc(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins;
                _reg.PC++;
                var nn = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = nn;
                if (TestCondition((_opcode >> 3) & 7)) _reg.PC = nn;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=3: JP nn / CB / OUT(n),A / IN A,(n) / EX(SP),HL / EX DE,HL / DI / EI --

    private ulong DispatchZ3(ulong pins)
    {
        switch ((_opcode >> 3) & 7)
        {
            case 1:
                throw new NotImplementedException("CB-prefixed opcodes are not implemented yet (milestone 6).");
            case 5: // EX DE,HL
                (_reg.DE, _reg.HL) = (_reg.HL, _reg.DE);
                return FinishInstruction(pins);
            case 6: // DI
                _reg.IFF1 = false;
                _reg.IFF2 = false;
                return FinishInstruction(pins);
            case 7: // EI
                _reg.IFF1 = true;
                _reg.IFF2 = true;
                _reg.EiPending = true;
                return FinishInstruction(pins);
            default:
                return EnterExecute(pins); // 0=JP nn, 2=OUT(n),A, 3=IN A,(n), 4=EX (SP),HL
        }
    }

    private ulong ExecuteZ3(ulong pins) => ((_opcode >> 3) & 7) switch
    {
        0 => ExecuteJpNn(pins),
        2 => ExecuteOutNA(pins),
        3 => ExecuteInANPort(pins),
        4 => ExecuteExSpHl(pins),
        _ => throw new InvalidOperationException(),
    };

    private ulong ExecuteJpNn(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins;
                _reg.PC = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteOutNA(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                var port = (ushort)((_reg.A << 8) | _latchLo);
                if (!IoWrite(ref pins, port, _reg.A)) return pins;
                _reg.WZ = (ushort)((_reg.A << 8) | ((port + 1) & 0xFF));
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteInANPort(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                var port = (ushort)((_reg.A << 8) | _latchLo);
                if (!IoRead(ref pins, port, out var data)) return pins;
                _reg.A = data;
                _reg.WZ = (ushort)(port + 1);
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteExSpHl(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.SP, out _latchLo)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, (ushort)(_reg.SP + 1), out _latchHi)) return pins;
                _step = 2;
                return pins;

            case 2:
                if (!InternalCycle(pins, 1)) return pins;
                _step = 3;
                return pins;

            case 3:
                if (!MemWrite(ref pins, (ushort)(_reg.SP + 1), _reg.H)) return pins;
                _step = 4;
                return pins;

            case 4:
                if (!MemWrite(ref pins, _reg.SP, _reg.L)) return pins;
                _reg.HL = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _reg.HL;
                _step = 5;
                return pins;

            case 5:
                if (!InternalCycle(pins, 2)) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=4: CALL cc,nn ----------------------------------------------------------

    private ulong ExecuteCallCc(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins;
                _reg.PC++;
                _addrLatch = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _addrLatch;
                if (!TestCondition((_opcode >> 3) & 7)) return FinishInstruction(pins);
                _step = 2;
                return pins;

            case 2:
                if (!InternalCycle(pins, 1)) return pins;
                _reg.SP--;
                _step = 3;
                return pins;

            case 3:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                _reg.SP--;
                _step = 4;
                return pins;

            case 4:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                _reg.PC = _addrLatch;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=5: PUSH rp2 (q=0) / CALL nn (q=1,y=1) / DD,ED,FD prefixes ----------

    private ulong DispatchZ5(ulong pins)
    {
        if (((_opcode >> 3) & 1) == 0) return EnterExecute(pins); // PUSH rp2
        if (((_opcode >> 3) & 7) == 1) return EnterExecute(pins); // CALL nn
        throw new NotImplementedException("DD/ED/FD-prefixed opcodes are not implemented yet (milestone 6).");
    }

    private ulong ExecuteZ5(ulong pins)
    {
        if (((_opcode >> 3) & 1) == 0) return ExecutePush(pins, (_opcode >> 4) & 3);
        return ExecuteCallNn(pins);
    }

    private ulong ExecutePush(ulong pins, int p)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                _addrLatch = Get16Af(p);
                _reg.SP--;
                _step = 1;
                return pins;

            case 1:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_addrLatch >> 8))) return pins;
                _reg.SP--;
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_addrLatch & 0xFF))) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteCallNn(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins;
                _reg.PC++;
                _addrLatch = (ushort)((_latchHi << 8) | _latchLo);
                _reg.WZ = _addrLatch;
                _step = 2;
                return pins;

            case 2:
                if (!InternalCycle(pins, 1)) return pins;
                _reg.SP--;
                _step = 3;
                return pins;

            case 3:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                _reg.SP--;
                _step = 4;
                return pins;

            case 4:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                _reg.PC = _addrLatch;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=6: ALU A,n ---------------------------------------------------------------

    private ulong ExecuteAluN(ulong pins)
    {
        if (!MemRead(ref pins, _reg.PC, out var data)) return pins;
        _reg.PC++;
        ApplyAluOp((_opcode >> 3) & 7, data);
        return FinishInstruction(pins);
    }

    // ---- z=7: RST y*8 -----------------------------------------------------------------

    private ulong ExecuteRst(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                _reg.SP--;
                _step = 1;
                return pins;

            case 1:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC >> 8))) return pins;
                _reg.SP--;
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.SP, (byte)(_reg.PC & 0xFF))) return pins;
                _reg.PC = (ushort)(((_opcode >> 3) & 7) * 8);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }
}

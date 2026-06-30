namespace Z80.Core;

/// <summary>
/// Opcodes 0x00-0x3F: the most irregular quadrant (relative jumps, 16-bit loads,
/// INC/DEC, and misc accumulator ops), decoded by the standard z/y/p/q bit fields
/// (z = opcode&amp;7, y = (opcode&gt;&gt;3)&amp;7, p = (opcode&gt;&gt;4)&amp;3, q = (opcode&gt;&gt;3)&amp;1).
/// WZ updates here follow the documented MEMPTR rules and are each confirmed
/// against the specific opcode's SingleStepTests case data, per CLAUDE.md §6.
/// </summary>
public sealed partial class Z80
{
    private ulong DispatchQuadrant00(ulong pins)
    {
        switch (_opcode & 7)
        {
            case 0: return DispatchZ0(pins);
            case 1 or 2 or 3 or 6: return EnterExecute(pins);
            case 4: return DispatchIncR(pins);
            case 5: return DispatchDecR(pins);
            case 7: return DispatchAccMisc(pins);
            default: throw new InvalidOperationException();
        }
    }

    private ulong ExecuteQuadrant00(ulong pins)
    {
        switch (_opcode & 7)
        {
            case 0: return ExecuteZ0(pins);
            case 1: return ExecuteZ1(pins);
            case 2: return ExecuteZ2(pins);
            case 3: return ExecuteIncDecRp(pins);
            case 4: return ExecuteIncDecHl(pins, increment: true);
            case 5: return ExecuteIncDecHl(pins, increment: false);
            case 6: return ExecuteLdRN(pins);
            default: throw new NotImplementedException($"Opcode 0x{_opcode:X2} (z=7) has no Execute-phase handler.");
        }
    }

    // ---- z=0: NOP / EX AF,AF' / DJNZ / JR / JR cc -----------------------------

    private ulong DispatchZ0(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        switch (y)
        {
            case 0: return pins; // NOP
            case 1:
                (_reg.AF, _reg.AF_) = (_reg.AF_, _reg.AF);
                return FinishInstruction(pins);
            default:
                return EnterExecute(pins); // DJNZ(2), JR e(3), JR cc,e(4-7)
        }
    }

    private ulong ExecuteZ0(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        return y == 2 ? ExecuteDjnz(pins) : ExecuteJr(pins, y);
    }

    private ulong ExecuteDjnz(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                _reg.B--;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.PC, out var data)) return pins;
                _reg.PC++;
                _displacement = (sbyte)data;
                if (_reg.B == 0) return FinishInstruction(pins);
                _step = 2;
                return pins;

            case 2:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC = (ushort)(_reg.PC + _displacement);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>y==3 is the unconditional JR e; y==4..7 is JR cc,e with cc=y-4.</summary>
    private ulong ExecuteJr(ulong pins, int y)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out var data)) return pins;
                _reg.PC++;
                _displacement = (sbyte)data;
                var taken = y == 3 || TestCondition(y - 4);
                if (!taken) return FinishInstruction(pins);
                _step = 1;
                return pins;

            case 1:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC = (ushort)(_reg.PC + _displacement);
                _reg.WZ = _reg.PC;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=1: LD rp,nn (q=0) / ADD HL,rp (q=1) --------------------------------

    private ulong ExecuteZ1(ulong pins)
    {
        var q = (_opcode >> 3) & 1;
        var p = (_opcode >> 4) & 3;

        if (q == 1)
        {
            if (!InternalCycle(pins, 7)) return pins;
            var hl = _reg.HL;
            (_reg.HL, _reg.F) = Alu.Add16(hl, Get16(p), _reg.F);
            _reg.WZ = (ushort)(hl + 1);
            _reg.Q = _reg.F;
            return FinishInstruction(pins);
        }

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
                Set16(p, (ushort)((_latchHi << 8) | _latchLo));
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=2: LD (BC)/(DE)/(nn),A|HL and LD A|HL,(BC)/(DE)/(nn) --------------

    private ulong ExecuteZ2(ulong pins)
    {
        var q = (_opcode >> 3) & 1;
        var p = (_opcode >> 4) & 3;

        if (p < 2)
        {
            var addr = p == 0 ? _reg.BC : _reg.DE;
            if (q == 0)
            {
                if (!MemWrite(ref pins, addr, _reg.A)) return pins;
                _reg.WZ = (ushort)((_reg.A << 8) | ((addr + 1) & 0xFF));
            }
            else
            {
                if (!MemRead(ref pins, addr, out var data)) return pins;
                _reg.A = data;
                _reg.WZ = (ushort)(addr + 1);
            }
            return FinishInstruction(pins);
        }

        // p==2 (HL) or p==3 (A), addressed via an immediate (nn).
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
                _step = 2;
                return pins;

            default:
                return p == 3 ? ExecuteZ2NnA(pins, q) : ExecuteZ2NnHl(pins, q);
        }
    }

    private ulong ExecuteZ2NnA(ulong pins, int q)
    {
        if (q == 0)
        {
            if (!MemWrite(ref pins, _addrLatch, _reg.A)) return pins;
            _reg.WZ = (ushort)((_reg.A << 8) | ((_addrLatch + 1) & 0xFF));
        }
        else
        {
            if (!MemRead(ref pins, _addrLatch, out var data)) return pins;
            _reg.A = data;
            _reg.WZ = (ushort)(_addrLatch + 1);
        }
        return FinishInstruction(pins);
    }

    private ulong ExecuteZ2NnHl(ulong pins, int q)
    {
        if (q == 0)
        {
            if (_step == 2)
            {
                if (!MemWrite(ref pins, _addrLatch, _reg.L)) return pins;
                _step = 3;
                return pins;
            }
            if (!MemWrite(ref pins, (ushort)(_addrLatch + 1), _reg.H)) return pins;
            _reg.WZ = (ushort)(_addrLatch + 1);
            return FinishInstruction(pins);
        }
        else
        {
            if (_step == 2)
            {
                if (!MemRead(ref pins, _addrLatch, out _latchLo)) return pins;
                _step = 3;
                return pins;
            }
            if (!MemRead(ref pins, (ushort)(_addrLatch + 1), out var hi)) return pins;
            _reg.HL = (ushort)((hi << 8) | _latchLo);
            _reg.WZ = (ushort)(_addrLatch + 1);
            return FinishInstruction(pins);
        }
    }

    // ---- z=3: INC rp (q=0) / DEC rp (q=1) — flags unaffected -------------------

    private ulong ExecuteIncDecRp(ulong pins)
    {
        if (!InternalCycle(pins, 2)) return pins;
        var p = (_opcode >> 4) & 3;
        var q = (_opcode >> 3) & 1;
        var value = Get16(p);
        Set16(p, (ushort)(q == 0 ? value + 1 : value - 1));
        return FinishInstruction(pins);
    }

    // ---- z=4/z=5: INC r / DEC r (and the (HL) read-modify-write form) --------

    private ulong DispatchIncR(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        if (y == 6) return EnterExecute(pins);
        (var result, _reg.F) = Alu.Inc8(Get8(y), _reg.F);
        Set8(y, result);
        _reg.Q = _reg.F;
        return FinishInstruction(pins);
    }

    private ulong DispatchDecR(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        if (y == 6) return EnterExecute(pins);
        (var result, _reg.F) = Alu.Dec8(Get8(y), _reg.F);
        Set8(y, result);
        _reg.Q = _reg.F;
        return FinishInstruction(pins);
    }

    /// <summary>INC (HL) / DEC (HL): MR, one internal T-state to perform the ALU
    /// op, then MW — 11 T-states total including the M1 fetch.</summary>
    private ulong ExecuteIncDecHl(ulong pins, bool increment)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!InternalCycle(pins, 1)) return pins;
                (_latchLo, _reg.F) = increment ? Alu.Inc8(_latchLo, _reg.F) : Alu.Dec8(_latchLo, _reg.F);
                _reg.Q = _reg.F;
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.HL, _latchLo)) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=6: LD r,n (and LD (HL),n) ------------------------------------------

    private ulong ExecuteLdRN(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        if (y != 6)
        {
            if (!MemRead(ref pins, _reg.PC, out var data)) return pins;
            _reg.PC++;
            Set8(y, data);
            return FinishInstruction(pins);
        }

        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _step = 1;
                return pins;

            case 1:
                if (!MemWrite(ref pins, _reg.HL, _latchLo)) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=7: RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF — all 0 extra cycles ----------

    private ulong DispatchAccMisc(ulong pins)
    {
        var y = (_opcode >> 3) & 7;
        switch (y)
        {
            case 0: (_reg.A, _reg.F) = Alu.Rlca(_reg.A, _reg.F); break;
            case 1: (_reg.A, _reg.F) = Alu.Rrca(_reg.A, _reg.F); break;
            case 2: (_reg.A, _reg.F) = Alu.Rla(_reg.A, _reg.F); break;
            case 3: (_reg.A, _reg.F) = Alu.Rra(_reg.A, _reg.F); break;
            case 4: (_reg.A, _reg.F) = Alu.Daa(_reg.A, _reg.F); break;
            case 5: (_reg.A, _reg.F) = Alu.Cpl(_reg.A, _reg.F); break;
            case 6: _reg.F = Alu.Scf(_reg.A, _reg.F, _incomingQ); break;
            case 7: _reg.F = Alu.Ccf(_reg.A, _reg.F, _incomingQ); break;
        }
        _reg.Q = _reg.F;
        return FinishInstruction(pins);
    }
}

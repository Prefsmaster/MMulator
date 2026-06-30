using PinBits = Z80.Core.Pins;

namespace Z80.Core;

/// <summary>
/// The unprefixed (base-page) opcode dispatch table. <see cref="Dispatch"/> is
/// called once, synchronously, on the T-state right after the M1 fetch completes;
/// it either finishes the instruction immediately (opcodes needing zero further
/// bus cycles) or hands off to <see cref="RunExecute"/> for the following Step()
/// calls. Where the opcode space is regular (LD r,r' and ALU A,r), a single
/// bit-field-decoding handler covers the whole block instead of one case per
/// opcode, per CLAUDE.md §5's "compose from templates, don't hand-write every
/// opcode" rule.
/// </summary>
public sealed partial class Z80
{
    // ---- 3-bit register code: 0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A -------------
    // Index 6 always means "needs a bus cycle", never resolved here.

    private byte Get8(int index) => index switch
    {
        0 => _reg.B,
        1 => _reg.C,
        2 => _reg.D,
        3 => _reg.E,
        4 => _reg.H,
        5 => _reg.L,
        7 => _reg.A,
        _ => throw new InvalidOperationException($"Get8({index}): (HL) needs a bus cycle, not a register read."),
    };

    private void Set8(int index, byte value)
    {
        switch (index)
        {
            case 0: _reg.B = value; break;
            case 1: _reg.C = value; break;
            case 2: _reg.D = value; break;
            case 3: _reg.E = value; break;
            case 4: _reg.H = value; break;
            case 5: _reg.L = value; break;
            case 7: _reg.A = value; break;
            default: throw new InvalidOperationException($"Set8({index}): (HL) needs a bus cycle, not a register write.");
        }
    }

    // ---- 2-bit register-pair code: 0=BC 1=DE 2=HL 3=SP (or AF for push/pop) --

    private ushort Get16(int p) => p switch
    {
        0 => _reg.BC,
        1 => _reg.DE,
        2 => _reg.HL,
        3 => _reg.SP,
        _ => throw new InvalidOperationException($"Get16({p}) out of range."),
    };

    private void Set16(int p, ushort value)
    {
        switch (p)
        {
            case 0: _reg.BC = value; break;
            case 1: _reg.DE = value; break;
            case 2: _reg.HL = value; break;
            case 3: _reg.SP = value; break;
            default: throw new InvalidOperationException($"Set16({p}) out of range.");
        }
    }

    private ushort Get16Af(int p) => p == 3 ? _reg.AF : Get16(p);

    private void Set16Af(int p, ushort value)
    {
        if (p == 3) _reg.AF = value;
        else Set16(p, value);
    }

    /// <summary>Entry point called once, synchronously, the instant M1 completes.</summary>
    private ulong Dispatch(ulong pins)
    {
        _reg.Q = 0;
        _reg.EiPending = false;
        _reg.LastWasLdAIR = false;

        if (_opcode == 0x76) return DispatchHalt(pins);
        if (_opcode is >= 0x40 and <= 0x7F) return DispatchLdRR(pins);
        if (_opcode is >= 0x80 and <= 0xBF) return DispatchAluR(pins);

        switch (_opcode)
        {
            case 0x00: // NOP
                return pins;

            default:
                throw new NotImplementedException($"Opcode 0x{_opcode:X2} is not implemented yet.");
        }
    }

    /// <summary>Entry point called for every Step() while _phase == Execute.</summary>
    private ulong RunExecute(ulong pins)
    {
        if (_opcode is >= 0x40 and <= 0x7F and not 0x76) return ExecuteLdRR(pins);
        if (_opcode is >= 0x80 and <= 0xBF) return ExecuteAluR(pins);

        throw new NotImplementedException($"Opcode 0x{_opcode:X2} has no Execute-phase handler yet.");
    }

    // ---- 0x76 HALT -------------------------------------------------------------

    private ulong DispatchHalt(ulong pins)
    {
        // This single execution of HALT advances PC exactly like any other
        // one-byte opcode (confirmed against SingleStepTests' 76.json — PC is
        // NOT reverted here). The "PC doesn't advance past HALT" behaviour in
        // CLAUDE.md §6 is about *subsequent* M1 re-fetches while halted staying
        // pinned at this address, which belongs to the interrupts milestone
        // (HALT pin asserted now so that machinery has something to key off).
        return pins | PinBits.HALT;
    }

    // ---- 0x40-0x7F LD r,r' (and the 0x76 HALT exception handled above) --------

    private ulong DispatchLdRR(ulong pins)
    {
        var dst = (_opcode >> 3) & 7;
        var src = _opcode & 7;

        if (dst != 6 && src != 6)
        {
            Set8(dst, Get8(src));
            return FinishInstruction(pins);
        }

        return EnterExecute(pins);
    }

    private ulong ExecuteLdRR(ulong pins)
    {
        var dst = (_opcode >> 3) & 7;
        var src = _opcode & 7;

        if (src == 6)
        {
            if (!MemRead(ref pins, _reg.HL, out var data)) return pins;
            Set8(dst, data);
            return FinishInstruction(pins);
        }
        else
        {
            if (!MemWrite(ref pins, _reg.HL, Get8(src))) return pins;
            return FinishInstruction(pins);
        }
    }

    // ---- 0x80-0xBF ALU A,r ------------------------------------------------------

    private ulong DispatchAluR(ulong pins)
    {
        var src = _opcode & 7;
        if (src != 6)
        {
            ApplyAluOp((_opcode >> 3) & 7, Get8(src));
            return FinishInstruction(pins);
        }

        return EnterExecute(pins);
    }

    private ulong ExecuteAluR(ulong pins)
    {
        if (!MemRead(ref pins, _reg.HL, out var data)) return pins;
        ApplyAluOp((_opcode >> 3) & 7, data);
        return FinishInstruction(pins);
    }

    /// <summary>Applies ALU op <paramref name="op"/> (0=ADD 1=ADC 2=SUB 3=SBC
    /// 4=AND 5=XOR 6=OR 7=CP) of A with <paramref name="value"/>, per the
    /// standard y-field encoding shared by the 0x80-0xBF and 0xC6-0xFE ALU groups.</summary>
    private void ApplyAluOp(int op, byte value)
    {
        var carryIn = (_reg.F & Alu.CF) != 0;
        switch (op)
        {
            case 0: (_reg.A, _reg.F) = Alu.Add8(_reg.A, value); break;
            case 1: (_reg.A, _reg.F) = Alu.Adc8(_reg.A, value, carryIn); break;
            case 2: (_reg.A, _reg.F) = Alu.Sub8(_reg.A, value); break;
            case 3: (_reg.A, _reg.F) = Alu.Sbc8(_reg.A, value, carryIn); break;
            case 4: (_reg.A, _reg.F) = Alu.And8(_reg.A, value); break;
            case 5: (_reg.A, _reg.F) = Alu.Xor8(_reg.A, value); break;
            case 6: (_reg.A, _reg.F) = Alu.Or8(_reg.A, value); break;
            case 7: _reg.F = Alu.Cp8(_reg.A, value); break;
            default: throw new InvalidOperationException($"ApplyAluOp: op {op} out of range.");
        }
        _reg.Q = _reg.F;
    }
}

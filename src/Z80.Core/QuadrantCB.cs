namespace Z80.Core;

/// <summary>
/// The CB-prefixed page: rotate/shift, BIT, RES, SET, decoded by x/y/z (x =
/// (opcode&gt;&gt;6)&amp;3 selects the operation group, y = (opcode&gt;&gt;3)&amp;7 selects which
/// rotate or which bit, z = opcode&amp;7 selects the register, 6=(HL)). The CB byte
/// itself is consumed by Z80.cs's prefix hand-off before Dispatch() ever sees
/// this opcode; what reaches here is the second byte.
/// </summary>
public sealed partial class Z80
{
    private ulong DispatchCB(ulong pins)
    {
        var z = _opcode & 7;
        if (z == 6) return EnterExecute(pins);

        var x = (_opcode >> 6) & 3;
        var y = (_opcode >> 3) & 7;
        var value = Get8(z);

        switch (x)
        {
            case 0:
                Set8(z, ApplyCbRotate(y, value));
                break;
            case 1:
                _reg.F = Alu.Bit(value, y, flagSource: value, _reg.F);
                _reg.Q = _reg.F;
                break;
            case 2:
                Set8(z, (byte)(value & ~(1 << y)));
                break;
            case 3:
                Set8(z, (byte)(value | (1 << y)));
                break;
        }
        return FinishInstruction(pins);
    }

    /// <summary>The z=6 ((HL)) forms: BIT reads only (no internal/write-back
    /// cycle); ROT/RES/SET are a full read-modify-write with one internal
    /// T-state between the read and the write, same shape as INC/DEC (HL).</summary>
    private ulong ExecuteCB(ulong pins)
    {
        var x = (_opcode >> 6) & 3;
        var y = (_opcode >> 3) & 7;

        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                if (x == 1)
                {
                    // BIT n,(HL): Y/X come from WZ's high byte, not the tested
                    // byte — the one case where the flag source isn't the value
                    // just read (CLAUDE.md §6).
                    _reg.F = Alu.Bit(_latchLo, y, flagSource: (byte)(_reg.WZ >> 8), _reg.F);
                    _reg.Q = _reg.F;
                    _step = 3; // BIT has no write-back, but still burns 1 internal T-state.
                    return pins;
                }
                _step = 1;
                return pins;

            case 1:
                if (!InternalCycle(pins, 1)) return pins;
                _latchLo = x switch
                {
                    0 => ApplyCbRotate(y, _latchLo),
                    2 => (byte)(_latchLo & ~(1 << y)),
                    3 => (byte)(_latchLo | (1 << y)),
                    _ => throw new InvalidOperationException(),
                };
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.HL, _latchLo)) return pins;
                return FinishInstruction(pins);

            case 3:
                if (!InternalCycle(pins, 1)) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>y selects RLC(0)/RRC(1)/RL(2)/RR(3)/SLA(4)/SRA(5)/SLL(6,
    /// undocumented)/SRL(7); sets F via the shared Alu rotate/shift functions.</summary>
    private byte ApplyCbRotate(int y, byte value)
    {
        byte result;
        switch (y)
        {
            case 0: (result, _reg.F) = Alu.Rlc8(value); break;
            case 1: (result, _reg.F) = Alu.Rrc8(value); break;
            case 2: (result, _reg.F) = Alu.Rl8(value, (_reg.F & Alu.CF) != 0); break;
            case 3: (result, _reg.F) = Alu.Rr8(value, (_reg.F & Alu.CF) != 0); break;
            case 4: (result, _reg.F) = Alu.Sla8(value); break;
            case 5: (result, _reg.F) = Alu.Sra8(value); break;
            case 6: (result, _reg.F) = Alu.Sll8(value); break;
            case 7: (result, _reg.F) = Alu.Srl8(value); break;
            default: throw new InvalidOperationException($"ApplyCbRotate: y {y} out of range.");
        }
        _reg.Q = _reg.F;
        return result;
    }
}

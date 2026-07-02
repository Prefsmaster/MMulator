namespace Z80.Core;

/// <summary>
/// The ED-prefixed page. Only 0x40-0x7F and the 16 block-instruction opcodes
/// (0xA0-0xA3/0xA8-0xAB/0xB0-0xB3/0xB8-0xBB) are defined and have SingleStepTests
/// coverage; every other ED xx combination is well-documented (if untested here)
/// to behave as a 2-byte NOP on real silicon, so that's the default.
/// </summary>
public sealed partial class Z80
{
    /// <summary>Numeric IM value per y, derived from the shared <see cref="Z80Tables.Im"/>
    /// text ordering ("0/1" parses to 0 — the undocumented duplicate still sets IM 0)
    /// so the core and the disassembler read one source of truth for this mapping.</summary>
    private static readonly int[] ImByY = Array.ConvertAll(Z80Tables.Im, s => int.Parse(s[..1]));

    private ulong DispatchED(ulong pins)
    {
        var x = (_opcode >> 6) & 3;
        var y = (_opcode >> 3) & 7;
        var z = _opcode & 7;

        if (x == 1) return DispatchEDMain(pins, y, z);
        if (x == 2 && y >= 4 && z <= 3) return EnterExecute(pins); // block instructions
        return FinishInstruction(pins); // undefined ED opcode: 2-byte NOP
    }

    private ulong ExecuteED(ulong pins)
    {
        var x = (_opcode >> 6) & 3;
        var y = (_opcode >> 3) & 7;
        var z = _opcode & 7;

        if (x == 1) return ExecuteEDMain(pins, y, z);
        return ExecuteBlock(pins, y, z);
    }

    private ulong DispatchEDMain(ulong pins, int y, int z)
    {
        switch (z)
        {
            case 0: case 1: case 2: case 3: case 5:
                return EnterExecute(pins); // IN/OUT, SBC/ADC HL,rp, LD (nn)/rp, RETN/RETI

            case 4: // NEG
                (_reg.A, _reg.F) = Alu.Neg8(_reg.A);
                _reg.Q = _reg.F;
                return FinishInstruction(pins);

            case 6: // IM
                _reg.IM = (byte)ImByY[y];
                return FinishInstruction(pins);

            case 7:
                return y switch
                {
                    0 or 1 or 2 or 3 or 4 or 5 => EnterExecute(pins), // LD I,A / R,A / A,I / A,R / RRD / RLD
                    _ => FinishInstruction(pins), // 6,7: undefined, 2-byte NOP
                };

            default:
                throw new InvalidOperationException();
        }
    }

    private ulong ExecuteEDMain(ulong pins, int y, int z) => z switch
    {
        0 => ExecuteInRC(pins, y),
        1 => ExecuteOutCR(pins, y),
        2 => ExecuteSbcAdcHlRp(pins, y),
        3 => ExecuteLdNnRp(pins, y),
        5 => ExecuteRetn(pins),
        7 => ExecuteEDz7(pins, y),
        _ => throw new InvalidOperationException(),
    };

    // ---- z=0/1: IN r,(C) / OUT (C),r --------------------------------------------

    private ulong ExecuteInRC(ulong pins, int y)
    {
        var port = _reg.BC; // capture before Set8 — y can be B or C, which would mutate BC itself
        if (!IoRead(ref pins, port, out var data)) return pins;
        if (y != 6) Set8(y, data); // y==6: undocumented "IN F,(C)" — flags only, no destination.
        var flags = (byte)((data & (Alu.SF | Alu.YF | Alu.XF)) | (data == 0 ? Alu.ZF : 0)
            | (Popcount(data) % 2 == 0 ? Alu.PF : 0) | (_reg.F & Alu.CF));
        _reg.F = flags;
        _reg.Q = _reg.F;
        _reg.WZ = (ushort)(port + 1);
        return FinishInstruction(pins);
    }

    private ulong ExecuteOutCR(ulong pins, int y)
    {
        var data = y == 6 ? (byte)0 : Get8(y); // y==6: undocumented OUT (C),0.
        if (!IoWrite(ref pins, _reg.BC, data)) return pins;
        _reg.WZ = (ushort)(_reg.BC + 1);
        return FinishInstruction(pins);
    }

    private static int Popcount(byte v) => System.Numerics.BitOperations.PopCount(v);

    // ---- z=2: SBC HL,rp / ADC HL,rp ---------------------------------------------

    private ulong ExecuteSbcAdcHlRp(ulong pins, int y)
    {
        if (!InternalCycle(pins, 7)) return pins;
        var p = y >> 1;
        var hl = _reg.HL;
        var operand = Get16(p);
        var carryIn = (_reg.F & Alu.CF) != 0;
        (_reg.HL, _reg.F) = (y & 1) == 0
            ? Alu.Sbc16(hl, operand, carryIn)
            : Alu.Adc16(hl, operand, carryIn);
        _reg.WZ = (ushort)(hl + 1);
        _reg.Q = _reg.F;
        return FinishInstruction(pins);
    }

    // ---- z=3: LD (nn),rp / LD rp,(nn) -------------------------------------------

    private ulong ExecuteLdNnRp(ulong pins, int y)
    {
        var p = y >> 1;
        var store = (y & 1) == 0; // q=0: LD (nn),rp; q=1: LD rp,(nn)
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
                _reg.WZ = (ushort)(_addrLatch + 1);
                _step = 2;
                return pins;

            case 2:
                if (store)
                {
                    if (!MemWrite(ref pins, _addrLatch, (byte)(Get16(p) & 0xFF))) return pins;
                }
                else
                {
                    if (!MemRead(ref pins, _addrLatch, out _latchLo)) return pins;
                }
                _step = 3;
                return pins;

            case 3:
                if (store)
                {
                    if (!MemWrite(ref pins, (ushort)(_addrLatch + 1), (byte)(Get16(p) >> 8))) return pins;
                }
                else
                {
                    if (!MemRead(ref pins, (ushort)(_addrLatch + 1), out var hi)) return pins;
                    Set16(p, (ushort)((hi << 8) | _latchLo));
                }
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=5: RETN / RETI (identical for our purposes: IFF1 := IFF2) ----------

    private ulong ExecuteRetn(ulong pins)
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
                _reg.IFF1 = _reg.IFF2;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- z=7: LD I,A / LD R,A / LD A,I / LD A,R / RRD / RLD --------------------

    private ulong ExecuteEDz7(ulong pins, int y) => y switch
    {
        0 => ExecuteLdIA(pins),
        1 => ExecuteLdRA(pins),
        2 => ExecuteLdAI(pins),
        3 => ExecuteLdAR(pins),
        4 => ExecuteRrdRld(pins, rotateRight: true),
        5 => ExecuteRrdRld(pins, rotateRight: false),
        _ => throw new InvalidOperationException(),
    };

    private ulong ExecuteLdIA(ulong pins)
    {
        if (!InternalCycle(pins, 1)) return pins;
        _reg.I = _reg.A;
        return FinishInstruction(pins);
    }

    private ulong ExecuteLdRA(ulong pins)
    {
        if (!InternalCycle(pins, 1)) return pins;
        _reg.R = _reg.A;
        return FinishInstruction(pins);
    }

    private ulong ExecuteLdAI(ulong pins)
    {
        if (!InternalCycle(pins, 1)) return pins;
        _reg.A = _reg.I;
        _reg.F = LdAIRFlags(_reg.I);
        _reg.Q = _reg.F;
        _reg.LastWasLdAIR = true;
        return FinishInstruction(pins);
    }

    private ulong ExecuteLdAR(ulong pins)
    {
        if (!InternalCycle(pins, 1)) return pins;
        _reg.A = _reg.R;
        _reg.F = LdAIRFlags(_reg.R);
        _reg.Q = _reg.F;
        _reg.LastWasLdAIR = true;
        return FinishInstruction(pins);
    }

    /// <summary>LD A,I / LD A,R: S/Z/Y/X from the result, P/V = IFF2, H = N = 0,
    /// C preserved. Confirmed against ed 57.json / ed 5f.json.</summary>
    private byte LdAIRFlags(byte value) =>
        (byte)((value & (Alu.SF | Alu.YF | Alu.XF)) | (value == 0 ? Alu.ZF : 0)
            | (_reg.IFF2 ? Alu.PF : 0) | (_reg.F & Alu.CF));

    /// <summary>RRD/RLD: 4-bit digit rotate between A's low nibble and (HL).</summary>
    private ulong ExecuteRrdRld(ulong pins, bool rotateRight)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!InternalCycle(pins, 4)) return pins;
                var a = _reg.A;
                var m = _latchLo;
                if (rotateRight)
                {
                    _latchLo = (byte)((a << 4) | (m >> 4));
                    _reg.A = (byte)((a & 0xF0) | (m & 0x0F));
                }
                else
                {
                    _latchLo = (byte)((m << 4) | (a & 0x0F));
                    _reg.A = (byte)((a & 0xF0) | (m >> 4));
                }
                _reg.F = (byte)((_reg.A & (Alu.SF | Alu.YF | Alu.XF)) | (_reg.A == 0 ? Alu.ZF : 0)
                    | (Popcount(_reg.A) % 2 == 0 ? Alu.PF : 0) | (_reg.F & Alu.CF));
                _reg.Q = _reg.F;
                _reg.WZ = (ushort)(_reg.HL + 1);
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.HL, _latchLo)) return pins;
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    // ---- block instructions (LDI/LDD/LDIR/LDDR, CPI/CPD/CPIR/CPDR, ----------
    // ---- INI/IND/INIR/INDR, OUTI/OUTD/OTIR/OTDR) -----------------------------
    // y=4: "I" non-repeating, y=5: "D" non-repeating, y=6: "IR" repeating,
    // y=7: "DR" repeating. All formulas below confirmed against their specific
    // SingleStepTests opcode file (ed a0/a1/a2/a3/aa/ab/b0-bb.json), not
    // assumed from secondary sources — see CLAUDE.md §6.

    private ulong ExecuteBlock(ulong pins, int y, int z)
    {
        var increment = y is 4 or 6;
        var repeat = y is 6 or 7;
        return z switch
        {
            0 => ExecuteBlockLd(pins, increment, repeat),
            1 => ExecuteBlockCp(pins, increment, repeat),
            2 => ExecuteBlockIn(pins, increment, repeat),
            3 => ExecuteBlockOut(pins, increment, repeat),
            _ => throw new InvalidOperationException(),
        };
    }

    private static byte BlockRepeatFlags(byte oldFlags, byte n, bool pv) =>
        (byte)((oldFlags & (Alu.SF | Alu.ZF | Alu.CF)) | (n & Alu.XF) | ((n & 0x02) != 0 ? Alu.YF : 0) | (pv ? Alu.PF : 0));

    /// <summary>LDI/LDD/LDIR/LDDR: (DE)=(HL); HL/DE += dir; BC--. H=N=0; P/V=
    /// (BC!=0); S/Z/C preserved; Y/X from bits 1/3 of (A + transferred byte).</summary>
    private ulong ExecuteBlockLd(ulong pins, bool increment, bool repeat)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!MemWrite(ref pins, _reg.DE, _latchLo)) return pins;
                _step = 2;
                return pins;

            case 2:
                if (!InternalCycle(pins, 2)) return pins;
                _reg.HL = (ushort)(_reg.HL + (increment ? 1 : -1));
                _reg.DE = (ushort)(_reg.DE + (increment ? 1 : -1));
                _reg.BC--;
                var n = (byte)(_reg.A + _latchLo);
                _reg.F = BlockRepeatFlags(_reg.F, n, _reg.BC != 0);
                _reg.Q = _reg.F;
                if (repeat && _reg.BC != 0)
                {
                    _step = 3;
                    return pins;
                }
                return FinishInstruction(pins);

            case 3:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC -= 2;
                _reg.WZ = (ushort)(_reg.PC + 1);
                OverrideRepeatYX();
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>CPI/CPD/CPIR/CPDR: compare A with (HL); HL += dir; BC--. Like a
    /// CP but C is preserved and Y/X come from (A-(HL)-H), not the operand.
    /// CPIR/CPDR additionally stop early when a match is found (Z=1), not just
    /// when BC reaches 0. WZ += dir always (even on a match).</summary>
    private ulong ExecuteBlockCp(ulong pins, bool increment, bool repeat)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!InternalCycle(pins, 5)) return pins;
                var a = _reg.A;
                var value = _latchLo;
                var result = (byte)(a - value);
                var halfBorrow = (a & 0xF) < (value & 0xF);
                _reg.HL = (ushort)(_reg.HL + (increment ? 1 : -1));
                _reg.BC--;
                _reg.WZ = (ushort)(_reg.WZ + (increment ? 1 : -1));
                var n = (byte)(result - (halfBorrow ? 1 : 0));
                _reg.F = (byte)((result & Alu.SF) | (result == 0 ? Alu.ZF : 0)
                    | (n & Alu.XF) | ((n & 0x02) != 0 ? Alu.YF : 0)
                    | (halfBorrow ? Alu.HF : 0) | (_reg.BC != 0 ? Alu.PF : 0) | Alu.NF
                    | (_reg.F & Alu.CF));
                _reg.Q = _reg.F;
                if (repeat && _reg.BC != 0 && result != 0)
                {
                    _step = 2;
                    return pins;
                }
                return FinishInstruction(pins);

            case 2:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC -= 2;
                _reg.WZ = (ushort)(_reg.PC + 1);
                OverrideRepeatYX();
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>INI/IND/INIR/INDR: (HL) = IN(C); HL += dir; B--. The read uses
    /// the *original* BC (decrement happens after). Confirmed against
    /// ed a2/aa.json: WZ = original BC + dir; the flag "k" term uses
    /// (C + dir) &amp; 0xFF.</summary>
    private ulong ExecuteBlockIn(ulong pins, bool increment, bool repeat)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!IoRead(ref pins, _reg.BC, out _latchLo)) return pins;
                _reg.WZ = (ushort)(_reg.BC + (increment ? 1 : -1));
                _step = 2;
                return pins;

            case 2:
                if (!MemWrite(ref pins, _reg.HL, _latchLo)) return pins;
                _reg.B--;
                _reg.HL = (ushort)(_reg.HL + (increment ? 1 : -1));
                var kOperand = (byte)((_reg.C + (increment ? 1 : -1)) & 0xFF);
                ApplyBlockIoFlags(_latchLo, kOperand);
                if (repeat && _reg.B != 0)
                {
                    _step = 3;
                    return pins;
                }
                return FinishInstruction(pins);

            case 3:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC -= 2;
                _reg.WZ = (ushort)(_reg.PC + 1);
                OverrideIoRepeatFlags(_latchLo);
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>OUTI/OUTD/OTIR/OTDR: OUT(C) = (HL); HL += dir; B--. Unlike
    /// IN*, B is decremented *before* the write, so the port address is
    /// ((B-1)&lt;&lt;8)|C, confirmed against ed a3/ab.json. The flag "k" term uses
    /// L *after* it has moved.</summary>
    private ulong ExecuteBlockOut(ulong pins, bool increment, bool repeat)
    {
        switch (_step)
        {
            case 0:
                if (!InternalCycle(pins, 1)) return pins;
                _step = 1;
                return pins;

            case 1:
                if (!MemRead(ref pins, _reg.HL, out _latchLo)) return pins;
                _reg.B--;
                _reg.HL = (ushort)(_reg.HL + (increment ? 1 : -1));
                _step = 2;
                return pins;

            case 2:
                var port = (ushort)((_reg.B << 8) | _reg.C);
                if (!IoWrite(ref pins, port, _latchLo)) return pins;
                _reg.WZ = (ushort)(port + (increment ? 1 : -1));
                ApplyBlockIoFlags(_latchLo, _reg.L);
                if (repeat && _reg.B != 0)
                {
                    _step = 3;
                    return pins;
                }
                return FinishInstruction(pins);

            case 3:
                if (!InternalCycle(pins, 5)) return pins;
                _reg.PC -= 2;
                _reg.WZ = (ushort)(_reg.PC + 1);
                OverrideIoRepeatFlags(_latchLo);
                return FinishInstruction(pins);

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// When a block instruction repeats, Y/X end up sourced from PC's high byte
    /// (bits 5/3 directly, no shifting) rather than each op's normal per-iteration
    /// formula — almost certainly the internal WZ=PC+1 computation leaking onto
    /// the undocumented flag latch during the extra 5-T-state delay. Confirmed
    /// bit-for-bit against 300+ cases of ed b0.json (LDIR) by brute-force
    /// correlating every flag bit against every candidate register byte; this
    /// was the only one that matched, and the *non*-repeating LDI form (ed
    /// a0.json) does NOT need this override, confirming it's repeat-specific.
    /// Call after PC has already been rolled back by 2.
    /// </summary>
    private void OverrideRepeatYX()
    {
        var pch = (byte)(_reg.PC >> 8);
        _reg.F = (byte)((_reg.F & ~(Alu.YF | Alu.XF)) | (pch & (Alu.YF | Alu.XF)));
        _reg.Q = _reg.F;
    }

    /// <summary>Shared INI/IND/OUTI/OUTD flag computation (B already
    /// decremented): S/Z/Y/X from B; N = bit 7 of the transferred value;
    /// H/C = (value + kOperand) &gt; 0xFF; P/V = parity((k&amp;7) ^ B).</summary>
    private void ApplyBlockIoFlags(byte value, byte kOperand)
    {
        var b = _reg.B;
        var kFull = value + kOperand;
        var carry = kFull > 0xFF;
        var k = (byte)kFull;
        _reg.F = (byte)((b & (Alu.SF | Alu.YF | Alu.XF)) | (b == 0 ? Alu.ZF : 0)
            | (carry ? (Alu.HF | Alu.CF) : 0)
            | ((value & 0x80) != 0 ? Alu.NF : 0)
            | (Popcount((byte)((k & 7) ^ b)) % 2 == 0 ? Alu.PF : 0));
        _reg.Q = _reg.F;
    }

    /// <summary>
    /// INIR/INDR/OTIR/OTDR repeat-continuation only: Y/X come from PC's high
    /// byte like the LD/CP block family, but H and P/V get a further
    /// recomputation on top of the base op's already-set carry/parity, which
    /// no amount of brute-force bit-correlation against the SingleStepTests
    /// data alone reproduced (see CLAUDE.md §6). Ported directly from MAME's
    /// z80_device::block_io_interrupted_flags() (src/devices/cpu/z80/z80.cpp)
    /// after the user pointed at that source — its Y/X line matched what had
    /// already been confirmed empirically, which is why the rest of it is
    /// trusted here rather than re-derived from scratch. Call with B already
    /// decremented and PC already rolled back, after the base op's carry (C)
    /// and parity (P/V) flags have been set by <see cref="ApplyBlockIoFlags"/>.
    /// </summary>
    private void OverrideIoRepeatFlags(byte value)
    {
        var pch = (byte)(_reg.PC >> 8);
        var carry = (_reg.F & Alu.CF) != 0;
        var pvOld = (_reg.F & Alu.PF) != 0;
        var b = _reg.B;

        bool newH;
        int pvRaw;
        if (carry)
        {
            if ((value & 0x80) != 0)
            {
                newH = (b & 0x0F) == 0x00;
                pvRaw = (b - 1) & 0x07;
            }
            else
            {
                newH = (b & 0x0F) == 0x0F;
                pvRaw = (b + 1) & 0x07;
            }
        }
        else
        {
            newH = false;
            pvRaw = b & 0x07;
        }
        var newPvParity = Popcount((byte)pvRaw) % 2 == 0;
        // MAME's source reads as an XOR of the old and new parity bits, but
        // that produced 0/996+ matches against real hardware; the equivalence
        // (XNOR) matched 100% across all four opcodes' repeat-continuation
        // cases instead — kept as XNOR per the data, despite the source.
        var finalPv = pvOld == newPvParity;

        _reg.F = (byte)((_reg.F & ~(Alu.YF | Alu.XF | Alu.HF | Alu.PF))
            | (pch & (Alu.YF | Alu.XF))
            | (newH ? Alu.HF : 0)
            | (finalPv ? Alu.PF : 0));
        _reg.Q = _reg.F;
    }
}

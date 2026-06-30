namespace Z80.Core;

/// <summary>
/// DD/FD-prefixed opcode page: the base-page opcode byte is reinterpreted with
/// HL→IX/IY and (HL)→(IX+d)/(IY+d) substitution. Only opcodes that actually
/// reference HL/H/L/(HL) are routed here via <see cref="IsIndexAffected"/>;
/// all others fall through to the normal base-page dispatch (wasted-prefix
/// behaviour — they just execute as-is, with R incremented twice).
///
/// Mixed-operand quirk (CLAUDE.md §6): when a single LD r,r' accesses (HL)/
/// (IX+d) on one side AND H/L on the other, the H/L side stays as the REAL
/// H or L register — it does NOT become IXH/IXL. Only when H/L appear as
/// standalone register operands (with neither side being (HL)/(IX+d)) are
/// they substituted.
///
/// DDCB/FDCB: the four-byte form. After the two M1s for DD/CB (or FD/CB),
/// Dispatch transitions to Execute phase; ExecuteIndexed fetches the signed
/// displacement and the CB-table opcode byte both via plain MR (not M1), then
/// applies the CB operation to (IX+d)/(IY+d) — with an optional dual-write
/// side-effect into register z when z≠6.
/// </summary>
public sealed partial class Z80
{
    // ---- index-register helpers -------------------------------------------------

    private bool IsIX => _prefix is Prefix.DD or Prefix.DDCB;

    private ushort GetIndexReg() => IsIX ? _reg.IX : _reg.IY;
    private void SetIndexReg(ushort v) { if (IsIX) _reg.IX = v; else _reg.IY = v; }

    private byte GetIndexHigh() => IsIX ? _reg.IXH : _reg.IYH;
    private void SetIndexHigh(byte v) { if (IsIX) _reg.IXH = v; else _reg.IYH = v; }

    private byte GetIndexLow() => IsIX ? _reg.IXL : _reg.IYL;
    private void SetIndexLow(byte v) { if (IsIX) _reg.IXL = v; else _reg.IYL = v; }

    /// <summary>Register access for contexts where H/L→IXH/IXL: index 4 and 5
    /// redirect to the index register's high/low byte; all others are unchanged.</summary>
    private byte GetIndexed8(int i) =>
        i == 4 ? GetIndexHigh() : i == 5 ? GetIndexLow() : Get8(i);

    private void SetIndexed8(int i, byte v)
    {
        if      (i == 4) SetIndexHigh(v);
        else if (i == 5) SetIndexLow(v);
        else             Set8(i, v);
    }

    // ---- IsIndexAffected --------------------------------------------------------

    /// <summary>Returns true when the opcode changes behaviour under DD/FD: i.e.
    /// it references H, L, (HL), or HL as a 16-bit pair in a way that must be
    /// substituted with IXH/IXL, (IX+d), or IX.  Everything else just runs as the
    /// base-page version (wasted prefix).</summary>
    private static bool IsIndexAffected(byte op)
    {
        if (op is >= 0x40 and <= 0x7F and not 0x76)
        {
            var y = (op >> 3) & 7;
            var z = op & 7;
            return y is 4 or 5 or 6 || z is 4 or 5 or 6;
        }
        if (op is >= 0x80 and <= 0xBF)
            return (op & 7) is 4 or 5 or 6;

        if (op <= 0x3F)
        {
            var z = op & 7;
            var y = (op >> 3) & 7;
            var p = (op >> 4) & 3;
            var q = (op >> 3) & 1;
            if (z == 1 && q == 0 && p == 2) return true; // LD IX,nn
            if (z == 1 && q == 1)           return true; // ADD IX,rp  (all p)
            if (z == 2 && p == 2)           return true; // LD (nn),IX / LD IX,(nn)
            if (z == 3 && p == 2)           return true; // INC/DEC IX
            if (z is 4 or 5 or 6 && y is 4 or 5 or 6) return true; // INC/DEC/LD H/L/(HL)
        }

        return op is 0xE1 or 0xE3 or 0xE5 or 0xE9 or 0xF9;
    }

    // ---- DispatchIndexed --------------------------------------------------------

    private ulong DispatchIndexed(ulong pins)
    {
        // Quadrant11 affected ops: must be checked FIRST before the z-field switch,
        // because their z/y values collide with quadrant00 cases (e.g. 0xE5 z=5,y=4
        // would look like DEC IXH; 0xE9 z=1,q=1 would look like ADD IX,rp).
        if (_opcode is >= 0xC0)
        {
            switch (_opcode)
            {
                case 0xE1: return EnterExecute(pins); // POP IX
                case 0xE3: return EnterExecute(pins); // EX (SP),IX
                case 0xE5: return EnterExecute(pins); // PUSH IX
                case 0xE9:                            // JP (IX): inline, no Execute phase
                    _reg.PC = GetIndexReg();
                    return FinishInstruction(pins);
                case 0xF9: return EnterExecute(pins); // LD SP,IX
            }
        }

        var z = _opcode & 7;
        var y = (_opcode >> 3) & 7;
        var p = (_opcode >> 4) & 3;
        var q = (_opcode >> 3) & 1;

        // LD r,r' block (0x40-0x7F): one or both sides touch H/L/(HL).
        if (_opcode is >= 0x40 and <= 0x7F and not 0x76)
        {
            var dst = y;
            var src = z;
            if (dst != 6 && src != 6)
            {
                // Pure-register: substitute H/L→IXH/IXL on both sides.
                SetIndexed8(dst, GetIndexed8(src));
                return FinishInstruction(pins);
            }
            // One side is (IX+d): needs displacement fetch.
            return EnterExecute(pins);
        }

        // ALU A,r (0x80-0xBF): src touches H/L/(HL).
        if (_opcode is >= 0x80 and <= 0xBF)
        {
            var src = z;
            if (src != 6)
            {
                ApplyAluOp(y, GetIndexed8(src));
                return FinishInstruction(pins);
            }
            return EnterExecute(pins);
        }

        // Quadrant00 affected ops:
        switch (z)
        {
            case 1:
                if (q == 0 && p == 2) return EnterExecute(pins); // LD IX,nn
                if (q == 1)           return EnterExecute(pins); // ADD IX,rp
                break;
            case 2: return EnterExecute(pins); // LD (nn),IX / LD IX,(nn)
            case 3: return EnterExecute(pins); // INC IX / DEC IX

            case 4:
                if (y == 6) return EnterExecute(pins); // INC (IX+d)
                // INC IXH (y==4) / INC IXL (y==5): pure register, 0 extra cycles.
                if (y == 4)
                {
                    (var r4, _reg.F) = Alu.Inc8(GetIndexHigh(), _reg.F);
                    SetIndexHigh(r4);
                }
                else
                {
                    (var r5, _reg.F) = Alu.Inc8(GetIndexLow(), _reg.F);
                    SetIndexLow(r5);
                }
                _reg.Q = _reg.F;
                return FinishInstruction(pins);

            case 5:
                if (y == 6) return EnterExecute(pins); // DEC (IX+d)
                if (y == 4)
                {
                    (var r4, _reg.F) = Alu.Dec8(GetIndexHigh(), _reg.F);
                    SetIndexHigh(r4);
                }
                else
                {
                    (var r5, _reg.F) = Alu.Dec8(GetIndexLow(), _reg.F);
                    SetIndexLow(r5);
                }
                _reg.Q = _reg.F;
                return FinishInstruction(pins);

            case 6:
                return EnterExecute(pins); // LD IXH,n / LD IXL,n / LD (IX+d),n
        }

        throw new InvalidOperationException($"DispatchIndexed: unhandled opcode 0x{_opcode:X2}");
    }

    // ---- ExecuteIndexed ---------------------------------------------------------

    private ulong ExecuteIndexed(ulong pins)
    {
        if (_prefix is Prefix.DDCB or Prefix.FDCB)
            return ExecuteDdCb(pins);

        // Quadrant11 affected ops: must be checked FIRST because their z-field values
        // (z=1 for 0xE1/0xF9, z=3 for 0xE3, z=5 for 0xE5) would otherwise fall into
        // the quadrant00 z-switch below and be misrouted.
        if (_opcode is >= 0xC0)
        {
            return _opcode switch
            {
                0xE1 => ExecutePop(pins, 2),
                0xE3 => ExecuteExSpIx(pins),
                0xE5 => ExecutePush(pins, 2),
                0xF9 => ExecuteLdSpIx(pins),
                _ => throw new InvalidOperationException($"ExecuteIndexed quadrant11: unhandled 0x{_opcode:X2}"),
            };
        }

        var z = _opcode & 7;
        var y = (_opcode >> 3) & 7;

        // LD r,r' block (both non-6 already handled at Dispatch time):
        if (_opcode is >= 0x40 and <= 0x7F and not 0x76)
        {
            var dst = y;
            var src = z;
            if (src == 6)
            {
                // LD r,(IX+d): dst may be 4/5 (real H/L per mixed quirk) → use Set8 not SetIndexed8.
                if (!IndexedMRWithDisplacement(ref pins, out var data)) return pins;
                Set8(dst, data); // deliberately plain Set8: H stays real H even when dst=4/5
                return FinishInstruction(pins);
            }
            else
            {
                // LD (IX+d),r: src may be 4/5 (real H/L) → use Get8 not GetIndexed8.
                var val = Get8(src); // plain Get8: H stays real H
                if (!IndexedMWWithDisplacement(ref pins, val)) return pins;
                return FinishInstruction(pins);
            }
        }

        // ALU A,(IX+d):
        if (_opcode is >= 0x80 and <= 0xBF)
        {
            if (!IndexedMRWithDisplacement(ref pins, out var data)) return pins;
            ApplyAluOp(y, data);
            return FinishInstruction(pins);
        }

        // Quadrant00 affected ops:
        switch (z)
        {
            case 1:
                // LD IX,nn (q=0, p=2): ExecuteZ1 handles it; Set16(2) is prefix-aware.
                // ADD IX,rp (q=1): uses custom handler (ExecuteZ1 q=1 names _reg.HL directly).
                if ((_opcode >> 3 & 1) == 0) return ExecuteZ1(pins);
                return ExecuteAddIxRp(pins);

            case 2: return ExecuteNnIx(pins);  // LD (nn),IX / LD IX,(nn)

            case 3: return ExecuteIncDecRp(pins); // INC/DEC IX via Get16(2)/Set16(2)

            case 4: return ExecuteIncDecIxd(pins, increment: true);

            case 5: return ExecuteIncDecIxd(pins, increment: false);

            case 6:
                if (y == 6) return ExecuteLdIxdN(pins);  // LD (IX+d),n
                // LD IXH,n (y==4) or LD IXL,n (y==5):
                if (!MemRead(ref pins, _reg.PC, out var imm)) return pins;
                _reg.PC++;
                if (y == 4) SetIndexHigh(imm); else SetIndexLow(imm);
                return FinishInstruction(pins);
        }

        throw new InvalidOperationException($"ExecuteIndexed: unhandled opcode 0x{_opcode:X2}");
    }

    // ---- (IX+d) displacement header helpers ------------------------------------

    /// <summary>Steps 0-2: fetch displacement (MR), burn 5 internal T-states (address
    /// calculation), then do one MR at (IX+d) — the combined "read from indexed address"
    /// header used by LD r,(IX+d) and ALU A,(IX+d). Returns true (with data) once the
    /// final read completes.</summary>
    private bool IndexedMRWithDisplacement(ref ulong pins, out byte data)
    {
        data = 0;
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return false;
                _reg.PC++;
                _displacement = (sbyte)_latchLo;
                _step = 1;
                return false;
            case 1:
                if (!InternalCycle(pins, 5)) return false;
                _addrLatch = (ushort)(GetIndexReg() + _displacement);
                _reg.WZ = _addrLatch;
                _step = 2;
                return false;
            case 2:
                if (!MemRead(ref pins, _addrLatch, out data)) return false;
                _step = 0;
                return true;
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>Steps 0-2: fetch displacement, burn 5 internal T-states, then do
    /// one MW to (IX+d) — the combined "write to indexed address" header.</summary>
    private bool IndexedMWWithDisplacement(ref ulong pins, byte data)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return false;
                _reg.PC++;
                _displacement = (sbyte)_latchLo;
                _step = 1;
                return false;
            case 1:
                if (!InternalCycle(pins, 5)) return false;
                _addrLatch = (ushort)(GetIndexReg() + _displacement);
                _reg.WZ = _addrLatch;
                _step = 2;
                return false;
            case 2:
                if (!MemWrite(ref pins, _addrLatch, data)) return false;
                _step = 0;
                return true;
            default: throw new InvalidOperationException();
        }
    }

    // ---- individual indexed execute handlers -----------------------------------

    /// <summary>ADD IX,rp: 7 internal T-states (same as ADD HL,rp) then 16-bit add.
    /// Explicit because ExecuteZ1's q=1 branch names _reg.HL directly.</summary>
    private ulong ExecuteAddIxRp(ulong pins)
    {
        if (!InternalCycle(pins, 7)) return pins;
        var ix = GetIndexReg();
        var p = (_opcode >> 4) & 3;
        (var result, _reg.F) = Alu.Add16(ix, Get16(p), _reg.F);
        SetIndexReg(result);
        _reg.WZ = (ushort)(ix + 1);
        _reg.Q = _reg.F;
        return FinishInstruction(pins);
    }

    /// <summary>LD (nn),IX / LD IX,(nn): fetch nn via 2 MR, then 2 MW or 2 MR.</summary>
    private ulong ExecuteNnIx(ulong pins)
    {
        var store = (_opcode & 0x08) == 0; // q=0: LD (nn),IX; q=1: LD IX,(nn)
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
                if (store) { if (!MemWrite(ref pins, _addrLatch, GetIndexLow())) return pins; }
                else       { if (!MemRead(ref pins, _addrLatch, out _latchLo))  return pins; }
                _step = 3;
                return pins;
            case 3:
                if (store)
                {
                    if (!MemWrite(ref pins, (ushort)(_addrLatch + 1), GetIndexHigh())) return pins;
                }
                else
                {
                    if (!MemRead(ref pins, (ushort)(_addrLatch + 1), out var hi)) return pins;
                    SetIndexReg((ushort)((hi << 8) | _latchLo));
                }
                return FinishInstruction(pins);
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>INC (IX+d) / DEC (IX+d): displacement + 5 internal + MR + 1 internal + MW
    /// = 23 T-states total (after the two M1s), confirmed against dd 34.json / dd 35.json.</summary>
    private ulong ExecuteIncDecIxd(ulong pins, bool increment)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _displacement = (sbyte)_latchLo;
                _step = 1;
                return pins;
            case 1:
                if (!InternalCycle(pins, 5)) return pins;
                _addrLatch = (ushort)(GetIndexReg() + _displacement);
                _reg.WZ = _addrLatch;
                _step = 2;
                return pins;
            case 2:
                if (!MemRead(ref pins, _addrLatch, out _latchLo)) return pins;
                _step = 3;
                return pins;
            case 3:
                if (!InternalCycle(pins, 1)) return pins;
                (_latchLo, _reg.F) = increment ? Alu.Inc8(_latchLo, _reg.F) : Alu.Dec8(_latchLo, _reg.F);
                _reg.Q = _reg.F;
                _step = 4;
                return pins;
            case 4:
                if (!MemWrite(ref pins, _addrLatch, _latchLo)) return pins;
                return FinishInstruction(pins);
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>LD (IX+d),n: displacement + immediate n + 2 internal + MW
    /// = 19 T-states total, confirmed against dd 36.json.</summary>
    private ulong ExecuteLdIxdN(ulong pins)
    {
        switch (_step)
        {
            case 0:
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _displacement = (sbyte)_latchLo;
                _step = 1;
                return pins;
            case 1:
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins; // immediate n
                _reg.PC++;
                _addrLatch = (ushort)(GetIndexReg() + _displacement);
                _reg.WZ = _addrLatch;
                _step = 2;
                return pins;
            case 2:
                if (!InternalCycle(pins, 2)) return pins;
                _step = 3;
                return pins;
            case 3:
                if (!MemWrite(ref pins, _addrLatch, _latchHi)) return pins;
                return FinishInstruction(pins);
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>EX (SP),IX: same structure as EX (SP),HL (23 T-states) but using
    /// the index register's high/low bytes.</summary>
    private ulong ExecuteExSpIx(ulong pins)
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
                if (!MemWrite(ref pins, (ushort)(_reg.SP + 1), GetIndexHigh())) return pins;
                _step = 4;
                return pins;
            case 4:
                if (!MemWrite(ref pins, _reg.SP, GetIndexLow())) return pins;
                SetIndexReg((ushort)((_latchHi << 8) | _latchLo));
                _reg.WZ = GetIndexReg();
                _step = 5;
                return pins;
            case 5:
                if (!InternalCycle(pins, 2)) return pins;
                return FinishInstruction(pins);
            default: throw new InvalidOperationException();
        }
    }

    /// <summary>LD SP,IX: 2 internal T-states, then SP = IX.</summary>
    private ulong ExecuteLdSpIx(ulong pins)
    {
        if (!InternalCycle(pins, 2)) return pins;
        _reg.SP = GetIndexReg();
        return FinishInstruction(pins);
    }

    // ---- DDCB/FDCB --------------------------------------------------------------

    /// <summary>DDCB/FDCB four-byte form: after the two M1s (DD/FD + CB), the
    /// displacement byte and the CB-table opcode are fetched via plain MR (not
    /// M1, so R does not increment for them). The CB operation then targets
    /// (IX+d)/(IY+d); for z≠6 the result is ALSO written into register z
    /// (undocumented dual-write, except for BIT which has no write-back).</summary>
    /// <summary>DDCB/FDCB four-byte form. Exact timing confirmed against
    /// dd cb __ 06.json (RLC, 23T) / dd cb __ 40.json (BIT, 20T) / dd cb __ 86.json (RES, 23T):
    /// step 0: MR(displacement d, 3T); step 1: MR(CB-table opcode, 3T);
    /// step 2: Internal(2T); step 3: MR(value at IX+d, 3T);
    /// step 4: Internal(1T) + operation; BIT finishes here (20T total);
    /// step 5 (rotate/RES/SET only): MW(3T) + optional dual-write to register z (z≠6).</summary>
    private ulong ExecuteDdCb(ulong pins)
    {
        switch (_step)
        {
            case 0: // MR: fetch displacement
                if (!MemRead(ref pins, _reg.PC, out _latchLo)) return pins;
                _reg.PC++;
                _displacement = (sbyte)_latchLo;
                _step = 1;
                return pins;

            case 1: // MR: fetch CB-table opcode (plain MR, not M1 — R does not increment)
                if (!MemRead(ref pins, _reg.PC, out _latchHi)) return pins;
                _reg.PC++;
                _addrLatch = (ushort)(GetIndexReg() + _displacement);
                _reg.WZ = _addrLatch;
                _step = 2;
                return pins;

            case 2: // Internal(2T): address calculation
                if (!InternalCycle(pins, 2)) return pins;
                _step = 3;
                return pins;

            case 3: // MR: read value at (IX+d)
                if (!MemRead(ref pins, _addrLatch, out _latchLo)) return pins;
                _step = 4;
                return pins;

            case 4: // Internal(1T): compute result; BIT finishes here
            {
                if (!InternalCycle(pins, 1)) return pins;
                var cbOp = _latchHi;
                var x = (cbOp >> 6) & 3;
                var y = (cbOp >> 3) & 7;
                var z = cbOp & 7;
                if (x == 1) // BIT n,(IX+d): 20T total, no write-back
                {
                    _reg.F = Alu.Bit(_latchLo, y, flagSource: (byte)(_reg.WZ >> 8), _reg.F);
                    _reg.Q = _reg.F;
                    return FinishInstruction(pins);
                }
                // Rotate/RES/SET: compute result, proceed to MW
                _latchLo = x switch
                {
                    0 => ApplyCbRotate(y, _latchLo),
                    2 => (byte)(_latchLo & ~(1 << y)),
                    3 => (byte)(_latchLo | (1 << y)),
                    _ => throw new InvalidOperationException(),
                };
                _step = 5;
                return pins;
            }

            case 5: // MW: write result to (IX+d); dual-write to register z when z≠6
            {
                var cbOp = _latchHi;
                var z = cbOp & 7;
                if (!MemWrite(ref pins, _addrLatch, _latchLo)) return pins;
                if (z != 6) Set8(z, _latchLo); // undocumented: result also stored in r[z]
                return FinishInstruction(pins);
            }

            default: throw new InvalidOperationException();
        }
    }
}

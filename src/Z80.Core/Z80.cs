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

    private enum Phase { Fetch, Execute, Interrupt }
    private Phase _phase;

    // ---- Interrupt / HALT state --------------------------------------------------

    /// <summary>True while the CPU is in the HALT loop. PC stays fixed; M1 fetches
    /// repeat at <c>_haltAddr</c> without incrementing PC.</summary>
    private bool _halted;
    private ushort _haltAddr;

    /// <summary>Edge-triggered NMI latch: set on a rising edge of the NMI pin,
    /// cleared when the NMI sequence begins.</summary>
    private bool _nmiPending;
    /// <summary>Previous-tick NMI pin level, used purely for edge detection.</summary>
    private bool _prevNmi;

    /// <summary>Which interrupt type is being serviced while <c>_phase == Interrupt</c>.</summary>
    private enum IntType { Nmi, Int }
    private IntType _intType;

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

    /// <summary>Which prefix page is currently active. Set when a prefix byte's
    /// M1 completes (instead of dispatching), so the *next* M1 fetch's result is
    /// decoded against that page's table. Stays set for the whole instruction
    /// (through Dispatch and any Execute-phase calls) and is only cleared by
    /// <see cref="FinishInstruction"/>, since RunExecute needs it on every call.</summary>
    private enum Prefix { None, CB, ED, DD, FD, DDCB, FDCB }
    private Prefix _prefix;

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
        _prefix = Prefix.None;
        _halted = false;
        _nmiPending = false;
        _prevNmi = false;
        Pins = 0;
    }

    /// <summary>True between instructions (just after one completed, just before the
    /// next M1 fetch begins). At this point <see cref="Registers.PC"/> holds the
    /// address of the instruction that is about to be fetched. The host may inspect
    /// or modify register state here without disrupting the execution stream.</summary>
    public bool AtInstructionBoundary =>
        _phase == Phase.Fetch && _tstate == 0 && _prefix == Prefix.None && !_halted;

    public ulong Step(ulong pins)
    {
        pins = _phase switch
        {
            Phase.Fetch     => RunFetch(pins),
            Phase.Execute   => RunExecute(pins),
            Phase.Interrupt => RunInterrupt(pins),
            _               => throw new InvalidOperationException($"Unknown phase {_phase}."),
        };
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
            case 0: // T1 — also the instruction-boundary where NMI/INT are sampled
                // NMI edge detection: latch on rising edge (0→1 of NMI pin).
                var nmiNow = (pins & PinBits.NMI) != 0;
                if (nmiNow && !_prevNmi) _nmiPending = true;
                _prevNmi = nmiNow;

                // --- Interrupt sampling at TRUE instruction boundaries only ---
                // A prefix byte (DD/FD/CB/ED) is NOT an instruction boundary: the real
                // Z80 defers both NMI and INT until the prefixed instruction completes.
                // _prefix == None means we are at a real boundary (no prefix pending).
                if (_prefix == Prefix.None)
                {
                    if (_nmiPending)
                    {
                        _nmiPending = false;
                        if (_halted) { _halted = false; pins &= ~PinBits.HALT; }
                        _intType = IntType.Nmi;
                        return EnterInterrupt(pins);
                    }
                    if ((pins & PinBits.INT) != 0 && _reg.IFF1 && !_reg.EiPending)
                    {
                        if (_halted) { _halted = false; pins &= ~PinBits.HALT; }
                        _intType = IntType.Int;
                        return EnterInterrupt(pins);
                    }
                }

                // --- HALT loop: re-fetch at _haltAddr without incrementing PC ---
                if (_halted)
                {
                    _fetchAddr = _haltAddr;
                    pins = PinBits.SetAddress(pins, _fetchAddr);
                    pins |= PinBits.M1 | PinBits.HALT;
                    pins &= ~(PinBits.MREQ | PinBits.RD | PinBits.RFSH);
                    _tstate = 1;
                    return pins;
                }

                // --- Normal M1 T1 ---
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

                // HALT loop: discard the opcode byte, keep HALT asserted, and loop
                // back for another M1 cycle without dispatching.
                if (_halted)
                {
                    pins |= PinBits.HALT;
                    return pins; // _phase remains Fetch; next Step() is T1 again
                }

                // Prefix-byte detection — applies whenever we're not yet inside a
                // terminal prefix (CB/ED only use their second byte as a real opcode,
                // never as another prefix; DD/FD can still chain or receive a new prefix):
                if (_prefix is not Prefix.CB and not Prefix.ED)
                {
                    if (_opcode == 0xDD || _opcode == 0xFD)
                    {
                        // DD/FD: "last wins" chaining. Each prefix byte is its own M1.
                        // Resets Q to 0 — the prefix M1 acts like a non-flag-modifying
                        // instruction for the SCF/CCF Q-quirk, confirmed against dd 37.json:
                        // when initial Q == initial F, DD resets Q to 0 so the subsequent
                        // SCF sees Q≠F and uses the (F|A) Y/X formula instead of A-only.
                        // EiPending/LastWasLdAIR also decay here since Dispatch() is
                        // bypassed for prefix bytes; for DDCB/FDCB it's the ONLY reset.
                        _prefix = _opcode == 0xDD ? Prefix.DD : Prefix.FD;
                        _reg.Q = 0;
                        _reg.EiPending = false;
                        _reg.LastWasLdAIR = false;
                        return pins;
                    }
                    if (_opcode == 0xCB)
                    {
                        _prefix = _prefix switch
                        {
                            Prefix.DD => Prefix.DDCB,
                            Prefix.FD => Prefix.FDCB,
                            _ => Prefix.CB,
                        };
                        _reg.Q = 0;
                        _reg.EiPending = false;
                        _reg.LastWasLdAIR = false;
                        if (_prefix is Prefix.DDCB or Prefix.FDCB)
                            return EnterExecute(pins); // fetch displacement via MR, not M1
                        return pins; // plain CB: continue to next M1
                    }
                    if (_opcode == 0xED)
                    {
                        _prefix = Prefix.ED;
                        _reg.Q = 0;
                        _reg.EiPending = false;
                        _reg.LastWasLdAIR = false;
                        return pins;
                    }
                }

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
        _prefix = Prefix.None;
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

    /// <summary>Begins the Interrupt phase and runs T1 of the NMI or INT-ack
    /// sequence immediately (so the caller's Step() sees T1 of the interrupt cycle,
    /// not a wasted instruction-boundary tick).</summary>
    private ulong EnterInterrupt(ulong pins)
    {
        _phase = Phase.Interrupt;
        _prefix = Prefix.None;
        _tstate = 0;
        _step = 0;
        return RunInterrupt(pins);
    }
}

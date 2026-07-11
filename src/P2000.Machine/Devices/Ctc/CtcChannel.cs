using P2000.Machine.Interrupts;
using P2000.Machine.State;

namespace P2000.Machine.Devices.Ctc;

/// <summary>
/// One Z80-CTC channel (reference doc §5d/§5e): control register, time-constant register,
/// down-counter, prescaler (timer mode only), and this channel's IM2 daisy-chain link. Internal
/// — only <see cref="Z80Ctc"/> constructs and drives channels; external code goes through the
/// chip's port-mapped surface (<see cref="Z80Ctc.WritePort"/>/<see cref="Z80Ctc.ReadPort"/>) and
/// <see cref="Z80Ctc.DaisyChainDevices"/>.
/// </summary>
internal sealed class CtcChannel : IDaisyChainDevice
{
    // Control-word bits (reference doc §5d): bit0 CTRLWRD, bit1 RESET, bit2 TCNEXT,
    // bit3 CLKSTRT, bit4 ACTTRG, bit5 PRE256, bit6 CNTMD, bit7 INTEN.
    private const byte CtrlWrd = 0x01;
    private const byte ResetBit = 0x02;
    private const byte TcNext = 0x04;
    private const byte ClkStartBit = 0x08;
    private const byte Pre256Bit = 0x20;
    private const byte CntMdBit = 0x40;
    private const byte IntEnBit = 0x80;

    private readonly Z80Ctc _chip;
    private readonly int _index;

    private byte _control;
    private bool _pendingTimeConstant;
    private byte _timeConstant = 1;
    private int _downCounter;
    private int _prescalerCounter;
    private bool _started;
    private bool _waitingForFirstEdge; // timer mode, CLKSTRT=1: gated until the first CLK/TRG edge
    private bool _softReset;

    public CtcChannel(Z80Ctc chip, int index)
    {
        _chip = chip;
        _index = index;
    }

    private bool ModeCounter => (_control & CntMdBit) != 0;
    private bool Prescale256 => (_control & Pre256Bit) != 0;
    private bool ClkStart => (_control & ClkStartBit) != 0;
    private bool InterruptEnabled => (_control & IntEnBit) != 0;

    /// <summary>Handles one byte written to this channel's port: a pending time-constant byte,
    /// an interrupt-vector byte (bit0=0, channel 0 only — reference doc §5e), or a control word
    /// (bit0=1).</summary>
    public void WritePort(byte value)
    {
        if (_pendingTimeConstant)
        {
            _pendingTimeConstant = false;
            _timeConstant = value;
            _downCounter = value == 0 ? 256 : value;
            _softReset = false;

            if (ModeCounter)
            {
                _started = true; // counts on delivered CLK/TRG edges from now on
                _waitingForFirstEdge = false;
            }
            else if (ClkStart)
            {
                _started = false;
                _waitingForFirstEdge = true; // timer starts on the next CLK/TRG edge
            }
            else
            {
                _started = true; // timer starts immediately
                _waitingForFirstEdge = false;
                _prescalerCounter = 0;
            }
            return;
        }

        if ((value & CtrlWrd) == 0)
        {
            // Interrupt-vector byte — only channel 0 accepts it (real Z80-CTC hardware); the
            // base is shared by all four channels via the chip (reference doc §5e).
            if (_index == 0) _chip.VectorBase = value;
            return;
        }

        _control = value;

        if ((value & ResetBit) != 0)
        {
            // Per-channel soft reset: halts counting until reprogrammed with a fresh TC.
            _softReset = true;
            _started = false;
            _waitingForFirstEdge = false;
            IntPending = false;
            return;
        }

        if ((value & TcNext) != 0)
            _pendingTimeConstant = true;
    }

    /// <summary>Live down-counter read-back. The ROM never reads a CTC channel (reference doc
    /// §5d confirms no disassembled <c>IN</c> on 0x88-0x8B), so this is a debugger nicety, not
    /// required for firmware correctness.</summary>
    public byte ReadPort() => (byte)_downCounter;

    /// <summary>Advances a timer-mode channel by one master-clock T-state (no-op for
    /// counter-mode channels, which decrement only via <see cref="ClkTrg"/>).</summary>
    public void Tick()
    {
        if (ModeCounter || !_started || _softReset) return;

        var prescaler = Prescale256 ? 256 : 16;
        _prescalerCounter++;
        if (_prescalerCounter < prescaler) return;
        _prescalerCounter = 0;
        DecrementAndFire();
    }

    /// <summary>Delivers one active CLK/TRG edge: decrements a counter-mode channel, or — for a
    /// timer-mode channel gated by CLKSTRT — releases the start gate.</summary>
    public void ClkTrg()
    {
        if (_softReset) return;

        if (ModeCounter)
        {
            if (_started) DecrementAndFire();
            return;
        }

        if (_waitingForFirstEdge)
        {
            _waitingForFirstEdge = false;
            _started = true;
            _prescalerCounter = 0;
        }
    }

    private void DecrementAndFire()
    {
        _downCounter--;
        if (_downCounter > 0) return;

        _downCounter = _timeConstant == 0 ? 256 : _timeConstant;
        ZcTo?.Invoke();
        if (InterruptEnabled) IntPending = true;
    }

    /// <summary>Fired when the down-counter reaches zero and reloads (ZC/TO pulse). Channel 3
    /// has no physical output pin on the real chip (reference doc §5e); the event is harmless
    /// if unused. A board wires it to the next channel's <see cref="ClkTrg"/> for cascading.</summary>
    public event Action? ZcTo;

    // ---- IDaisyChainDevice --------------------------------------------------------------

    public bool IntPending { get; private set; }
    public bool InService { get; private set; }

    public byte Acknowledge()
    {
        IntPending = false;
        InService = true;
        return (byte)((_chip.VectorBase & 0xF8) | (_index << 1));
    }

    public void ClearInService() => InService = false;

    // ---- State ----------------------------------------------------------------------------

    public void Reset()
    {
        _control = 0;
        _pendingTimeConstant = false;
        _timeConstant = 1;
        _downCounter = 0;
        _prescalerCounter = 0;
        _started = false;
        _waitingForFirstEdge = false;
        _softReset = false;
        IntPending = false;
        InService = false;
    }

    public void SaveState(IStateWriter w)
    {
        w.WriteByte(_control);
        w.WriteBool(_pendingTimeConstant);
        w.WriteByte(_timeConstant);
        w.WriteInt32(_downCounter);
        w.WriteInt32(_prescalerCounter);
        w.WriteBool(_started);
        w.WriteBool(_waitingForFirstEdge);
        w.WriteBool(_softReset);
        w.WriteBool(IntPending);
        w.WriteBool(InService);
    }

    public void LoadState(IStateReader r)
    {
        _control = r.ReadByte();
        _pendingTimeConstant = r.ReadBool();
        _timeConstant = r.ReadByte();
        _downCounter = r.ReadInt32();
        _prescalerCounter = r.ReadInt32();
        _started = r.ReadBool();
        _waitingForFirstEdge = r.ReadBool();
        _softReset = r.ReadBool();
        IntPending = r.ReadBool();
        InService = r.ReadBool();
    }
}

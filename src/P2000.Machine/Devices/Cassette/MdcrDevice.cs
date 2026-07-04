using P2000.Machine.Io;
using P2000.Machine.State;

namespace P2000.Machine.Devices.Cassette;

/// <summary>
/// MDCR (Mini Digital Cassette Recorder) device — I/O port 0x20 read / 0x10 write, upper
/// and lower 4 bits respectively (MDCR-implementation.md §1; project CLAUDE.md §7).
///
/// <b>Tick-driven phase engine:</b> accumulates master-clock cycles and processes one tape
/// phase per 209 cycles while the motor runs, exactly as the hardware's bit-rate clock does.
/// On the read path a phase-locked PLL recovers clock (RDC) and data (RDA) bits, which the
/// ROM's CLOAD routine polls from port 0x20. On the write path it stores the WDA level
/// phase-by-phase onto the tape.
///
/// <b>Reverse-direction bit mapping</b> is toggleable via <see cref="ReverseDataBitMapping"/>
/// because the owner flagged it unverified (MDCR-implementation.md §4). Default = current
/// behaviour (RDA flipped instead of RDC when reversing); set to false to flip only RDC.
///
/// <b>WEN active sense:</b> bit set = write-protected (matches the reference doc §5f (N) table
/// and the existing <c>CprinReader</c> convention). The MDCR-implementation.md §5 notes this
/// may disagree with the ROM — confirm once a CSAVE is observed and update the findings log.
///
/// CIP is a LIVE transition (machine CLAUDE.md §7): inserting/ejecting a tape at runtime flips
/// the bit immediately so the ROM's busy-wait loop sees it without a reset.
/// </summary>
public sealed class MdcrDevice : IDevice
{
    // Port 0x10 is shared (CPOUT); this device reads control bits from CPoutLatch.
    // Port 0x20 is shared (CPRIN); this device registers a read source for bits 3–7.
    public const byte StatusPort = 0x20;

    private const int CyclesPerPhase = 209;

    // Status bits returned on CPRIN 0x20 (upper 5 bits — printer owns bits 0–2)
    private const byte WenBit = 0x08; // write-protected: set = protected (doc §5f (N))
    private const byte CipBit = 0x10; // cassette-in-place: set = NO cassette (active-low)
    private const byte BetBit = 0x20; // begin/end-of-tape: set = tape OK, clear = at end
    private const byte RdcBit = 0x40; // read clock — toggles per recovered bit
    private const byte RdaBit = 0x80; // read data — level alongside RDC toggle

    private readonly CPoutLatch _cpOut;

    private MiniTape? _tape;
    private byte _status;
    private int _tickCount;

    // PLL state (MDCR-implementation.md §4)
    private bool _phaseLocked;
    private int _phaseCount;
    private bool _phaseOld;

    /// <summary>When true (default, owner-unverified) and the motor runs in REVERSE: toggles
    /// RDA instead of RDC per recovered bit (MDCR-implementation.md §4 reverse-direction
    /// branch). Set false once hardware confirms the correct mapping.</summary>
    public bool ReverseDataBitMapping { get; set; } = true;

    public MdcrDevice(CPoutLatch cpOut)
    {
        _cpOut = cpOut;
        UpdateStatusFromTape();
    }

    // ---- Host face ---------------------------------------------------------------

    public bool HasTape => _tape != null;

    /// <summary>Insert a loaded <c>.cas</c> image at runtime (CIP flips live — the ROM's
    /// busy-wait loop sees the cassette appear without a machine reset).</summary>
    public void InsertTape(byte[] casImage, bool writeProtect = true)
    {
        _tape = new MiniTape();
        _tape.LoadCasImage(casImage, writeProtect);
        ResetPll();
        UpdateStatusFromTape();
    }

    /// <summary>Eject the current cassette at runtime. CIP flips live.</summary>
    public void EjectTape()
    {
        _tape = null;
        ResetPll();
        UpdateStatusFromTape();
    }

    // ---- Bus face ----------------------------------------------------------------

    /// <summary>Returns the cassette status byte for port 0x20 (bits 3–7; bits 0–2 are
    /// printer-owned and are contributed by <c>CprinReader</c> via fan-out combine).</summary>
    public byte ReadStatus() => _status;

    // ---- Master-clock tick -------------------------------------------------------

    /// <summary>Advances the MDCR by one master-clock cycle. Call from
    /// <c>Machine.Tick()</c> every T-state.</summary>
    public void Tick(int cycles = 1)
    {
        if (_tape == null) return;
        if (!_cpOut.Forward && !_cpOut.Reverse) return; // motor stopped

        _tickCount += cycles;
        while (_tickCount >= CyclesPerPhase)
        {
            _tickCount -= CyclesPerPhase;
            ProcessPhase();
        }
    }

    // ---- IDevice -----------------------------------------------------------------

    public void Reset()
    {
        _tickCount = 0;
        ResetPll();
        // Preserve tape mount; only reset motor/clock/data bits in status.
        UpdateStatusFromTape();
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteByte(_status);
        writer.WriteInt32(_tickCount);
        writer.WriteBool(_phaseLocked);
        writer.WriteInt32(_phaseCount);
        writer.WriteBool(_phaseOld);
        // Tape position only — the .cas image must be remounted externally (same as ROM
        // files: not embedded in state snapshots).
        writer.WriteBool(_tape != null);
        if (_tape != null)
        {
            writer.WriteInt32(_tape.Position);
            writer.WriteInt32(_tape.Side);
        }
    }

    public void LoadState(IStateReader reader)
    {
        _status = reader.ReadByte();
        _tickCount = reader.ReadInt32();
        _phaseLocked = reader.ReadBool();
        _phaseCount = reader.ReadInt32();
        _phaseOld = reader.ReadBool();
        var hasTape = reader.ReadBool();
        if (hasTape)
        {
            var pos = reader.ReadInt32();
            var side = reader.ReadInt32();
            _tape?.SeekTo(pos, side);
        }
    }

    // ---- Private -----------------------------------------------------------------

    private void ProcessPhase()
    {
        if (_cpOut.WriteCommand)
        {
            // Write mode: record current WDA level to tape
            _tape!.Write(_cpOut.WriteData);
        }
        else
        {
            // Read mode: recover clock+data via PLL
            var phaseNew = _tape!.Read();
            ProcessPll(phaseNew, _cpOut.Reverse);
        }

        // Advance tape head
        if (_cpOut.Forward)
            _tape!.Forward();
        else
            _tape!.Reverse();

        // Update BET: clear when at either physical end
        if (_tape!.IsAtEnd)
            _status &= unchecked((byte)~BetBit);
        else
            _status |= BetBit;
    }

    private void ProcessPll(bool phaseNew, bool reverse)
    {
        if (!_phaseLocked)
        {
            if (phaseNew != _phaseOld)
            {
                _phaseLocked = true;
                _phaseCount = 0;
                BitToStatus(phaseNew, reverse);
            }
        }
        else
        {
            _phaseCount++;
            if (_phaseCount >= 2)
            {
                _phaseCount = 0;
                if (phaseNew != _phaseOld)
                    BitToStatus(phaseNew, reverse);
                else
                    _phaseLocked = false; // no transition = lock lost, resynchronise
            }
        }
        _phaseOld = phaseNew;
    }

    private void BitToStatus(bool phase, bool reverse)
    {
        if (reverse && ReverseDataBitMapping)
        {
            // Unverified reverse-direction branch (MDCR-implementation.md §4): toggle RDA
            // (data) instead of RDC (clock). Flag in findings once confirmed.
            _status ^= RdaBit;
            if (phase) _status |= RdcBit; else _status &= unchecked((byte)~RdcBit);
        }
        else
        {
            _status ^= RdcBit; // toggle clock on every recovered bit
            if (phase) _status |= RdaBit; else _status &= unchecked((byte)~RdaBit);
        }
    }

    private void ResetPll()
    {
        _phaseLocked = false;
        _phaseCount = 0;
        _phaseOld = false;
    }

    private void UpdateStatusFromTape()
    {
        // Preserve RDC/RDA across tape state updates (they track the PLL output, not the
        // tape presence).
        var rdBits = (byte)(_status & (RdcBit | RdaBit));
        _status = rdBits;

        if (_tape == null)
        {
            // No cassette: CIP active-low → bit SET. BET: set (no physical end). WEN: 0 (N/A).
            _status |= CipBit | BetBit;
        }
        else
        {
            // Cassette present: CIP clear.
            if (!_tape.IsAtEnd) _status |= BetBit;
            if (_tape.IsProtected) _status |= WenBit; // bit set = protected (doc §5f (N))
        }
    }
}

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
/// <b>WEN active sense:</b> bit set = write-protected — CONFIRMED correct from monitor-ROM
/// disassembly (<c>Symbols.asm</c> / <c>Cassette.asm:47</c>; findings log milestone 9).
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
    // Tracks whether the motor was running on the previous Tick to detect motor-start.
    private bool _motorWasRunning;

    /// <summary>Selects authentic phase-bitstream or turbo ROM-trap mode
    /// (MDCR-implementation.md §0). Default: <see cref="TimingPolicy.Authentic"/>. Turbo mode
    /// is implemented by <see cref="CassetteTurboTrap"/>, checked from <c>Machine.Tick()</c>
    /// (project CLAUDE.md §13.18).</summary>
    public TimingPolicy Policy { get; set; } = TimingPolicy.Authentic;

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

    /// <summary>True when a tape is mounted and its current side is write-protected. False
    /// (writable) when no tape is mounted — callers must check <see cref="HasTape"/>
    /// separately (matches <c>cas_writable</c>'s CIP-before-WEN check, Cassette.asm:1618).</summary>
    public bool IsWriteProtected => _tape?.IsProtected ?? false;

    /// <summary>Turbo-trap read: decodes one block at the tape's current head position and
    /// advances past it (project CLAUDE.md §13.18). Delegates to
    /// <see cref="MiniTape.TryReadBlockAtHead"/>; false when no tape is mounted or no block
    /// is found before the tape end.</summary>
    public bool TryReadBlockAtHead(out byte[] header, out byte[] data)
    {
        if (_tape == null)
        {
            header = Array.Empty<byte>();
            data = Array.Empty<byte>();
            return false;
        }
        return _tape.TryReadBlockAtHead(out header, out data);
    }

    /// <summary>Turbo-trap write: encodes one block at the tape's current head position and
    /// advances past it (project CLAUDE.md §13.18). Delegates to
    /// <see cref="MiniTape.WriteBlockAtHead"/>; false when no tape is mounted, the tape is
    /// write-protected, or the block would run past the physical end of the side.</summary>
    public bool WriteBlockAtHead(byte[] header, byte[] data) => _tape?.WriteBlockAtHead(header, data) ?? false;

    /// <summary>Decodes the mounted tape's phase bitstream back into a P2000T <c>.cas</c>
    /// image. Always instant regardless of <see cref="Policy"/> (host-side API —
    /// MDCR-implementation.md §8). Returns null if no tape is inserted or no valid blocks
    /// are found.</summary>
    public byte[]? SaveTape() => _tape?.Save();

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
        bool motorRunning = _cpOut.Forward || _cpOut.Reverse;
        if (!motorRunning)
        {
            if (_motorWasRunning)
            {
                // Real MDCR: when the motor stops at BOT/EOT the tape springs slightly off the
                // optical end-sensor, so BET returns to 1 before the ROM's post-motor-off cooldown
                // wait reads it.  Without this, cas_motor_off's 120 ms wait sees BET=0, triggers
                // cas_removedorEOT, sets cassette_error='E', and cas_Rewind returns NZ — the caller
                // (CLOAD/bootloader) aborts before ever starting the forward search.
                if (_tape != null && _tape.IsAtEnd)
                {
                    if (_tape.Position == 0)
                        _tape.Forward();  // BOT: spring forward one phase
                    else
                        _tape.Reverse();  // EOT: spring back one phase
                    _status |= BetBit;   // tape is no longer at the optical end-sensor
                }
            }
            _motorWasRunning = false;
            return;
        }
        if (Policy == TimingPolicy.Turbo) return;

        // One-shot eager phase when the motor JUST started from a physical end (BOT or EOT).
        // On real MDCR hardware the optical sensor clears within microseconds of the tape
        // moving — well before the ROM's first status read at ~105 T-states.  Without this,
        // BET stays 0 until the first normal phase fires at 209 T-states, causing cas_removedorEOT
        // to trip immediately with a spurious 'E' error.
        // Only at BOT (position 0): Forward() advances to 1 → BET=1 ✓.
        // At EOT (position max): Forward() is a no-op → BET stays 0 ✓ (tape IS at EOT).
        if (!_motorWasRunning && _tape.IsAtEnd)
            ProcessPhase(); // eager phase at BOT so BET clears before first ROM status read
        _motorWasRunning = true;

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
        _motorWasRunning = false;
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
                // phase0 (= _phaseOld) is the data bit; phase1 (= phaseNew) is its complement.
                // Lock fires at the phase0→phase1 mid-bit transition; sample phase0 for correct data.
                BitToStatus(_phaseOld, reverse);
            }
        }
        else
        {
            _phaseCount++;
            if (_phaseCount >= 2)
            {
                _phaseCount = 0;
                if (phaseNew != _phaseOld)
                    BitToStatus(_phaseOld, reverse); // sample phase0 (first half = data bit value)
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
            _status ^= RdaBit;
            if (phase) _status |= RdcBit; else _status &= unchecked((byte)~RdcBit);
        }
        else
        {
            _status ^= RdcBit;
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
            // No cassette: CIP active-low → bit SET. BET: set (no physical end).
            // WEN: SET — the write-protect pin is pulled high when no cassette is present
            // (just like a real MDCR). Without this, CIP=1 WEN=0 is the one invalid
            // combination cas_Init rejects, causing BASIC to abort CLOAD without starting
            // the motor at all.
            _status |= CipBit | BetBit | WenBit;
        }
        else
        {
            // Cassette present: CIP clear.
            if (!_tape.IsAtEnd) _status |= BetBit;
            if (_tape.IsProtected) _status |= WenBit; // bit set = protected (doc §5f (N))
        }
    }
}

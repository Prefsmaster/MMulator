using P2000.Machine.Devices.Cassette;
using P2000.Machine.State;

namespace P2000.Machine.Devices.Fdc;

/// <summary>
/// Standalone, board-agnostic µPD765 floppy disk controller (project CLAUDE.md §13 milestone
/// 19; reference doc §5d; format specifics <c>docs/JWSDOS-format.md</c>). Modelled like the
/// SAA5050/Z80-CTC: the chip has no opinion on which board it's mounted on. The OWNING BOARD
/// (<see cref="InternalExtensionBoard"/>) maps <see cref="ReadStatus"/>/<see cref="ReadData"/>/
/// <see cref="WriteData"/>/<see cref="ReadControl"/>/<see cref="WriteControl"/> onto ports
/// 0x8C/0x8D/0x90 and wires <see cref="ResultReady"/> to the CTC ch0 CLK/TRG input — the FDC
/// has NO direct CPU INT line (reference doc §5d).
///
/// Command subset is exactly what the ROM driver issues (reference doc §5d, confirmed exact
/// bytes) — SPECIFY, RECALIBRATE, SEEK, READ DATA, WRITE DATA, SENSE INTERRUPT STATUS. Match
/// dispatch on the opcode byte and standard µPD765 parameter-block positions, not a
/// reconstructed MT/MF/SK bit-flag theory of the opcode itself.
///
/// <b>Deliberate simplification (documented, not ROM-confirmed either way):</b> READ DATA/WRITE
/// DATA do not implement their own 7-byte result phase (ST0/ST1/ST2/C/H/R/N) — the ROM driver
/// never reads it (the FDC's result-phase INT redirects the polling loop's return address
/// instead, an ISR technique, not a protocol requirement per <c>docs/JWSDOS-format.md</c> §6).
/// <see cref="ResultReady"/> fires and the chip returns directly to <see cref="Phase.Idle"/>.
/// </summary>
public sealed class Upd765 : IDevice
{
    public const byte StatusPort = 0x8C;  // DSKIO1, IN — Main Status Register
    public const byte DataPort = 0x8D;    // DSKSTAT, IN/OUT — data register
    public const byte ControlPort = 0x90; // DSKCTRL — OUT: control latch, IN: semi-DMA byte-ready

    // DSKCTRL OUT bits (reference doc §5d).
    private const byte CtrlEnable = 0x01;
    private const byte CtrlTerminalCount = 0x02;
    private const byte CtrlReset = 0x04;
    private const byte CtrlMotor = 0x08;
    private const byte CtrlSelDis = 0x10;

    // MSR bits (this project's confirmed naming — bit7 RDY/RQM, bit6 DIO, bit4 FDC-busy).
    private const byte MsrRqm = 0x80;
    private const byte MsrDio = 0x40;
    private const byte MsrBusy = 0x10;

    /// <summary>Approximate seek-settle cost per track, honoured only under
    /// <see cref="TimingPolicy.Authentic"/> (Turbo completes seeks/transfers instantly — reference
    /// doc §5d "Two-level speed"). Not sourced from a datasheet SRT value; a reasonable,
    /// documented approximation since no test depends on the exact duration.</summary>
    private const int SeekTStatesPerTrack = 100;
    private const int HeadSettleTStates = 2000;

    /// <summary>Approximate per-byte semi-DMA transfer pacing under Authentic (reference doc
    /// §5d: MFM, ~250 kbit/s ≈ one byte per ~32 µs). Turbo makes every byte ready instantly.</summary>
    private const int ByteTransferTStates = 32;

    private enum Phase { Idle, CommandPhase, ExecutionPhase, ResultPhase }
    private enum PendingAction { None, SeekSettle, ByteReady }

    private static readonly Dictionary<byte, int> CommandLengths = new()
    {
        { 0x03, 3 }, // SPECIFY
        { 0x07, 2 }, // RECALIBRATE
        { 0x0F, 3 }, // SEEK
        { 0x42, 9 }, // READ DATA
        { 0x45, 9 }, // WRITE DATA
        { 0x08, 1 }, // SENSE INTERRUPT STATUS
    };

    public TimingPolicy Policy { get; set; } = TimingPolicy.Authentic;

    private readonly DskImage?[] _drives = new DskImage?[4];

    private Phase _phase = Phase.Idle;
    private readonly List<byte> _commandBuffer = new();
    private int _expectedLength;

    private readonly byte[] _resultBuffer = new byte[2];
    private int _resultLength;
    private int _resultIndex;

    private byte[] _transferBuffer = Array.Empty<byte>();
    private int _transferIndex;
    private bool _transferIsWrite;
    private int _transferCylinder;
    private int _transferHead;
    private int _transferDrive;
    private bool _byteReady;

    private PendingAction _pending = PendingAction.None;
    private int _delayCounter;
    private int _pendingCylinder;
    private int _pendingDrive;

    private bool _seekInterruptPending;
    private int _lastCompletedDrive;

    private readonly int[] _cylinder = new int[4];
    private int _selectedDrive;
    private bool _motorOn;
    private bool _enabled;

    /// <summary>Fires when a RECALIBRATE/SEEK settle completes or a READ/WRITE DATA transfer
    /// finishes — the board wires this to <c>Ctc.ClkTrg(0)</c> (the FDC has no direct CPU INT
    /// line, reference doc §5d). Not fired for SPECIFY (no interrupt) or SENSE INTERRUPT STATUS
    /// (which consumes a prior pending interrupt rather than creating one).</summary>
    public event Action? ResultReady;

    /// <summary>Mounts a disk image on the given drive (0-3; the ROM driver only ever
    /// addresses drive 1 per the confirmed command bytes, but the chip itself is drive-agnostic).</summary>
    public void MountDisk(int drive, DskImage image) => _drives[drive] = image;

    public void EjectDisk(int drive) => _drives[drive] = null;

    public DskImage? GetDisk(int drive) => _drives[drive];

    // ---- Port-facing surface (mapped by InternalExtensionBoard) --------------------------

    /// <summary>0x8C IN — Main Status Register. Idle/reset value is exactly 0x80 (the ROM's
    /// presence probe does a `CP 0x80` exact match, reference doc §5d) — not just bit 7 set.</summary>
    public byte ReadStatus()
    {
        return _phase switch
        {
            Phase.Idle => MsrRqm,
            Phase.CommandPhase => MsrRqm,
            Phase.ExecutionPhase => (byte)(MsrBusy
                | (_transferIsWrite ? 0x00 : MsrDio)
                | (_byteReady ? MsrRqm : 0x00)),
            Phase.ResultPhase => (byte)(MsrBusy | MsrDio | MsrRqm),
            _ => MsrRqm,
        };
    }

    /// <summary>0x8D IN — data register: the next transfer byte during a READ DATA execution
    /// phase, or the next SENSE INTERRUPT STATUS result byte.</summary>
    public byte ReadData()
    {
        if (_phase == Phase.ResultPhase)
        {
            var b = _resultBuffer[_resultIndex];
            _resultIndex++;
            if (_resultIndex >= _resultLength) _phase = Phase.Idle;
            return b;
        }

        if (_phase == Phase.ExecutionPhase && !_transferIsWrite && _byteReady)
        {
            var b = _transferBuffer[_transferIndex];
            _transferIndex++;
            _byteReady = false;
            if (_transferIndex >= _transferBuffer.Length)
            {
                CompleteTransfer();
            }
            else
            {
                StartByteDelay();
            }
            return b;
        }

        return PortDispatch_OpenBusLike;
    }

    private const byte PortDispatch_OpenBusLike = 0xFF;

    /// <summary>0x8D OUT — data register: a command byte (Idle/CommandPhase) or a WRITE DATA
    /// transfer byte (ExecutionPhase).</summary>
    public void WriteData(byte value)
    {
        if (_phase == Phase.Idle)
        {
            _commandBuffer.Clear();
            _commandBuffer.Add(value);
            if (!CommandLengths.TryGetValue(value, out _expectedLength))
            {
                // Unknown opcode — invalid command, standard µPD765 1-byte ST0=0x80 result.
                _resultBuffer[0] = 0x80;
                _resultLength = 1;
                _resultIndex = 0;
                _phase = Phase.ResultPhase;
                return;
            }
            _phase = _expectedLength == 1 ? Phase.Idle : Phase.CommandPhase;
            if (_expectedLength == 1) Dispatch();
            return;
        }

        if (_phase == Phase.CommandPhase)
        {
            _commandBuffer.Add(value);
            if (_commandBuffer.Count >= _expectedLength)
            {
                _phase = Phase.Idle;
                Dispatch();
            }
            return;
        }

        if (_phase == Phase.ExecutionPhase && _transferIsWrite && _byteReady)
        {
            _transferBuffer[_transferIndex] = value;
            _transferIndex++;
            _byteReady = false;
            if (_transferIndex >= _transferBuffer.Length)
            {
                CompleteTransfer();
            }
            else
            {
                StartByteDelay();
            }
        }
    }

    /// <summary>0x90 IN — semi-DMA per-byte poll target: bit0 set when a transfer byte is ready
    /// at <see cref="ReadData"/>/expected at <see cref="WriteData"/> (reference doc §5d — a
    /// genuinely separate register from the OUT-direction control latch below).</summary>
    public byte ReadControl() =>
        (byte)(_phase == Phase.ExecutionPhase && _byteReady ? 0x01 : 0x00);

    /// <summary>0x90 OUT — control latch: ENABLE/TC/RESET/MOTOR/SELDIS (reference doc §5d).</summary>
    public void WriteControl(byte value)
    {
        _enabled = (value & CtrlEnable) != 0;
        _motorOn = (value & CtrlMotor) != 0;

        if ((value & CtrlReset) != 0)
        {
            // Chip reset is synchronous — no settle delay of its own. The ROM's ~1.3 ms DJNZ
            // delay before probing MSR is a pure CPU busy-loop (reference doc §5d); the chip
            // must already read back as idle (0x80) the instant this returns.
            _phase = Phase.Idle;
            _commandBuffer.Clear();
            _pending = PendingAction.None;
            _delayCounter = 0;
            _byteReady = false;
            // Cylinder positions and seek-interrupt-pending state survive a controller reset —
            // real hardware's RESET does not rehome the heads.
        }

        if ((value & CtrlTerminalCount) != 0 && _phase == Phase.ExecutionPhase)
        {
            // Force-ends a transfer early (real µPD765 TC pin). Not exercised by the ROM's
            // fixed-EOT reads (which end naturally), but honoured for chip fidelity.
            CompleteTransfer();
        }
    }

    /// <summary>Advances master-clock-driven delays: seek/recalibrate settle time and
    /// per-byte semi-DMA pacing (both Authentic-only — Turbo completes synchronously at
    /// dispatch time, see <see cref="Dispatch"/>/<see cref="StartByteDelay"/>).</summary>
    public void Tick()
    {
        if (_delayCounter <= 0) return;
        _delayCounter--;
        if (_delayCounter > 0) return;

        switch (_pending)
        {
            case PendingAction.SeekSettle:
                _pending = PendingAction.None;
                _cylinder[_pendingDrive] = _pendingCylinder;
                _selectedDrive = _pendingDrive;
                _seekInterruptPending = true;
                _lastCompletedDrive = _pendingDrive;
                ResultReady?.Invoke();
                break;

            case PendingAction.ByteReady:
                _pending = PendingAction.None;
                _byteReady = true;
                break;
        }
    }

    // ---- Command dispatch -----------------------------------------------------------------

    private void Dispatch()
    {
        var opcode = _commandBuffer[0];
        switch (opcode)
        {
            case 0x03: DispatchSpecify(); break;
            case 0x07: DispatchRecalibrate(); break;
            case 0x0F: DispatchSeek(); break;
            case 0x42: DispatchReadWrite(isWrite: false); break;
            case 0x45: DispatchReadWrite(isWrite: true); break;
            case 0x08: DispatchSenseInterruptStatus(); break;
        }
    }

    private void DispatchSpecify()
    {
        // SRT/HUT (byte 1), HLT/ND (byte 2) — stored for a future more-precise seek-timing
        // model; not currently consulted (see SeekTStatesPerTrack). No interrupt, no result
        // phase, immediate.
        _phase = Phase.Idle;
    }

    private void DispatchRecalibrate()
    {
        var drive = _commandBuffer[1] & 0x03;
        BeginSeek(drive, targetCylinder: 0);
    }

    private void DispatchSeek()
    {
        var drive = _commandBuffer[1] & 0x03;
        var target = _commandBuffer[2];
        BeginSeek(drive, target);
    }

    private void BeginSeek(int drive, int targetCylinder)
    {
        _phase = Phase.ExecutionPhase;
        _transferIsWrite = false; // no byte transfer during a seek — MSR DIO bit is irrelevant here
        _pending = PendingAction.SeekSettle;
        _pendingDrive = drive;
        _pendingCylinder = targetCylinder;

        var distance = Math.Abs(targetCylinder - _cylinder[drive]);
        if (Policy == TimingPolicy.Turbo)
        {
            // Instant: apply immediately, no Tick()-driven delay.
            _pending = PendingAction.None;
            _phase = Phase.Idle;
            _cylinder[drive] = targetCylinder;
            _selectedDrive = drive;
            _seekInterruptPending = true;
            _lastCompletedDrive = drive;
            ResultReady?.Invoke();
        }
        else
        {
            _delayCounter = HeadSettleTStates + distance * SeekTStatesPerTrack;
            if (_delayCounter <= 0) _delayCounter = 1; // Tick() always needs >=1 to fire
        }
    }

    private void DispatchSenseInterruptStatus()
    {
        var drive = _seekInterruptPending ? _lastCompletedDrive : _selectedDrive;
        _resultBuffer[0] = _seekInterruptPending ? (byte)(0x20 | drive) : (byte)0x80; // ST0
        _resultBuffer[1] = (byte)_cylinder[drive]; // PCN
        _resultLength = 2;
        _resultIndex = 0;
        _seekInterruptPending = false;
        _phase = Phase.ResultPhase;
    }

    private void DispatchReadWrite(bool isWrite)
    {
        var drive = _commandBuffer[1] & 0x03;
        var cylinder = _commandBuffer[2];
        var head = _commandBuffer[3] & 0x01;
        var startSector = _commandBuffer[4];
        var sizeCode = _commandBuffer[5];
        var endOfTrack = _commandBuffer[6];

        var sectorSize = 128 << sizeCode;
        var sectorCount = Math.Max(1, endOfTrack - startSector + 1);
        var length = sectorCount * sectorSize;

        _transferIsWrite = isWrite;
        _transferCylinder = cylinder;
        _transferHead = head;
        _transferDrive = drive;
        _transferIndex = 0;
        _phase = Phase.ExecutionPhase;

        if (isWrite)
        {
            _transferBuffer = new byte[length];
        }
        else
        {
            var disk = _drives[drive];
            _transferBuffer = new byte[length];
            if (disk is not null && sectorSize == DskImage.BytesPerSector)
            {
                for (var s = 0; s < sectorCount; s++)
                {
                    disk.ReadSector(cylinder, head, startSector + s)
                        .CopyTo(_transferBuffer.AsSpan(s * sectorSize, sectorSize));
                }
            }
        }

        if (Policy == TimingPolicy.Turbo)
        {
            _byteReady = true;
        }
        else
        {
            StartByteDelay();
        }
    }

    private void StartByteDelay()
    {
        if (Policy == TimingPolicy.Turbo)
        {
            _byteReady = true;
            return;
        }
        _pending = PendingAction.ByteReady;
        _delayCounter = ByteTransferTStates;
    }

    private void CompleteTransfer()
    {
        if (_transferIsWrite)
        {
            var disk = _drives[_transferDrive];
            var sectorSize = DskImage.BytesPerSector;
            if (disk is not null && _transferBuffer.Length % sectorSize == 0)
            {
                var sectorCount = _transferBuffer.Length / sectorSize;
                for (var s = 0; s < sectorCount; s++)
                {
                    disk.WriteSector(_transferCylinder, _transferHead, 1 + s,
                        _transferBuffer.AsSpan(s * sectorSize, sectorSize));
                }
            }
        }

        _phase = Phase.Idle;
        _byteReady = false;
        _pending = PendingAction.None;
        ResultReady?.Invoke();
    }

    // ---- IDevice ----------------------------------------------------------------------------

    public void Reset()
    {
        _phase = Phase.Idle;
        _commandBuffer.Clear();
        _expectedLength = 0;
        _resultLength = 0;
        _resultIndex = 0;
        _transferBuffer = Array.Empty<byte>();
        _transferIndex = 0;
        _transferIsWrite = false;
        _byteReady = false;
        _pending = PendingAction.None;
        _delayCounter = 0;
        _seekInterruptPending = false;
        _lastCompletedDrive = 0;
        Array.Clear(_cylinder);
        _selectedDrive = 0;
        _motorOn = false;
        _enabled = false;
    }

    public void SaveState(IStateWriter w)
    {
        w.WriteByte((byte)_phase);
        w.WriteInt32(_commandBuffer.Count);
        foreach (var b in _commandBuffer) w.WriteByte(b);
        w.WriteInt32(_expectedLength);

        w.WriteByte(_resultBuffer[0]);
        w.WriteByte(_resultBuffer[1]);
        w.WriteInt32(_resultLength);
        w.WriteInt32(_resultIndex);

        w.WriteInt32(_transferBuffer.Length);
        w.WriteBytes(_transferBuffer);
        w.WriteInt32(_transferIndex);
        w.WriteBool(_transferIsWrite);
        w.WriteInt32(_transferCylinder);
        w.WriteInt32(_transferHead);
        w.WriteInt32(_transferDrive);
        w.WriteBool(_byteReady);

        w.WriteByte((byte)_pending);
        w.WriteInt32(_delayCounter);
        w.WriteInt32(_pendingCylinder);
        w.WriteInt32(_pendingDrive);

        w.WriteBool(_seekInterruptPending);
        w.WriteInt32(_lastCompletedDrive);

        for (var i = 0; i < _cylinder.Length; i++) w.WriteInt32(_cylinder[i]);
        w.WriteInt32(_selectedDrive);
        w.WriteBool(_motorOn);
        w.WriteBool(_enabled);
    }

    public void LoadState(IStateReader r)
    {
        _phase = (Phase)r.ReadByte();
        var cmdCount = r.ReadInt32();
        _commandBuffer.Clear();
        for (var i = 0; i < cmdCount; i++) _commandBuffer.Add(r.ReadByte());
        _expectedLength = r.ReadInt32();

        _resultBuffer[0] = r.ReadByte();
        _resultBuffer[1] = r.ReadByte();
        _resultLength = r.ReadInt32();
        _resultIndex = r.ReadInt32();

        var transferLength = r.ReadInt32();
        _transferBuffer = new byte[transferLength];
        r.ReadBytes(_transferBuffer);
        _transferIndex = r.ReadInt32();
        _transferIsWrite = r.ReadBool();
        _transferCylinder = r.ReadInt32();
        _transferHead = r.ReadInt32();
        _transferDrive = r.ReadInt32();
        _byteReady = r.ReadBool();

        _pending = (PendingAction)r.ReadByte();
        _delayCounter = r.ReadInt32();
        _pendingCylinder = r.ReadInt32();
        _pendingDrive = r.ReadInt32();

        _seekInterruptPending = r.ReadBool();
        _lastCompletedDrive = r.ReadInt32();

        for (var i = 0; i < _cylinder.Length; i++) _cylinder[i] = r.ReadInt32();
        _selectedDrive = r.ReadInt32();
        _motorOn = r.ReadBool();
        _enabled = r.ReadBool();
    }
}

using P2000.Machine.Devices.Cassette;
using P2000.Machine.State;

namespace P2000.Machine.Devices.Fdc;

/// <summary>
/// Standalone, board-agnostic µPD765 floppy disk controller (project CLAUDE.md §13 milestones
/// 19 and 19a; reference doc §5d; <c>docs/FDC-implementation.md</c> for the full device guide).
/// Modelled like the SAA5050/Z80-CTC: the chip has no opinion on which board it's mounted on.
/// The OWNING BOARD (<see cref="InternalExtensionBoard"/>) maps <see cref="ReadStatus"/>/
/// <see cref="ReadData"/>/<see cref="WriteData"/>/<see cref="ReadControl"/>/
/// <see cref="WriteControl"/> onto ports 0x8C/0x8D/0x90 and wires <see cref="ResultReady"/> to
/// the CTC ch0 CLK/TRG input — the FDC has NO direct CPU INT line (reference doc §5d).
///
/// <b>Full 15-command µPD765/8272A set</b> (milestone 19a — chip fidelity for its own sake, the
/// same way <c>Z80.Core</c> targets the whole instruction set rather than just what one ROM
/// uses). Dispatch keys on the command byte's masked base opcode (bits 4-0; <see cref="OpcodeMask"/>
/// strips MT/MF/SK), not a literal per-caller byte — this generalizes milestone 19's "match on
/// real confirmed bytes" rule to the full command space, since 6 of the 15 (7 counting Sense
/// Drive Status, 8 counting Format A Track) are real, confirmed ROM/JWSDOS/JWSFormat usage and
/// the remaining 7 have no known real P2000 caller (docs/FDC-implementation.md §2/§4).
///
/// <b>Opcode-identity finding (project CLAUDE.md §17, 2026-07-24):</b> the byte milestone 19
/// confirmed from the ROM disassembly and labelled "READ DATA" — <c>0x42</c> — does NOT decode
/// to READ DATA's official base opcode (<c>0x06</c>). WRITE DATA's own confirmed real byte
/// (<c>0x45 = 0x05|0x40</c>) already proves the MF bit (bit 6) is set platform-wide, and
/// <c>0x42</c> can only equal <c>0x02|0x40</c> (READ A TRACK's base, per the datasheet's own
/// numbering) — never <c>0x06|0x40 = 0x46</c>. So the ROM's real read command is READ A TRACK,
/// not READ DATA: it necessarily ignores R and always starts at sector 1 (right after the index
/// pulse) rather than searching for R. This is behaviourally invisible in every known real usage
/// (R is always 1 there), so nothing that worked before changes — but a genuine, separate READ
/// DATA (<c>0x06</c>) now exists as one of the 7 synthetic-only commands, since no real P2000
/// software has been found to issue it.
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

    /// <summary>Which of the 15 commands is driving the current execution-phase byte loop —
    /// needed because the loop's shape (direction, per-byte meaning, completion behaviour)
    /// differs per command (docs/FDC-implementation.md §6).</summary>
    private enum TransferKind
    {
        None,
        ReadData,
        WriteData,
        ReadDeletedData,
        WriteDeletedData,
        ReadTrack,
        Format,
        ScanEqual,
        ScanLowOrEqual,
        ScanHighOrEqual,
    }

    // Base opcodes (bits 4-0 once MT/MF/SK are masked off) — NEC µPD765/8272A datasheet, cross-
    // checked against MAME's check_command() (docs/FDC-implementation.md §0/§4).
    private const byte OpReadTrack = 0x02;
    private const byte OpSpecify = 0x03;
    private const byte OpSenseDriveStatus = 0x04;
    private const byte OpWriteData = 0x05;
    private const byte OpReadData = 0x06;
    private const byte OpRecalibrate = 0x07;
    private const byte OpSenseInterruptStatus = 0x08;
    private const byte OpWriteDeletedData = 0x09;
    private const byte OpReadId = 0x0A;
    private const byte OpReadDeletedData = 0x0C;
    private const byte OpFormatATrack = 0x0D;
    private const byte OpSeek = 0x0F;
    private const byte OpScanEqual = 0x11;
    private const byte OpScanLowOrEqual = 0x19;
    private const byte OpScanHighOrEqual = 0x1D;

    /// <summary>Strips MT(bit7)/MF(bit6)/SK(bit5) to isolate the 5-bit base command code — every
    /// real base opcode in the 15-command set fits in bits 4-0 (docs/FDC-implementation.md §4).</summary>
    private const byte OpcodeMask = 0x1F;

    private static readonly Dictionary<byte, int> CommandLengths = new()
    {
        { OpReadTrack, 9 },
        { OpSpecify, 3 },
        { OpSenseDriveStatus, 2 },
        { OpWriteData, 9 },
        { OpReadData, 9 },
        { OpRecalibrate, 2 },
        { OpSenseInterruptStatus, 1 },
        { OpWriteDeletedData, 9 },
        { OpReadId, 2 },
        { OpReadDeletedData, 9 },
        { OpFormatATrack, 6 },
        { OpSeek, 3 },
        { OpScanEqual, 9 },
        { OpScanLowOrEqual, 9 },
        { OpScanHighOrEqual, 9 },
    };

    private const int FormatBytesPerSectorGroup = 4; // host feeds (C,H,R,N) per formatted sector

    public TimingPolicy Policy { get; set; } = TimingPolicy.Authentic;

    private readonly DskImage?[] _drives = new DskImage?[4];

    private Phase _phase = Phase.Idle;
    private readonly List<byte> _commandBuffer = new();
    private int _expectedLength;

    private readonly byte[] _resultBuffer = new byte[7]; // ST0,ST1,ST2,C,H,R,N — the widest shape
    private int _resultLength;
    private int _resultIndex;

    private TransferKind _transferKind = TransferKind.None;
    private byte[] _transferBuffer = Array.Empty<byte>();
    private int _transferIndex;
    private bool _transferIsWrite;
    private int _transferCylinder;
    private int _transferHead;
    private int _transferDrive;
    private int _transferStartSector;
    private int _transferSectorSize;
    private bool _byteReady;

    // FORMAT A TRACK-only execution state (docs/FDC-implementation.md §2/§5/§6).
    private byte _formatFillByte;
    private int _formatSectorSize;

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

    /// <summary>Fires when a RECALIBRATE/SEEK settle completes or a data-shaped command's
    /// transfer finishes — the board wires this to <c>Ctc.ClkTrg(0)</c> (the FDC has no direct
    /// CPU INT line, reference doc §5d). Not fired for SPECIFY (no interrupt) or SENSE
    /// INTERRUPT STATUS/SENSE DRIVE STATUS/READ ID (which complete via an immediate result
    /// phase rather than an execution-phase transfer).</summary>
    public event Action? ResultReady;

    /// <summary>Mounts a disk image on the given drive (0-3; the ROM driver only ever
    /// addresses drive 1 per the confirmed command bytes, but the chip itself is drive-agnostic).</summary>
    public void MountDisk(int drive, DskImage image) => _drives[drive] = image;

    public void EjectDisk(int drive) => _drives[drive] = null;

    public DskImage? GetDisk(int drive) => _drives[drive];

    // ---- Host status surface (P2000.UI milestone 14 — disk drive window) ------------------

    /// <summary>The board's single shared MOTORON line (reference doc §5d, project CLAUDE.md
    /// §13.20's M2200-manual finding) — NOT per-drive; every configured drive's status row
    /// reflects this SAME bit.</summary>
    public bool MotorOn => _motorOn;

    /// <summary>The given drive's own tracked head position (0-3; out-of-range drives read 0,
    /// same as a drive that's never been sought). Survives across commands — real hardware's
    /// RESET does not rehome the heads (see <see cref="WriteControl"/>'s doc comment).</summary>
    public int GetCylinder(int drive) => _cylinder[drive];

    /// <summary>Snapshot of a semi-DMA transfer in progress — the current status display's
    /// activity/direction/head/sector source. <c>null</c> when idle or between command phases
    /// (not consulted by the chip's own command dispatch, host-status-only). <see cref="Sector"/>
    /// is the REAL current sector (project CLAUDE.md §17, 2026-07-23 owner decision) — derived
    /// from the command's own starting sector (R) plus how many bytes have moved through the
    /// semi-DMA byte-loop so far, not just echoing R for the whole transfer; exposing state the
    /// chip already implicitly tracks, not new state. <c>Head</c>/<c>Sector</c> are only
    /// meaningful during an actual data-shaped transfer — a SEEK/RECALIBRATE settle is also
    /// <see cref="Phase.ExecutionPhase"/> but has no head/sector of its own; it reads whatever
    /// the LAST real transfer's values were (a known, accepted cosmetic imprecision, not a data
    /// hazard — nothing in the chip's own dispatch consults this struct).</summary>
    public readonly record struct TransferStatus(int Drive, int Head, bool IsWrite, int Sector);

    public TransferStatus? CurrentTransfer => _phase == Phase.ExecutionPhase
        ? new TransferStatus(_transferDrive, _transferHead, _transferIsWrite, CurrentSector())
        : null;

    private int CurrentSector() =>
        _transferSectorSize > 0
            ? _transferStartSector + _transferIndex / _transferSectorSize
            : _transferStartSector;

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

    /// <summary>0x8D IN — data register: the next transfer byte during a read-shaped execution
    /// phase, or the next result-phase byte.</summary>
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

    /// <summary>0x8D OUT — data register: a command byte (Idle/CommandPhase) or a write-shaped
    /// (WRITE DATA/WRITE DELETED DATA/FORMAT A TRACK/SCAN*) transfer byte (ExecutionPhase).</summary>
    public void WriteData(byte value)
    {
        if (_phase == Phase.Idle)
        {
            _commandBuffer.Clear();
            _commandBuffer.Add(value);
            var baseOpcode = (byte)(value & OpcodeMask);
            if (!CommandLengths.TryGetValue(baseOpcode, out _expectedLength))
            {
                // Unknown opcode — invalid command, standard µPD765 1-byte ST0=0x80 result.
                SetResult(0x80);
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

        if ((value & CtrlReset) != 0 && _phase == Phase.Idle)
        {
            // Chip reset is synchronous — no settle delay of its own. The ROM's ~1.3 ms DJNZ
            // delay before probing MSR is a pure CPU busy-loop (reference doc §5d); the chip
            // must already read back as idle (0x80) the instant this returns.
            //
            // ONLY takes effect from Idle — confirmed necessary against the real ROM driver
            // (docs/Monitor Documented Disassembly/Disk.asm), which writes this same RESET bit
            // WHILE a command is still active in TWO separate confirmed places, and both are
            // ordinary, working code, not an error path:
            //   - `read_status_bytes` writes RESET|MOTOR (0x0C) WHILE a SENSE INTERRUPT STATUS
            //     result phase is still pending readout (`read_dsk_status`'s `out (DSKSTAT),
            //     0x08` just before) — if RESET discarded it, those bytes would never be valid.
            //   - `read_track` writes RESET|MOTOR|ENABLE (0x0D) to ARM the semi-DMA transfer
            //     immediately AFTER `disk_send_command` already dispatched the 9-byte READ DATA
            //     command (putting the chip in ExecutionPhase) — if RESET aborted that transfer,
            //     the subsequent `wait_next_trk_byte` busy-poll would find MSR idle forever
            ///    (reproduced live: this exact sequence hung a boot test at that poll loop).
            // Two independent confirmed sites writing RESET mid-command, in real working code,
            // is strong enough evidence that this bit simply has no effect once a command is
            // already in flight — it's the presence-probe/initial-power-on reset only.
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

    /// <summary>Advances master-clock-driven delays: seek/recalibrate settle time and per-byte
    /// semi-DMA pacing. Authentic honours a realistic delay for both. Turbo still defers
    /// SEEK/RECALIBRATE completion by <see cref="MinimumTurboSeekTStates"/> — NOT
    /// synchronous with the command dispatch, see that constant's doc comment for why (a real,
    /// found-via-boot-hang bug). Per-byte semi-DMA readiness stays synchronous under Turbo
    /// (<see cref="StartByteDelay"/>) since the ROM only ever busy-polls for it, never waits on
    /// it via an interrupt/HALT — no equivalent lost-wakeup risk there.</summary>
    public void Tick()
    {
        if (_delayCounter <= 0) return;
        _delayCounter--;
        if (_delayCounter > 0) return;

        switch (_pending)
        {
            case PendingAction.SeekSettle:
                _pending = PendingAction.None;
                _phase = Phase.Idle; // was missing — the seek's ExecutionPhase never actually
                                     // ended on this (Tick()-driven) completion path, leaving
                                     // MSR permanently reporting busy after a completed seek.
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
        var baseOpcode = (byte)(opcode & OpcodeMask);
        switch (baseOpcode)
        {
            case OpSpecify: DispatchSpecify(); break;
            case OpSenseDriveStatus: DispatchSenseDriveStatus(); break;
            case OpRecalibrate: DispatchRecalibrate(); break;
            case OpSeek: DispatchSeek(); break;
            case OpSenseInterruptStatus: DispatchSenseInterruptStatus(); break;
            case OpReadId: DispatchReadId(); break;
            case OpFormatATrack: DispatchFormat(); break;
            case OpReadData: DispatchDataCommand(TransferKind.ReadData); break;
            case OpWriteData: DispatchDataCommand(TransferKind.WriteData); break;
            case OpReadDeletedData: DispatchDataCommand(TransferKind.ReadDeletedData); break;
            case OpWriteDeletedData: DispatchDataCommand(TransferKind.WriteDeletedData); break;
            case OpReadTrack: DispatchDataCommand(TransferKind.ReadTrack); break;
            case OpScanEqual: DispatchDataCommand(TransferKind.ScanEqual); break;
            case OpScanLowOrEqual: DispatchDataCommand(TransferKind.ScanLowOrEqual); break;
            case OpScanHighOrEqual: DispatchDataCommand(TransferKind.ScanHighOrEqual); break;
        }
    }

    private void DispatchSpecify()
    {
        // SRT/HUT (byte 1), HLT/ND (byte 2) — stored for a future more-precise seek-timing
        // model; not currently consulted (see SeekTStatesPerTrack). No interrupt, no result
        // phase, immediate.
        _phase = Phase.Idle;
    }

    /// <summary>SENSE DRIVE STATUS (0x04) — confirmed real usage, TWO independent callers
    /// (JWSDOS's <c>check_write_enable</c> and JWSFormat's <c>check_write_protect</c>, both
    /// `02 04 &lt;drive&gt;` and both testing ST3 bit 6 — docs/FDC-implementation.md §2).</summary>
    private void DispatchSenseDriveStatus()
    {
        var driveHeadByte = _commandBuffer[1];
        var drive = driveHeadByte & 0x03;
        var head = (driveHeadByte >> 2) & 0x01;
        var disk = _drives[drive];

        byte st3 = 0;
        if (disk is not null)
        {
            if (disk.WriteProtected) st3 |= 0x40; // WP — confirmed real usage, §2
            st3 |= 0x20; // RY — a mounted disk is ready; no separate "spinning up" model
            if (_cylinder[drive] == 0) st3 |= 0x10; // T0
            if (disk.Sides == 2) st3 |= 0x08; // TS — two side
        }
        st3 |= (byte)((head & 0x01) << 2); // HD
        st3 |= (byte)(driveHeadByte & 0x03); // US1/US0 echoed back

        SetResult(st3);
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

    /// <summary>Minimum settle delay under Turbo, in T-states. NOT zero — real bug, found via
    /// a live boot-test hang and fixed here: the ROM always sends the SEEK/RECALIBRATE command
    /// bytes and THEN executes an explicit `halt` a few instructions later, expecting to be
    /// woken by ITS OWN completion interrupt (Disk.asm `disk_recall`/`disk_do_search`). If the
    /// chip fires <see cref="ResultReady"/> SYNCHRONOUSLY inside the command dispatch (i.e.
    /// before the ROM ever reaches that `halt`), the interrupt is accepted and fully serviced
    /// (IM2 vector → ISR → RETI) at the very next instruction boundary — often still inside
    /// `disk_send_command`'s own loop — and is gone by the time `halt` actually executes. A
    /// real one-shot, level-cleared-on-Acknowledge interrupt that fires and is consumed BEFORE
    /// the intended waiter ever looks is a lost wakeup: the CPU halts forever waiting for an
    /// event that already happened. This constant only needs to safely outlast the handful of
    /// T-states between command dispatch and the ROM's own `halt` (a RET + a few fetches,
    /// nowhere near this many) — it is not meant to model a real seek time.</summary>
    private const int MinimumTurboSeekTStates = 200;

    private void BeginSeek(int drive, int targetCylinder)
    {
        _phase = Phase.ExecutionPhase;
        _transferIsWrite = false; // no byte transfer during a seek — MSR DIO bit is irrelevant here
        // Real bug, fixed 2026-07-23: _transferDrive previously wasn't updated here, so
        // CurrentTransfer.Drive reported whichever drive last did a data-shaped transfer
        // (or 0, if none ever had) during a seek on a DIFFERENT drive — the host status
        // surface (P2000.UI milestone 14) would light up the wrong drive's activity indicator.
        _transferDrive = drive;
        _pending = PendingAction.SeekSettle;
        _pendingDrive = drive;
        _pendingCylinder = targetCylinder;

        var distance = Math.Abs(targetCylinder - _cylinder[drive]);
        if (Policy == TimingPolicy.Turbo)
        {
            _delayCounter = MinimumTurboSeekTStates;
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
        var st0 = _seekInterruptPending ? (byte)(0x20 | drive) : (byte)0x80; // ST0
        var pcn = (byte)_cylinder[drive];
        _seekInterruptPending = false;
        SetResult(st0, pcn);
    }

    /// <summary>READ ID (0x0A) — no execution phase (§5/§6 of the FDC guide: it just latches the
    /// next ID address mark's C/H/R/N). This project has no separate per-sector ID-field model
    /// (sectors are addressed directly, not scanned from a bitstream — same reasoning as Format
    /// A Track's don't-care CHRN, docs/FDC-implementation.md §2), so the most faithful stand-in
    /// is the drive's current physical position, sector 1, and this platform's one real sector
    /// size (N=1, 256 B) — "the next ID field encountered" on a freshly-seeked, unread track.</summary>
    private void DispatchReadId()
    {
        var driveHeadByte = _commandBuffer[1];
        var drive = driveHeadByte & 0x03;
        var head = (byte)((driveHeadByte >> 2) & 0x01);
        var cylinder = (byte)_cylinder[drive];
        SetResult(0x00, 0x00, 0x00, cylinder, head, 0x01, 0x01);
    }

    /// <summary>FORMAT A TRACK (0x0D) — confirmed real bytes and execution mechanism
    /// (JWSFormat.bin/jwsformat.asm, docs/FDC-implementation.md §2): 6-byte command phase
    /// (cmd,HD/US,N,SC,GPL,D), then the host feeds 4 bytes (C,H,R,N) per sector, SC times,
    /// through the SAME semi-DMA byte-poll mechanism WRITE DATA already uses.</summary>
    private void DispatchFormat()
    {
        var driveHeadByte = _commandBuffer[1];
        var drive = driveHeadByte & 0x03;
        var head = (driveHeadByte >> 2) & 0x01;
        var sizeCode = _commandBuffer[2]; // N
        var sectorsPerCylinder = _commandBuffer[3]; // SC
        // _commandBuffer[4] is GPL — not consulted (gap timing has no effect on this project's
        // sector-addressed DskImage model).
        _formatFillByte = _commandBuffer[5]; // D
        _formatSectorSize = 128 << sizeCode;

        _transferKind = TransferKind.Format;
        _transferCylinder = _cylinder[drive];
        _transferHead = head;
        _transferDrive = drive;
        _transferStartSector = 1;
        _transferSectorSize = FormatBytesPerSectorGroup; // host-status cosmetic only — see CurrentSector()
        _transferIndex = 0;
        _transferIsWrite = true;
        _phase = Phase.ExecutionPhase;
        _transferBuffer = new byte[Math.Max(1, (int)sectorsPerCylinder) * FormatBytesPerSectorGroup];

        if (Policy == TimingPolicy.Turbo) _byteReady = true; else StartByteDelay();
    }

    private static bool IsHostToFdd(TransferKind kind) => kind is TransferKind.WriteData
        or TransferKind.WriteDeletedData or TransferKind.ScanEqual or TransferKind.ScanLowOrEqual
        or TransferKind.ScanHighOrEqual;

    /// <summary>Shared dispatcher for the 7 commands sharing the standard 9-byte
    /// cmd,HD/US,C,H,R,N,EOT,GPL,DTL/STP command shape (READ/WRITE DATA, READ/WRITE DELETED
    /// DATA, READ A TRACK, SCAN EQUAL/LOW/HIGH).</summary>
    private void DispatchDataCommand(TransferKind kind)
    {
        var drive = _commandBuffer[1] & 0x03;
        // The command's OWN cylinder byte is NOT used for addressing — confirmed against the
        // real ROM driver (docs/Monitor Documented Disassembly/Disk.asm `getdos`/`read_track`):
        // the command template is copied to RAM ONCE and its cylinder field is never updated
        // between the two DOS-track reads (both send the identical hardcoded byte), while the
        // ACTUAL cylinder read differs (track 1 vs track 2) purely because a separate SEEK
        // command physically repositioned the head in between. Real µPD765 hardware reads/
        // writes at wherever the head physically IS (tracked via prior SEEK/RECALIBRATE) — the
        // command's C field is for ID-field verification against the medium, not addressing.
        // This emulator has no separate per-sector ID-field model, so the most faithful
        // equivalent is: address the mounted image using the FDC's own internally-tracked
        // <see cref="_cylinder"/>, not the command byte.
        var cylinder = _cylinder[drive];
        var head = _commandBuffer[3] & 0x01;
        var startSectorField = _commandBuffer[4];
        var sizeCode = _commandBuffer[5];
        var endOfTrack = _commandBuffer[6];

        var sectorSize = 128 << sizeCode;
        // READ A TRACK ignores R and always starts right after the index pulse — sector 1 in
        // this project's fixed sector-per-track layout (this class's own doc comment, "opcode-
        // identity finding," §17 2026-07-24).
        var startSector = kind == TransferKind.ReadTrack ? 1 : startSectorField;
        var sectorCount = Math.Max(1, endOfTrack - startSector + 1);
        var length = sectorCount * sectorSize;

        _transferKind = kind;
        _transferCylinder = cylinder;
        _transferHead = head;
        _transferDrive = drive;
        _transferStartSector = startSector;
        _transferSectorSize = sectorSize;
        _transferIndex = 0;
        _phase = Phase.ExecutionPhase;

        var hostToFdd = IsHostToFdd(kind);
        _transferIsWrite = hostToFdd;
        _transferBuffer = new byte[length];

        if (!hostToFdd)
        {
            var disk = _drives[drive];
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

    // ---- Execution-phase completion (docs/FDC-implementation.md §6 step 3 — a real result
    // phase for every command, backfilled retroactively onto Read/Write Data too) -------------

    private void CompleteTransfer()
    {
        _byteReady = false;
        _pending = PendingAction.None;

        byte st0 = 0x00, st1 = 0x00, st2 = 0x00;
        byte c, h, r, n;

        switch (_transferKind)
        {
            case TransferKind.WriteData:
                CommitSectorWrites();
                (c, h, r, n) = LastSectorResultFields();
                break;

            case TransferKind.WriteDeletedData:
                // Documented simplification (docs/FDC-implementation.md §4/§6): this DskImage
                // model has no separate deleted-DAM marker, so a "deleted data" write is stored
                // exactly like a normal write — content correctness matters, DAM-type tracking
                // has no representation in this raw sector-storage model.
                CommitSectorWrites();
                (c, h, r, n) = LastSectorResultFields();
                break;

            case TransferKind.ReadData:
            case TransferKind.ReadTrack:
                (c, h, r, n) = LastSectorResultFields();
                break;

            case TransferKind.ReadDeletedData:
                // No sector in this model is EVER marked "deleted" — every sector the chip
                // encounters is normal-marked, which is exactly the mismatch condition the real
                // datasheet calls Control Mark (ST2 bit 6) + abnormal termination (ST0 IC=01).
                st0 |= 0x40;
                st2 |= 0x40;
                (c, h, r, n) = LastSectorResultFields();
                break;

            case TransferKind.Format:
                CommitFormat();
                c = 0; h = 0; r = 0; n = 0; // don't-care per datasheet (MAME just echoes the
                                            // last N) — docs/FDC-implementation.md §5.
                break;

            case TransferKind.ScanEqual:
            case TransferKind.ScanLowOrEqual:
            case TransferKind.ScanHighOrEqual:
                (st1, st2) = CompleteScan(_transferKind);
                (c, h, r, n) = LastSectorResultFields();
                break;

            default:
                (c, h, r, n) = LastSectorResultFields();
                break;
        }

        SetResult(st0, st1, st2, c, h, r, n);
        ResultReady?.Invoke();
    }

    private (byte c, byte h, byte r, byte n) LastSectorResultFields()
    {
        var lastSector = _transferSectorSize > 0
            ? _transferStartSector + _transferBuffer.Length / _transferSectorSize - 1
            : _transferStartSector;
        return ((byte)_transferCylinder, (byte)_transferHead, (byte)lastSector,
            SizeCodeFor(_transferSectorSize));
    }

    private static byte SizeCodeFor(int sectorSize)
    {
        byte n = 0;
        var size = 128;
        while (size < sectorSize) { size <<= 1; n++; }
        return n;
    }

    private void CommitSectorWrites()
    {
        var disk = _drives[_transferDrive];
        var sectorSize = DskImage.BytesPerSector;
        if (disk is not null && _transferSectorSize == sectorSize && _transferBuffer.Length % sectorSize == 0)
        {
            var sectorCount = _transferBuffer.Length / sectorSize;
            for (var s = 0; s < sectorCount; s++)
            {
                disk.WriteSector(_transferCylinder, _transferHead, _transferStartSector + s,
                    _transferBuffer.AsSpan(s * sectorSize, sectorSize));
            }
        }
    }

    /// <summary>Formats the SC sectors of the currently-seeked (cylinder,head) with the fill
    /// byte D, in host-supplied R order — per docs/FDC-implementation.md §2's recommendation, no
    /// ID-mark bookkeeping needed since this project's DskImage addresses sectors directly. Each
    /// 4-byte group the host fed is (C,H,R,N); C/N are not used for addressing (jwsformat.asm's
    /// own Cylinder byte is a confirmed off-by-one from the real physical track, project
    /// CLAUDE.md §17 2026-07-24) — only H/R select where the fill byte lands.</summary>
    private void CommitFormat()
    {
        var disk = _drives[_transferDrive];
        if (disk is null || _formatSectorSize != DskImage.BytesPerSector) return;

        var fill = new byte[DskImage.BytesPerSector];
        Array.Fill(fill, _formatFillByte);

        for (var offset = 0; offset + FormatBytesPerSectorGroup <= _transferBuffer.Length; offset += FormatBytesPerSectorGroup)
        {
            var h = _transferBuffer[offset + 1] & 0x01;
            var r = _transferBuffer[offset + 2];
            disk.WriteSector(_transferCylinder, h, r, fill);
        }
    }

    /// <summary>SCAN EQUAL/LOW-OR-EQUAL/HIGH-OR-EQUAL — byte-by-byte compare of the host-fed
    /// bytes (already accumulated in <see cref="_transferBuffer"/> like a write) against the
    /// mounted disk's actual sector content. SH/SN semantics per docs/FDC-implementation.md §5
    /// (cross-checked against MAME's compare loop): SH starts SET, clears on the first mismatch
    /// and stays clear. SN starts CLEAR; each mismatch sets it UNLESS that specific byte pair
    /// satisfies the variant's inequality (disk&lt;host for Low, disk&gt;host for High), in which
    /// case SN clears again — so the final SN reflects the LAST mismatch's outcome, not a sticky
    /// OR across the whole scan (this is how the datasheet's per-event wording reads literally;
    /// no real caller exists to confirm either way).</summary>
    private (byte st1, byte st2) CompleteScan(TransferKind kind)
    {
        var disk = _drives[_transferDrive];
        var sectorSize = DskImage.BytesPerSector;

        if (disk is null || _transferSectorSize != sectorSize || _transferBuffer.Length % sectorSize != 0)
        {
            return (0x04, 0x08); // ST1 ND (no data — nothing to compare against), ST2 SH still set (vacuously)
        }

        var sectorCount = _transferBuffer.Length / sectorSize;
        var sh = true;
        var sn = false;

        for (var s = 0; s < sectorCount; s++)
        {
            var diskBytes = disk.ReadSector(_transferCylinder, _transferHead, _transferStartSector + s);
            var hostBytes = _transferBuffer.AsSpan(s * sectorSize, sectorSize);
            for (var i = 0; i < sectorSize; i++)
            {
                var diskByte = diskBytes[i];
                var hostByte = hostBytes[i];
                if (diskByte == hostByte) continue;

                sh = false;
                var conditionSatisfied = kind switch
                {
                    TransferKind.ScanLowOrEqual => diskByte < hostByte,
                    TransferKind.ScanHighOrEqual => diskByte > hostByte,
                    _ => false, // ScanEqual has no inequality condition
                };
                sn = !conditionSatisfied;
            }
        }

        byte st2 = 0;
        if (sh) st2 |= 0x08; // SH
        if (sn) st2 |= 0x04; // SN
        return (0x00, st2);
    }

    private void SetResult(params byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++) _resultBuffer[i] = bytes[i];
        _resultLength = bytes.Length;
        _resultIndex = 0;
        _phase = Phase.ResultPhase;
    }

    // ---- IDevice ----------------------------------------------------------------------------

    public void Reset()
    {
        _phase = Phase.Idle;
        _commandBuffer.Clear();
        _expectedLength = 0;
        _resultLength = 0;
        _resultIndex = 0;
        _transferKind = TransferKind.None;
        _transferBuffer = Array.Empty<byte>();
        _transferIndex = 0;
        _transferIsWrite = false;
        _transferStartSector = 0;
        _transferSectorSize = 0;
        _formatFillByte = 0;
        _formatSectorSize = 0;
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

        foreach (var b in _resultBuffer) w.WriteByte(b);
        w.WriteInt32(_resultLength);
        w.WriteInt32(_resultIndex);

        w.WriteByte((byte)_transferKind);
        w.WriteInt32(_transferBuffer.Length);
        w.WriteBytes(_transferBuffer);
        w.WriteInt32(_transferIndex);
        w.WriteBool(_transferIsWrite);
        w.WriteInt32(_transferCylinder);
        w.WriteInt32(_transferHead);
        w.WriteInt32(_transferDrive);
        w.WriteInt32(_transferStartSector);
        w.WriteInt32(_transferSectorSize);
        w.WriteBool(_byteReady);
        w.WriteByte(_formatFillByte);
        w.WriteInt32(_formatSectorSize);

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

        for (var i = 0; i < _resultBuffer.Length; i++) _resultBuffer[i] = r.ReadByte();
        _resultLength = r.ReadInt32();
        _resultIndex = r.ReadInt32();

        _transferKind = (TransferKind)r.ReadByte();
        var transferLength = r.ReadInt32();
        _transferBuffer = new byte[transferLength];
        r.ReadBytes(_transferBuffer);
        _transferIndex = r.ReadInt32();
        _transferIsWrite = r.ReadBool();
        _transferCylinder = r.ReadInt32();
        _transferHead = r.ReadInt32();
        _transferDrive = r.ReadInt32();
        _transferStartSector = r.ReadInt32();
        _transferSectorSize = r.ReadInt32();
        _byteReady = r.ReadBool();
        _formatFillByte = r.ReadByte();
        _formatSectorSize = r.ReadInt32();

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

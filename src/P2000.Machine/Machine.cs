using System.Collections.Concurrent;
using P2000.Machine.Contention;
using P2000.Machine.Debug;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Interrupts;
using P2000.Machine.Io;
using P2000.Machine.Memory;
using P2000.Machine.Slots;
using P2000.Machine.State;
using Z80.Core;

namespace P2000.Machine;

/// <summary>
/// Assembles a <see cref="Z80.Core.Z80"/> CPU plus memory and I/O devices into a
/// cycle-exact, bus-accurate P2000T/M and owns the deterministic emulation loop (project
/// CLAUDE.md §3). <see cref="Tick"/> steps the core one T-state at a time and services
/// whatever bus request it made this tick: MREQ against the <see cref="Memory"/> page
/// table, IORQ against the <see cref="Ports"/> dispatch.
/// </summary>
public sealed class Machine
{
    public MachineConfig Config { get; }

    public Z80.Core.Z80 Cpu { get; } = new();

    public PageTable Memory { get; }

    public PortDispatch Ports { get; } = new();

    public CPoutLatch CpOut { get; } = new();

    public CprinReader CpIn { get; } = new();

    /// <summary>SAA5050 + fetch timing (T only - the M's display is a separate, deferred
    /// device; project CLAUDE.md §1/§14).</summary>
    public Video Video { get; }

    /// <summary>10×8 keyboard matrix (reference doc §5f, project CLAUDE.md §7). Bus face:
    /// port reads on 0x00–0x09 through the port dispatch. Host face: <see cref="KeyboardDevice.SetKey"/>
    /// called at field boundaries (observer rule, root CLAUDE.md).</summary>
    public KeyboardDevice Keyboard { get; }

    /// <summary>MDCR (cassette) device (project CLAUDE.md §7, MDCR-implementation.md).
    /// Bus face: status on port 0x20 (bits 3–7), control from port 0x10 via CPoutLatch.
    /// Host face: <see cref="MdcrDevice.InsertTape"/>/<see cref="MdcrDevice.EjectTape"/>
    /// at runtime (CIP is a live transition — machine CLAUDE.md §7).</summary>
    public MdcrDevice Mdcr { get; }

    /// <summary>1-bit beeper synthesizer (project CLAUDE.md §7 Sound). Monitors CPOUT bit 4
    /// (assumed BEEP line — see §17 finding), synthesizes 44100 Hz PCM blocks at each 50 Hz
    /// field boundary, and exposes them via <see cref="SoundDevice.SamplesReady"/>.</summary>
    public SoundDevice Sound { get; }

    /// <summary>Wired-OR INT/NMI aggregator (project CLAUDE.md §8). The video 50 Hz VBLANK
    /// is the only registered INT source for the T-first build. NMI seam is present but no
    /// source fires yet (front-panel soft-reset and SLOT1 pin 1A are wired later).</summary>
    public InterruptAggregator Interrupts { get; } = new();

    /// <summary>SLOT1 cartridge (0x1000–0x4FFF, reference doc §5c), or <c>null</c> when the
    /// machine is bare (cassette-wait boot). Constructed from
    /// <see cref="MachineConfig.Slot1CartridgePath"/>; null when that path is not set.</summary>
    public IMemorySlot? Slot1 { get; }

    /// <summary>SLOT2 expansion card slot (I/O-mapped, reference doc §5c). Always
    /// <c>null</c> in the T-first build — SLOT2 cards are deferred (project CLAUDE.md §14).
    /// The <see cref="IIoSlot"/> seam is ready for future expansion.</summary>
    public IIoSlot? Slot2 => null;

    /// <summary>Machine-owned breakpoint store (project CLAUDE.md §3b.2). Evaluated inside
    /// <see cref="Tick"/> behind a zero-cost <see cref="BreakpointStore.AnyArmed"/> fast path;
    /// a hit raises <see cref="BreakHit"/> at the next instruction boundary and sets
    /// <see cref="IsPaused"/>.</summary>
    public BreakpointStore Breakpoints { get; } = new();

    /// <summary>Raised at an instruction boundary when a breakpoint fires. The handler runs
    /// synchronously inside <see cref="Tick"/>; the machine is already paused
    /// (<see cref="IsPaused"/> set) before the event fires. Call <see cref="Resume"/> to
    /// continue execution.</summary>
    public event Action<BreakEvent>? BreakHit;

    /// <summary>True when the machine is paused at a breakpoint hit. While paused,
    /// <see cref="Tick"/> returns immediately without advancing anything. Call
    /// <see cref="Resume"/> to re-enable ticking.</summary>
    public bool IsPaused { get; private set; }

    private bool _breakPending;
    private BreakEvent _pendingBreak;

    // ── Command queue (project CLAUDE.md §3b.3, milestone 15) ──────────────────────────

    /// <summary>Raised when a <see cref="MemoryWriteCommand"/> or
    /// <see cref="LoadImageCommand"/> is applied. A mid-run memory mutation breaks
    /// cycle-exact replay for this session (project CLAUDE.md §3b.3).</summary>
    public event Action? NonReplayableAction;

    private readonly ConcurrentQueue<MachineCommand> _commandQueue = new();

    // Transient stepping / run-to state (set by drain; evaluated at next boundary).
    private bool _pauseAtNextBoundary;
    private int _runToFieldTState = -1;

    // ID of the temporary exec breakpoint planted by StepOver/StepOut; -1 = none.
    private int _tempBpId = -1;

    private ulong _pins;

    public Machine(MachineConfig? config = null)
    {
        Config = config ?? new MachineConfig();

        // Construct SLOT1 cartridge (if configured) before building PageTable so it
        // can route 0x1000–0x4FFF reads through the typed IMemorySlot interface.
        Slot1 = Config.Slot1CartridgePath is not null
            ? new Slot1Cartridge(Config.Slot1CartridgePath)
            : null;

        Memory = new PageTable(Config, Slot1);
        Video = new Video(Memory);
        Keyboard = new KeyboardDevice(CpOut);
        Mdcr = new MdcrDevice(CpOut);
        Sound = new SoundDevice(CpOut, () => Video.FieldTState);

        Ports.RegisterWrite(CPoutLatch.Port, CpOut.Write);
        Ports.RegisterRead(CprinReader.Port, CpIn.Read);
        Ports.RegisterRead(CprinReader.Port, Mdcr.ReadStatus);
        Ports.RegisterWrite(PageTable.BankSelectPort, Memory.SelectBank);

        // Keyboard: ports 0x00-0x09 (reference doc §5f). Each port needs its own closure
        // capturing the port index so the keyboard knows which row is being read.
        for (byte port = 0; port <= 9; port++)
        {
            var p = port;
            Ports.RegisterRead(p, () => Keyboard.ReadPort(p));
        }

        // Wire the 50 Hz video VBLANK → INT (project CLAUDE.md §8: T-first INT source).
        Video.FieldComplete += Interrupts.RaiseInt;
        // Wire the 50 Hz field boundary → Sound sample-block synthesis.
        Video.FieldComplete += Sound.OnFieldComplete;

        Cpu.Reset();
    }

    /// <summary>
    /// Queues a command to be applied at the next instruction boundary
    /// (project CLAUDE.md §3b.3, milestone 15). Thread-safe: may be called from any
    /// thread; the command is applied on the emulation thread inside <see cref="Tick"/>.
    /// </summary>
    public void Enqueue(MachineCommand command) => _commandQueue.Enqueue(command);

    /// <summary>
    /// Returns a read-only snapshot of the machine's current state (project CLAUDE.md §3b.1,
    /// milestone 13). The snapshot captures the full register file, all flag bits broken out,
    /// the in-frame T-state/cycle position, and a live memory-read delegate.
    ///
    /// <b>Must only be called at an instruction boundary</b>
    /// (<see cref="Z80.Core.Z80.AtInstructionBoundary"/> is true): the same safe point
    /// <see cref="SaveState"/> relies on. Throws <see cref="InvalidOperationException"/>
    /// if called mid-instruction, to prevent silently inconsistent snapshots.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the CPU is not at an
    /// instruction boundary (mid-instruction or halted).</exception>
    public MachineSnapshot TakeSnapshot()
    {
        if (!Cpu.AtInstructionBoundary)
            throw new InvalidOperationException(
                "TakeSnapshot must be called at an instruction boundary " +
                "(Z80.AtInstructionBoundary must be true). " +
                "Drive Tick() until AtInstructionBoundary before calling.");

        return new MachineSnapshot(Cpu.Reg, Video.FieldTState, Memory.Read);
    }

    /// <summary>Rebuilds the machine to its cold-reset state (locked decision §2.3:
    /// topology is fixed once running; only a reset re-applies it).</summary>
    public void Reset()
    {
        Cpu.Reset();
        CpOut.Reset();
        CpIn.Reset();
        Video.Reset();
        Interrupts.Reset();
        Keyboard.Reset();
        Mdcr.Reset();
        Sound.Reset();
        Slot1?.Reset();
        _pins = 0;
        _breakPending = false;
        _pauseAtNextBoundary = false;
        _runToFieldTState = -1;
        // If a step-over/step-out temp bp is in the store, remove it.
        if (_tempBpId != -1)
        {
            Breakpoints.Remove(_tempBpId);
            _tempBpId = -1;
        }
        IsPaused = false;
    }

    /// <summary>Clears the <see cref="IsPaused"/> flag set by a breakpoint hit so
    /// <see cref="Tick"/> resumes advancing the machine. Does NOT clear the breakpoint
    /// store — the same breakpoints remain armed and will fire again on the next match.
    /// Call <see cref="Breakpoints"/>.<see cref="BreakpointStore.Remove"/> or
    /// <see cref="BreakpointStore.Clear"/> first if you want to remove them.</summary>
    public void Resume()
    {
        IsPaused = false;
        _breakPending = false;
    }

    /// <summary>Advances the whole machine by exactly one T-state (project CLAUDE.md §3):
    /// tick the video fetch unit, assert the INT pin if pending, step the CPU, then service
    /// whatever bus request it made this tick against the page table (MREQ) or the port
    /// dispatch (IORQ); detect the int-ack cycle (M1+IORQ) and acknowledge the aggregator.
    ///
    /// Returns immediately without advancing anything when <see cref="IsPaused"/> is true.
    /// A breakpoint hit sets <see cref="IsPaused"/> and raises <see cref="BreakHit"/> before
    /// returning (project CLAUDE.md §3b.2).</summary>
    public void Tick()
    {
        if (Cpu.AtInstructionBoundary)
        {
            // A–D: Check conditions set by PREVIOUS drains. Skipped while paused so we
            // never double-fire; the drain (step E) still runs so RunCommand can unset IsPaused.
            if (!IsPaused)
            {
                // A. Pause-at-next-boundary: set by a prior SingleStep/StepOver-non-call drain.
                //    Checked here (before the current drain) so the command that set it lands
                //    on the NEXT boundary, not the same one where the drain ran.
                if (_pauseAtNextBoundary)
                {
                    _pauseAtNextBoundary = false;
                    IsPaused = true;
                    return;
                }

                // B. Run-to-cycle / run-to-scanline (project CLAUDE.md §3b.3).
                if (_runToFieldTState >= 0 && Video.FieldTState >= _runToFieldTState)
                {
                    _runToFieldTState = -1;
                    IsPaused = true;
                    return;
                }

                // C. Deferred mid-instruction breakpoint (project CLAUDE.md §3b.2).
                if (Breakpoints.AnyArmed && _breakPending)
                {
                    var ev = _pendingBreak;
                    _breakPending = false;
                    IsPaused = true;
                    BreakHit?.Invoke(ev);
                    return;
                }

                // D. Exec breakpoint — includes temporary bps planted by StepOver/StepOut.
                if (Breakpoints.AnyArmed)
                {
                    var execHit = Breakpoints.CheckExec(Cpu.Reg.PC);
                    if (execHit.HasValue)
                    {
                        // Temp bp fired: remove it so it cannot re-fire on the next hit.
                        if (_tempBpId != -1 && execHit.Value.BreakpointId == _tempBpId)
                        {
                            Breakpoints.Remove(_tempBpId);
                            _tempBpId = -1;
                        }
                        IsPaused = true;
                        BreakHit?.Invoke(execHit.Value);
                        return;
                    }
                }
            }

            // E. Drain the command queue (project CLAUDE.md §3b.3, milestone 15).
            //    Runs even while paused so RunCommand / WarmReset etc. take effect when
            //    the runner calls Tick on a paused machine. Conditions set HERE
            //    (_pauseAtNextBoundary, _runToFieldTState) are evaluated on the NEXT
            //    boundary (checks A–D above are already past for this tick).
            DrainCommandQueue();
        }

        if (IsPaused) return;

        Video.Tick();

        // Drive the INT pin level into the pin word before the CPU samples it at the
        // next instruction boundary (project CLAUDE.md §8, Z80.Core §4 host-loop shape).
        if (Interrupts.IntPending)
            _pins |= Pins.INT;
        else
            _pins &= ~Pins.INT;

        // NMI: edge-triggered — assert for exactly one T-state so the Z80 core latches
        // a single 0→1 rising edge per request (Z80.Core Interrupts.cs _prevNmi tracking).
        // No NMI source fires in the T-first build; the seam is ready (project CLAUDE.md §8).
        if (Interrupts.NmiPending)
        {
            _pins |= Pins.NMI;
            Interrupts.ClearNmi(); // consumed — next tick returns pin to low
        }
        else
        {
            _pins &= ~Pins.NMI;
        }

        _pins = Cpu.Step(_pins);

        if ((_pins & Pins.MREQ) != 0)
        {
            if ((_pins & Pins.RD) != 0)
            {
                var addr = Pins.GetAddress(_pins);
                _pins = Pins.SetData(_pins, Memory.Read(addr));
                if (Breakpoints.AnyArmed && !_breakPending)
                {
                    var hit = Breakpoints.CheckMemRead(addr);
                    if (hit.HasValue) { _pendingBreak = hit.Value; _breakPending = true; }
                }
            }
            else if ((_pins & Pins.WR) != 0)
            {
                var addr = Pins.GetAddress(_pins);
                Memory.Write(addr, Pins.GetData(_pins));
                if (Breakpoints.AnyArmed && !_breakPending)
                {
                    var hit = Breakpoints.CheckMemWrite(addr);
                    if (hit.HasValue) { _pendingBreak = hit.Value; _breakPending = true; }
                }
            }
        }
        else if ((_pins & Pins.IORQ) != 0)
        {
            // Reference doc §5c: the P2000T decodes only A0-A7 for I/O (8-bit port space).
            var port = (byte)Pins.GetAddress(_pins);

            if ((_pins & Pins.M1) != 0)
            {
                // INT-ack cycle: M1+IORQ asserted together (Z80.Core §5 "INT ack" template).
                // The aggregator clears its pending latch and returns the vector byte; for IM1
                // the CPU ignores the byte (uses the fixed 0x0038 vector) but we drive it anyway
                // so the bus looks correct and a future IM2 source can supply a real vector.
                // Int-ack is not a user I/O access — no IO breakpoint check here.
                _pins = Pins.SetData(_pins, Interrupts.Acknowledge());
            }
            else if ((_pins & Pins.RD) != 0)
            {
                _pins = Pins.SetData(_pins, Ports.Read(port));
                if (Breakpoints.AnyArmed && !_breakPending)
                {
                    var hit = Breakpoints.CheckIoRead(port);
                    if (hit.HasValue) { _pendingBreak = hit.Value; _breakPending = true; }
                }
            }
            else if ((_pins & Pins.WR) != 0)
            {
                Ports.Write(port, Pins.GetData(_pins));
                if (Breakpoints.AnyArmed && !_breakPending)
                {
                    var hit = Breakpoints.CheckIoWrite(port);
                    if (hit.HasValue) { _pendingBreak = hit.Value; _breakPending = true; }
                }
            }
        }

        // Step 4: contention — Z80 always wins (reference doc §4, project CLAUDE.md §10).
        // Only VRAM (0x5000–0x57FF T / 0x5000–0x5FFF M) is shared with the SAA5020 fetch
        // unit. Base RAM, expansion RAM, and the banked window are separate chips — MREQ to
        // those addresses cannot collide with a display fetch. Default corruption: blanked.
        if ((_pins & Pins.MREQ) != 0 && Memory.IsVideoRamAddress(Pins.GetAddress(_pins)))
            Video.CorruptLastFetch();

        // Advance master-clock devices (cassette bit engine; later CTC — machine CLAUDE.md §3 step 5).
        Mdcr.Tick(1);
    }

    // ── Command queue drain (project CLAUDE.md §3b.3, milestone 15) ──────────────────────

    /// <summary>Drains all currently-queued commands, applying each in FIFO order.
    /// Called from <see cref="Tick"/> at every instruction boundary.
    /// Returns as soon as IsPaused is set (Pause/Reset consumed), so the caller can
    /// exit the tick immediately.</summary>
    private void DrainCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            switch (cmd)
            {
                case RunCommand:
                    // Symmetric with direct Resume() — both clear the paused state.
                    IsPaused = false;
                    _breakPending = false;
                    break;

                case PauseCommand:
                    IsPaused = true;
                    break; // continue draining; subsequent commands still apply

                case WarmResetCommand:
                    Reset(); // CPU + devices; RAM preserved
                    _commandQueue.Clear(); // drop pre-reset commands
                    return;

                case ColdResetCommand:
                    Reset();
                    Memory.ClearRam();
                    _commandQueue.Clear();
                    return;

                case SingleStepCommand:
                    // Pause after the instruction that is about to execute (at the next
                    // boundary).  Setting the flag here means the CURRENT boundary is
                    // skipped (we execute one instruction) and the NEXT boundary pauses.
                    _pauseAtNextBoundary = true;
                    break;

                case StepOverCommand:
                    ApplyStepOver();
                    break;

                case StepOutCommand:
                    ApplyStepOut();
                    break;

                case RunToCycleCommand runTo:
                    _runToFieldTState = runTo.FieldTState;
                    break;

                case RunToScanlineCommand runToLine:
                    _runToFieldTState = runToLine.Line * VideoFetchUnit.TStatesPerLine;
                    break;

                case SetPcCommand setPc:
                    Cpu.Reg.PC = setPc.Address;
                    break;

                case MemoryWriteCommand memWrite:
                    Memory.Write(memWrite.Address, memWrite.Value);
                    NonReplayableAction?.Invoke();
                    break;

                case LoadImageCommand load:
                    for (var i = 0; i < load.Data.Length; i++)
                        Memory.Write((ushort)(load.StartAddress + i), load.Data[i]);
                    NonReplayableAction?.Invoke();
                    break;

                case AddExecBreakpointCommand addExec:
                    Breakpoints.AddExec(addExec.Address);
                    break;
                case AddMemReadBreakpointCommand addMr:
                    Breakpoints.AddMemRead(addMr.Address);
                    break;
                case AddMemWriteBreakpointCommand addMw:
                    Breakpoints.AddMemWrite(addMw.Address);
                    break;
                case AddMemAccessBreakpointCommand addMa:
                    Breakpoints.AddMemAccess(addMa.Address);
                    break;
                case AddIoReadBreakpointCommand addIr:
                    Breakpoints.AddIoRead(addIr.Port);
                    break;
                case AddIoWriteBreakpointCommand addIw:
                    Breakpoints.AddIoWrite(addIw.Port);
                    break;
                case RemoveBreakpointCommand removeBp:
                    Breakpoints.Remove(removeBp.Id);
                    break;
                case ClearBreakpointsCommand:
                    Breakpoints.Clear();
                    break;
            }
        }
    }

    /// <summary>Implements <see cref="StepOverCommand"/>: if the instruction at PC is
    /// a CALL/RST/DJNZ/block instruction, plants a temporary exec breakpoint at the
    /// return site (PC + instruction length) and lets the machine run; otherwise
    /// falls back to single-step.</summary>
    private void ApplyStepOver()
    {
        var pc = Cpu.Reg.PC;
        var len = GetCallLikeLength(pc);
        if (len > 0)
        {
            _tempBpId = Breakpoints.AddExec((ushort)(pc + len));
        }
        else
        {
            _pauseAtNextBoundary = true;
        }
    }

    /// <summary>Implements <see cref="StepOutCommand"/>: reads the return address from
    /// [SP]|[SP+1]&lt;&lt;8 and plants a temporary exec breakpoint there.</summary>
    private void ApplyStepOut()
    {
        var sp = Cpu.Reg.SP;
        var lo = Memory.Read(sp);
        var hi = Memory.Read((ushort)(sp + 1));
        _tempBpId = Breakpoints.AddExec((ushort)(lo | (hi << 8)));
    }

    /// <summary>Returns the byte length of the instruction at <paramref name="pc"/> if it
    /// is a call-like opcode that step-over should skip (CALL, RST, DJNZ, block instructions);
    /// otherwise returns -1 (caller falls back to single-step).</summary>
    private int GetCallLikeLength(ushort pc)
    {
        var opcode = Memory.Read(pc);
        return opcode switch
        {
            0xCD => 3,   // CALL nn
            // CALL cc,nn (all eight conditional forms)
            0xC4 or 0xCC or 0xD4 or 0xDC or 0xE4 or 0xEC or 0xF4 or 0xFC => 3,
            // RST p (eight restart vectors: 00/08/10/18/20/28/30/38)
            0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF => 1,
            0x10 => 2,   // DJNZ e
            0xED => GetEdCallLikeLength(pc),
            _ => -1
        };
    }

    /// <summary>Checks whether the ED-prefixed instruction at <paramref name="pc"/> is
    /// a block instruction (LDIR/LDDR/CPIR/CPDR/INIR/OTIR/IND/OUTD); returns 2 if so,
    /// -1 otherwise.</summary>
    private int GetEdCallLikeLength(ushort pc)
    {
        var op2 = Memory.Read((ushort)(pc + 1));
        // Block instructions (ED Bx): LDIR B0, CPIR B1, INIR B2, OTIR B3,
        //                              LDDR B8, CPDR B9, IND BA, OUTD BB
        return op2 is 0xB0 or 0xB1 or 0xB2 or 0xB3 or 0xB8 or 0xB9 or 0xBA or 0xBB
            ? 2
            : -1;
    }

    /// <summary>Serializes the machine's complete runtime state (project CLAUDE.md §11).
    /// <b>Must only be called at an instruction boundary</b>
    /// (<see cref="Z80.Core.Z80.AtInstructionBoundary"/> is true); the Z80 internal
    /// mid-instruction state (phase, tstate, latches) is not accessible from this layer
    /// and is implicitly zero at a boundary. Call from a <see cref="Video.FieldComplete"/>
    /// handler or after a <see cref="Reset"/> to guarantee a boundary.
    /// Write order is fixed — any change requires a state-format version bump (§11).</summary>
    public void SaveState(IStateWriter writer)
    {
        WriteCpuRegisters(writer, Cpu.Reg);
        writer.WriteUInt64(_pins);
        Memory.SaveState(writer);
        Video.SaveState(writer);
        CpOut.SaveState(writer);
        CpIn.SaveState(writer);
        Keyboard.SaveState(writer);
        Mdcr.SaveState(writer);
        Sound.SaveState(writer);
        Interrupts.SaveState(writer);
    }

    /// <summary>Restores runtime state saved by <see cref="SaveState"/>. Intended to be
    /// called on a freshly constructed machine (rebuilt from the embedded config in the
    /// state file header by <see cref="MachineStateFile"/>), so topology is already correct
    /// and this method only overwrites mutable runtime fields.</summary>
    public void LoadState(IStateReader reader)
    {
        Cpu.Reg = ReadCpuRegisters(reader);
        _pins = reader.ReadUInt64();
        Memory.LoadState(reader);
        Video.LoadState(reader);
        CpOut.LoadState(reader);
        CpIn.LoadState(reader);
        Keyboard.LoadState(reader);
        Mdcr.LoadState(reader);
        Sound.LoadState(reader);
        Interrupts.LoadState(reader);
    }

    // ---- CPU register serialization (Z80.Core has no IStateWriter dependency) -------------

    private static void WriteCpuRegisters(IStateWriter w, Registers r)
    {
        w.WriteByte(r.A); w.WriteByte(r.F);
        w.WriteByte(r.B); w.WriteByte(r.C);
        w.WriteByte(r.D); w.WriteByte(r.E);
        w.WriteByte(r.H); w.WriteByte(r.L);
        w.WriteByte(r.A_); w.WriteByte(r.F_);
        w.WriteByte(r.B_); w.WriteByte(r.C_);
        w.WriteByte(r.D_); w.WriteByte(r.E_);
        w.WriteByte(r.H_); w.WriteByte(r.L_);
        w.WriteByte(r.IXH); w.WriteByte(r.IXL);
        w.WriteByte(r.IYH); w.WriteByte(r.IYL);
        w.WriteUInt16(r.SP);
        w.WriteUInt16(r.PC);
        w.WriteByte(r.I);
        w.WriteByte(r.R);
        w.WriteUInt16(r.WZ);
        w.WriteBool(r.IFF1);
        w.WriteBool(r.IFF2);
        w.WriteByte(r.IM);
        w.WriteByte(r.Q);
        w.WriteBool(r.EiPending);
        w.WriteBool(r.LastWasLdAIR);
    }

    private static Registers ReadCpuRegisters(IStateReader r)
    {
        var reg = new Registers();
        reg.A = r.ReadByte(); reg.F = r.ReadByte();
        reg.B = r.ReadByte(); reg.C = r.ReadByte();
        reg.D = r.ReadByte(); reg.E = r.ReadByte();
        reg.H = r.ReadByte(); reg.L = r.ReadByte();
        reg.A_ = r.ReadByte(); reg.F_ = r.ReadByte();
        reg.B_ = r.ReadByte(); reg.C_ = r.ReadByte();
        reg.D_ = r.ReadByte(); reg.E_ = r.ReadByte();
        reg.H_ = r.ReadByte(); reg.L_ = r.ReadByte();
        reg.IXH = r.ReadByte(); reg.IXL = r.ReadByte();
        reg.IYH = r.ReadByte(); reg.IYL = r.ReadByte();
        reg.SP = r.ReadUInt16();
        reg.PC = r.ReadUInt16();
        reg.I = r.ReadByte();
        reg.R = r.ReadByte();
        reg.WZ = r.ReadUInt16();
        reg.IFF1 = r.ReadBool();
        reg.IFF2 = r.ReadBool();
        reg.IM = r.ReadByte();
        reg.Q = r.ReadByte();
        reg.EiPending = r.ReadBool();
        reg.LastWasLdAIR = r.ReadBool();
        return reg;
    }
}

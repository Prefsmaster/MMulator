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

        Cpu.Reset();
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
        Slot1?.Reset();
        _pins = 0;
    }

    /// <summary>Advances the whole machine by exactly one T-state (project CLAUDE.md §3):
    /// tick the video fetch unit, assert the INT pin if pending, step the CPU, then service
    /// whatever bus request it made this tick against the page table (MREQ) or the port
    /// dispatch (IORQ); detect the int-ack cycle (M1+IORQ) and acknowledge the aggregator.</summary>
    public void Tick()
    {
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
                _pins = Pins.SetData(_pins, Memory.Read(Pins.GetAddress(_pins)));
            }
            else if ((_pins & Pins.WR) != 0)
            {
                Memory.Write(Pins.GetAddress(_pins), Pins.GetData(_pins));
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
                _pins = Pins.SetData(_pins, Interrupts.Acknowledge());
            }
            else if ((_pins & Pins.RD) != 0)
            {
                _pins = Pins.SetData(_pins, Ports.Read(port));
            }
            else if ((_pins & Pins.WR) != 0)
            {
                Ports.Write(port, Pins.GetData(_pins));
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

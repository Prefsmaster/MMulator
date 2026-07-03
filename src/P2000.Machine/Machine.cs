using P2000.Machine.Devices;
using P2000.Machine.Io;
using P2000.Machine.Memory;
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

    private ulong _pins;

    public Machine(MachineConfig? config = null)
    {
        Config = config ?? new MachineConfig();
        Memory = new PageTable(Config);
        Video = new Video(Memory);

        Ports.RegisterWrite(CPoutLatch.Port, CpOut.Write);
        Ports.RegisterRead(CprinReader.Port, CpIn.Read);
        Ports.RegisterWrite(PageTable.BankSelectPort, Memory.SelectBank);

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
        _pins = 0;
    }

    /// <summary>Advances the whole machine by exactly one T-state (project CLAUDE.md §3):
    /// tick the video fetch unit, step the CPU, then service whatever bus request it made
    /// this tick against the page table (MREQ) or the port dispatch (IORQ).</summary>
    public void Tick()
    {
        Video.Tick();

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

            if ((_pins & Pins.RD) != 0)
            {
                _pins = Pins.SetData(_pins, Ports.Read(port));
            }
            else if ((_pins & Pins.WR) != 0)
            {
                Ports.Write(port, Pins.GetData(_pins));
            }
        }
    }
}

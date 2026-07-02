using Z80.Core;

namespace P2000.Machine;

/// <summary>
/// Assembles a <see cref="Z80.Core.Z80"/> CPU plus (eventually) memory and devices into a
/// cycle-exact, bus-accurate P2000T/M and owns the deterministic emulation loop (project
/// CLAUDE.md §3). This milestone wires the CPU only: <see cref="Tick"/> steps the core one
/// T-state at a time and answers every memory request with open bus (0xFF) — the same
/// presence-probe convention the real page table (milestone 2) will use for unpopulated
/// regions, so nothing here gets thrown away once memory lands.
/// </summary>
public sealed class Machine
{
    public MachineConfig Config { get; }

    public Z80.Core.Z80 Cpu { get; } = new();

    private ulong _pins;

    public Machine(MachineConfig? config = null)
    {
        Config = config ?? new MachineConfig();
        Cpu.Reset();
    }

    /// <summary>Rebuilds the machine to its cold-reset state (locked decision §2.3:
    /// topology is fixed once running; only a reset re-applies it).</summary>
    public void Reset()
    {
        Cpu.Reset();
        _pins = 0;
    }

    /// <summary>Advances the whole machine by exactly one T-state (project CLAUDE.md
    /// §3): step the CPU, then service whatever bus request it made this tick. No page
    /// table or I/O dispatch exists yet, so every request reads open bus and writes are
    /// discarded.</summary>
    public void Tick()
    {
        _pins = Cpu.Step(_pins);

        if ((_pins & Pins.MREQ) != 0 && (_pins & Pins.RD) != 0)
        {
            _pins = Pins.SetData(_pins, 0xFF);
        }
    }
}

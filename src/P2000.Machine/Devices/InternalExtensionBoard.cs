using P2000.Machine.Devices.Ctc;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Io;
using P2000.Machine.Slots;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// Thin wiring object for the floppy+RAM internal-extension board (project CLAUDE.md §13
/// milestone 19; reference doc §5c/§5d/§5e) — the only board carrying a CTC (milestone 17) and
/// an FDC (this milestone). Deliberately NOT the general multi-board RAM-variant framework
/// (T/54 vs T/102 vs PTC-96K socket population, deferred to milestone 20) — this object's only
/// job is to instantiate one <see cref="Z80Ctc"/> + one <see cref="Upd765"/>, route their ports,
/// and wire the FDC's completion signal to the CTC channel the ROM driver expects it on. Both
/// chips stay board-agnostic; this is "the owning board"'s job, same design as the chips
/// themselves.
/// </summary>
public sealed class InternalExtensionBoard : IDevice, IIoSlot
{
    public Z80Ctc Ctc { get; } = new();
    public Upd765 Fdc { get; } = new();

    public InternalExtensionBoard()
    {
        // FDC has no direct CPU INT line (reference doc §5d) — its result-phase completion
        // feeds CTC ch0, exactly like the video 50 Hz field pulse feeds ch3 (wired by
        // Machine, since that's the video device's seam, not this board's).
        Fdc.ResultReady += () => Ctc.ClkTrg(0);
    }

    /// <summary>Registers CTC ports 0x88-0x8B and FDC ports 0x8C/0x8D/0x90 (reference doc §5d
    /// I/O port map). Port 0x94 (RAMSW) stays wired by <see cref="Machine"/> directly to
    /// <see cref="Memory.PageTable"/> — it's a memory-bank-select register the board carries,
    /// not a chip register either of these two chips owns.</summary>
    public void RegisterPorts(PortDispatch ports)
    {
        for (var ch = 0; ch < Z80Ctc.ChannelCount; ch++)
        {
            var channel = ch;
            var port = (byte)(Z80Ctc.PortBase + ch);
            ports.RegisterWrite(port, v => Ctc.WritePort(channel, v));
            ports.RegisterRead(port, () => Ctc.ReadPort(channel));
        }

        ports.RegisterRead(Upd765.StatusPort, Fdc.ReadStatus);
        ports.RegisterRead(Upd765.DataPort, Fdc.ReadData);
        ports.RegisterWrite(Upd765.DataPort, Fdc.WriteData);
        ports.RegisterRead(Upd765.ControlPort, Fdc.ReadControl);
        ports.RegisterWrite(Upd765.ControlPort, Fdc.WriteControl);
    }

    /// <summary>Advances both chips' master-clock-driven state one T-state (CTC timer-mode
    /// channels; FDC seek-settle/semi-DMA byte pacing under Authentic timing).</summary>
    public void Tick()
    {
        Ctc.Tick();
        Fdc.Tick();
    }

    public void Reset()
    {
        Ctc.Reset();
        Fdc.Reset();
    }

    public void SaveState(IStateWriter w)
    {
        Ctc.SaveState(w);
        Fdc.SaveState(w);
    }

    public void LoadState(IStateReader r)
    {
        Ctc.LoadState(r);
        Fdc.LoadState(r);
    }
}

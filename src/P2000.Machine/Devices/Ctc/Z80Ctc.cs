using P2000.Machine.Interrupts;
using P2000.Machine.State;

namespace P2000.Machine.Devices.Ctc;

/// <summary>
/// Standalone, board-agnostic Zilog Z8430 (Z80-CTC) — 4 independent channels, each with a
/// control register, time-constant register, and down-counter (reference doc §5d/§5e, project
/// CLAUDE.md §13 milestone 17). Modelled like the SAA5050: the chip has no opinion on which
/// board it's mounted on or which ports it answers. The OWNING BOARD (currently: the
/// floppy+RAM internal-extension board, <see cref="MachineConfig"/>
/// <see cref="P2000.Machine.InternalBoard.FloppyRam"/>) maps <see cref="WritePort"/>/
/// <see cref="ReadPort"/> onto ports 0x88 (ch0) .. 0x8B (ch3), feeds each channel's CLK/TRG
/// input (ch3 ← the video 50 Hz field pulse — reference doc §5e), and registers
/// <see cref="DaisyChainDevices"/> into the machine's <see cref="DaisyChain"/> in priority
/// order (ch0 highest, confirmed reference doc §5d).
/// </summary>
public sealed class Z80Ctc : IDevice
{
    public const int ChannelCount = 4;

    /// <summary>Confirmed I/O port base — one port per channel, A0/A1 select (reference doc
    /// §5d). Channel n lives at <c>PortBase + n</c>.</summary>
    public const byte PortBase = 0x88;

    private readonly CtcChannel[] _channels;

    /// <summary>Shared interrupt-vector base, set by a bit0=0 byte written to channel 0
    /// (reference doc §5e: "writes the base low byte 0x20 to CTC ch0"). Channel n's vector is
    /// <c>(VectorBase &amp; 0xF8) | (n &lt;&lt; 1)</c>.</summary>
    internal byte VectorBase { get; set; }

    public Z80Ctc()
    {
        _channels = new CtcChannel[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
            _channels[i] = new CtcChannel(this, i);
    }

    /// <summary>The four channels' IM2 daisy-chain links, in priority order (ch0 highest). The
    /// owning board registers these into the machine's <see cref="DaisyChain"/>.</summary>
    public IReadOnlyList<IDaisyChainDevice> DaisyChainDevices => _channels;

    /// <summary>Writes a byte to channel <paramref name="channel"/> (0-3) — the owning board
    /// maps this from an IORQ write on that channel's port.</summary>
    public void WritePort(int channel, byte value) => _channels[channel].WritePort(value);

    /// <summary>Reads channel <paramref name="channel"/>'s live down-counter (debugger nicety —
    /// reference doc §5d confirms the ROM never reads a CTC channel).</summary>
    public byte ReadPort(int channel) => _channels[channel].ReadPort();

    /// <summary>Advances every timer-mode channel by one master-clock T-state. Counter-mode
    /// channels are unaffected — they decrement only via <see cref="ClkTrg"/>.</summary>
    public void Tick()
    {
        foreach (var ch in _channels) ch.Tick();
    }

    /// <summary>Delivers one active CLK/TRG edge to <paramref name="channel"/> — the owning
    /// board's job (e.g. ch3 ← the video 50 Hz field pulse, reference doc §5e).</summary>
    public void ClkTrg(int channel) => _channels[channel].ClkTrg();

    public void Reset()
    {
        VectorBase = 0;
        foreach (var ch in _channels) ch.Reset();
    }

    public void SaveState(IStateWriter w)
    {
        w.WriteByte(VectorBase);
        foreach (var ch in _channels) ch.SaveState(w);
    }

    public void LoadState(IStateReader r)
    {
        VectorBase = r.ReadByte();
        foreach (var ch in _channels) ch.LoadState(r);
    }
}

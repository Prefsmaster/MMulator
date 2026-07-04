using P2000.Machine.State;

namespace P2000.Machine.Io;

/// <summary>
/// Shared input port 0x20 (reference doc §5f, project CLAUDE.md §6/§7) — the read-side
/// counterpart to <see cref="CPoutLatch"/>. This class owns the PRINTER bits (0–2):
/// PRI/READY/STRAP. The cassette bits (3–7: WEN/CIP/BET/RDC/RDA) are owned by
/// <see cref="Devices.Cassette.MdcrDevice"/> at milestone 9; both register read sources on
/// 0x20 and the port dispatch OR-combines them.
///
/// Printer device is deferred (project CLAUDE.md §14); all printer bits read 0 until a
/// printer device is wired.
/// </summary>
public sealed class CprinReader : IDevice
{
    public const byte Port = 0x20;

    // Bits 0–2 reserved for printer; currently inactive (all 0).
    // private const byte PriBit   = 0x01;
    // private const byte ReadyBit = 0x02;
    // private const byte StrapBit = 0x04;

    public byte Read() => 0x00; // printer deferred — contribute nothing to the OR-combine

    public void Reset() { }

    public void SaveState(IStateWriter writer) { }

    public void LoadState(IStateReader reader) { }
}

using P2000.Machine.State;

namespace P2000.Machine.Io;

/// <summary>
/// Shared write-only latch at I/O port 0x10 (reference doc §5f, project CLAUDE.md §6/§7).
/// Keyboard KBIEN, printer PRD, and cassette FWD/REV/WCD/WDA all live in this one byte; the
/// ROM always rewrites the whole byte (it ORs KBIEN into every MDCR command), so the latch
/// keeps a shadow copy as the single source of truth rather than per-bit registers.
/// KBIEN/PRD/FWD/REV are levels — read them straight off <see cref="Current"/>. WCD/WDA
/// carry the cassette bitstream as edges, not levels, so <see cref="Written"/> exposes the
/// previous/current pair on every write for an edge-sensitive consumer (the cassette
/// encoder, milestone 9) to detect the transition itself.
/// </summary>
public sealed class CPoutLatch : IDevice
{
    public const byte Port = 0x10;

    private const byte PrinterDataBit = 0x80;
    private const byte KbienBit = 0x40;
    private const byte ForwardBit = 0x08;
    private const byte ReverseBit = 0x04;
    private const byte WriteCommandBit = 0x02;
    private const byte WriteDataBit = 0x01;

    public byte Current { get; private set; }

    /// <summary>Fires with (previous, current) on every write to port 0x10 so an
    /// edge-sensitive consumer can detect a WCD/WDA transition; by the time this fires,
    /// <see cref="Current"/> already reflects the new value.</summary>
    public event Action<byte, byte>? Written;

    public bool PrinterData => (Current & PrinterDataBit) != 0;
    public bool Kbien => (Current & KbienBit) != 0;
    public bool Forward => (Current & ForwardBit) != 0;
    public bool Reverse => (Current & ReverseBit) != 0;
    public bool WriteCommand => (Current & WriteCommandBit) != 0;
    public bool WriteData => (Current & WriteDataBit) != 0;

    public void Write(byte value)
    {
        var previous = Current;
        Current = value;
        Written?.Invoke(previous, value);
    }

    public void Reset()
    {
        Current = 0;
    }

    public void SaveState(IStateWriter writer) => writer.WriteByte(Current);

    public void LoadState(IStateReader reader) => Current = reader.ReadByte();
}

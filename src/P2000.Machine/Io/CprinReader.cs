using P2000.Machine.State;

namespace P2000.Machine.Io;

/// <summary>
/// Shared input port 0x20 (reference doc §5f, project CLAUDE.md §6/§7) — the read-side
/// counterpart to <see cref="CPoutLatch"/>. Cassette RDC/RDA/CIP/BET/WEN and printer
/// PRI/READY/STRAP all answer here. Each bit's asserted level is encoded explicitly per
/// the reference doc's bit table (several are active-low) rather than by inference, since
/// mixing one up yields a tape that reads as perpetually-ending or never-present.
///
/// The cassette device is milestone 9 (project CLAUDE.md §13), so this reader owns the
/// cassette-facing bits directly for now, defaulted to the bare-machine state (no cassette
/// mounted). The printer is deferred entirely (§14); its bits (PRI/READY/STRAP) read 0
/// until a printer device exists to drive them.
/// </summary>
public sealed class CprinReader : IDevice
{
    public const byte Port = 0x20;

    private const byte PriBit = 0x01;
    private const byte ReadyBit = 0x02;
    private const byte StrapBit = 0x04;
    private const byte WenBit = 0x08;
    private const byte CipBit = 0x10;
    private const byte BetBit = 0x20;
    private const byte RdcBit = 0x40;
    private const byte RdaBit = 0x80;

    /// <summary>Whether a `.cas` is mounted. CIP is active-low (bit set = no cassette), so
    /// the bare-machine default (<c>false</c>) reads as cassette-absent.</summary>
    public bool CassettePresent { get; set; }

    /// <summary>Whether the tape is at a physical end. BET is active-low (bit set = tape
    /// OK), so the default (<c>false</c>) reads as "tape OK" — there is no tape to be at
    /// the end of.</summary>
    public bool TapeAtEnd { get; set; }

    /// <summary>Whether the mounted `.cas` is write-protected. WEN is active-low in the
    /// opposite sense: the bit is SET when protected (see reference doc §5f table).</summary>
    public bool WriteProtected { get; set; }

    /// <summary>Read Clock — toggles once per bit in the authentic-mode bit engine
    /// (milestone 9); level, not an edge, at this port.</summary>
    public bool ReadClock { get; set; }

    /// <summary>Read Data — the data bit sampled alongside a <see cref="ReadClock"/> edge.</summary>
    public bool ReadData { get; set; }

    public byte Read()
    {
        byte value = 0;
        if (!CassettePresent) value |= CipBit;
        if (!TapeAtEnd) value |= BetBit;
        if (WriteProtected) value |= WenBit;
        if (ReadClock) value |= RdcBit;
        if (ReadData) value |= RdaBit;

        // PRI/READY/STRAP: printer device deferred (project CLAUDE.md §14) - inactive for now.
        return value;
    }

    public void Reset()
    {
        CassettePresent = false;
        TapeAtEnd = false;
        WriteProtected = false;
        ReadClock = false;
        ReadData = false;
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteBool(CassettePresent);
        writer.WriteBool(TapeAtEnd);
        writer.WriteBool(WriteProtected);
        writer.WriteBool(ReadClock);
        writer.WriteBool(ReadData);
    }

    public void LoadState(IStateReader reader)
    {
        CassettePresent = reader.ReadBool();
        TapeAtEnd = reader.ReadBool();
        WriteProtected = reader.ReadBool();
        ReadClock = reader.ReadBool();
        ReadData = reader.ReadBool();
    }
}

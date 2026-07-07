using System.Reflection;
using P2000.Machine.Slots;
using P2000.Machine.State;

namespace P2000.Machine.Memory;

/// <summary>
/// The per-model 64 KB memory map (reference doc §5, project CLAUDE.md §5), built once at
/// machine-assembly time from a <see cref="MachineConfig"/>. Unpopulated regions read
/// <see cref="OpenBus"/> (0xFF) and discard writes — the presence-probe convention the
/// monitor ROM's boot-time RAM/cartridge/disk tests rely on, with no special-casing needed
/// anywhere else.
///
/// The monitor ROM is loaded automatically: from <see cref="MachineConfig.MonitorRomPath"/>
/// if set, otherwise from the embedded <c>P2000.Machine.MonitorRom</c> resource (so the
/// machine boots out of the box with zero setup — project CLAUDE.md §5). SLOT1 is loaded
/// from <see cref="MachineConfig.Slot1CartridgePath"/> when set, otherwise left open-bus
/// (cassette-wait boot path).
/// </summary>
public sealed class PageTable
{
    public const byte OpenBus = 0xFF;

    public const ushort RomStart = 0x0000;
    public const ushort RomEnd = 0x0FFF;
    private const int RomSize = RomEnd - RomStart + 1;

    /// <summary>SLOT1 cartridge address range (reference doc §5c). Open bus when
    /// <see cref="_cartridge"/> is null; delegated to the fitted <see cref="IMemorySlot"/>
    /// otherwise. Read-only: SLOT1 has no WR pin (writes are silently discarded).</summary>
    public const ushort CartridgeStart = 0x1000;
    public const ushort CartridgeEnd = 0x4FFF;

    public const ushort VideoRamStart = 0x5000;
    private const int VideoRamSizeT = 0x0800; // 2 KB: 0x5000-0x57FF, + 2 KB open-bus gap
    private const int VideoRamSizeM = 0x1000; // 4 KB: the full 0x5000-0x5FFF block

    public const ushort BaseRamStart = 0x6000;
    public const ushort BaseRamEnd = 0x9FFF;
    private const int BaseRamSize = BaseRamEnd - BaseRamStart + 1; // 16 KB, always populated

    public const ushort ExpansionRamStart = 0xA000;
    public const ushort ExpansionRamEnd = 0xDFFF;
    private const int ExpansionRamSize = ExpansionRamEnd - ExpansionRamStart + 1; // 16 KB

    public const ushort BankedWindowStart = 0xE000;
    public const ushort BankedWindowEnd = 0xFFFF;
    private const int BankSize = BankedWindowEnd - BankedWindowStart + 1; // 8 KB per bank

    /// <summary>I/O port 0x94 (reference doc §5), the write-only bank-select register for
    /// the 0xE000-0xFFFF window. <see cref="Machine"/> wires a write here to
    /// <see cref="SelectBank"/> via the port dispatch.</summary>
    public const byte BankSelectPort = 0x94;

    private readonly byte[] _rom = new byte[RomSize];
    private readonly IMemorySlot? _cartridge; // null = open-bus (no cartridge fitted)

    private readonly ushort _videoRamEnd;
    private readonly byte[] _videoRam;

    private readonly byte[] _baseRam = new byte[BaseRamSize];
    private readonly byte[]? _expansionRam;

    private readonly byte[][] _banks;
    private byte _bankIndex;

    /// <param name="config">Machine topology — RAM variant, model, ROM override.</param>
    /// <param name="cartridge">SLOT1 cartridge to map at 0x1000–0x4FFF, or <c>null</c>
    /// for open-bus (bare machine / cassette-wait boot). Constructed by
    /// <see cref="Machine"/> from <see cref="MachineConfig.Slot1CartridgePath"/>.</param>
    public PageTable(MachineConfig config, IMemorySlot? cartridge = null)
    {
        _cartridge = cartridge;

        var videoRamSize = config.Model == MachineModel.P2000M ? VideoRamSizeM : VideoRamSizeT;
        _videoRamEnd = (ushort)(VideoRamStart + videoRamSize - 1);
        _videoRam = new byte[videoRamSize];

        _expansionRam = config.RamVariant == RamVariant.T38 ? null : new byte[ExpansionRamSize];

        _banks = new byte[config.EffectiveBankCount][];
        for (var i = 0; i < _banks.Length; i++)
        {
            _banks[i] = new byte[BankSize];
        }

        // Monitor ROM: embedded default or config override (project CLAUDE.md §5).
        LoadRom(config.MonitorRomPath is not null
            ? File.ReadAllBytes(config.MonitorRomPath)
            : LoadEmbeddedMonitorRom());
    }

    /// <summary>Loads the embedded monitor ROM from the assembly manifest resource
    /// <c>P2000.Machine.MonitorRom</c> (linked from <c>assets/P2000ROM.rom</c>).</summary>
    private static byte[] LoadEmbeddedMonitorRom()
    {
        var asm = typeof(PageTable).Assembly;
        using var stream = asm.GetManifestResourceStream("P2000.Machine.MonitorRom")
            ?? throw new InvalidOperationException(
                "Embedded monitor ROM resource 'P2000.Machine.MonitorRom' not found. " +
                "Ensure assets/P2000ROM.rom is present and the project was rebuilt.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Loads a monitor ROM image into the read-only 0x0000-0x0FFF region.</summary>
    public void LoadRom(ReadOnlySpan<byte> rom)
    {
        if (rom.Length > RomSize)
        {
            throw new ArgumentException($"ROM image is {rom.Length} bytes; the monitor ROM region is only {RomSize} bytes.", nameof(rom));
        }

        rom.CopyTo(_rom);
    }

    /// <summary>Sets the raw byte written to I/O port 0x94, selecting which 8 KB bank
    /// answers at 0xE000-0xFFFF. Stored unmasked (reference doc §5: the hardware places no
    /// range restriction on this register); an index at or beyond the configured bank
    /// count reads open bus, same as any other unpopulated region. <see cref="Machine"/>
    /// registers this as the port dispatch's write listener for <see cref="BankSelectPort"/>.</summary>
    public void SelectBank(byte index) => _bankIndex = index;

    /// <summary>True when <paramref name="addr"/> is backed by the DRAM array (VRAM,
    /// the SAA5020 fetch unit exclusively addresses this VRAM chip. Base RAM, expansion RAM,
    /// and the banked window are separate chips not accessed by the SAA5020 — a Z80 write to
    /// those addresses cannot collide with a display fetch (reference doc §4).
    /// Used by the contention model (milestone 10) to filter MREQ cycles.</summary>
    public bool IsVideoRamAddress(ushort addr) => addr >= VideoRamStart && addr <= _videoRamEnd;

    public byte Read(ushort address)
    {
        if (address <= RomEnd)
        {
            return _rom[address];
        }

        if (address <= CartridgeEnd)
        {
            return _cartridge?.Read(address) ?? OpenBus;
        }

        if (address <= _videoRamEnd)
        {
            return _videoRam[address - VideoRamStart];
        }

        if (address <= BaseRamEnd)
        {
            return address < BaseRamStart ? OpenBus : _baseRam[address - BaseRamStart];
        }

        if (address <= ExpansionRamEnd)
        {
            return _expansionRam?[address - ExpansionRamStart] ?? OpenBus;
        }

        return ReadBank(address);
    }

    public void Write(ushort address, byte value)
    {
        if (address <= RomEnd)
        {
            return; // ROM is read-only
        }

        if (address <= CartridgeEnd)
        {
            _cartridge?.Write(address, value); // SLOT1 has no WR pin — Slot1Cartridge discards
            return;
        }

        if (address <= _videoRamEnd)
        {
            _videoRam[address - VideoRamStart] = value;
            return;
        }

        if (address <= BaseRamEnd)
        {
            if (address >= BaseRamStart)
            {
                _baseRam[address - BaseRamStart] = value;
            }

            return;
        }

        if (address <= ExpansionRamEnd)
        {
            if (_expansionRam is not null)
            {
                _expansionRam[address - ExpansionRamStart] = value;
            }

            return;
        }

        WriteBank(address, value);
    }

    private byte ReadBank(ushort address) =>
        _bankIndex < _banks.Length ? _banks[_bankIndex][address - BankedWindowStart] : OpenBus;

    private void WriteBank(ushort address, byte value)
    {
        if (_bankIndex < _banks.Length)
        {
            _banks[_bankIndex][address - BankedWindowStart] = value;
        }
    }

    /// <summary>Zeroes all mutable DRAM (VRAM, base RAM, expansion RAM, banked window) without
    /// touching the ROM or cartridge. Used by <see cref="Machine"/> for a cold reset
    /// (project CLAUDE.md §3b.3 <c>ColdResetCommand</c>).</summary>
    public void ClearRam()
    {
        Array.Clear(_videoRam);
        Array.Clear(_baseRam);
        if (_expansionRam is not null) Array.Clear(_expansionRam);
        foreach (var bank in _banks) Array.Clear(bank);
        _bankIndex = 0;
    }

    /// <summary>Serializes the runtime RAM contents (project CLAUDE.md §11). The embedded
    /// monitor ROM and any fitted SLOT1 cartridge are NOT saved — both are static and
    /// reconstructed from config at machine-assembly time. Only mutable DRAM is persisted:
    /// VRAM, base RAM, expansion RAM (if fitted), each bank of the banked window (if fitted),
    /// and the current bank-select index.
    /// </summary>
    public void SaveState(IStateWriter writer)
    {
        writer.WriteBytes(_videoRam);
        writer.WriteBytes(_baseRam);
        writer.WriteBool(_expansionRam is not null);
        if (_expansionRam is not null)
            writer.WriteBytes(_expansionRam);
        writer.WriteInt32(_banks.Length);
        foreach (var bank in _banks)
            writer.WriteBytes(bank);
        writer.WriteByte(_bankIndex);
    }

    /// <summary>Restores RAM contents saved by <see cref="SaveState"/>. Called after a
    /// cold reset (machine reconstructed from config), so the monitor ROM and any fitted
    /// SLOT1 cartridge are already in place — only the mutable DRAM fields are overwritten.</summary>
    public void LoadState(IStateReader reader)
    {
        reader.ReadBytes(_videoRam);
        reader.ReadBytes(_baseRam);
        var hasExpansion = reader.ReadBool();
        if (hasExpansion && _expansionRam is not null)
            reader.ReadBytes(_expansionRam);
        var bankCount = reader.ReadInt32();
        for (var i = 0; i < bankCount; i++)
        {
            if (i < _banks.Length)
                reader.ReadBytes(_banks[i]);
            else
            {
                // state has more banks than this machine — skip (version mismatch guard).
                var buf = new byte[BankSize];
                reader.ReadBytes(buf);
            }
        }
        _bankIndex = reader.ReadByte();
    }
}

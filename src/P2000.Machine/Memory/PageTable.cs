using System.Reflection;

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

    /// <summary>SLOT1 cartridge region (reference doc §5c). Open bus when no cartridge
    /// is configured; loaded from <see cref="MachineConfig.Slot1CartridgePath"/> when set.
    /// Read-only: cartridge slots have no WR pin (writes are silently discarded).</summary>
    public const ushort CartridgeStart = 0x1000;
    public const ushort CartridgeEnd = 0x4FFF;
    private const int CartridgeSize = CartridgeEnd - CartridgeStart + 1; // 16 KB

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
    private readonly byte[]? _slot1; // null = open-bus (no cartridge fitted)

    private readonly ushort _videoRamEnd;
    private readonly byte[] _videoRam;

    private readonly byte[] _baseRam = new byte[BaseRamSize];
    private readonly byte[]? _expansionRam;

    private readonly byte[][] _banks;
    private byte _bankIndex;

    public PageTable(MachineConfig config)
    {
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

        // SLOT1 cartridge: open bus when absent; loaded from path when configured.
        if (config.Slot1CartridgePath is not null)
        {
            var cart = File.ReadAllBytes(config.Slot1CartridgePath);
            _slot1 = new byte[CartridgeSize];
            cart.AsSpan(0, Math.Min(cart.Length, CartridgeSize)).CopyTo(_slot1);
        }
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

    public byte Read(ushort address)
    {
        if (address <= RomEnd)
        {
            return _rom[address];
        }

        if (address <= CartridgeEnd)
        {
            return _slot1?[address - CartridgeStart] ?? OpenBus;
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
            return; // SLOT1 has no RD/WR pins (reference doc §5c) - always read-only/absent
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
}

using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Slots;

/// <summary>
/// A read-only ROM cartridge fitted in SLOT1 (0x1000–0x4FFF, reference doc §5c).
/// Constructed from a flat binary image file; bytes beyond the image length read as
/// <see cref="PageTable.OpenBus"/> (0xFF) — an unprogrammed EPROM returns 0xFF — so an
/// 8 KB CARS1-only image naturally leaves CARS2 at open-bus without special-casing
/// (project CLAUDE.md §7 boot findings).
///
/// SLOT1 has no WR pin on the bus connector (reference doc §5c), so <see cref="Write"/>
/// silently discards all bus writes. The ROM content is static and configuration-determined;
/// <see cref="SaveState"/> and <see cref="LoadState"/> are no-ops — the image is re-loaded
/// from its source on every cold reset, exactly like the embedded monitor ROM.
/// </summary>
public sealed class Slot1Cartridge : IMemorySlot
{
    private readonly byte[] _rom;
    private readonly int _imageLength;

    /// <param name="imagePath">Path to a flat binary cartridge image (.bin / .rom).</param>
    public Slot1Cartridge(string imagePath)
        : this(File.ReadAllBytes(imagePath).AsSpan()) { }

    /// <summary>Constructs directly from a byte span (test fixtures and in-memory images).</summary>
    public Slot1Cartridge(ReadOnlySpan<byte> image)
    {
        const int CartridgeSize = PageTable.CartridgeEnd - PageTable.CartridgeStart + 1;
        _imageLength = Math.Min(image.Length, CartridgeSize);
        _rom = new byte[_imageLength];
        image[.._imageLength].CopyTo(_rom);
    }

    public ushort AddressStart => PageTable.CartridgeStart;
    public ushort AddressEnd => PageTable.CartridgeEnd;

    public byte Read(ushort address)
    {
        var offset = address - PageTable.CartridgeStart;
        return offset < _imageLength ? _rom[offset] : PageTable.OpenBus;
    }

    /// <summary>SLOT1 has no WR pin — all writes are silently discarded.</summary>
    public void Write(ushort address, byte value) { }

    // ROM content is static and configuration-determined; no runtime state to persist.
    public void Reset() { }
    public void SaveState(IStateWriter writer) { }
    public void LoadState(IStateReader reader) { }
}

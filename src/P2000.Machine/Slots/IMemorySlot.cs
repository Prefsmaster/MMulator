namespace P2000.Machine.Slots;

/// <summary>
/// A slot card that occupies a contiguous range of the 64 KB address space (reference doc
/// §5c). Implemented by SLOT1 cartridges (memory-mapped ROM at 0x1000–0x4FFF) and the
/// memory-mapped side of internal-slot boards (CTC/FDC, hires overlay — deferred, §14).
///
/// <see cref="Memory.PageTable"/> routes MREQ bus cycles to the registered
/// <see cref="IMemorySlot"/> for addresses in [<see cref="AddressStart"/>,
/// <see cref="AddressEnd"/>], bypassing its own arrays. Unpopulated slots leave that region
/// at open-bus (0xFF) — the same presence-probe convention used throughout the page table.
/// </summary>
public interface IMemorySlot : ISlotCard
{
    ushort AddressStart { get; }
    ushort AddressEnd { get; }

    byte Read(ushort address);

    /// <summary>Bus write cycle to this card's address range. ROM/EPROM cards discard the
    /// write silently — SLOT1 has no WR pin on its bus connector (reference doc §5c).</summary>
    void Write(ushort address, byte value);
}

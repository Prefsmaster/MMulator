using P2000.Machine.Io;

namespace P2000.Machine.Slots;

/// <summary>
/// A slot card that occupies a range of the 256-port I/O space (reference doc §5c).
/// Implemented by SLOT2 expansion cards (I/O-mapped only) and the I/O-mapped side of
/// internal-slot boards (CTC/FDC — deferred, §14).
///
/// The card self-registers its read/write listeners with the <see cref="PortDispatch"/> at
/// machine-assembly time via <see cref="RegisterPorts"/>. Reset-to-apply (locked decision
/// §2.3): the machine is rebuilt from config on topology changes, so there is no unregister
/// path — listeners registered here live for the machine's lifetime.
/// </summary>
public interface IIoSlot : ISlotCard
{
    /// <summary>Called once at machine-assembly time. Register all port read sources and
    /// write listeners this card drives with <paramref name="ports"/>.</summary>
    void RegisterPorts(PortDispatch ports);
}

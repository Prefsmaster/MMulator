namespace P2000.Machine.Slots;

/// <summary>
/// Common marker interface for all slot card occupants (project CLAUDE.md §4, §12.12).
/// Every slot card participates in the standard device lifecycle: cold reset, state save,
/// and state restore. ROM-only cards (e.g. <see cref="Slot1Cartridge"/>) implement these
/// as no-ops since their content is static and configuration-determined; cards with
/// mutable runtime state (future CTC, FDC, hires overlay) use them fully.
/// </summary>
public interface ISlotCard : IDevice { }

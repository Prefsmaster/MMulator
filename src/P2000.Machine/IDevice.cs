using P2000.Machine.State;

namespace P2000.Machine;

/// <summary>
/// The common shape every machine component implements (project CLAUDE.md §4): the CPU
/// wrapper, memory pages, video, keyboard, CPOUT/CPRIN latches, cassette, and later
/// CTC/FDC/slot cards. <see cref="Reset"/> plus state save/load are present from day one
/// so nothing needs retrofitting once save-state (project CLAUDE.md §11) lands.
/// </summary>
public interface IDevice
{
    /// <summary>Cold-reset behaviour for this device.</summary>
    void Reset();

    /// <summary>Serialize this device's runtime state (not its topology/config).</summary>
    void SaveState(IStateWriter writer);

    /// <summary>Restore runtime state previously written by <see cref="SaveState"/>.</summary>
    void LoadState(IStateReader reader);
}

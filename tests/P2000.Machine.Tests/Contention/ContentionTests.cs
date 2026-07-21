using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Contention;

/// <summary>
/// Validates the milestone-10 contention model (project CLAUDE.md §10, reference doc §4):
/// Z80 always wins — a CPU DRAM access in a video fetch slot produces a single-cell black
/// glitch (<see cref="Video.CorruptLastFetch"/>) and sets the debug overlay; no CPU access
/// during an active fetch slot leaves the display clean.
/// </summary>
public class ContentionTests
{
    // ---- Speckle: CPU hammering DRAM during active display ---------------------------------

    /// <summary>
    /// A tight loop that writes to VRAM (0x5000) runs throughout the field, hitting both
    /// active display lines and vblank. The collision with video fetch slots must produce
    /// at least one corrupted cell, flagged in the overlay.
    /// </summary>
    [Fact]
    public void ContentionDuringActiveDisplay_ProducesCorruptedCells()
    {
        var machine = new Machine();

        // Tight VRAM-write loop: LD HL,0x5000 / LD (HL),H / JR -3
        // LD (HL),H drives MREQ with address 0x5000 (IsVideoRamAddress = true).
        // Base RAM (0x6000+) would NOT cause contention — only VRAM is shared with the SAA5020.
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x50, // LD HL, 0x5000
            0x74,             // LD (HL), H   — VRAM write each pass
            0x18, 0xFD,       // JR -3        — loop back to LD (HL),H
        });

        // Capture overlay inside FieldComplete (before it's cleared).
        bool anyCorrupted = false;
        machine.Video.FieldComplete += () =>
            anyCorrupted |= machine.Video.CorruptionOverlay.Any(c => c);

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            machine.Tick();

        Assert.True(anyCorrupted,
            "At least one video fetch cell should be corrupted when CPU writes to DRAM during active display.");
    }

    // ---- Clean: no DRAM access during active display ---------------------------------------

    /// <summary>
    /// A CPU halted in ROM (0x0000 — not DRAM) produces no MREQ cycles to DRAM during the
    /// field, so no video fetch slots are ever corrupted.
    /// </summary>
    [Fact]
    public void NoDramAccessDuringActiveDisplay_LeavesDisplayClean()
    {
        var machine = new Machine();

        // HALT: the CPU repeatedly fetches 0x76 from PC=0x0000 (ROM — not VRAM).
        // IsVideoRamAddress(0x0000) == false → no contention triggered.
        machine.Memory.LoadRom(new byte[] { 0x76 }); // HALT at 0x0000

        bool[] overlaySnapshot = Array.Empty<bool>();
        machine.Video.FieldComplete += () =>
            overlaySnapshot = (bool[])machine.Video.CorruptionOverlay.Clone();

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            machine.Tick();

        Assert.All(overlaySnapshot,
            corrupted => Assert.False(corrupted));
    }

    // ---- Single-cell, non-persistent -------------------------------------------------------

    /// <summary>
    /// Corruption is confined to the cell(s) whose fetch slots collided. Cells that were not
    /// contended in this field remain unset in the overlay.
    /// </summary>
    [Fact]
    public void Contention_IsSingleCell_NotWholeDisplay()
    {
        var machine = new Machine();

        // Same VRAM hammering loop as the speckle test.
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x50, // LD HL, 0x5000
            0x74,             // LD (HL), H
            0x18, 0xFD,       // JR -3
        });

        int corruptedCount = 0;
        int totalCells = VideoFetchUnit.Columns * Video.CharRows;
        machine.Video.FieldComplete += () =>
            corruptedCount = machine.Video.CorruptionOverlay.Count(c => c);

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            machine.Tick();

        // At least one corrupted but not ALL cells can be corrupted in a single field —
        // a 7-T-state write loop runs at most ~7142 times per field (50 000 / 7 T-states),
        // which is far fewer than the 9600 fetch slots (240 lines × 40 cells).
        Assert.InRange(corruptedCount, 1, totalCells - 1);
    }

    // ---- Pre-roll vertical blank: never contended (2026-07-22 fix) -------------------------

    /// <summary>
    /// Project CLAUDE.md §17 (2026-07-19/2026-07-22): the 49-line vertical-blank pre-roll must
    /// be fetch-free, so hammering VRAM confined to ONLY that window must never corrupt a cell
    /// — the fix for the reported Ghosthunt top-of-screen glitch. Confined by tick-count, not
    /// by relying on the loop naturally stopping — the same VRAM-write loop as the speckle test,
    /// run for exactly the pre-roll's T-state budget then halted.
    /// </summary>
    [Fact]
    public void ContentionDuringPreRollVblank_NeverCorrupts()
    {
        var machine = new Machine();

        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x50, // LD HL, 0x5000
            0x74,             // LD (HL), H
            0x18, 0xFD,       // JR -3
        });

        var preRollTStates = VideoFetchUnit.VerticalBlankLines * VideoFetchUnit.TStatesPerLine;
        for (var i = 0; i < preRollTStates; i++)
            machine.Tick();

        Assert.All(machine.Video.CorruptionOverlay, c => Assert.False(c));
    }

    // ---- Overlay reset ---------------------------------------------------------------------

    [Fact]
    public void CorruptionOverlay_ClearsAfterEachField()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x50, // LD HL, 0x5000
            0x74,             // LD (HL), H
            0x18, 0xFD,       // JR -3
        });

        // Run one full field to build up some corruption.
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            machine.Tick();

        // The overlay is cleared after FieldComplete fires, so reading it NOW (after the
        // field is complete) should show all-false.
        Assert.All(machine.Video.CorruptionOverlay, c => Assert.False(c));
    }

    // ---- IsVideoRamAddress filter ----------------------------------------------------------
    // Only the VRAM chip (shared with the SAA5020) causes contention. Base RAM, expansion
    // RAM, and the banked window are separate chips — reference doc §4 correction.

    [Fact]
    public void IsVideoRamAddress_ReturnsFalse_ForRomSlot1AndRam()
    {
        var pt = new PageTable(new MachineConfig()); // default = P2000T
        Assert.False(pt.IsVideoRamAddress(PageTable.RomStart));
        Assert.False(pt.IsVideoRamAddress(PageTable.RomEnd));
        Assert.False(pt.IsVideoRamAddress(PageTable.CartridgeStart));
        Assert.False(pt.IsVideoRamAddress(PageTable.CartridgeEnd));
        Assert.False(pt.IsVideoRamAddress(PageTable.BaseRamStart)); // separate chip
        Assert.False(pt.IsVideoRamAddress(PageTable.BaseRamEnd));
        Assert.False(pt.IsVideoRamAddress(PageTable.ExpansionRamStart));
        Assert.False(pt.IsVideoRamAddress(PageTable.BankedWindowStart));
    }

    [Fact]
    public void IsVideoRamAddress_ReturnsTrue_ForVramWindow()
    {
        var pt = new PageTable(new MachineConfig()); // default = P2000T, VRAM 0x5000-0x57FF
        Assert.True(pt.IsVideoRamAddress(PageTable.VideoRamStart));       // 0x5000
        Assert.True(pt.IsVideoRamAddress(0x57FF));                         // T-model end
        Assert.False(pt.IsVideoRamAddress(0x5800));                        // open-bus gap
        Assert.False(pt.IsVideoRamAddress(0x5FFF));
    }

    [Fact]
    public void IsVideoRamAddress_P2000M_FullVramWindow()
    {
        var pt = new PageTable(new MachineConfig { Model = MachineModel.P2000M });
        Assert.True(pt.IsVideoRamAddress(PageTable.VideoRamStart));        // 0x5000
        Assert.True(pt.IsVideoRamAddress(0x5FFF));                         // M-model end
        Assert.False(pt.IsVideoRamAddress(PageTable.BaseRamStart));        // 0x6000 — separate chip
    }
}

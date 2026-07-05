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
    /// A tight loop that writes to base RAM (0x6000) runs throughout the field, hitting
    /// both active display lines and vblank. The collision with video fetch slots must
    /// produce at least one corrupted cell, flagged in the overlay.
    /// </summary>
    [Fact]
    public void ContentionDuringActiveDisplay_ProducesCorruptedCells()
    {
        var machine = new Machine();

        // Tight DRAM-write loop: LD HL,0x6000 / LD (HL),H / JR -3
        // LD (HL),H drives MREQ with address 0x6000 (IsDramAddress = true) during the write phase.
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x60, // LD HL, 0x6000
            0x74,             // LD (HL), H   — DRAM write each pass
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

        // HALT: the CPU repeatedly fetches 0x76 from PC=0x0000 (ROM — not DRAM).
        // IsDramAddress(0x0000) == false → no contention triggered.
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

        // Same hammering loop as the speckle test.
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x60, // LD HL, 0x6000
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

    // ---- Overlay reset ---------------------------------------------------------------------

    [Fact]
    public void CorruptionOverlay_ClearsAfterEachField()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x21, 0x00, 0x60, // LD HL, 0x6000
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

    // ---- IsDramAddress filter --------------------------------------------------------------

    [Fact]
    public void IsDramAddress_ReturnsFalse_ForRomAndSlot1()
    {
        Assert.False(PageTable.IsDramAddress(PageTable.RomStart));
        Assert.False(PageTable.IsDramAddress(PageTable.RomEnd));
        Assert.False(PageTable.IsDramAddress(PageTable.CartridgeStart));
        Assert.False(PageTable.IsDramAddress(PageTable.CartridgeEnd));
    }

    [Fact]
    public void IsDramAddress_ReturnsTrue_ForVramAndRam()
    {
        Assert.True(PageTable.IsDramAddress(PageTable.VideoRamStart));
        Assert.True(PageTable.IsDramAddress(PageTable.BaseRamStart));
        Assert.True(PageTable.IsDramAddress(PageTable.BaseRamEnd));
        Assert.True(PageTable.IsDramAddress(PageTable.ExpansionRamStart));
        Assert.True(PageTable.IsDramAddress(PageTable.BankedWindowStart));
    }
}

using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Io;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// The 80-column modification board (machine milestone 25; reference doc §5 "80-column mode",
/// primary source <c>docs/P2000T-80column-board-1986-newsletter.md</c>).
///
/// The regression gate for this milestone lives in the rest of the suite, not here: every
/// pre-existing test must pass unchanged with the board absent. These tests only cover the
/// fitted machine, plus the two "absent" behaviours that are load-bearing in their own right
/// (open-bus port 0x70 and the presence probe).
/// </summary>
public class EightyColumnBoardTests
{
    private static MachineConfig FittedConfig() => new()
    {
        Modifications = new ModificationsConfig { EightyColumnBoard = true },
    };

    private static Machine FittedMachine() => new(FittedConfig());

    /// <summary>Flat pixel offset of VRAM row 0/column 0's EVEN sub-scanline in the full-field
    /// buffer (the active graphics-window crop origin).</summary>
    private const int ActiveOrigin = Video.ActiveOffsetY * Video.Width + Video.ActiveOffsetX;

    /// <summary>Same cell, but the buffer row carrying glyph row <see cref="MidGlyphRow"/> —
    /// active scanline 2's even sub-row. Glyph row 0 is blank padding for most SAA5050 glyphs
    /// (the trap the 2026-07-22 scanline-counter bug hid behind), so any test that has to tell
    /// two different characters apart must look at a row with actual pixels in it.</summary>
    private const int MidGlyphOrigin = (Video.ActiveOffsetY + 4) * Video.Width + Video.ActiveOffsetX;

    private const int MidGlyphRow = 4;

    private static (Video Video, PageTable Memory) CreateVideo(bool boardFitted)
    {
        var memory = new PageTable(new MachineConfig());
        return (new Video(memory, boardFitted), memory);
    }

    private static void RunOneField(Video video)
    {
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) video.Tick();
    }

    // ── Ports and presence ────────────────────────────────────────────────────────────────

    [Fact]
    public void BoardAbsent_Port70_ReadsOpenBus_SoThePresenceProbeCorrectlyReportsAbsent()
    {
        var machine = new Machine();

        // The article's own probe (§13.25.9): write, read back, conclude "present" only if the
        // write was taken over. 0xFF is neither 0x00 nor 0x01, so the probe correctly fails.
        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);
        var readBack = machine.Ports.Read(EightyColumnBoard.StatusPort);

        Assert.Equal(PortDispatch.OpenBus, readBack);
        Assert.Equal(0xFF, readBack);
        Assert.NotEqual(0x01, readBack);
        Assert.NotEqual(0x00, readBack); // the specific wrong answer a zero-returning stub gives
        Assert.Null(machine.EightyColumn);
        Assert.False(machine.Video.IsEightyColumn);
    }

    [Fact]
    public void BoardAbsent_WriteToPort00_DoesNotDisturbTheKeyboardsOwnReadsOfPorts00To09()
    {
        var machine = new Machine();
        var before = machine.Ports.Read(0x00);

        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);

        Assert.Equal(before, machine.Ports.Read(0x00));
    }

    [Fact]
    public void BoardFitted_ModeLatchRoundTripsThroughPort70()
    {
        var machine = FittedMachine();

        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);
        Assert.Equal(0x01, machine.Ports.Read(EightyColumnBoard.StatusPort));
        Assert.True(machine.Video.IsEightyColumn);

        machine.Ports.Write(EightyColumnBoard.ModePort, 0x00);
        Assert.Equal(0x00, machine.Ports.Read(EightyColumnBoard.StatusPort));
        Assert.False(machine.Video.IsEightyColumn);
    }

    [Fact]
    public void BoardFitted_PresenceProbeSucceeds_SwitchingBackAndForth()
    {
        var machine = FittedMachine();

        // §13.25.9: "by switching back and forth a few times ... and each time checking whether
        // this has been taken over, a program can 'see' whether an 80-character board is present."
        for (var i = 0; i < 3; i++)
        {
            machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);
            Assert.Equal(0x01, machine.Ports.Read(EightyColumnBoard.StatusPort));
            machine.Ports.Write(EightyColumnBoard.ModePort, 0x00);
            Assert.Equal(0x00, machine.Ports.Read(EightyColumnBoard.StatusPort));
        }
    }

    [Fact]
    public void BoardFitted_Port70_UpperBitsReadZero()
    {
        var machine = FittedMachine();

        machine.Ports.Write(EightyColumnBoard.ModePort, 0xFF);

        // Flagged in the device as likely-but-not-certain: the article gives the whole returned
        // byte as 0 or 1, but BASIC's INP shorthand could be hiding a mask.
        Assert.Equal(0x01, machine.Ports.Read(EightyColumnBoard.StatusPort));
    }

    [Theory]
    [InlineData(0xFE, false)] // every bit but bit 0
    [InlineData(0xFF, true)]
    [InlineData(0x02, false)]
    [InlineData(0x81, true)]
    public void BoardFitted_OnlyBitZeroIsHonoured(byte written, bool expectEightyColumn)
    {
        var machine = FittedMachine();

        machine.Ports.Write(EightyColumnBoard.ModePort, written);

        Assert.Equal(expectEightyColumn, machine.EightyColumn!.EightyColumn);
        Assert.Equal(expectEightyColumn ? 0x01 : 0x00,
                     machine.Ports.Read(EightyColumnBoard.StatusPort));
    }

    [Fact]
    public void BoardFitted_ResetSelects40Columns()
    {
        // "Bij RESET wordt automatisch de 40 karakter-stand gekozen" (§13.25.9). The mode latch
        // is a flip-flop on the board's reset line, so this holds for BOTH cold and warm reset
        // — Machine.Reset() is the shared path for both.
        var machine = FittedMachine();
        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);
        Assert.True(machine.Video.IsEightyColumn);

        machine.Reset();

        Assert.False(machine.EightyColumn!.EightyColumn);
        Assert.False(machine.Video.IsEightyColumn);
        Assert.Equal(0x00, machine.Ports.Read(EightyColumnBoard.StatusPort));
    }

    [Fact]
    public void BoardFitted_ColdBoot_StartsIn40Columns()
    {
        var machine = FittedMachine();

        Assert.False(machine.EightyColumn!.EightyColumn);
        Assert.False(machine.Video.IsEightyColumn);
        Assert.Equal(VideoFetchUnit.Columns, machine.Video.CorruptionOverlayWidth);
    }

    [Fact]
    public void OutInstruction_FromRealZ80Code_DrivesTheLatch()
    {
        // End-to-end through the CPU + port dispatch, not just Ports.Write: the article's own
        // commissioning procedure is literally `OUT 0,1`.
        var machine = FittedMachine();
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01,       // LD A,1
            0xD3, 0x00,       // OUT (0x00),A
            0xDB, 0x70,       // IN A,(0x70)
            0x76,             // HALT
        });

        for (var i = 0; i < 100; i++) machine.Tick();

        Assert.True(machine.Video.IsEightyColumn);
        Assert.Equal(0x01, machine.Cpu.Reg.A);
    }

    // ── Cadence and geometry ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, VideoFetchUnit.Columns)]
    [InlineData(true, VideoFetchUnit.ColumnsEightyColumn)]
    public void FetchSlotsPerActiveLine_MatchTheCadence(bool eightyColumn, int expectedColumns)
    {
        var unit = new VideoFetchUnit { EightyColumn = eightyColumn };
        for (var i = 0; i < VideoFetchUnit.VerticalBlankLines * VideoFetchUnit.TStatesPerLine; i++)
            unit.Tick();

        var columns = new List<int>();
        unit.ColumnFetch += columns.Add;
        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++) unit.Tick();

        Assert.Equal(Enumerable.Range(0, expectedColumns), columns);
    }

    [Fact]
    public void TotalFetchEligibleTimePerActiveLine_IsIdenticalInBothModes()
    {
        // The single best assertion that the cadence change did not disturb the raster: the
        // SAA5020 is not overclocked, so the 40 µs active window is unchanged — 80 slots each
        // half as long, inside the same window, not 80 slots taking twice as long.
        var (first40, last40) = FetchWindow(eightyColumn: false);
        var (first80, last80) = FetchWindow(eightyColumn: true);

        Assert.Equal(0, first40);
        Assert.Equal(0, first80);
        Assert.True(last40 < VideoFetchUnit.ActiveTStatesPerLine);
        Assert.True(last80 < VideoFetchUnit.ActiveTStatesPerLine);
        Assert.Equal(100, VideoFetchUnit.ActiveTStatesPerLine); // 40 µs at 2.5 MHz
    }

    [Fact]
    public void EightyColumnFetchSlots_AreOnePerTState_NeverColliding()
    {
        // 0.5 µs is 1.25 T-states, so slots do not align to T-state boundaries. Contention is
        // resolved once per T-state, so two slots sharing one T-state would silently lose a
        // fetch. Confirm the documented +1,+1,+1,+2 pattern instead.
        var slots = FetchSlotTStates(eightyColumn: true);

        Assert.Equal(VideoFetchUnit.ColumnsEightyColumn, slots.Count);
        Assert.Equal(slots.Count, slots.Distinct().Count());
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 6, 7, 8, 10 }, slots.Take(9));
        Assert.Equal(98, slots[^1]);
    }

    [Fact]
    public void FortyColumnFetchSlots_AreUnchangedByTheCadenceParameter()
    {
        var slots = FetchSlotTStates(eightyColumn: false);

        Assert.Equal(VideoFetchUnit.Columns, slots.Count);
        Assert.Equal(Enumerable.Range(0, VideoFetchUnit.Columns)
                               .Select(c => c * VideoFetchUnit.ActiveTStatesPerLine / VideoFetchUnit.Columns),
                     slots);
    }

    [Fact]
    public void RasterGeometry_IsIdenticalInBothModes()
    {
        // The SAA5020 keeps generating line and field timing at 6 MHz (§13.25.2/§13.25.3), so
        // NONE of this may move: only the character-fetch cadence doubles.
        var (video, _) = CreateVideo(boardFitted: true);
        var beforeLength = video.Framebuffer.Length;

        video.SetEightyColumnMode(true);

        Assert.Equal(beforeLength, video.Framebuffer.Length);
        Assert.Equal(928 * 626, video.Framebuffer.Length);
        Assert.Equal(160, VideoFetchUnit.TStatesPerLine);
        Assert.Equal(50_000, VideoFetchUnit.TStatesPerField);
        Assert.Equal(240, VideoFetchUnit.ActiveLines);
        Assert.Equal(49, VideoFetchUnit.VerticalBlankLines);
        Assert.Equal(144, Video.ActiveOffsetX);
        Assert.Equal(98, Video.ActiveOffsetY);
        Assert.Equal(640, Video.ActiveWidth);
        Assert.Equal(480, Video.ActiveHeight);
    }

    [Fact]
    public void EightyColumnMode_FetchesAllEightyVramColumnsOfARow_NoGapsNoRepeats()
    {
        // Address coverage proven through the rendered result: 80 DISTINCT glyphs across the
        // row can only happen if the fetch walked videoBase + charRow*80 + 0..79 exactly once
        // each. (The row stride was already 80 before this milestone — see the findings log.)
        var (video, memory) = CreateVideo(boardFitted: true);
        video.SetEightyColumnMode(true);
        for (var col = 0; col < 80; col++)
            memory.Write((ushort)(PageTable.VideoRamStart + col), (byte)('A' + col % 26));

        RunOneField(video);

        var rendered = new List<string>();
        for (var col = 0; col < 80; col++)
        {
            var expected = ExpectedCellRow80((char)('A' + col % 26), MidGlyphRow);
            var actual = new uint[Saa5050Generator.EightyColumnLanes];
            Array.Copy(video.Framebuffer,
                       MidGlyphOrigin + col * Saa5050Generator.EightyColumnLanes,
                       actual, 0, actual.Length);
            Assert.Equal(expected, actual);
            rendered.Add(string.Join(',', actual));
        }

        // Guard against passing vacuously on a row where every glyph is blank padding: the 26
        // letters must produce a real spread of distinct 8-lane patterns. (Not 26 distinct —
        // many capitals genuinely share a mid-glyph row shape at 16 lanes too; see
        // EightyColumnDownsample_LosesNoGlyphIdentity below for the exact claim about lanes.)
        Assert.True(rendered.Distinct().Count() >= 5,
                    $"only {rendered.Distinct().Count()} distinct cell renderings - the chosen " +
                    "glyph row cannot distinguish characters, so this test proves nothing");
    }

    [Fact]
    public void EightyColumnMode_SecondScreenHalf_IsVisibleWithoutPanning()
    {
        // The motivating case (VOLORG): what a 40-column machine could only reach by panning
        // to column 40 is simply on screen in 80-column mode.
        var (video, memory) = CreateVideo(boardFitted: true);
        video.SetEightyColumnMode(true);
        memory.Write((ushort)(PageTable.VideoRamStart + 40), (byte)'#');

        RunOneField(video);

        var expected = ExpectedCellRow80('#', MidGlyphRow);
        var actual = new uint[Saa5050Generator.EightyColumnLanes];
        Array.Copy(video.Framebuffer,
                   MidGlyphOrigin + 40 * Saa5050Generator.EightyColumnLanes,
                   actual, 0, actual.Length);
        Assert.Equal(expected, actual);
        Assert.NotEqual(ExpectedCellRow80(' ', MidGlyphRow), actual); // not vacuously blank
    }

    [Fact]
    public void EightyColumnDownsample_LosesNoGlyphIdentity()
    {
        // Milestone spec §5.2's decision point, measured rather than assumed. 8 lanes per
        // character is fewer than the 12 half-dot sub-columns the rounding pass computes at, so
        // the 16-lane table's extra resolution is genuinely being discarded — but the question
        // that matters is whether that makes CHARACTERS ambiguous. It does not: for every one of
        // the 20 packed glyph rows, the number of distinct patterns across all 96 characters is
        // EXACTLY the same at 8 lanes as at 16. Two glyphs that a 40-column screen can tell
        // apart are still distinguishable at 80 columns; only the anti-aliasing gradient is
        // coarser. This is the evidence behind not raising the global lanes-per-char-time
        // constant (an owner decision — it would change the buffer width for both modes).
        for (var row = 0; row < Saa5050GlyphTables.PackedRowsPerGlyph; row++)
        {
            var at16 = new HashSet<string>();
            var at8 = new HashSet<string>();
            for (var code = 0x20; code < 0x80; code++)
            {
                at16.Add(string.Join(',', ExpectedCellRow16((char)code, row)));
                at8.Add(string.Join(',', ExpectedCellRow80((char)code, row)));
            }

            Assert.Equal(at16.Count, at8.Count);
        }
    }

    // ── Pan register (cleared in hardware, not saved and restored) ─────────────────────────

    [Fact]
    public void EnteringEightyColumnMode_ClearsPanX()
    {
        var (video, _) = CreateVideo(boardFitted: true);
        video.PanX = 40;

        video.SetEightyColumnMode(true);

        Assert.Equal(0, video.PanX);
    }

    [Fact]
    public void WhileInEightyColumnMode_PanWritesAreHeldIneffective()
    {
        var (video, _) = CreateVideo(boardFitted: true);
        video.SetEightyColumnMode(true);

        video.PanX = 17;

        Assert.Equal(0, video.PanX);
    }

    [Fact]
    public void ReturningTo40Columns_LeavesPanXAtZero_NotItsPreviousValue()
    {
        // The mode latch drives the 74LS273's ASYNCHRONOUS master reset (§13.25.2). A
        // save-and-restore implementation would look plausible here and be wrong.
        var (video, _) = CreateVideo(boardFitted: true);
        video.PanX = 40;

        video.SetEightyColumnMode(true);
        video.SetEightyColumnMode(false);

        Assert.Equal(0, video.PanX);

        video.PanX = 40; // ...and it is writable again
        Assert.Equal(40, video.PanX);
    }

    [Fact]
    public void PanRegisterClear_IsDrivenByTheBoardsLatch_EndToEnd()
    {
        var machine = FittedMachine();
        machine.Video.PanX = 40;

        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);

        Assert.Equal(0, machine.Video.PanX);
    }

    // ── Corrupted-cell overlay ────────────────────────────────────────────────────────────

    [Fact]
    public void CorruptionOverlayWidth_TracksTheMode_AndIsExposedNotAssumed()
    {
        var (video, _) = CreateVideo(boardFitted: true);
        Assert.Equal(40, video.CorruptionOverlayWidth);

        video.SetEightyColumnMode(true);
        Assert.Equal(80, video.CorruptionOverlayWidth);

        video.SetEightyColumnMode(false);
        Assert.Equal(40, video.CorruptionOverlayWidth);
    }

    [Fact]
    public void CorruptionOverlay_IsAllocatedAtTheWidestViewport_WhenTheBoardIsFitted()
    {
        var (fitted, _) = CreateVideo(boardFitted: true);
        var (stock, _) = CreateVideo(boardFitted: false);

        Assert.Equal(80 * Video.CharRows, fitted.CorruptionOverlay.Length);
        Assert.Equal(40 * Video.CharRows, stock.CorruptionOverlay.Length);
    }

    [Fact]
    public void CorruptionOverlay_MidFieldModeChange_StaysInRange_AndReportsTheFinalStride()
    {
        // Milestone spec §5.4's documented edge case: the map is read at the width in effect at
        // FieldComplete; cells fetched under the other cadence are reinterpreted under that
        // final stride ("nearest cell"). What must NOT happen is an out-of-range write.
        var (video, _) = CreateVideo(boardFitted: true);
        var widthAtFieldComplete = -1;
        video.FieldComplete += () => widthAtFieldComplete = video.CorruptionOverlayWidth;

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            if (i == VideoFetchUnit.TStatesPerField / 2) video.SetEightyColumnMode(true);
            video.Tick();
            video.CorruptLastFetch(); // pretend the CPU collides with every single fetch
        }

        Assert.Equal(80, widthAtFieldComplete);
    }

    // ── Save-state ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SaveState_RoundTripsTheModeBit()
    {
        var machine = FittedMachine();
        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);

        using var buffer = new MemoryStream();
        MachineStateFile.Save(machine, buffer);
        buffer.Position = 0;
        var restored = MachineStateFile.Load(buffer);

        Assert.True(restored.EightyColumn!.EightyColumn);
        Assert.True(restored.Video.IsEightyColumn);
        Assert.Equal(0x01, restored.Ports.Read(EightyColumnBoard.StatusPort));
        Assert.Equal(80, restored.Video.CorruptionOverlayWidth);
    }

    [Fact]
    public void SaveState_RoundTripsFortyColumnMode_OnAFittedMachine()
    {
        var machine = FittedMachine();

        using var buffer = new MemoryStream();
        MachineStateFile.Save(machine, buffer);
        buffer.Position = 0;
        var restored = MachineStateFile.Load(buffer);

        Assert.False(restored.EightyColumn!.EightyColumn);
        Assert.False(restored.Video.IsEightyColumn);
        Assert.Equal(40, restored.Video.CorruptionOverlayWidth);
    }

    // ── Config ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Config_EightyColumnBoardOnAP2000M_IsRejectedAsInvalid()
    {
        var config = new MachineConfig
        {
            Model = MachineModel.P2000M,
            Modifications = new ModificationsConfig { EightyColumnBoard = true },
        };

        var ex = Assert.Throws<ArgumentException>(() => new Machine(config));
        Assert.Contains("P2000T-only", ex.Message);
    }

    [Fact]
    public void Config_DefaultsToNoBoard_AndArtifactsOn()
    {
        var config = new MachineConfig();

        Assert.False(config.Modifications.EightyColumnBoard);
        Assert.True(config.Modifications.ShowEightyColumnArtifacts);
    }

    [Fact]
    public void Config_RoundTripsThroughCfg_AndAPreExistingCfgLoadsAsNoBoard()
    {
        var json = MachineConfigFile.Serialize(FittedConfig());
        var reloaded = MachineConfigFile.Deserialize(json);
        Assert.True(reloaded.Modifications.EightyColumnBoard);
        Assert.True(reloaded.Modifications.ShowEightyColumnArtifacts);

        // A v1 .cfg predates the axis entirely — no `modifications` key at all.
        var legacy = MachineConfigFile.Deserialize("""{ "version": 1, "model": "P2000T" }""");
        Assert.False(legacy.Modifications.EightyColumnBoard);
        Assert.True(legacy.Modifications.ShowEightyColumnArtifacts);
    }

    [Fact]
    public void Config_ArtifactToggleRoundTripsFalse()
    {
        var json = MachineConfigFile.Serialize(new MachineConfig
        {
            Modifications = new ModificationsConfig
            {
                EightyColumnBoard = true,
                ShowEightyColumnArtifacts = false,
            },
        });

        var reloaded = MachineConfigFile.Deserialize(json);

        Assert.True(reloaded.Modifications.EightyColumnBoard);
        Assert.False(reloaded.Modifications.ShowEightyColumnArtifacts);
    }

    [Fact]
    public void CaptureCurrentConfig_CarriesTheModificationsAxis()
    {
        var machine = FittedMachine();

        var captured = machine.CaptureCurrentConfig();

        Assert.True(captured.Modifications.EightyColumnBoard);
    }

    // ── The out-of-spec artifact placeholder ──────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true)]    // toggle on, 80 columns  -> block
    [InlineData(true, false, false)]  // toggle on, 40 columns  -> unchanged
    [InlineData(false, true, false)]  // toggle off, 80 columns -> unchanged
    [InlineData(false, false, false)] // toggle off, 40 columns -> unchanged
    public void ArtifactControlCharacter_RendersAsABlock_OnlyWithTheToggleOnAndInEightyColumnMode(
        bool artifacts, bool eightyColumn, bool expectBlock)
    {
        AssertCellRendersAsBlock(code: 13, artifacts, eightyColumn, expectBlock); // double height
    }

    [Theory]
    [InlineData(8)]  // Flash
    [InlineData(13)] // Double height
    [InlineData(24)] // Conceal ("hidden")
    public void ArtifactPlaceholder_FiresOnTheOwnerSpecifiedControlCodes(byte code)
    {
        AssertCellRendersAsBlock(code, artifacts: true, eightyColumn: true, expectBlock: true);
    }

    [Theory]
    [InlineData(0x00)] // Not a defined control code at all — and the cleared/power-on VRAM fill.
    [InlineData(0x02)] // Alpha green
    [InlineData(0x09)] // Steady (flash off)
    [InlineData(0x0C)] // Normal height (double height off)
    [InlineData(0x1D)] // New background
    [InlineData(0x1E)] // Hold graphics
    public void ArtifactPlaceholder_DoesNotFireOnOtherBlankCells(byte code)
    {
        // Owner report, 2026-08-04, from a real 80-column screen: firing on everything that
        // renders blank is far too strong. 0x00 is the case that made it obvious — it is the
        // cleared/power-on VRAM fill AND not a defined SAA5050 control code, so an empty screen
        // rendered as solid blocks edge to edge.
        AssertCellRendersAsBlock(code, artifacts: true, eightyColumn: true, expectBlock: false);
    }

    [Fact]
    public void ArtifactPlaceholder_LeavesAnEntirelyBlankScreenBlank()
    {
        // The owner-visible symptom, asserted across a whole row rather than one cell.
        var (video, _) = CreateVideo(boardFitted: true); // VRAM is all 0x00
        video.ShowEightyColumnArtifacts = true;
        video.SetEightyColumnMode(true);

        RunOneField(video);

        var paletteIndex = (byte)((0 << 5) | (7 << 2));
        var spaceColor = Saa5050Palette.ColorTable[paletteIndex + 0];
        var row = new uint[Video.ActiveWidth];
        Array.Copy(video.Framebuffer, ActiveOrigin, row, 0, row.Length);
        Assert.All(row, pixel => Assert.Equal(spaceColor, pixel));
    }

    private static void AssertCellRendersAsBlock(
        byte code, bool artifacts, bool eightyColumn, bool expectBlock)
    {
        var cell = RenderSingleCell(code, artifacts, eightyColumn);

        if (expectBlock)
        {
            // The three artifact codes (8/13/24) are all set-AFTER and none of them touch the
            // background, so this cell renders with the previous foreground, white (7), over
            // black (0) — the block is that foreground at full coverage.
            var paletteIndex = (byte)((0 << 5) | (7 << 2));
            var blockColor = Saa5050Palette.ColorTable[paletteIndex + 3];
            var spaceColor = Saa5050Palette.ColorTable[paletteIndex + 0];
            Assert.NotEqual(blockColor, spaceColor); // guard: the assertion below must be real
            Assert.All(cell, pixel => Assert.Equal(blockColor, pixel));
            return;
        }

        // "No change from today" stated literally, rather than against a hardcoded palette:
        // some control codes legitimately alter this very cell's colours (29 "new background"
        // applies immediately, not set-after), so the only claim worth asserting is that the
        // artifact toggle made no difference at all.
        var withArtifactsOff = RenderSingleCell(code, artifacts: false, eightyColumn);
        Assert.Equal(withArtifactsOff, cell);
    }

    /// <summary>Renders VRAM row 0 / column 0 holding <paramref name="code"/> for one field and
    /// returns that cell's lanes.</summary>
    private static uint[] RenderSingleCell(byte code, bool artifacts, bool eightyColumn)
    {
        var (video, memory) = CreateVideo(boardFitted: true);
        video.ShowEightyColumnArtifacts = artifacts;
        video.SetEightyColumnMode(eightyColumn);
        memory.Write(PageTable.VideoRamStart, code);

        RunOneField(video);

        var lanes = eightyColumn ? Saa5050Generator.EightyColumnLanes : Saa5050Generator.HiResLanes;
        var cell = new uint[lanes];
        Array.Copy(video.Framebuffer, ActiveOrigin, cell, 0, lanes);
        return cell;
    }

    [Fact]
    public void ArtifactPlaceholder_LeavesOrdinaryCharactersAlone()
    {
        var (video, memory) = CreateVideo(boardFitted: true);
        video.ShowEightyColumnArtifacts = true;
        video.SetEightyColumnMode(true);
        memory.Write(PageTable.VideoRamStart, (byte)'A');

        RunOneField(video);

        var expected = ExpectedCellRow80('A', MidGlyphRow);
        var actual = new uint[Saa5050Generator.EightyColumnLanes];
        Array.Copy(video.Framebuffer, MidGlyphOrigin, actual, 0, actual.Length);
        Assert.Equal(expected, actual);
        // 'A' must not have been replaced by the artifact block, and must not be blank either.
        Assert.NotEqual(ExpectedCellRow80(' ', MidGlyphRow), actual);
    }

    [Fact]
    public void ArtifactToggle_IsSeededFromConfig()
    {
        var on = new Machine(FittedConfig());
        var off = new Machine(new MachineConfig
        {
            Modifications = new ModificationsConfig
            {
                EightyColumnBoard = true,
                ShowEightyColumnArtifacts = false,
            },
        });

        Assert.True(on.Video.ShowEightyColumnArtifacts);
        Assert.False(off.Video.ShowEightyColumnArtifacts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static List<int> FetchSlotTStates(bool eightyColumn)
    {
        var unit = new VideoFetchUnit { EightyColumn = eightyColumn };
        for (var i = 0; i < VideoFetchUnit.VerticalBlankLines * VideoFetchUnit.TStatesPerLine; i++)
            unit.Tick();

        var slots = new List<int>();
        unit.ColumnFetch += _ => slots.Add(unit.LineTState);
        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++) unit.Tick();
        return slots;
    }

    private static (int First, int Last) FetchWindow(bool eightyColumn)
    {
        var slots = FetchSlotTStates(eightyColumn);
        return (slots[0], slots[^1]);
    }

    /// <summary>The expected 16 lanes of a cell in 40-column mode — the stock render path,
    /// reproduced here only as the baseline the 80-column downsample is measured against.</summary>
    private static uint[] ExpectedCellRow16(char code, int glyphRow, int fg = 7, int bg = 0)
    {
        var chardef = Saa5050GlyphTables.Normal[
            (code - 0x20) * Saa5050GlyphTables.PackedRowsPerGlyph + glyphRow];
        var paletteIndex = (byte)((bg << 5) | (fg << 2));
        var expected = new uint[Saa5050Generator.HiResLanes];
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            expected[pixel] = Saa5050Palette.ColorTable[paletteIndex + (chardef & 3)];
            chardef >>= 2;
        }

        return expected;
    }

    /// <summary>The expected 8 lanes of a cell in 80-column mode: the same packed 16-lane
    /// rounded glyph row, box-filtered down to 8 (see
    /// <see cref="Saa5050Generator.RenderField"/>).</summary>
    private static uint[] ExpectedCellRow80(char code, int glyphRow, int fg = 7, int bg = 0)
    {
        var chardef = Saa5050GlyphTables.Normal[
            (code - 0x20) * Saa5050GlyphTables.PackedRowsPerGlyph + glyphRow];
        var paletteIndex = (byte)((bg << 5) | (fg << 2));
        var expected = new uint[Saa5050Generator.EightyColumnLanes];
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            var a = (int)(chardef & 3);
            var b = (int)((chardef >> 2) & 3);
            expected[pixel] = Saa5050Palette.ColorTable[paletteIndex + ((a + b + 1) >> 1)];
            chardef >>= 4;
        }

        return expected;
    }
}

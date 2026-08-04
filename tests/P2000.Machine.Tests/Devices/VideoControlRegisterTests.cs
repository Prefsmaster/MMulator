using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Io;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// The video control register — output ports <c>0x30</c>-<c>0x3F</c> (machine milestone 26;
/// reference doc §5g, sourced from the official Philips manual). Bit 7 blanks the video, bits
/// 6-0 are the horizontal pan. Before this milestone nothing registered the range at all, so no
/// software could pan or blank.
/// </summary>
public class VideoControlRegisterTests
{
    private const int ActiveOrigin = Video.ActiveOffsetY * Video.Width + Video.ActiveOffsetX;

    /// <summary>Glyph row 0 is blank padding for nearly every SAA5050 glyph, and a blank cell on
    /// a black background renders as exactly <see cref="Video.BlankedColor"/> — so a test that
    /// compares cell contents, or checks that blanking changed anything, MUST look at a row with
    /// real pixels in it or it passes vacuously. (Two of these tests did, and were caught by
    /// their own NotEqual guards.) Row 4 = active scanline 2's even sub-row.</summary>
    private const int MidGlyphRow = 4;

    private const int MidGlyphOrigin =
        (Video.ActiveOffsetY + 4) * Video.Width + Video.ActiveOffsetX;

    private static (Video Video, PageTable Memory) CreateVideo()
    {
        var memory = new PageTable(new MachineConfig());
        return (new Video(memory), memory);
    }

    private static void RunOneField(Video video)
    {
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) video.Tick();
    }

    /// <summary>Runs both parities so every row of the active window has been written this
    /// pass — there is no inter-field clear (the interlace comb), so a single field only ever
    /// touches half the rows.</summary>
    private static void RunBothFields(Video video)
    {
        RunOneField(video);
        RunOneField(video);
    }

    // ── Ports and decode ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x31)]
    [InlineData(0x37)]
    [InlineData(0x3E)]
    [InlineData(0x3F)]
    public void EveryPortInTheRange_IsTheSameRegister(byte port)
    {
        // Partial address decode: only the high nibble is significant (§5g), so a write to 0x3A
        // must behave identically to one to 0x30. Claiming a single port would silently drop 15
        // of the 16 aliases.
        var machine = new Machine();

        machine.Ports.Write(port, 0x80 | 25);

        Assert.Equal(25, machine.Video.PanX);
        Assert.True(machine.Video.VideoBlanked);
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x37)]
    [InlineData(0x3F)]
    public void TheRangeIsWriteOnly_ReadsReturnOpenBus_NotAShadowOfTheLastWrite(byte port)
    {
        var machine = new Machine();

        machine.Ports.Write(port, 0x80 | 25);

        // §5g: "there is no read-back of pan or blank state". A shadow-byte read would invent a
        // path the hardware does not have — the same trap the 80-column milestone avoided on
        // port 0x70.
        Assert.Equal(PortDispatch.OpenBus, machine.Ports.Read(port));
        Assert.Equal(0xFF, machine.Ports.Read(port));
        Assert.NotEqual(0x80 | 25, machine.Ports.Read(port));
    }

    [Fact]
    public void OutInstruction_FromRealZ80Code_DrivesTheRegister()
    {
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x28,       // LD A,40
            0xD3, 0x30,       // OUT (0x30),A
            0x76,             // HALT
        });

        for (var i = 0; i < 100; i++) machine.Tick();

        Assert.Equal(40, machine.Video.PanX);
        Assert.False(machine.Video.VideoBlanked);
    }

    [Fact]
    public void OutInstruction_ToAnAliasPort_IsIdentical()
    {
        // `OUT 63,40` from the manual acceptance check.
        var machine = new Machine();
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x28,       // LD A,40
            0xD3, 0x3F,       // OUT (0x3F),A
            0x76,             // HALT
        });

        for (var i = 0; i < 100; i++) machine.Tick();

        Assert.Equal(40, machine.Video.PanX);
    }

    // ── Pan ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pan40_PutsTheSecondScreenAtTheLeftmostOnScreenColumn()
    {
        // The sourced meaning of pan 40: "the 2nd screen to the right" (§5/§5g). Proven through
        // the fetch address — on-screen column 0 must render VRAM column 40 of that row.
        var (video, memory) = CreateVideo();
        memory.Write((ushort)(PageTable.VideoRamStart + 40), (byte)'Z');
        video.PanX = 40;

        RunOneField(video);

        Assert.Equal(ExpectedCellRow('Z', MidGlyphRow), ReadCell(video, column: 0));
    }

    [Fact]
    public void Pan40_ReachesTheLastByteOfTheRow_WithoutRunningIntoTheNextRow()
    {
        // pan 40 + column 39 = 79, exactly the last byte of an 80-wide row. This is the
        // arithmetic that makes the old `% 80` wrap unreachable, so it is worth pinning
        // directly: VRAM column 79 must appear, and row 1's first byte must NOT.
        var (video, memory) = CreateVideo();
        memory.Write((ushort)(PageTable.VideoRamStart + 79), (byte)'Z');
        memory.Write((ushort)(PageTable.VideoRamStart + 80), (byte)'Q'); // row 1, column 0
        video.PanX = 40;

        RunOneField(video);

        Assert.Equal(ExpectedCellRow('Z', MidGlyphRow), ReadCell(video, column: 39));
        Assert.NotEqual(ExpectedCellRow('Q', MidGlyphRow), ReadCell(video, column: 39));
    }

    [Theory]
    [InlineData(41)]
    [InlineData(100)]
    [InlineData(127)]
    public void PanValuesAbove40_ClampTo40_TheyDoNotWrapOrMask(int written)
    {
        // §5g: values above 40 are genuinely undefined on real hardware; clamping is the owner's
        // chosen PLACEHOLDER pending a real-machine test. What matters here is that it is a
        // clamp — a wrap would give 127 % 80 = 47, a mask 127 & 0x3F = 63.
        var machine = new Machine();

        machine.Ports.Write(Video.ControlPortFirst, (byte)written);

        Assert.Equal(40, machine.Video.PanX);
    }

    [Fact]
    public void ClampedPanValues_DisplayIdenticallyToPan40()
    {
        // The behavioural form of the clamp, from the manual acceptance check: OUT 48,127 must
        // look exactly like OUT 48,40, not merely report the same number.
        var at40 = RenderRowWithPan(40);
        var at127 = RenderRowWithPan(127);

        Assert.Equal(at40, at127);
    }

    [Fact]
    public void Bit7SetAlongsideAPanValue_DoesNotCorruptThePan()
    {
        var machine = new Machine();

        machine.Ports.Write(Video.ControlPortFirst, 0x80 | 20);

        Assert.Equal(20, machine.Video.PanX);
        Assert.True(machine.Video.VideoBlanked);
    }

    [Fact]
    public void Panning_ChangesWhatIsDisplayed_WithoutChangingVramContents()
    {
        var (video, memory) = CreateVideo();
        memory.Write(PageTable.VideoRamStart, (byte)'A');
        memory.Write((ushort)(PageTable.VideoRamStart + 5), (byte)'Z');

        // Both parities each time: a single field only writes its own half of the rows (the
        // interlace comb), so re-reading the same row after ONE more field would still show the
        // previous pan's render.
        video.PanX = 0;
        RunBothFields(video);
        var atPan0 = ReadCell(video, column: 0);

        video.PanX = 5;
        RunBothFields(video);
        var atPan5 = ReadCell(video, column: 0);

        Assert.Equal(ExpectedCellRow('A', MidGlyphRow), atPan0);
        Assert.Equal(ExpectedCellRow('Z', MidGlyphRow), atPan5);
        // VRAM itself is untouched by panning — it is a fetch-address offset, not a data move.
        Assert.Equal((byte)'A', memory.Read(PageTable.VideoRamStart));
        Assert.Equal((byte)'Z', memory.Read((ushort)(PageTable.VideoRamStart + 5)));
    }

    // ── Blank (bit 7) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bit7_BlanksTheActiveWindowToBlack_LeavingVramIntact()
    {
        var (video, memory) = CreateVideo();
        for (var col = 0; col < 40; col++)
            memory.Write((ushort)(PageTable.VideoRamStart + col), (byte)'W');

        // Positive control first: unblanked, this row is genuinely NOT uniformly black, so the
        // assertion below is about blanking rather than about an empty screen.
        RunBothFields(video);
        Assert.Contains(ReadActiveRow(video), pixel => pixel != Video.BlankedColor);

        video.WriteControlRegister(0x80);
        RunBothFields(video);

        Assert.True(video.VideoBlanked);
        Assert.All(ReadActiveRow(video), pixel => Assert.Equal(Video.BlankedColor, pixel));
        Assert.Equal((byte)'W', memory.Read(PageTable.VideoRamStart));
    }

    [Fact]
    public void Unblanking_RestoresExactlyThePictureThatWouldHaveBeenShowing()
    {
        var (video, memory) = CreateVideo();
        memory.Write(PageTable.VideoRamStart, (byte)'W');

        RunOneField(video);
        var beforeBlank = ReadCell(video, column: 0);

        video.WriteControlRegister(0x80);
        RunBothFields(video);
        Assert.NotEqual(beforeBlank, ReadCell(video, column: 0));

        video.WriteControlRegister(0x00);
        RunBothFields(video);

        Assert.Equal(beforeBlank, ReadCell(video, column: 0));
    }

    [Fact]
    public void VramWrittenWhileBlanked_IsVisibleImmediatelyOnUnblank()
    {
        // The manual acceptance check's own sequence: blank, POKE, unblank, see what was written.
        var (video, memory) = CreateVideo();
        memory.Write(PageTable.VideoRamStart, (byte)'A');

        video.WriteControlRegister(0x80);
        RunBothFields(video);
        memory.Write(PageTable.VideoRamStart, (byte)'Z'); // written while blanked

        video.WriteControlRegister(0x00);
        RunBothFields(video);

        Assert.Equal(ExpectedCellRow('Z', MidGlyphRow), ReadCell(video, column: 0));
    }

    [Fact]
    public void Blanking_LeavesTheBorderExactlyAsItRendersToday()
    {
        // Decided and documented rather than chosen silently (§5g): only the ACTIVE WINDOW goes
        // black, because the bit describes blanking VIDEO and the borders carry none. Visible in
        // the UI's Full-Field display mode, which is why it is pinned.
        var (video, _) = CreateVideo();

        video.WriteControlRegister(0x80);
        RunBothFields(video);

        var borderPixel = Video.ActiveOffsetY * Video.Width; // leading margin, an active row
        Assert.Equal(Video.BlankingColor, video.Framebuffer[borderPixel]);
        Assert.NotEqual(Video.BlankedColor, video.Framebuffer[borderPixel]);
    }

    [Fact]
    public void Contention_IsIdenticalBlankedAndUnblanked()
    {
        // PINS THE BUILD-AGAINST-NOW DEFAULT: fetches CONTINUE while blanked; blanking gates the
        // output stage only. This test does NOT distinguish real-hardware behaviour — nothing in
        // software can (§5g: the Z80 never waits, so timing cannot tell; and while blanked a
        // corrupted and a suppressed fetch look identical). It exists to make the emulator's
        // chosen model explicit and named, so switching to suppress-fetches is a one-test change
        // if a logic-analyzer capture ever settles it.
        var unblanked = CorruptEveryFetchForOneField(blanked: false);
        var blanked = CorruptEveryFetchForOneField(blanked: true);

        Assert.Equal(unblanked, blanked);
        Assert.True(unblanked > 0, "the harness must actually be recording collisions");
    }

    [Fact]
    public void CpuTiming_IsIdenticalBlankedAndUnblanked()
    {
        // Trivially true given the Z80's unconditional priority (§4) — pinned precisely because
        // it is the assumption that makes every "blanking as a speed trick" idea wrong. If this
        // ever fails, the priority model has been broken, not this milestone.
        var plain = new Machine();
        var blanked = new Machine();
        blanked.Ports.Write(Video.ControlPortFirst, 0x80); // via the port: costs no CPU time

        for (var i = 0; i < 200_000; i++)
        {
            plain.Tick();
            blanked.Tick();
        }

        Assert.True(blanked.Video.VideoBlanked);
        Assert.Equal(plain.Cpu.Reg.PC, blanked.Cpu.Reg.PC);
        Assert.Equal(plain.Cpu.Reg.SP, blanked.Cpu.Reg.SP);
        Assert.Equal(plain.Video.FieldTState, blanked.Video.FieldTState);
    }

    // ── 80-column interaction ─────────────────────────────────────────────────────────────

    [Fact]
    public void InEightyColumnMode_Bit7StillBlanks_ButThePanFieldStaysHeldAtZero()
    {
        // The one place milestones 25 and 26 touch. The mode latch drives the scroll register's
        // asynchronous clear, so the PAN field is held at 0 — but bit 7 is not part of that
        // register's reset. They share a register, not a fate.
        var machine = new Machine(new MachineConfig
        {
            Modifications = new ModificationsConfig { EightyColumnBoard = true },
        });
        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);
        Assert.True(machine.Video.IsEightyColumn);

        machine.Ports.Write(Video.ControlPortFirst, 0x80 | 25);

        Assert.True(machine.Video.VideoBlanked);
        Assert.Equal(0, machine.Video.PanX);
    }

    [Fact]
    public void InEightyColumnMode_ANonZeroPanWriteThroughTheRealPort_LeavesPanAtZero()
    {
        var machine = new Machine(new MachineConfig
        {
            Modifications = new ModificationsConfig { EightyColumnBoard = true },
        });
        machine.Ports.Write(EightyColumnBoard.ModePort, 0x01);

        machine.Ports.Write(Video.ControlPortFirst, 25);

        Assert.Equal(0, machine.Video.PanX);
        Assert.False(machine.Video.VideoBlanked);
    }

    // ── Reset and state ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsPanAndUnblanks()
    {
        // A blanked machine must not survive a reset. Machine.Reset() is the shared path for
        // both cold and warm reset.
        var machine = new Machine();
        machine.Ports.Write(Video.ControlPortFirst, 0x80 | 40);

        machine.Reset();

        Assert.Equal(0, machine.Video.PanX);
        Assert.False(machine.Video.VideoBlanked);
    }

    [Fact]
    public void SaveState_RoundTripsPanAndBlank()
    {
        var machine = new Machine();
        machine.Ports.Write(Video.ControlPortFirst, 0x80 | 33);

        using var buffer = new MemoryStream();
        MachineStateFile.Save(machine, buffer);
        buffer.Position = 0;
        var restored = MachineStateFile.Load(buffer);

        Assert.Equal(33, restored.Video.PanX);
        Assert.True(restored.Video.VideoBlanked);
    }

    [Fact]
    public void SaveState_RoundTripsTheUnblankedDefault()
    {
        var machine = new Machine();
        machine.Ports.Write(Video.ControlPortFirst, 7);

        using var buffer = new MemoryStream();
        MachineStateFile.Save(machine, buffer);
        buffer.Position = 0;
        var restored = MachineStateFile.Load(buffer);

        Assert.Equal(7, restored.Video.PanX);
        Assert.False(restored.Video.VideoBlanked);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static int CorruptEveryFetchForOneField(bool blanked)
    {
        var (video, memory) = CreateVideo();
        for (var col = 0; col < 80; col++)
            memory.Write((ushort)(PageTable.VideoRamStart + col), (byte)'X');
        if (blanked) video.WriteControlRegister(0x80);

        var corruptedCells = 0;
        video.FieldComplete += () => corruptedCells = video.CorruptionOverlay.Count(c => c);

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            video.Tick();
            video.CorruptLastFetch(); // stand in for a CPU colliding with every single fetch
        }

        return corruptedCells;
    }

    private static uint[] RenderRowWithPan(int pan)
    {
        var (video, memory) = CreateVideo();
        for (var col = 0; col < 80; col++)
            memory.Write((ushort)(PageTable.VideoRamStart + col), (byte)('A' + col % 26));
        video.PanX = pan;

        RunOneField(video);

        var row = new uint[Video.ActiveWidth];
        Array.Copy(video.Framebuffer, MidGlyphOrigin, row, 0, row.Length);
        return row;
    }

    private static uint[] ReadActiveRow(Video video)
    {
        var row = new uint[Video.ActiveWidth];
        Array.Copy(video.Framebuffer, MidGlyphOrigin, row, 0, row.Length);
        return row;
    }

    private static uint[] ReadCell(Video video, int column)
    {
        var cell = new uint[16];
        Array.Copy(video.Framebuffer, MidGlyphOrigin + column * 16, cell, 0, 16);
        return cell;
    }

    private static uint[] ExpectedCellRow(char code, int glyphRow, int fg = 7, int bg = 0)
    {
        var chardef = Saa5050GlyphTables.Normal[
            (code - 0x20) * Saa5050GlyphTables.PackedRowsPerGlyph + glyphRow];
        var paletteIndex = (byte)((bg << 5) | (fg << 2));
        var expected = new uint[16];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            expected[pixel] = Saa5050Palette.ColorTable[paletteIndex + (chardef & 3)];
            chardef >>= 2;
        }

        return expected;
    }
}

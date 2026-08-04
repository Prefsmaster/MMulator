using P2000.Machine.Contention;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// The SAA5050 + fetch-timing video device (project CLAUDE.md §7/§9, reference doc §5/§5f).
/// Owns the machine's framebuffer (§3 framebuffer contract, CHANGED 2026-07-22: the machine
/// renders the FULL FIELD — 928×626 BGRA, including blanking — not just the 640×480 active
/// picture; see <see cref="ActiveOffsetX"/>/<see cref="ActiveOffsetY"/>/<see cref="ActiveWidth"/>/
/// <see cref="ActiveHeight"/> below for the fixed crop rectangle. A SINGLE persistent buffer -
/// the P2000T is interlaced at 50 fields/sec, not 50 progressive frames/sec) and wires the
/// fetch-timing unit (<see cref="VideoFetchUnit"/>, the SAA5020's role) to the character
/// generator (<see cref="Saa5050Generator"/>, the SAA5050's role) exactly along the
/// fetch/generate split `docs/SAA5050-implementation.md` §6 calls for: the fetch unit issues a
/// real VRAM read every column slot on the master clock (the future contention seam,
/// milestone 10); the generator only ever consumes the byte it's handed.
///
/// Each 50 Hz field pass (<see cref="VideoFetchUnit.TStatesPerField"/>) renders ONLY that
/// field's scanlines (even field → even output rows, odd field → odd rows) into the SAME
/// buffer, with NO inter-field clear - the other field's rows are left as they were ~20 ms
/// ago. This reproduces the real interlace "comb" artifact on fast motion and is the
/// project-mandated default (§3: "four display modes... default interlaced/comb" - the other
/// three are a UI-presentation concern, not a machine one, since "the toggle only affects UI
/// presentation"). <see cref="FieldComplete"/> fires every field (50 Hz - drives the
/// interrupt/CTC cadence); <see cref="FrameComplete"/> fires only after the odd field, once
/// every two fields, marking a complete full-field image.
/// </summary>
public sealed class Video : IDevice
{
    /// <summary>Full field width: 144 px leading blank (9 char-times, the horizontal retrace's
    /// 6 char-times already excluded entirely — reference doc §4a) + 640 px active (40
    /// char-times) + 144 px trailing blank (9 char-times) = 928.</summary>
    public const int Width = 928;

    /// <summary>Full field height: 98 px pre-roll blank (49 scanlines × 2 rows/scanline) + 480
    /// px active (240 scanlines × 2) + 48 px post-roll blank (24 scanlines × 2) = 626.</summary>
    public const int Height = 626;

    /// <summary>Fixed crop rectangle — the "graphics window" — within the full field buffer
    /// (reference doc §4a): constant every field, not data-dependent.</summary>
    public const int ActiveOffsetX = 144;
    public const int ActiveOffsetY = 98;
    public const int ActiveWidth = 640;
    public const int ActiveHeight = 480;

    /// <summary>40 columns × 24 rows = 960 character cells per field.</summary>
    public const int CharRows = ActiveHeight / 20; // 480 / (10 scanlines × 2 output rows)

    /// <summary>Screen buffer is 2 screens wide × 24 rows (reference doc §5): 80 columns per
    /// row, panned by <see cref="PanX"/> to select which 40-wide slice is visible.</summary>
    private const int BufferColumns = 80;

    /// <summary>Fill colour for the blanking margins outside the active (144,98)-(784,578)
    /// window (reference doc §4a) — the periods where the SAA5050 isn't driving active
    /// picture. A very dark grey rather than pure black (UI/UX choice, not a hardware fact —
    /// real hardware's blanking signal IS black) so the Full-Field crop's boundary against the
    /// active window stays visible even when the active picture itself shows a black
    /// background: background-colour-0 palette entries (<see cref="Saa5050Palette.ColorTable"/>)
    /// are ALSO pure black, so without this the margin and an all-black screen would be
    /// visually indistinguishable. BGRA8888, opaque alpha, RGB (32,32,32) — channel order is
    /// irrelevant for a pure grey. <c>internal</c> (not <c>private</c>) so
    /// <c>P2000.Machine.Tests</c> can assert against it directly.</summary>
    internal const uint BlankingColor = 0xFF202020;

    /// <summary>Colour the ACTIVE WINDOW renders as while the video control register's bit 7
    /// (video blank) is set — genuine opaque black, identical to
    /// <c>Saa5050Palette.ColorTable[0]</c> (background 0, coverage 0). Deliberately NOT
    /// <see cref="BlankingColor"/>: that is the dark grey the BORDER renders as, a UI/UX choice
    /// so the Full-Field crop's boundary stays visible (see its own doc comment). Blanking is a
    /// real hardware effect and really is black.</summary>
    internal const uint BlankedColor = 0xFF000000;

    /// <summary>First and last port of the video control register's partially-decoded range
    /// (reference doc §5g, Philips manual): only the high nibble is decoded, so all sixteen of
    /// <c>0x30</c>-<c>0x3F</c> are the same register — a write to <c>0x3A</c> behaves identically
    /// to one to <c>0x30</c>. <b>Write-only:</b> there is no read-back of pan or blank, so the
    /// read side of this range is deliberately left unclaimed and reads open-bus <c>0xFF</c>.
    /// (Same trap the 80-column milestone avoided on port <c>0x70</c>: a shadow-byte read would
    /// invent a read-back path the hardware does not have.)</summary>
    public const byte ControlPortFirst = 0x30;

    /// <inheritdoc cref="ControlPortFirst"/>
    public const byte ControlPortLast = 0x3F;

    /// <summary>Highest defined horizontal-pan value: 0 = leftmost, 40 = rightmost ("the 2nd
    /// screen to the right"). Reference doc §5g.</summary>
    public const int MaxPan = 40;

    private readonly PageTable _memory;
    private readonly VideoFetchUnit _fetchUnit = new();
    private readonly Saa5050Generator _generator;

    private readonly uint[] _framebuffer = CreateBlankedFramebuffer();
    private readonly bool[] _corruptionOverlay;
    private readonly bool _eightyColumnBoardFitted;
    private bool _oddField;
    private int _panX;

    /// <param name="memory">The page table the SAA5020 fetch stage reads VRAM through.</param>
    /// <param name="eightyColumnBoardFitted">True when the machine carries the 80-column
    /// modification board (<c>MachineConfig.Modifications.EightyColumnBoard</c>). This is
    /// TOPOLOGY, fixed for the machine's lifetime — it only sizes the buffers that have to be
    /// able to hold 80 columns. Whether 80-column mode is currently ENABLED is the board's
    /// own port-0x00 latch, applied via <see cref="SetEightyColumnMode"/>. Sizing on the board's
    /// presence rather than unconditionally keeps an unmodified machine byte-for-byte identical
    /// (the generator's per-line data array is part of the <c>.state</c> stream).</param>
    public Video(PageTable memory, bool eightyColumnBoardFitted = false)
    {
        _memory = memory;
        _eightyColumnBoardFitted = eightyColumnBoardFitted;
        var maxColumns = eightyColumnBoardFitted
            ? VideoFetchUnit.ColumnsEightyColumn
            : VideoFetchUnit.Columns;
        _generator = new Saa5050Generator(maxColumns);
        _corruptionOverlay = new bool[maxColumns * CharRows];
        _fetchUnit.ColumnFetch += OnColumnFetch;
        _fetchUnit.LineComplete += OnLineComplete;
        _fetchUnit.FieldComplete += OnFieldComplete;
    }

    private static uint[] CreateBlankedFramebuffer()
    {
        var buffer = new uint[Width * Height];
        Array.Fill(buffer, BlankingColor);
        return buffer;
    }

    /// <summary>Upper-left X of the panned 40-column viewport into the 80-column screen
    /// buffer, 0-79 (reference doc §5: "pan the viewport... between 0 and 40"; wrapping past
    /// 79 is harmless bookkeeping since the buffer is a ring of 80 columns). The exact
    /// CPU-facing control that sets this (port vs memory-mapped register) is unconfirmed -
    /// exposed as a plain property for now, same as <c>CprinReader</c>'s ahead of its device
    /// (see milestone 5 findings).
    ///
    /// <b>Held CLEARED while 80-column mode is enabled</b> (article §13.25.1/§13.25.2,
    /// reference doc §5): the mode latch drives pin 1 of the 74LS273 scroll register — its
    /// ASYNCHRONOUS MASTER RESET — so entering 80-column mode zeroes this register and holds
    /// it zeroed. Writes while there are genuinely ineffective (the reset line is asserted,
    /// not merely gated), and returning to 40 columns leaves it at 0 until the CPU writes the
    /// scroll register again. This is deliberately NOT save-and-restore: a program that set a
    /// pan, switched to 80 columns and back finds its pan gone, and that is user-visible.</summary>
    public int PanX
    {
        get => _panX;
        set
        {
            if (_fetchUnit.EightyColumn) return;
            _panX = ClampPan(value);
        }
    }

    /// <summary>Clamps a pan value to 0-<see cref="MaxPan"/>.
    ///
    /// <b>PLACEHOLDER for genuinely undefined hardware behaviour</b> (reference doc §5g). The
    /// manual defines 0-40 and says nothing about 41-127; wrap, clamp, mirror and garbage are all
    /// plausible for a design decoding a 7-bit field into a 41-value range. The owner intends to
    /// test a real machine, and whatever they find replaces this — it is deliberately clamp, NOT
    /// mask and NOT modulo (41→40, 100→40, 127→40).
    ///
    /// Applied in the SETTER, not only at the port, so the 0-40 invariant holds for every writer
    /// (the debugger and tests can set <see cref="PanX"/> directly). That is what makes the fetch
    /// address's old <c>% 80</c> wrap genuinely unreachable rather than merely unlikely — see
    /// <see cref="OnColumnFetch"/>.</summary>
    private static int ClampPan(int value) => Math.Clamp(value, 0, MaxPan);

    /// <summary>Video control register bit 7 (reference doc §5g): blank the video to black.
    /// <b>Does not touch VRAM</b> — unblanking restores exactly the picture that would have been
    /// showing, including anything the CPU wrote while blanked, because this gates the OUTPUT
    /// stage only.</summary>
    public bool VideoBlanked { get; private set; }

    /// <summary>The video control register, output ports <see cref="ControlPortFirst"/>-<see
    /// cref="ControlPortLast"/> (reference doc §5g, sourced from the official Philips manual):
    /// bit 7 blanks the video, bits 6-0 are the horizontal pan.
    ///
    /// <b>Bit 7 does NOT ride the 80-column pan hold.</b> In 80-column mode the scroll register
    /// is held cleared by the mode latch's asynchronous reset (§5, machine milestone 25), so the
    /// pan field is ineffective there — but the blank bit is not part of that register's reset,
    /// so it keeps working. They share a register, not a fate: a write of <c>0x80 | 25</c> while
    /// in 80-column mode blanks the display and leaves <see cref="PanX"/> at 0.</summary>
    public void WriteControlRegister(byte value)
    {
        // Assigned independently of, and before, the pan — see the doc comment: the 80-column
        // hold silently swallows the PanX assignment, and must not swallow this one with it.
        VideoBlanked = (value & 0x80) != 0;
        PanX = value & 0x7F; // setter clamps to 0-MaxPan
    }

    /// <summary>The single persistent framebuffer (project CLAUDE.md §3 framebuffer
    /// contract). Mutated in place, field by field, with no inter-field clear - read it at a
    /// <see cref="FieldComplete"/> boundary for a tear-free (but intentionally comb-able)
    /// snapshot.</summary>
    public uint[] Framebuffer => _framebuffer;

    /// <summary>Debug overlay for the contention model (project CLAUDE.md §10, reference doc §4).
    /// A flat <see cref="CorruptionOverlayWidth"/>×24 array
    /// (index = charRow * <see cref="CorruptionOverlayWidth"/> + viewportColumn) where each
    /// entry is <c>true</c> when the CPU's DRAM access collided with the video fetch for that
    /// cell during the current field. Populated by <see cref="CorruptLastFetch"/> and cleared
    /// AFTER <see cref="FieldComplete"/> fires, so consumers can inspect it from the
    /// FieldComplete handler.
    ///
    /// <b>Read <see cref="CorruptionOverlayWidth"/> — do not assume 40.</b> With the 80-column
    /// board fitted the array is allocated 80 wide for the machine's lifetime and the width
    /// switches with the mode; without the board it is 40 wide and never changes, exactly as
    /// before.</summary>
    public bool[] CorruptionOverlay => _corruptionOverlay;

    /// <summary>The viewport width the <see cref="CorruptionOverlay"/> is indexed by right
    /// now: 40 normally, 80 while the 80-column board is enabled. In 40-column mode a cell's
    /// index uses its VIEWPORT column (<c>vramCol − PanX</c>); in 80-column mode the pan
    /// register is held cleared, so viewport column == absolute VRAM column.
    ///
    /// <b>Mid-field mode change (milestone spec §5.4):</b> cells are recorded under whatever
    /// stride was in effect at the moment of the fetch, and this property reports the stride in
    /// effect when it is read (i.e. at <c>FieldComplete</c>, the mode the field ended in).
    /// Cells fetched under the other cadence are therefore reinterpreted under the final
    /// stride — the "nearest cell" rule. Deterministic, never out of range (the array is
    /// allocated at the 80-column size whenever the board is fitted), and self-correcting
    /// after one field since the map is cleared every <c>FieldComplete</c>.</summary>
    public int CorruptionOverlayWidth => _fetchUnit.ActiveColumns;

    /// <summary>True while the machine is in 80-column mode (the board's port-0x00 bit-0
    /// latch). Always false on a machine with no 80-column board fitted.</summary>
    public bool IsEightyColumn => _fetchUnit.EightyColumn;

    /// <summary>True when the 80-column modification board is fitted (topology, fixed for the
    /// machine's lifetime). Distinct from <see cref="IsEightyColumn"/>, which is the current
    /// mode.</summary>
    public bool EightyColumnBoardFitted => _eightyColumnBoardFitted;

    /// <summary>Applies the 80-column board's mode latch (article §13.25.9: <c>OUT 0,1</c> →
    /// 80 columns, <c>OUT 0,0</c> → 40). Takes effect immediately, mid-frame included — real
    /// hardware has no synchronisation here. Entering 80-column mode also clears the
    /// horizontal-scroll register in hardware (see <see cref="PanX"/>).</summary>
    public void SetEightyColumnMode(bool enabled)
    {
        if (enabled) _panX = 0;
        _fetchUnit.EightyColumn = enabled;
        _generator.EightyColumn = enabled;
    }

    /// <summary>Restores the mode latch from a <c>.state</c> load WITHOUT re-applying the
    /// hardware scroll-register clear — <see cref="PanX"/> has already been restored from the
    /// same state file and must not be overwritten by the restore path (in a state genuinely
    /// captured in 80-column mode it is 0 anyway, so this only matters as a guarantee, not as
    /// a behaviour difference).</summary>
    internal void RestoreEightyColumnMode(bool enabled)
    {
        _fetchUnit.EightyColumn = enabled;
        _generator.EightyColumn = enabled;
    }

    /// <summary>Config toggle: reproduce the out-of-spec SAA5050 rendering artifact the 1986
    /// article documents for 80-column mode (<c>MachineConfig.Modifications
    /// .ShowEightyColumnArtifacts</c>). No effect with the board absent or in 40-column mode —
    /// see <see cref="Saa5050Generator.RenderField"/> for the placeholder rule and why it is
    /// deliberately flat.</summary>
    public bool ShowEightyColumnArtifacts
    {
        get => _generator.ShowEightyColumnArtifacts;
        set => _generator.ShowEightyColumnArtifacts = value;
    }

    /// <summary>True while the CURRENTLY RUNNING field is the odd (CRS=true, smoothed) one.</summary>
    public bool IsOddField => _oddField;

    /// <summary>T-state offset within the current 50 Hz field (0 –
    /// <see cref="VideoFetchUnit.TStatesPerField"/>-1). Exposed for the observer
    /// state-snapshot surface (project CLAUDE.md §3b.1, milestone 13).</summary>
    public int FieldTState => _fetchUnit.FieldTState;

    /// <summary>Raised at each 50 Hz field boundary (SAA5020 DEW pulse) - the video VBLANK
    /// interrupt source (project CLAUDE.md §8) fires once per field, not once per frame.</summary>
    public event Action? FieldComplete;

    /// <summary>Raised only after the ODD field completes (`docs/SAA5050-implementation.md`
    /// §5: "FrameComplete (odd-field only)") - once every TWO fields, marking the point where
    /// both interlaced fields have been rendered and the persistent buffer holds a complete
    /// 640×480 image. A future progressive/composited display mode would read on this event
    /// instead of <see cref="FieldComplete"/>.</summary>
    public event Action? FrameComplete;

    public void Reset()
    {
        _fetchUnit.Reset();
        _generator.Reset();
        // "Bij RESET wordt automatisch de 40 karakter-stand gekozen" (article §13.25.9) — the
        // mode latch is a flip-flop on the board's reset line, so BOTH cold and warm reset
        // land in 40 columns. PanX is deliberately left alone here: it isn't reset today on an
        // unmodified machine either, and this milestone must not change that.
        _fetchUnit.EightyColumn = false;
        _generator.EightyColumn = false;
        // Video control register (reference doc §5g, machine milestone 26): pan 0, unblanked, on
        // BOTH cold and warm reset — a blanked machine must not survive a reset. NOTE this is a
        // deliberate behaviour change: before milestone 26 PanX was left alone here, because
        // nothing could write it and there was no sourced reset behaviour to model.
        _panX = 0;
        VideoBlanked = false;
        _oddField = false;
        Array.Fill(_framebuffer, BlankingColor);
        Array.Clear(_corruptionOverlay);
    }

    /// <summary>Called by <see cref="Machine"/> when a CPU DRAM access (MREQ to an address
    /// ≥ 0x5000) overlaps the video fetch slot that just fired (project CLAUDE.md §3 step 4,
    /// §10, reference doc §4): Z80 always wins — the video cell is blanked (default corruption
    /// mode: black/suppression, swappable once a hardware capture confirms the exact mode).
    /// No-op when no fetch occurred this tick (<see cref="VideoFetchUnit.IsFetchTick"/> false)
    /// or when the fetch was outside the active display window.</summary>
    public void CorruptLastFetch()
    {
        if (!_fetchUnit.IsFetchTick) return;
        var col = _fetchUnit.LastFetchColumn;
        var activeLine = _fetchUnit.LastFetchLine - VideoFetchUnit.VerticalBlankLines;
        var charRow = activeLine / 10;
        var row = ActiveOffsetY + activeLine * 2 + (_oddField ? 1 : 0);
        var lanes = ActiveWidth / _fetchUnit.ActiveColumns; // 16 at 40 columns, 8 at 80
        _framebuffer.AsSpan(row * Width + ActiveOffsetX + col * lanes, lanes).Clear();
        _corruptionOverlay[charRow * _fetchUnit.ActiveColumns + col] = true;
    }

    /// <summary>Advances the video device by one master-clock T-state (project CLAUDE.md §3
    /// step 1 - called before the CPU steps).</summary>
    public void Tick() => _fetchUnit.Tick();

    private void OnColumnFetch(int column)
    {
        var activeLine = _fetchUnit.Line - VideoFetchUnit.VerticalBlankLines;
        var charRow = activeLine / 10;
        // Row stride is the FULL 80-column buffer width, with the pan register as a sideways
        // offset into it (reference doc §5: 80 × 24 = 1920 bytes at 0x5000-0x577F; §4's
        // `charRow * 40` pseudocode snippet is wrong and this code has always been right).
        // 80-column mode therefore needs NO address remapping at all: PanX is held cleared and
        // `column` simply runs 0..79 over the same buffer.
        //
        // The `% BufferColumns` wrap this line used to carry is GONE (machine milestone 26,
        // reference doc §5g): it only ever existed to contain unclamped pan values. Now that
        // PanX is clamped to 0-40 in its own setter, the worst case is pan 40 + column 39 = 79
        // in 40-column mode and pan 0 + column 79 = 79 in 80-column mode — exactly the last byte
        // of the row, either way. Demoted to a debug assertion rather than deleted outright:
        // it costs nothing in Release and would catch a future change that reintroduces an
        // unclamped writer, which would otherwise silently read into the NEXT character row.
        var bufferColumn = PanX + column;
        System.Diagnostics.Debug.Assert(
            bufferColumn < BufferColumns,
            $"VRAM column {bufferColumn} ran past the {BufferColumns}-wide row: PanX={PanX} is " +
            "out of its clamped 0-40 range (reference doc §5g).");
        var address = (ushort)(PageTable.VideoRamStart + charRow * BufferColumns + bufferColumn);
        var data = _memory.Read(address);

        _generator.BeginCell(data, column);

        // Interlaced (project CLAUDE.md §3): this field pass owns only ITS rows (even or odd),
        // not both - the other field's rows are left untouched from ~20 ms ago (the comb).
        // Offset into the active "graphics window" crop rectangle within the full-field buffer
        // (reference doc §4a) - blanking pixels around it are never touched, staying at
        // BlankingColor (a very dark grey, not pure black - see that constant's doc comment).
        var row = ActiveOffsetY + activeLine * 2 + (_oddField ? 1 : 0);
        var lanes = ActiveWidth / _fetchUnit.ActiveColumns;
        var pixelX = ActiveOffsetX + column * lanes;

        // ── THE VIDEO-BLANK DECISION POINT (reference doc §5g, machine milestone 26) ──────────
        // Bit 7 gates the OUTPUT stage only: the VRAM read above already happened, the generator
        // already consumed the byte, and the contention model is untouched — a blanked field
        // still fetches, so it still collides, so the corrupted-cell overlay still lights up.
        //
        // Whether real hardware keeps the SAA5020 addressing VRAM while blanked is UNKNOWN and
        // is a logic-analyzer question. It is NOT contention-relevant, despite first appearances:
        // on the P2000T the Z80 has unconditional priority and never waits (§4), so CPU timing is
        // already independent of whether a video fetch happens — blanking cannot be a speed
        // trick, and no timing measurement can distinguish the two models. Nor is it
        // software-visible on real hardware: while blanked the output is black, so a corrupted
        // fetch and a suppressed fetch look identical, and corruption is non-persistent so
        // nothing survives into the next unblanked field.
        //
        // In THIS emulator the only difference is a diagnostic: whether "show contention
        // glitches" lights cells during a blanked field. To switch to the suppress-fetches
        // model, move this check up to the top of OnColumnFetch so the VRAM read and BeginCell
        // are skipped too. One obvious change, deliberately marked.
        //
        // Only the ACTIVE WINDOW goes black. The border keeps rendering as it does today: the
        // bit describes blanking VIDEO, and the borders carry no video (they are blanking
        // intervals already). Visible in Full-Field display mode, hence stated rather than
        // assumed.
        if (VideoBlanked)
        {
            _framebuffer.AsSpan(row * Width + pixelX, lanes).Fill(BlankedColor);
            return;
        }

        _generator.RenderField(_framebuffer, row * Width + pixelX, oddField: _oddField);
    }

    // Only advance the generator's per-scanline state (Saa5050Generator._scanLineCounter,
    // which tracks "which of the 10 scanlines within the current character row") for lines
    // that were actually part of the active fetch window. LineComplete fires unconditionally
    // for every raw line, including the 49-line vertical pre-roll and post-roll (added
    // 2026-07-22, see VideoFetchUnit.VerticalBlankLines) - calling EndLine() for those too
    // would desync the counter by 49 (mod 10 = 9) before the first real scanline ever
    // renders, corrupting every character's glyph-row selection for the whole field.
    // IsActiveLine reflects the JUST-COMPLETED line here: VideoFetchUnit.Tick() raises
    // LineComplete before updating Line to the new value.
    private void OnLineComplete()
    {
        if (_fetchUnit.IsActiveLine) _generator.EndLine();
    }

    private void OnFieldComplete()
    {
        var completedFieldWasOdd = _oddField; // parity of the field that JUST finished
        _generator.BeginField();
        _oddField = !_oddField;
        FieldComplete?.Invoke();
        // Clear AFTER firing so FieldComplete consumers can still read the current field's overlay.
        Array.Clear(_corruptionOverlay);
        if (completedFieldWasOdd)
        {
            FrameComplete?.Invoke();
        }
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteInt32(_panX);
        writer.WriteBool(VideoBlanked);
        writer.WriteBool(_oddField);
        _fetchUnit.SaveState(writer);
        _generator.SaveState(writer);
    }

    public void LoadState(IStateReader reader)
    {
        // Assign the backing field directly: the public setter is held ineffective while the
        // mode latch says 80 columns, which would silently drop a restored pan. Still clamped,
        // so a hand-edited state file cannot smuggle an out-of-range pan past the invariant
        // OnColumnFetch now relies on.
        _panX = ClampPan(reader.ReadInt32());
        VideoBlanked = reader.ReadBool();
        _oddField = reader.ReadBool();
        _fetchUnit.LoadState(reader);
        _generator.LoadState(reader);
    }
}

using P2000.Machine.State;

namespace P2000.Machine.Devices.Saa5050;

/// <summary>Which of the three glyph tables a cell renders from - tracked as an enum rather
/// than an array reference so <see cref="Saa5050Generator"/> can serialize it (project
/// CLAUDE.md §11).</summary>
internal enum Saa5050GlyphSet
{
    Normal,
    Graphics,
    Separated,
}

/// <summary>
/// The SAA5050's role (reference doc §4 layer 3, `docs/SAA5050-implementation.md`): control
/// codes, hold-graphics, rounding, palette, and the P2000T's 160-255 inverted-colour trick.
/// Contention-irrelevant by design - it never touches the bus, it only consumes whatever byte
/// <see cref="BeginCell"/> is handed by the fetch stage (<see cref="Contention.VideoFetchUnit"/>
/// via <see cref="Video"/>).
///
/// Restructured from the reference C#/JS renderers' single `Render()` (impl guide §9 - "drive
/// the generator from OUR fetch unit... a genuine simplification") into separate calls:
/// <see cref="BeginCell"/> resolves state for the cell (control codes, hold-graphics, the
/// inverted-colour trick), <see cref="RenderField"/> blits its pixels for whichever field
/// (even/CRS=false or odd/CRS=true) is currently running - the P2000T is interlaced at 50
/// fields/sec (project CLAUDE.md §3), so a single field pass renders each cell exactly ONCE,
/// using a field-wide-constant CRS value <see cref="Video"/> supplies (see the milestone 5
/// findings log for the field/frame correction). <see cref="EndLine"/>/<see cref="BeginField"/>
/// replace the reference code's edge-triggered DEW/LOSE/CRS pins with plain calls (same reset
/// semantics, no pin ceremony - impl guide §9 sanctions this).
/// </summary>
internal sealed class Saa5050Generator : IDevice
{
    private readonly byte[] _previousLineData;

    private int _foreground = 7;
    private int _background;
    private bool _graphicsMode;
    private bool _separatedGraphics;
    private bool _doubleHeight;
    private bool _oldDoubleHeight;
    private bool _wasDoubleHeight;
    private bool _secondHalfOfDouble;
    private bool _flashing;
    private bool _flashOn;
    private int _flashTime;
    private byte _heldChar = 0x20;
    private bool _holdChar;
    private int _scanLineCounter;

    private Saa5050GlyphSet _nextGlyphSet = Saa5050GlyphSet.Normal;
    private Saa5050GlyphSet _heldGlyphSet = Saa5050GlyphSet.Normal;
    private Saa5050GlyphSet _currentGlyphSet = Saa5050GlyphSet.Normal;

    // Per-cell: latched by BeginCell, consumed by RenderField for that same cell's pass.
    private int _previousColor = 7;
    private bool _prevFlash;
    private byte _renderCode = 0x20;
    private bool _invert;

    public Saa5050Generator(int columns = Contention.VideoFetchUnit.Columns)
    {
        _previousLineData = new byte[columns];
    }

    public void Reset()
    {
        Array.Clear(_previousLineData);
        _foreground = 7;
        _background = 0;
        _graphicsMode = false;
        _separatedGraphics = false;
        _doubleHeight = false;
        _oldDoubleHeight = false;
        _wasDoubleHeight = false;
        _secondHalfOfDouble = false;
        _flashing = false;
        _flashOn = false;
        _flashTime = 0;
        _heldChar = 0x20;
        _holdChar = false;
        _scanLineCounter = 0;
        _nextGlyphSet = _heldGlyphSet = _currentGlyphSet = Saa5050GlyphSet.Normal;
        _previousColor = 7;
        _prevFlash = false;
        _renderCode = 0x20;
        _invert = false;
    }

    /// <summary>Field-boundary reset (SAA5020 DEW pulse, impl guide §5): rewinds the
    /// scan-line counter and advances the flash cadence.</summary>
    public void BeginField()
    {
        _scanLineCounter = 0;
        _secondHalfOfDouble = false;
        _flashTime = (_flashTime + 1) % 48;
        _flashOn = _flashTime < 16;
    }

    /// <summary>End-of-scanline reset (SAA5020 LOSE pulse, impl guide §5): per-row attribute
    /// state resets to its defaults, and the scan-line counter advances, carrying
    /// double-height into its second half at the 10-line char-row boundary.</summary>
    public void EndLine()
    {
        _foreground = 7;
        _background = 0;
        _holdChar = false;
        _heldChar = 0x20;
        _nextGlyphSet = _heldGlyphSet = Saa5050GlyphSet.Normal;
        _flashing = false;
        _separatedGraphics = false;
        _graphicsMode = false;
        _doubleHeight = false;

        _scanLineCounter++;
        if (_scanLineCounter == 10)
        {
            _scanLineCounter = 0;
            _secondHalfOfDouble = !_secondHalfOfDouble && _wasDoubleHeight;
        }

        _wasDoubleHeight = false;
    }

    /// <summary>Processes one column's fetched byte for the current scanline (control-code
    /// handling, hold-graphics, the 160-255 inverted-colour trick) and latches everything
    /// <see cref="RenderField"/> needs, without touching pixels.</summary>
    public void BeginCell(byte fetchedData, int column)
    {
        _invert = (fetchedData & 0x80) != 0;
        var data = (byte)(fetchedData & 0x7F);

        // Double-height's bottom half re-shows the TOP row's byte at this column instead of
        // whatever is actually in VRAM here (confirmed hardware quirk carried from the
        // reference PERender path) - the fetch stage still reads real VRAM (a future
        // contention check still sees it); only the glyph choice is overridden.
        if (_secondHalfOfDouble)
        {
            data = _previousLineData[column];
        }

        _previousLineData[column] = data;

        _oldDoubleHeight = _doubleHeight;
        _previousColor = _foreground;
        _currentGlyphSet = _nextGlyphSet;
        _prevFlash = _flashing;

        if (data < 0x20)
        {
            data = HandleControlCode(data);
        }
        else if (_graphicsMode)
        {
            _heldChar = data;
            _heldGlyphSet = _currentGlyphSet;
        }

        _renderCode = data;
    }

    /// <summary>Renders this cell's pixels for whichever field is currently running -
    /// <paramref name="oddField"/> is constant for the whole field pass (project CLAUDE.md
    /// §3: interlaced 50 fields/sec), selecting the raw (even, CRS=false) or smoothed (odd,
    /// CRS=true) glyph-row variant - into 16 BGRA pixel lanes starting at
    /// <paramref name="offset"/>.</summary>
    public void RenderField(uint[] frameBuffer, int offset, bool oddField)
    {
        var scanLine = _scanLineCounter << 1;
        if (oddField)
        {
            scanLine++;
        }

        if (_oldDoubleHeight)
        {
            scanLine >>= 1;
            if (_secondHalfOfDouble)
            {
                scanLine += 10;
            }
        }

        var glyphs = Saa5050GlyphTables.Select(_currentGlyphSet);
        var chardef = glyphs[(_renderCode - 0x20) * Saa5050GlyphTables.PackedRowsPerGlyph + scanLine];
        var colorTable = Saa5050Palette.ColorTable;

        if ((_prevFlash && _flashOn) || (_secondHalfOfDouble && !_doubleHeight))
        {
            var fillColor = colorTable[(_background & 7) << (_invert ? 2 : 5)];
            for (var pixel = 0; pixel < 16; pixel++)
            {
                frameBuffer[offset + pixel] = fillColor;
            }

            return;
        }

        var paletteIndex = (byte)(((_background & 7) << (_invert ? 2 : 5)) | ((_previousColor & 7) << (_invert ? 5 : 2)));
        for (var pixel = 0; pixel < 16; pixel++)
        {
            frameBuffer[offset + pixel] = colorTable[paletteIndex + (chardef & 3)];
            chardef >>= 2;
        }
    }

    private void SetNextGlyphSet()
    {
        _nextGlyphSet = _graphicsMode
            ? (_separatedGraphics ? Saa5050GlyphSet.Separated : Saa5050GlyphSet.Graphics)
            : Saa5050GlyphSet.Normal;
    }

    /// <summary>Serial attribute codes 0x00-0x1F (impl guide §3): set-after semantics - the
    /// control cell itself renders a space (or the held graphics glyph), and the attribute
    /// takes effect from the FOLLOWING cell.</summary>
    private byte HandleControlCode(byte data)
    {
        var holdOff = false;

        switch (data)
        {
            case 1: case 2: case 3: case 4: case 5: case 6: case 7:
                _graphicsMode = false;
                _foreground = data;
                SetNextGlyphSet();
                break;
            case 8: case 9:
                _flashing = data == 8;
                break;
            case 12: case 13:
                _doubleHeight = data == 13;
                if (_doubleHeight)
                {
                    _wasDoubleHeight = true;
                }
                break;
            case 17: case 18: case 19: case 20: case 21: case 22: case 23:
                _graphicsMode = true;
                _foreground = data & 0x07;
                SetNextGlyphSet();
                break;
            case 24:
                _foreground = _background;
                break;
            case 25: case 26:
                _separatedGraphics = data == 25;
                SetNextGlyphSet();
                break;
            case 28:
                _background = 0;
                break;
            case 29:
                _background = _foreground;
                break;
            case 30:
                _holdChar = true;
                break;
            case 31:
                holdOff = true;
                break;
        }

        byte shown;
        if (_holdChar && _doubleHeight == _oldDoubleHeight)
        {
            shown = _heldChar is >= 0x40 and < 0x60 ? (byte)0x20 : _heldChar;
            _currentGlyphSet = _heldGlyphSet;
        }
        else
        {
            _heldChar = 0x20;
            shown = 0x20;
        }

        if (holdOff)
        {
            _holdChar = false;
            _heldChar = 0x20;
        }

        return shown;
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteBytes(_previousLineData);
        writer.WriteInt32(_foreground);
        writer.WriteInt32(_background);
        writer.WriteBool(_graphicsMode);
        writer.WriteBool(_separatedGraphics);
        writer.WriteBool(_doubleHeight);
        writer.WriteBool(_oldDoubleHeight);
        writer.WriteBool(_wasDoubleHeight);
        writer.WriteBool(_secondHalfOfDouble);
        writer.WriteBool(_flashing);
        writer.WriteBool(_flashOn);
        writer.WriteInt32(_flashTime);
        writer.WriteByte(_heldChar);
        writer.WriteBool(_holdChar);
        writer.WriteInt32(_scanLineCounter);
        writer.WriteByte((byte)_nextGlyphSet);
        writer.WriteByte((byte)_heldGlyphSet);
        writer.WriteByte((byte)_currentGlyphSet);
        writer.WriteInt32(_previousColor);
        writer.WriteBool(_prevFlash);
        writer.WriteByte(_renderCode);
        writer.WriteBool(_invert);
    }

    public void LoadState(IStateReader reader)
    {
        reader.ReadBytes(_previousLineData);
        _foreground = reader.ReadInt32();
        _background = reader.ReadInt32();
        _graphicsMode = reader.ReadBool();
        _separatedGraphics = reader.ReadBool();
        _doubleHeight = reader.ReadBool();
        _oldDoubleHeight = reader.ReadBool();
        _wasDoubleHeight = reader.ReadBool();
        _secondHalfOfDouble = reader.ReadBool();
        _flashing = reader.ReadBool();
        _flashOn = reader.ReadBool();
        _flashTime = reader.ReadInt32();
        _heldChar = reader.ReadByte();
        _holdChar = reader.ReadBool();
        _scanLineCounter = reader.ReadInt32();
        _nextGlyphSet = (Saa5050GlyphSet)reader.ReadByte();
        _heldGlyphSet = (Saa5050GlyphSet)reader.ReadByte();
        _currentGlyphSet = (Saa5050GlyphSet)reader.ReadByte();
        _previousColor = reader.ReadInt32();
        _prevFlash = reader.ReadBool();
        _renderCode = reader.ReadByte();
        _invert = reader.ReadBool();
    }
}

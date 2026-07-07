namespace P2000.Machine.Devices.Cassette;

/// <summary>
/// Physical tape transport for the MDCR (Mini Digital Cassette Recorder). Stores the tape
/// as a phase bitstream — 1,088,520 phases per side — and provides random-access read/write
/// at the current head position plus forward/reverse motor motion. Two sides; flipping the
/// tape mirrors the position.
///
/// A blank tape is seeded with deterministic pseudo-noise (so the ROM's block search finds
/// garbage until real data, while save-state remains reproducible — machine CLAUDE.md §2).
///
/// Loaded <c>.cas</c> images are write-protected by default. The host face (<see
/// cref="LoadCasImage"/>) is always instant; timing is the MDCR device's concern.
/// </summary>
public sealed class MiniTape
{
    /// <summary>Phase count per side (≈ 91 s × 2 500 000 Hz / 209 — MDCR-implementation.md §2).</summary>
    public const int PhasesPerSide = 1_088_520;

    public const int Sides = 2;

    // Gap lengths in phases (MDCR-implementation.md §6)
    private const int BotGap = 5_800;   // BOT sensor area
    private const int BobGap = 6_160;   // inter-block gap (~500ms); ROM skips 120ms then searches
    private const int MarkDataGap = 970; // gap between MARK and DATA BLOCK (~81ms write / 70ms read)
    private const int EobGap = 1_856;   // post-data silence; ensures read_until_timeout detects end

    private readonly bool[][] _phases;
    private readonly bool[] _protected = new bool[Sides];
    private int _position;
    private int _side;

    /// <param name="seed">RNG seed for the blank-tape noise fill. Fixed default keeps
    /// save-state deterministic (MDCR-implementation.md §9).</param>
    public MiniTape(int seed = 42)
    {
        _phases = new bool[Sides][];
        var rng = new Random(seed);
        for (var s = 0; s < Sides; s++)
        {
            _phases[s] = new bool[PhasesPerSide];
            for (var i = 0; i < PhasesPerSide; i++)
                _phases[s][i] = rng.Next(2) == 1;
        }
    }

    // ---- State -------------------------------------------------------------------

    public int Position => _position;
    public int Side => _side;

    /// <summary>True when the head is at either physical end of the current side.</summary>
    public bool IsAtEnd => _position == 0 || _position >= PhasesPerSide - 1;

    /// <summary>True when the current side is write-protected.</summary>
    public bool IsProtected => _protected[_side];

    // ---- Head operations (one phase at a time) ------------------------------------

    public bool Read() => _phases[_side][_position];

    /// <summary>Writes one phase at the current position. No-op when write-protected.</summary>
    public void Write(bool phase)
    {
        if (!_protected[_side])
            _phases[_side][_position] = phase;
    }

    // ---- Motor -------------------------------------------------------------------

    public void Forward()
    {
        if (_position < PhasesPerSide - 1) _position++;
    }

    public void Reverse()
    {
        if (_position > 0) _position--;
    }

    /// <summary>Seeks to an arbitrary position (for state restore). No bounds clamping —
    /// caller must supply a valid position from a prior <see cref="Position"/> read.</summary>
    public void SeekTo(int position, int side)
    {
        _position = position;
        _side = side;
    }

    // ---- Host face ---------------------------------------------------------------

    /// <summary>Encodes a P2000T <c>.cas</c> image onto the current side, overwriting it.
    /// A <c>.cas</c> file represents one physical side of a cassette; to access the other
    /// side the tape must be ejected and flipped (call <see cref="SeekTo"/> with side 1 after
    /// a new load, or load a second .cas via a separate call on side 1). Tape is
    /// write-protected after loading (MDCR-implementation.md §3). Rewinds to BOT when done.
    ///
    /// Tape block structure (Cassette.asm lines 912-918, load_block):
    ///   MARK:       0xAA | 0x00 | 0x00 | 0xAA  (empty block — 4 bytes total)
    ///   [MarkDataGap: ~81ms silence; ROM waits ~70ms then starts reading]
    ///   DATA BLOCK: 0xAA | header(32B) | data(1024B) | CRC(2B) | 0xAA
    ///
    /// Header and data share ONE block with ONE combined CRC (not separate frames).
    /// Format: 1280 bytes/record; header (32 bytes) from record offset 0x30, data (1024 bytes)
    /// from offset 0x100. See MDCR-implementation.md §6.</summary>
    public void LoadCasImage(byte[] casImage, bool writeProtect = true)
    {
        var blocks = casImage.Length / 1280;

        Array.Clear(_phases[_side], 0, PhasesPerSide);
        _position = 0;
        _protected[_side] = false; // allow writing during encoding

        WriteGap(BotGap);

        for (var b = 0; b < blocks; b++)
        {
            WriteGap(BobGap);
            WriteData(Array.Empty<byte>());    // MARK: empty sync block (0xAA,0,0,0xAA)
            WriteGap(MarkDataGap);             // ~81ms silence between MARK and DATA BLOCK
            var header = casImage.AsSpan(b * 1280 + 0x30, 32).ToArray();
            var data = casImage.AsSpan(b * 1280 + 0x100, 1024).ToArray();
            WriteData([..header, ..data]);     // DATA BLOCK: header(32B)+data(1024B), one combined CRC
            WriteGap(EobGap);
        }
        // Remaining phases are already zero-filled (Array.Clear) → EOT silence

        _protected[_side] = writeProtect;
        _position = 1; // just past the BOT sensor — IsAtEnd(0) would keep BET=0 forever
    }

    // ---- Host face (save / serializer) ------------------------------------------

    /// <summary>Decodes the phase bitstream of the current side back into a P2000T
    /// <c>.cas</c> image. Each decoded block pair (MARK + DATA BLOCK) becomes a 1280-byte
    /// record with the 32-byte header at offset 0x30 and the 1024-byte data at offset 0x100
    /// (MDCR-implementation.md §6). Returns null if no valid pairs are found. Does NOT
    /// move the tape head — safe to call at any time.</summary>
    public byte[]? Save()
    {
        var cursor = 0;
        var records = new List<(byte[] header, byte[] data)>();

        while (cursor < PhasesPerSide)
        {
            // Skip gap to next MARK's 0xAA preamble.
            // 0xAA LSB-first: bit0=0 → phase0=F, so one extra false blends in; step back 1 to re-align.
            while (cursor < PhasesPerSide && !_phases[_side][cursor])
                cursor++;
            if (cursor == 0 || cursor >= PhasesPerSide) break;
            cursor--; // re-align to phase0 of bit0 of 0xAA

            if (!TryDecodeFrame(ref cursor, 0, out _)) break; // MARK (empty)

            // Skip MarkDataGap between MARK and DATA BLOCK
            while (cursor < PhasesPerSide && !_phases[_side][cursor])
                cursor++;
            if (cursor >= PhasesPerSide) break;
            cursor--; // re-align to phase0 of DATA BLOCK's 0xAA

            // DATA BLOCK: header(32B) + data(1024B) combined in one frame, one CRC
            if (!TryDecodeFrame(ref cursor, 1056, out var combined)) break;

            records.Add((combined![..32], combined[32..]));
            // EOB gap follows — outer loop's gap-skip handles it on the next iteration
        }

        if (records.Count == 0) return null;

        var casImage = new byte[records.Count * 1280];
        for (var i = 0; i < records.Count; i++)
        {
            Array.Copy(records[i].header, 0, casImage, i * 1280 + 0x30, 32);
            Array.Copy(records[i].data, 0, casImage, i * 1280 + 0x100, 1024);
        }
        return casImage;
    }

    /// <summary>Attempts to decode one framed block: lead 0x55 | dataLength bytes | CRC-lo |
    /// CRC-hi | trail 0x55. Verifies the CRC-16 and both framing bytes. Advances
    /// <paramref name="cursor"/> only on success; restores it on failure.</summary>
    private bool TryDecodeFrame(ref int cursor, int dataLength, out byte[]? payload)
    {
        payload = null;
        var phasesNeeded = (1 + dataLength + 2 + 1) * 16;
        if (cursor + phasesNeeded > PhasesPerSide) return false;

        var saved = cursor;

        if (ReadByte(ref cursor) != 0xAA) { cursor = saved; return false; }

        var data = new byte[dataLength];
        ushort checksum = 0;
        for (var i = 0; i < dataLength; i++)
        {
            data[i] = ReadByte(ref cursor);
            checksum = UpdateChecksum(checksum, data[i]);
        }

        var crcLo = ReadByte(ref cursor);
        var crcHi = ReadByte(ref cursor);
        if ((ushort)(crcLo | (crcHi << 8)) != checksum) { cursor = saved; return false; }

        if (ReadByte(ref cursor) != 0xAA) { cursor = saved; return false; }

        payload = data;
        return true;
    }

    /// <summary>Reads one byte from the phase stream at <paramref name="cursor"/> by sampling
    /// the first phase of each 2-phase bit pair (LSB first — matches WriteByte). Advances cursor
    /// by 16.</summary>
    private byte ReadByte(ref int cursor)
    {
        byte b = 0;
        for (var i = 0; i < 8; i++)
        {
            if (_phases[_side][cursor]) b |= (byte)(1 << i);
            cursor += 2; // skip both phases of this bit
        }
        return b;
    }

    // ---- Private encoding helpers ------------------------------------------------

    private void WriteGap(int phases)
    {
        // Gap = all-low phases (no transitions → PLL stays unlocked)
        for (var i = 0; i < phases; i++)
            _phases[_side][_position++] = false;
    }

    private void WriteData(byte[] bytes)
    {
        // Framing: 0xAA lead | data | CRC-16 (lo, hi) | 0xAA trail.
        // 0xAA (10101010) LSB-first: bit0=0 → phase0=F, PLL locks at the F→T of phase1.
        // The ROM assembles bytes via "rr d" (Cassette.asm:1140), which is LSB-first:
        // first received bit → bit0 of result. So we send bit0 first.
        ushort checksum = 0;
        WriteByte(0xAA);
        foreach (var b in bytes)
        {
            WriteByte(b);
            checksum = UpdateChecksum(checksum, b);
        }
        WriteByte((byte)(checksum & 0xFF));
        WriteByte((byte)(checksum >> 8));
        WriteByte(0xAA);
    }

    private void WriteByte(byte b)
    {
        // LSB first (bit0 first): ROM assembles via "rr d" so first received → bit0 of result.
        // bit=1 → hi-lo (true,false); bit=0 → lo-hi (false,true).
        for (var i = 0; i < 8; i++)
        {
            var bit = (b & 1) != 0;
            _phases[_side][_position++] = bit;
            _phases[_side][_position++] = !bit;
            b >>= 1;
        }
    }

    /// <summary>CRC-16 variant used by the P2000T cassette format (MDCR-implementation.md §6).
    /// Process one byte LSB-first (matches ROM's bit-reception order via "rr d"):
    /// XOR bit into checksum, conditionally XOR 0x4002, rotate right.</summary>
    internal static ushort UpdateChecksum(ushort cs, byte b)
    {
        for (var i = 0; i < 8; i++)
        {
            cs ^= (ushort)(b & 1); // LSB first
            if ((cs & 1) != 0) cs ^= 0x4002;
            cs = (ushort)((cs >> 1) | (cs << 15)); // rotate right 1
            b >>= 1;
        }
        return cs;
    }
}

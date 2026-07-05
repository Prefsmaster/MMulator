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

    // BOT / BOB / EOB gap lengths in phases (MDCR-implementation.md §6)
    private const int BotGap = 5_800;
    private const int BobGap = 6_160;
    private const int EobGap = 1_856;

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
    /// Format: 1280 bytes/record; header (32 bytes) from record offset 0x30, data (1024 bytes)
    /// from offset 0x100. Both are encoded on tape — the ROM's ZOEK reads headers from the
    /// bitstream. See MDCR-implementation.md §6.</summary>
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
            WriteData(Array.Empty<byte>()); // MARK — empty sync block
            var header = casImage.AsSpan(b * 1280 + 0x30, 32).ToArray();
            WriteData(header); // 32-byte block header (ZOEK reads this from the tape bitstream)
            var data = casImage.AsSpan(b * 1280 + 0x100, 1024).ToArray();
            WriteData(data);
            WriteGap(EobGap);
        }
        // Remaining phases are already zero-filled (Array.Clear) → EOT silence

        _protected[_side] = writeProtect;
        _position = 0; // rewind
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
        // Framing: 0xAA lead | data | CRC-16 (lo, hi) | 0xAA trail
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
        // LSB first; bit=1 → hi-lo (true,false); bit=0 → lo-hi (false,true)
        for (var i = 0; i < 8; i++)
        {
            var bit = (b & 1) != 0;
            _phases[_side][_position++] = bit;
            _phases[_side][_position++] = !bit;
            b >>= 1;
        }
    }

    /// <summary>CRC-16 variant used by the P2000T cassette format (MDCR-implementation.md §6).
    /// Process one byte LSB-first: XOR bit into checksum, conditionally XOR 0x4002, rotate right.</summary>
    internal static ushort UpdateChecksum(ushort cs, byte b)
    {
        for (var i = 0; i < 8; i++)
        {
            cs ^= (ushort)(b & 1);
            if ((cs & 1) != 0) cs ^= 0x4002;
            cs = (ushort)((cs >> 1) | (cs << 15)); // rotate right 1
            b >>= 1;
        }
        return cs;
    }
}

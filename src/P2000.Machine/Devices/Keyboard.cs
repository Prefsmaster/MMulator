using P2000.Machine.Io;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// P2000T keyboard — I/O device on ports 0x00–0x09 (project CLAUDE.md §7/§9, reference
/// doc §5f). Implements the two-face model: a bus face (plain port reads) and a host face
/// (the UI sets intersection state at frame boundaries).
///
/// <b>Matrix model:</b> 10 rows × 8 columns, stored as a <c>bool[10, 8]</c> crosspoint
/// array (one entry per physical key switch). Active-low: a pressed key reads as bit 0 in
/// the returned row byte. Phantom ghosting emerges naturally from the crosspoint model —
/// three pressed corners of a rectangle in the matrix make the fourth corner appear pressed
/// (diode-less matrix, same as real hardware).
///
/// <b>Scan protocol (reference doc §5f):</b>
/// <list type="bullet">
///   <item>KBIEN = 1 (bit 6 of CPOUT, scan ON): only port 0 is meaningful — returns the
///   AND of all 10 row bytes. 0xFF = no key anywhere; non-0xFF = at least one key down.
///   Ports 1–9 return 0xFF (consistent "nothing", active-low).</item>
///   <item>KBIEN = 0 (scan OFF): each port 0–9 returns its own row byte (pressed columns
///   read as 0 bit, released as 1 bit).</item>
/// </list>
///
/// KBIEN is read live from <see cref="CPoutLatch.Kbien"/> on every port read — no separate
/// event subscription needed. Debounce and repeat are handled by the ROM's 50 Hz ISR; this
/// device presents a stable matrix only.
/// </summary>
public sealed class KeyboardDevice : IDevice
{
    public const int Rows = 10;
    public const int Columns = 8;

    private readonly CPoutLatch _cpOut;
    private readonly bool[,] _matrix = new bool[Rows, Columns];

    public KeyboardDevice(CPoutLatch cpOut) => _cpOut = cpOut;

    // ---- Bus face ------------------------------------------------------------------

    /// <summary>Answers an <c>IN</c> from keyboard port <paramref name="port"/>
    /// (0x00–0x09). Returns 0xFF for any port outside that range.</summary>
    public byte ReadPort(byte port)
    {
        if (port >= Rows) return 0xFF;

        if (_cpOut.Kbien)
        {
            // KBIEN=1: only port 0 is meaningful (AND of all rows); 1-9 = 0xFF.
            return port == 0 ? AndOfAllRows() : (byte)0xFF;
        }
        else
        {
            return RowByte(port);
        }
    }

    // ---- Host face -----------------------------------------------------------------

    /// <summary>Sets or clears one key switch in the matrix. Call this at a field boundary
    /// on the emulation thread (project CLAUDE.md §7 observer rule) to avoid races with the
    /// 50 Hz ISR scan. Row 0–9, column 0–7.</summary>
    public void SetKey(int row, int col, bool pressed)
    {
        if ((uint)row >= Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)col >= Columns) throw new ArgumentOutOfRangeException(nameof(col));
        _matrix[row, col] = pressed;
    }

    /// <summary>Returns whether the key at (row, col) is currently pressed (direct matrix
    /// state, no ghosting). Useful for the UI to reflect which keys are down.</summary>
    public bool IsKeyPressed(int row, int col) => _matrix[row, col];

    // ---- IDevice -------------------------------------------------------------------

    public void Reset() => Array.Clear(_matrix);

    public void SaveState(IStateWriter writer)
    {
        // Pack each row's 8-column booleans into one byte (bit N = column N, pressed=1).
        for (var row = 0; row < Rows; row++)
        {
            byte b = 0;
            for (var col = 0; col < Columns; col++)
                if (_matrix[row, col]) b |= (byte)(1 << col);
            writer.WriteByte(b);
        }
    }

    public void LoadState(IStateReader reader)
    {
        for (var row = 0; row < Rows; row++)
        {
            var b = reader.ReadByte();
            for (var col = 0; col < Columns; col++)
                _matrix[row, col] = (b & (1 << col)) != 0;
        }
    }

    // ---- Private helpers -----------------------------------------------------------

    /// <summary>Active-low byte for one row, including phantom ghosting from three-corner
    /// combinations (diode-less matrix — reference doc §5f: "ghosting emerges naturally").</summary>
    private byte RowByte(int row)
    {
        byte value = 0xFF;
        for (var col = 0; col < Columns; col++)
        {
            if (IsColumnLow(row, col))
                value &= (byte)~(1 << col);
        }
        return value;
    }

    /// <summary>Returns true if column <paramref name="col"/> reads low (pressed) when
    /// scanning row <paramref name="row"/>. True for a directly-pressed key OR for a phantom
    /// caused by three other pressed keys forming the other three corners of a matrix
    /// rectangle (the diode-less ghost condition).</summary>
    private bool IsColumnLow(int row, int col)
    {
        // Direct: key at this crosspoint is pressed.
        if (_matrix[row, col]) return true;

        // Ghost: exists (row2, col2) ≠ (row, col) such that
        // (row, col2), (row2, col2), (row2, col) are ALL pressed → current loops
        // row → col2 → row2 → col, pulling col low even though (row, col) is open.
        for (var row2 = 0; row2 < Rows; row2++)
        {
            if (row2 == row) continue;
            for (var col2 = 0; col2 < Columns; col2++)
            {
                if (col2 == col) continue;
                if (_matrix[row, col2] && _matrix[row2, col2] && _matrix[row2, col])
                    return true;
            }
        }
        return false;
    }

    private byte AndOfAllRows()
    {
        byte value = 0xFF;
        for (var row = 0; row < Rows; row++)
            value &= RowByte(row);
        return value;
    }
}

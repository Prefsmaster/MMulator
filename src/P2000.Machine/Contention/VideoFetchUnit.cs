using P2000.Machine.State;

namespace P2000.Machine.Contention;

/// <summary>
/// The SAA5020's role (reference doc §4/§4a): a deterministic character-clock counter that
/// knows, for every master-clock T-state, whether a VRAM display fetch is happening this slot
/// and which column it's for. This is the seam milestone 10's bus contention plugs into -
/// today (milestone 5) it just drives the video device's fetch/render schedule; no corruption
/// yet (the video device always sees a clean fetch).
///
/// Counts in **fields**, not frames (project CLAUDE.md §3): the P2000T is interlaced at 50
/// fields/sec, so this 50,000-T-state/240-active-line cycle is one field (either the even or
/// odd set of sub-scanlines) - two fields make one interlaced frame. <see cref="Video"/> is
/// the one that knows which field is currently running.
/// </summary>
public sealed class VideoFetchUnit : IDevice
{
    /// <summary>64 µs/line × 2.5 MHz (reference doc §4a).</summary>
    public const int TStatesPerLine = 160;

    /// <summary>50 Hz field rate at 2.5 MHz (reference doc §4a / project CLAUDE.md §3: the
    /// P2000T's "50 Hz" cycle is a field, not a frame).</summary>
    public const int TStatesPerField = 50_000;

    /// <summary>24 rows × 10 scanlines (reference doc §4a).</summary>
    public const int ActiveLines = 240;

    /// <summary>Scanlines preceding the active window within a 313-line field (reference doc
    /// §4/§4a, owner-supplied Field Service manual: active window is scanlines 49-289) —
    /// CORRECTED 2026-07-22 (project CLAUDE.md §17, 2026-07-19 finding): fetch scheduling
    /// previously started at field-T-state 0 with no pre-roll offset at all, treating real
    /// hardware's vertical-blank T-states as fetch-eligible — the leading hypothesis for the
    /// reported Ghosthunt top-of-screen glitch (48/313 ≈ 15.3% ≈ "top 15%"). 49 pre-roll +
    /// 240 active + 24 post-roll = 313 lines total (unchanged field length).</summary>
    public const int VerticalBlankLines = 49;

    /// <summary>40 µs active fetch / 64 µs line (reference doc §4a): 100 of the 160
    /// T-states/line are the fetch window; the rest is horizontal blank.</summary>
    public const int ActiveTStatesPerLine = 100;

    public const int Columns = 40;

    private int _fieldTState;
    private int _column;

    /// <summary>T-state offset within the current 50 Hz field (0 –
    /// <see cref="TStatesPerField"/>-1). Together with <see cref="Line"/> and
    /// <see cref="LineTState"/> this gives the debugger's in-frame cycle position.</summary>
    public int FieldTState => _fieldTState;

    public int Line { get; private set; }

    public int LineTState { get; private set; }

    public bool IsActiveLine => Line >= VerticalBlankLines && Line < VerticalBlankLines + ActiveLines;

    /// <summary>True for the T-state immediately after a <see cref="ColumnFetch"/> fired -
    /// i.e., this tick a VRAM display fetch was issued. <see cref="Machine"/> reads this
    /// after the CPU step to detect bus contention (milestone 10, project CLAUDE.md §3 step 4,
    /// reference doc §4).</summary>
    public bool IsFetchTick { get; private set; }

    /// <summary>Column index (0-39) of the fetch that fired during the current tick.
    /// Only valid when <see cref="IsFetchTick"/> is true.</summary>
    public int LastFetchColumn { get; private set; }

    /// <summary>Scanline (0-239) on which the current tick's fetch was issued.
    /// Only valid when <see cref="IsFetchTick"/> is true.</summary>
    public int LastFetchLine { get; private set; }

    /// <summary>Raised once per column, at that column's fetch slot, with the column index
    /// (0-39). The listener reads VRAM and feeds the generator - kept a real per-T-state
    /// event so milestone 10 can intercept it for contention without restructuring the
    /// schedule.</summary>
    public event Action<int>? ColumnFetch;

    /// <summary>Raised once a scanline's fetch window has fully elapsed (SAA5020 LOSE pulse),
    /// so the video device can advance its own per-row state. All 40 <see cref="ColumnFetch"/>
    /// events for that line have already fired by the time this raises.</summary>
    public event Action? LineComplete;

    /// <summary>Raised at the 50 Hz field boundary (SAA5020 DEW pulse), after that field's
    /// final <see cref="LineComplete"/>.</summary>
    public event Action? FieldComplete;

    public void Reset()
    {
        _fieldTState = 0;
        _column = 0;
        Line = 0;
        LineTState = 0;
    }

    /// <summary>Advances the fetch-timing unit by exactly one T-state (project CLAUDE.md §3
    /// step 1: this runs before the CPU steps, ahead of the contention check for "was a
    /// fetch requested this slot"). Sets <see cref="IsFetchTick"/> so <see cref="Machine"/>
    /// can resolve contention after the CPU step (reference doc §4).</summary>
    public void Tick()
    {
        IsFetchTick = false;
        if (IsActiveLine && _column < Columns && LineTState == FetchSlot(_column))
        {
            IsFetchTick = true;
            LastFetchColumn = _column;
            LastFetchLine = Line; // capture before Line is updated at the end of this tick
            ColumnFetch?.Invoke(_column);
            _column++;
        }

        _fieldTState++;
        var wrapped = _fieldTState == TStatesPerField;
        if (wrapped)
        {
            _fieldTState = 0;
        }

        var newLine = _fieldTState / TStatesPerLine;
        var newLineTState = _fieldTState % TStatesPerLine;

        if (newLine != Line || wrapped)
        {
            LineComplete?.Invoke();
            _column = 0;
        }

        Line = newLine;
        LineTState = newLineTState;

        if (wrapped)
        {
            FieldComplete?.Invoke();
        }
    }

    /// <summary>The character-fetch rate is 1 MHz against the 2.5 MHz master clock - 2.5
    /// T-states/column, not an integer, so slots land at <c>floor(column * 2.5)</c>
    /// (reference doc §4a: the exact fetch bus-occupancy is unconfirmed pending a
    /// logic-analyzer capture; evenly-spaced integer slots are the best available
    /// approximation until then).</summary>
    private static int FetchSlot(int column) => column * ActiveTStatesPerLine / Columns;

    public void SaveState(IStateWriter writer)
    {
        writer.WriteInt32(_fieldTState);
        writer.WriteInt32(_column);
        writer.WriteInt32(Line);
        writer.WriteInt32(LineTState);
    }

    public void LoadState(IStateReader reader)
    {
        _fieldTState = reader.ReadInt32();
        _column = reader.ReadInt32();
        Line = reader.ReadInt32();
        LineTState = reader.ReadInt32();
    }
}

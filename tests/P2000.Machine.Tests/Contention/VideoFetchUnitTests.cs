using P2000.Machine.Contention;

namespace P2000.Machine.Tests.Contention;

public class VideoFetchUnitTests
{
    /// <summary>Ticks past the 49-line vertical-blank pre-roll (project CLAUDE.md §17,
    /// 2026-07-22 correction) so a test starts right at the active window's first T-state.</summary>
    private static void AdvanceToActiveWindowStart(VideoFetchUnit unit)
    {
        for (var i = 0; i < VideoFetchUnit.VerticalBlankLines * VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }
    }

    // ---- Column fetch scheduling within one active line -----------------------------------

    [Fact]
    public void Tick_OneActiveLine_FiresColumnFetch40Times_InOrder()
    {
        var unit = new VideoFetchUnit();
        AdvanceToActiveWindowStart(unit);
        var columns = new List<int>();
        unit.ColumnFetch += columns.Add;

        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.Equal(Enumerable.Range(0, VideoFetchUnit.Columns), columns);
    }

    [Fact]
    public void Tick_ColumnZero_FetchesOnTheActiveWindowsFirstTState()
    {
        var unit = new VideoFetchUnit();
        AdvanceToActiveWindowStart(unit);
        var fired = false;
        unit.ColumnFetch += column => fired |= column == 0;

        unit.Tick();

        Assert.True(fired);
    }

    [Fact]
    public void Tick_LastColumn_FetchesWithinTheActiveWindow()
    {
        var unit = new VideoFetchUnit();
        AdvanceToActiveWindowStart(unit);
        var lastColumnTState = -1;
        unit.ColumnFetch += column =>
        {
            if (column == VideoFetchUnit.Columns - 1)
            {
                lastColumnTState = unit.LineTState;
            }
        };

        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.InRange(lastColumnTState, 0, VideoFetchUnit.ActiveTStatesPerLine - 1);
    }

    // ---- Line/field boundaries -------------------------------------------------------------

    [Fact]
    public void Tick_OneLine_FiresLineCompleteExactlyOnce()
    {
        var unit = new VideoFetchUnit();
        var lineCompletions = 0;
        unit.LineComplete += () => lineCompletions++;

        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.Equal(1, lineCompletions);
    }

    [Fact]
    public void Tick_OneField_FiresFieldCompleteExactlyOnce()
    {
        var unit = new VideoFetchUnit();
        var fieldCompletions = 0;
        unit.FieldComplete += () => fieldCompletions++;

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            unit.Tick();
        }

        Assert.Equal(1, fieldCompletions);
    }

    [Fact]
    public void Tick_OneField_FetchesEveryActiveLineColumnExactlyOnce()
    {
        var unit = new VideoFetchUnit();
        var fetchCount = 0;
        unit.ColumnFetch += _ => fetchCount++;

        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
        {
            unit.Tick();
        }

        Assert.Equal(VideoFetchUnit.ActiveLines * VideoFetchUnit.Columns, fetchCount);
    }

    /// <summary>Project CLAUDE.md §17, 2026-07-19/2026-07-22: the 49-line vertical-blank
    /// pre-roll must never fetch — this is the fix for the reported Ghosthunt
    /// top-of-screen glitch (fetch scheduling previously started at field-T-state 0, treating
    /// these lines as fetch-eligible).</summary>
    [Fact]
    public void Tick_PreRollLines_NeverFetch()
    {
        var unit = new VideoFetchUnit();
        var fetchedDuringPreRoll = false;
        unit.ColumnFetch += _ => fetchedDuringPreRoll = true;

        for (var i = 0; i < VideoFetchUnit.VerticalBlankLines * VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.False(fetchedDuringPreRoll);
    }

    /// <summary>The post-roll lines (after the 240-line active window) must also never fetch —
    /// unchanged behaviour from before the pre-roll fix, re-asserted at the new offset.</summary>
    [Fact]
    public void Tick_PostRollLines_NeverFetch()
    {
        var unit = new VideoFetchUnit();
        var linesBeforePostRoll = VideoFetchUnit.VerticalBlankLines + VideoFetchUnit.ActiveLines;

        for (var i = 0; i < linesBeforePostRoll * VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        var fetchedDuringPostRoll = false;
        unit.ColumnFetch += _ => fetchedDuringPostRoll = true;

        for (var i = 0; i < VideoFetchUnit.TStatesPerField - linesBeforePostRoll * VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.False(fetchedDuringPostRoll);
    }

    [Fact]
    public void Reset_ReturnsToLineZeroTStateZero()
    {
        var unit = new VideoFetchUnit();
        for (var i = 0; i < 12_345; i++)
        {
            unit.Tick();
        }

        unit.Reset();

        Assert.Equal(0, unit.Line);
        Assert.Equal(0, unit.LineTState);
    }
}

using P2000.Machine.Contention;

namespace P2000.Machine.Tests.Contention;

public class VideoFetchUnitTests
{
    // ---- Column fetch scheduling within one active line -----------------------------------

    [Fact]
    public void Tick_OneActiveLine_FiresColumnFetch40Times_InOrder()
    {
        var unit = new VideoFetchUnit();
        var columns = new List<int>();
        unit.ColumnFetch += columns.Add;

        for (var i = 0; i < VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        Assert.Equal(Enumerable.Range(0, VideoFetchUnit.Columns), columns);
    }

    [Fact]
    public void Tick_ColumnZero_FetchesOnTheLinesFirstTState()
    {
        var unit = new VideoFetchUnit();
        var fired = false;
        unit.ColumnFetch += column => fired |= column == 0;

        unit.Tick();

        Assert.True(fired);
    }

    [Fact]
    public void Tick_LastColumn_FetchesWithinTheActiveWindow()
    {
        var unit = new VideoFetchUnit();
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

    [Fact]
    public void Tick_VblankLines_NeverFetch()
    {
        var unit = new VideoFetchUnit();
        var fetchedDuringVblank = false;

        // Run past the active display (240 lines) into vblank and watch for stray fetches.
        for (var i = 0; i < VideoFetchUnit.ActiveLines * VideoFetchUnit.TStatesPerLine; i++)
        {
            unit.Tick();
        }

        unit.ColumnFetch += _ => fetchedDuringVblank = true;

        for (var i = 0; i < (VideoFetchUnit.TStatesPerField - VideoFetchUnit.ActiveLines * VideoFetchUnit.TStatesPerLine); i++)
        {
            unit.Tick();
        }

        Assert.False(fetchedDuringVblank);
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

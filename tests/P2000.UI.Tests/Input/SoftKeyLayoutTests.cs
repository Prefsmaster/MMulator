using P2000.UI.Input;

namespace P2000.UI.Tests.Input;

/// <summary>
/// Data-integrity tests for <see cref="SoftKeyLayout"/> (project CLAUDE.md §14.3a test (e)):
/// every position must be either host-reachable via ms.3's <see cref="KeyMap"/>, or one of the
/// three positions confirmed to have no host equivalent at all — never a guessed coordinate.
/// </summary>
public class SoftKeyLayoutTests
{
    private static IEnumerable<SoftKeyDef> AllKeys()
        => SoftKeyLayout.Rows.SelectMany(r => r).Concat(SoftKeyLayout.Numpad.SelectMany(r => r));

    [Fact]
    public void EveryKeyWithHostKey_MatrixPositionMatchesKeyMap()
    {
        foreach (var def in AllKeys())
        {
            if (def.HostKey is not { } key) continue;
            var mapped = KeyMap.Map(key);
            Assert.True(mapped.HasValue, $"{key} has no KeyMap entry but SoftKeyDef at ({def.Row},{def.Col}) claims it.");
            Assert.Equal((def.Row, def.Col), (mapped!.Value.Row, mapped.Value.Col));
        }
    }

    [Fact]
    public void KeysWithNoHostKey_AreExactlyTheTwoConfirmedUnreachablePositions()
    {
        var noHostKey = AllKeys().Where(d => d.HostKey is null).Select(d => (d.Row, d.Col)).ToHashSet();

        // np00/TB and the envelope/centre-tab key — see §18 2026-07-19 finding. "#/°" (2,4) is
        // no longer in this set: real-hardware confirmation (2026-07-19) wired OemPipe there.
        var expected = new HashSet<(int, int)> { (2, 2), (5, 0) };

        Assert.Equal(expected, noHostKey);
    }

    [Fact]
    public void NoDuplicateMatrixPositions()
    {
        var positions = AllKeys().Select(d => (d.Row, d.Col)).ToList();
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }
}

using P2000.Machine.Devices;
using P2000.Machine.Io;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices;

public class KeyboardTests
{
    private static (KeyboardDevice kb, CPoutLatch cpOut) Create()
    {
        var cpOut = new CPoutLatch();
        return (new KeyboardDevice(cpOut), cpOut);
    }

    // ---- All keys released ---------------------------------------------------------

    [Fact]
    public void AllReleased_KbienOff_AllPortsReturn0xFF()
    {
        var (kb, _) = Create(); // KBIEN=0 by default (latch resets to 0)
        for (byte port = 0; port <= 9; port++)
            Assert.Equal(0xFF, kb.ReadPort(port));
    }

    [Fact]
    public void AllReleased_KbienOn_Port0Returns0xFF()
    {
        var (kb, cpOut) = Create();
        cpOut.Write(0x40); // KBIEN=1
        Assert.Equal(0xFF, kb.ReadPort(0));
    }

    [Fact]
    public void PortBeyond9_ReturnsOpenBus()
    {
        var (kb, _) = Create();
        Assert.Equal(0xFF, kb.ReadPort(10));
        Assert.Equal(0xFF, kb.ReadPort(255));
    }

    // ---- Single key pressed, KBIEN=0 -----------------------------------------------

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 7)]
    [InlineData(5, 3)] [InlineData(9, 7)]
    public void SingleKey_KbienOff_CorrectRowBitCleared(int row, int col)
    {
        var (kb, _) = Create();
        kb.SetKey(row, col, pressed: true);

        var rowByte = kb.ReadPort((byte)row);
        Assert.Equal(0, rowByte & (1 << col)); // pressed column bit is 0 (active-low)
        Assert.Equal(0xFF & ~(1 << col), rowByte); // all other bits still 1
    }

    [Fact]
    public void SingleKey_KbienOff_OtherRowsUnaffected()
    {
        var (kb, _) = Create();
        kb.SetKey(3, 2, pressed: true);

        for (byte port = 0; port <= 9; port++)
        {
            if (port == 3) continue;
            Assert.Equal(0xFF, kb.ReadPort(port));
        }
    }

    // ---- Single key pressed, KBIEN=1 -----------------------------------------------

    [Fact]
    public void SingleKey_KbienOn_Port0ReturnsAndOfAllRows_NonFF()
    {
        var (kb, cpOut) = Create();
        cpOut.Write(0x40); // KBIEN=1
        kb.SetKey(5, 3, pressed: true);

        var port0 = kb.ReadPort(0);
        Assert.NotEqual(0xFF, port0);
        // AND of all rows: only row 5 col 3 is low, so bit 3 should be clear
        Assert.Equal(0, port0 & (1 << 3));
    }

    [Fact]
    public void SingleKey_KbienOn_Ports1Through9Return0xFF()
    {
        var (kb, cpOut) = Create();
        cpOut.Write(0x40); // KBIEN=1
        kb.SetKey(2, 1, pressed: true);

        for (byte port = 1; port <= 9; port++)
            Assert.Equal(0xFF, kb.ReadPort(port));
    }

    // ---- Multiple keys, KBIEN=0 ----------------------------------------------------

    [Fact]
    public void MultipleKeysInSameRow_KbienOff_MultipleBitsCleared()
    {
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true);
        kb.SetKey(0, 3, pressed: true);
        kb.SetKey(0, 7, pressed: true);

        var row0 = kb.ReadPort(0);
        Assert.Equal(0, row0 & 0b10001001); // bits 0, 3, 7 low
        Assert.Equal(0xFF & ~0b10001001, row0); // others high
    }

    // ---- Ghosting (three-corner → phantom fourth) ----------------------------------

    /// <summary>
    /// Pressing keys at (r0,c0), (r0,c1), and (r1,c0) — three corners of a 2×2
    /// matrix rectangle — must make (r1,c1) appear pressed (the phantom fourth corner),
    /// exactly as the diode-less hardware produces (reference doc §5f).
    /// </summary>
    [Fact]
    public void Ghost_ThreeCorners_PhantomFourthAppears()
    {
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true); // corner A
        kb.SetKey(0, 1, pressed: true); // corner B
        kb.SetKey(1, 0, pressed: true); // corner C
        // (1, 1) NOT physically pressed — but should ghost

        var row1 = kb.ReadPort(1);
        Assert.Equal(0, row1 & (1 << 1)); // bit 1 low = phantom key at (1,1)
    }

    [Fact]
    public void Ghost_TwoCornersOnly_NoPhantom()
    {
        // Only two corners of a rectangle → no ghost (need all three).
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true);
        kb.SetKey(0, 1, pressed: true);
        // row 1 has nothing pressed directly

        var row1 = kb.ReadPort(1);
        Assert.Equal(0xFF, row1); // no phantom
    }

    [Fact]
    public void Ghost_AllFourCornersPressed_StillReadsLow()
    {
        var (kb, _) = Create();
        kb.SetKey(2, 2, pressed: true);
        kb.SetKey(2, 5, pressed: true);
        kb.SetKey(4, 2, pressed: true);
        kb.SetKey(4, 5, pressed: true);

        // All four directly pressed — both rows should have both columns low.
        Assert.Equal(0, kb.ReadPort(2) & ((1 << 2) | (1 << 5)));
        Assert.Equal(0, kb.ReadPort(4) & ((1 << 2) | (1 << 5)));
    }

    // ---- SetKey validation ---------------------------------------------------------

    [Fact]
    public void SetKey_InvalidRow_Throws()
    {
        var (kb, _) = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => kb.SetKey(10, 0, true));
    }

    [Fact]
    public void SetKey_InvalidCol_Throws()
    {
        var (kb, _) = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => kb.SetKey(0, 8, true));
    }

    // ---- IsKeyPressed --------------------------------------------------------------

    [Fact]
    public void IsKeyPressed_ReflectsDirectStateOnly_NotGhost()
    {
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true);
        kb.SetKey(0, 1, pressed: true);
        kb.SetKey(1, 0, pressed: true);

        Assert.True(kb.IsKeyPressed(0, 0));
        Assert.True(kb.IsKeyPressed(0, 1));
        Assert.True(kb.IsKeyPressed(1, 0));
        Assert.False(kb.IsKeyPressed(1, 1)); // ghost in port read, but NOT pressed directly
    }

    // ---- Reset ---------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsAllKeys()
    {
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true);
        kb.SetKey(9, 7, pressed: true);

        kb.Reset();

        for (byte port = 0; port <= 9; port++)
            Assert.Equal(0xFF, kb.ReadPort(port));
    }

    // ---- Save/Load state -----------------------------------------------------------

    [Fact]
    public void SaveLoad_RoundTrip_PreservesMatrix()
    {
        var (kb, _) = Create();
        kb.SetKey(0, 0, pressed: true);
        kb.SetKey(3, 5, pressed: true);
        kb.SetKey(9, 7, pressed: true);

        var state = new InMemoryState();
        kb.SaveState(state);

        var (kb2, _) = Create();
        kb2.LoadState(state.BeginRead());

        Assert.True(kb2.IsKeyPressed(0, 0));
        Assert.True(kb2.IsKeyPressed(3, 5));
        Assert.True(kb2.IsKeyPressed(9, 7));
        Assert.False(kb2.IsKeyPressed(1, 1));
    }

    [Fact]
    public void SaveLoad_RoundTrip_AllReleased()
    {
        var (kb, _) = Create();

        var state = new InMemoryState();
        kb.SaveState(state);

        var (kb2, _) = Create();
        kb2.SetKey(0, 0, pressed: true); // pre-pollute
        kb2.LoadState(state.BeginRead());

        Assert.False(kb2.IsKeyPressed(0, 0));
        for (byte port = 0; port <= 9; port++)
            Assert.Equal(0xFF, kb2.ReadPort(port));
    }
}

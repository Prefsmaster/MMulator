using P2000.Machine.Io;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Io;

public class CPoutLatchTests
{
    // ---- Level bits (reference doc §5f: KBIEN/PRD/FWD/REV are levels) --------------------

    [Fact]
    public void Reset_AllLevelsLow()
    {
        var latch = new CPoutLatch();

        Assert.False(latch.Kbien);
        Assert.False(latch.PrinterData);
        Assert.False(latch.Forward);
        Assert.False(latch.Reverse);
        Assert.False(latch.WriteCommand);
        Assert.False(latch.WriteData);
    }

    [Fact]
    public void Write_SetsAllLevelBitsFromTheWholeByte()
    {
        var latch = new CPoutLatch();

        latch.Write(0xC0 | 0x08 | 0x04); // PRD + KBIEN + FWD + REV

        Assert.True(latch.PrinterData);
        Assert.True(latch.Kbien);
        Assert.True(latch.Forward);
        Assert.True(latch.Reverse);
    }

    [Fact]
    public void Write_KbienClear_ScanModeReadsOff()
    {
        var latch = new CPoutLatch();
        latch.Write(0x40);

        latch.Write(0x00);

        Assert.False(latch.Kbien);
    }

    // ---- The ROM always rewrites the whole byte (KBIEN ORed into every MDCR command) ------

    [Fact]
    public void Write_PreservesOtherBits_WhenCallerOrsTheWholeByteItself()
    {
        // The latch has no per-bit "set" API - the caller (ROM model) always supplies the
        // full byte, exactly like the real firmware's shadow-copy idiom (reference doc §5f).
        var latch = new CPoutLatch();
        latch.Write(0x40); // KBIEN only

        latch.Write((byte)(latch.Current | 0x08)); // ORs in FWD, keeping KBIEN

        Assert.True(latch.Kbien);
        Assert.True(latch.Forward);
    }

    // ---- WCD/WDA are edges, not levels - Written exposes (previous, current) -------------

    [Fact]
    public void Write_RaisesWritten_WithPreviousAndCurrentBytes()
    {
        var latch = new CPoutLatch();
        latch.Write(0x01); // WDA set
        (byte previous, byte current)? seen = null;
        latch.Written += (prev, cur) => seen = (prev, cur);

        latch.Write(0x02); // WCD set, WDA cleared - a transition on both bits

        Assert.NotNull(seen);
        Assert.Equal((byte)0x01, seen.Value.previous);
        Assert.Equal((byte)0x02, seen.Value.current);
    }

    [Fact]
    public void Write_NoSubscriber_DoesNotThrow()
    {
        var latch = new CPoutLatch();

        latch.Write(0xFF);
    }

    // ---- Reset returns the shadow byte to 0 ------------------------------------------------

    [Fact]
    public void Reset_ClearsTheShadowByte()
    {
        var latch = new CPoutLatch();
        latch.Write(0xFF);

        latch.Reset();

        Assert.Equal(0x00, latch.Current);
    }

    // ---- SaveState/LoadState round-trip the shadow byte ------------------------------------

    [Fact]
    public void SaveState_ThenLoadState_RestoresTheShadowByte()
    {
        var latch = new CPoutLatch();
        latch.Write(0xAB);
        var state = new InMemoryState();
        latch.SaveState(state);

        var restored = new CPoutLatch();
        restored.LoadState(state.BeginRead());

        Assert.Equal(0xAB, restored.Current);
    }
}

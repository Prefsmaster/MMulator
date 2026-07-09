using P2000.Machine.Devices;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// Machine-level tests for <see cref="SoundDevice"/> (milestone 16).
/// These prove the machine can drive audio OUT to a consumer via SamplesReady — the
/// UI/OpenAL sink is just another subscriber; nothing here touches OpenAL.
/// Beeper source: I/O port 0x50 bit 0 (confirmed 2026-07-09 — §17 finding).
/// </summary>
public class SoundDeviceTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    // SetFieldT writes the variable that SoundDevice's lambda captures — they must be the same.
    private static (SoundDevice Sound, List<short[]> Blocks, Action<int> SetFieldT)
        CreateSink()
    {
        int fieldTState = 0;
        var sound  = new SoundDevice(() => fieldTState);
        var blocks = new List<short[]>();

        sound.SamplesReady += buf =>
        {
            var copy = new short[buf.Length];
            Array.Copy(buf, copy, buf.Length);
            blocks.Add(copy);
        };

        return (sound, blocks, t => { fieldTState = t; });
    }

    /// <summary>Runs one 50 000-T-state field, toggling port 0x50 bit 0 at each listed
    /// T-state, then fires <c>OnFieldComplete</c>.</summary>
    /// <param name="initialPort50">Current port-0x50 byte at field start — must reflect the
    /// device's current beeper state so the first toggle actually flips, not repeats.</param>
    private static void RunField(
        SoundDevice sound, Action<int> setFieldT,
        IEnumerable<int>? togglesAtTState = null, byte initialPort50 = 0x00)
    {
        var toggleQueue = new Queue<int>(togglesAtTState ?? []);
        byte port50 = initialPort50;

        for (int t = 0; t < 50_000; t++)
        {
            setFieldT(t);
            while (toggleQueue.Count > 0 && t >= toggleQueue.Peek())
            {
                toggleQueue.Dequeue();
                port50 ^= 0x01;             // flip beeper bit (bit 0 of port 0x50)
                sound.OnPortWrite(port50);
            }
        }

        sound.OnFieldComplete();
        setFieldT(0);
    }

    // ─── Port / bit constants ──────────────────────────────────────────────────

    [Fact]
    public void Port_Is0x50()
    {
        Assert.Equal(0x50, SoundDevice.Port);
    }

    // ─── (a) One block per field ───────────────────────────────────────────────

    [Fact]
    public void OneBlock_PerField_NoBeeper()
    {
        var (sound, blocks, setT) = CreateSink();

        for (int f = 0; f < 5; f++)
            RunField(sound, setT);

        Assert.Equal(5, blocks.Count);
    }

    [Fact]
    public void OneBlock_PerField_WithBeeperToggle()
    {
        var (sound, blocks, setT) = CreateSink();

        for (int f = 0; f < 3; f++)
            RunField(sound, setT, [10_000, 30_000]);

        Assert.Equal(3, blocks.Count);
    }

    // ─── (b) Block length + sample rate constants ──────────────────────────────

    [Fact]
    public void Constants_SampleRate44100_And882SamplesPerField()
    {
        Assert.Equal(44_100, SoundDevice.SampleRate);
        Assert.Equal(882,    SoundDevice.SamplesPerField);
        Assert.Equal(882,    SoundDevice.SampleRate / 50);
    }

    [Fact]
    public void Block_Length_Is882()
    {
        var (sound, blocks, setT) = CreateSink();
        RunField(sound, setT);

        Assert.Equal(882, blocks[0].Length);
    }

    // ─── (c) Silence when beeper is off ───────────────────────────────────────

    [Fact]
    public void Block_IsFlatZero_WhenBeeperNeverFired()
    {
        var (sound, blocks, setT) = CreateSink();
        RunField(sound, setT);

        Assert.All(blocks[0], s => Assert.Equal(0, s));
    }

    [Fact]
    public void Block_IsFlatZero_ForMultipleFields_WhenBeeperNeverFired()
    {
        var (sound, blocks, setT) = CreateSink();

        for (int f = 0; f < 4; f++)
            RunField(sound, setT);

        foreach (var block in blocks)
            Assert.All(block, s => Assert.Equal(0, s));
    }

    // ─── (c) Non-constant square wave when beeper toggles ─────────────────────

    [Fact]
    public void Block_IsNonConstant_WhenBeeperTogglesMidField()
    {
        var (sound, blocks, setT) = CreateSink();

        // Toggle ON at T=10 000 (port 0x50 bit 0 set), OFF at T=40 000.
        RunField(sound, setT, [10_000, 40_000]);

        var block = blocks[0];
        Assert.True(block.Any(s => s > 0), "Expected high samples while beeper is on");
        Assert.True(block.Any(s => s == 0), "Expected zero samples while beeper is off");
    }

    [Fact]
    public void Block_HighSamples_OccupyExpectedProportion_WhenBeeperOnHalfField()
    {
        var (sound, blocks, setT) = CreateSink();

        // Toggle ON at T=25 000 → beeper on for the second half.
        RunField(sound, setT, [25_000]);

        var block     = blocks[0];
        int highCount = block.Count(s => s > 0);

        int half = SoundDevice.SamplesPerField / 2;
        Assert.InRange(highCount, half - 50, half + 50);
    }

    [Fact]
    public void Block_AllHigh_WhenBeeperOnForEntireField()
    {
        var (sound, blocks, setT) = CreateSink();

        // Turn beeper ON before the field starts (simulates previous-field state).
        sound.OnPortWrite(0x01);
        sound.OnFieldComplete();   // clears transitions; beeper already on
        blocks.Clear();

        // Full field with no further toggles.
        RunField(sound, setT);

        Assert.All(blocks[0], s => Assert.True(s > 0, $"Expected all samples high, got {s}"));
    }

    [Fact]
    public void Block_HiToLo_Transition_SamplesAreCausal()
    {
        // Beeper starts ON (prior field), turns OFF at the midpoint.
        var (sound, blocks, setT) = CreateSink();

        sound.OnPortWrite(0x01);   // turn beeper on
        sound.OnFieldComplete();   // beeper=on going into next field
        blocks.Clear();

        RunField(sound, setT, [25_000], initialPort50: 0x01);   // toggle OFF at midpoint

        var block = blocks[0];
        int mid   = SoundDevice.SamplesPerField / 2;

        // First quarter must be non-zero (beeper was on).
        for (int i = 0; i < mid - 5; i++)
            Assert.True(block[i] > 0, $"Sample {i} before midpoint should be high");

        // Last quarter must be zero (beeper went off).
        for (int i = mid + 5; i < SoundDevice.SamplesPerField; i++)
            Assert.Equal(0, block[i]);
    }

    // ─── Reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterReset_BlockIsSilent_EvenIfBeeperWasOn()
    {
        var (sound, blocks, setT) = CreateSink();

        sound.OnPortWrite(0x01);   // turn beeper on
        sound.Reset();             // must clear state

        RunField(sound, setT);     // no further writes

        Assert.All(blocks[0], s => Assert.Equal(0, s));
    }

    // ─── SaveState / LoadState ─────────────────────────────────────────────────

    [Fact]
    public void SaveLoad_PreservesBeeper_OnState()
    {
        var (sound, _, _) = CreateSink();

        sound.OnPortWrite(0x01);   // turn beeper on

        var state = new InMemoryState();
        sound.SaveState(state);
        state.BeginRead();

        var (sound2, blocks2, setT2) = CreateSink();
        sound2.LoadState(state);

        RunField(sound2, setT2);

        Assert.All(blocks2[0], s => Assert.True(s > 0,
            "Loaded beeper-on state should produce all-high samples"));
    }

    [Fact]
    public void SaveLoad_PreservesBeeper_OffState()
    {
        var (sound, _, _) = CreateSink();
        // beeper stays off (default)

        var state = new InMemoryState();
        sound.SaveState(state);
        state.BeginRead();

        var (sound2, blocks2, setT2) = CreateSink();
        sound2.OnPortWrite(0x01);   // turn beeper on before loading — load must override
        sound2.LoadState(state);

        RunField(sound2, setT2);

        Assert.All(blocks2[0], s => Assert.Equal(0, s));
    }

    // ─── Machine integration: port 0x50 wired to SoundDevice ──────────────────

    [Fact]
    public void Machine_OutToPort50_Bit0_TriggersBeeper()
    {
        // Confirm the machine's port dispatch routes port 0x50 writes to SoundDevice.
        var machine = new P2000.Machine.Machine();
        var blocks = new List<short[]>();
        machine.Sound.SamplesReady += buf =>
        {
            var copy = new short[buf.Length];
            Array.Copy(buf, copy, buf.Length);
            blocks.Add(copy);
        };

        // OUT (0x50), 0x01 — turn beeper on
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x01,       // LD A, 1
            0xD3, 0x50,       // OUT (0x50), A
            0x76,             // HALT
        });

        // Run enough ticks to execute the three instructions (≤ 30 T-states)
        // then drive a full field so OnFieldComplete fires.
        for (int i = 0; i < 30; i++) machine.Tick();
        for (int i = 0; i < 50_000; i++) machine.Tick();  // one full field

        Assert.True(blocks.Count > 0, "SamplesReady should have fired after one field");
        Assert.True(blocks[^1].Any(s => s > 0),
            "Beeper was set via port 0x50 — at least some samples should be high");
    }
}

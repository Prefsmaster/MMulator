using P2000.Machine.Devices;
using P2000.Machine.Io;
using P2000.Machine.Tests.State;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// Machine-level tests for <see cref="SoundDevice"/> (milestone 16).
/// These prove the machine can drive audio OUT to a consumer via SamplesReady — the
/// UI/OpenAL sink is just another subscriber; nothing here touches OpenAL.
/// </summary>
public class SoundDeviceTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    // SetFieldT writes the variable that SoundDevice's lambda captures — they must be the same.
    private static (SoundDevice Sound, CPoutLatch CpOut, List<short[]> Blocks, Action<int> SetFieldT)
        CreateSink()
    {
        int fieldTState = 0;
        var cpOut  = new CPoutLatch();
        var sound  = new SoundDevice(cpOut, () => fieldTState);
        var blocks = new List<short[]>();

        sound.SamplesReady += buf =>
        {
            var copy = new short[buf.Length];
            Array.Copy(buf, copy, buf.Length);
            blocks.Add(copy);
        };

        return (sound, cpOut, blocks, t => { fieldTState = t; });
    }

    /// <summary>Runs one 50 000-T-state field, toggling CPOUT bit 4 at each listed T-state,
    /// then fires <c>OnFieldComplete</c>.</summary>
    /// <param name="initialCpout">Current CPOUT byte at field start — must match the latch's
    /// state so the first toggle actually flips the beeper rather than repeating it.</param>
    private static void RunField(
        SoundDevice sound, CPoutLatch cpOut, Action<int> setFieldT,
        IEnumerable<int>? togglesAtTState = null, byte initialCpout = 0x00)
    {
        var toggleQueue = new Queue<int>(togglesAtTState ?? []);
        byte cpoutByte = initialCpout;

        for (int t = 0; t < 50_000; t++)
        {
            setFieldT(t);
            while (toggleQueue.Count > 0 && t >= toggleQueue.Peek())
            {
                toggleQueue.Dequeue();
                cpoutByte ^= 0x10;
                cpOut.Write(cpoutByte);
            }
        }

        sound.OnFieldComplete();
        setFieldT(0);
    }

    // ─── (a) One block per field ───────────────────────────────────────────────

    [Fact]
    public void OneBlock_PerField_NoBeeper()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        for (int f = 0; f < 5; f++)
            RunField(sound, cpOut, setT);

        Assert.Equal(5, blocks.Count);
    }

    [Fact]
    public void OneBlock_PerField_WithBeeperToggle()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        for (int f = 0; f < 3; f++)
            RunField(sound, cpOut, setT, [10_000, 30_000]);

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
        var (sound, cpOut, blocks, setT) = CreateSink();
        RunField(sound, cpOut, setT);

        Assert.Equal(882, blocks[0].Length);
    }

    // ─── (c) Silence when beeper is off ───────────────────────────────────────

    [Fact]
    public void Block_IsFlatZero_WhenBeeperNeverFired()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();
        RunField(sound, cpOut, setT);

        Assert.All(blocks[0], s => Assert.Equal(0, s));
    }

    [Fact]
    public void Block_IsFlatZero_ForMultipleFields_WhenBeeperNeverFired()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        for (int f = 0; f < 4; f++)
            RunField(sound, cpOut, setT);

        foreach (var block in blocks)
            Assert.All(block, s => Assert.Equal(0, s));
    }

    // ─── (c) Non-constant square wave when beeper toggles ─────────────────────

    [Fact]
    public void Block_IsNonConstant_WhenBeeperTogglesMidField()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        // Toggle ON at T=10 000, OFF at T=40 000.
        RunField(sound, cpOut, setT, [10_000, 40_000]);

        var block = blocks[0];
        Assert.True(block.Any(s => s > 0), "Expected high samples while beeper is on");
        Assert.True(block.Any(s => s == 0), "Expected zero samples while beeper is off");
    }

    [Fact]
    public void Block_HighSamples_OccupyExpectedProportion_WhenBeeperOnHalfField()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        // Toggle ON at T=25 000 → beeper on for the second half.
        RunField(sound, cpOut, setT, [25_000]);

        var block     = blocks[0];
        int highCount = block.Count(s => s > 0);

        int half = SoundDevice.SamplesPerField / 2;
        Assert.InRange(highCount, half - 50, half + 50);
    }

    [Fact]
    public void Block_AllHigh_WhenBeeperOnForEntireField()
    {
        var (sound, cpOut, blocks, setT) = CreateSink();

        // Turn beeper ON before the field starts (simulates previous-field state).
        cpOut.Write(0x10);
        sound.OnFieldComplete();   // clears transitions; beeper already on
        blocks.Clear();

        // Full field with no further toggles.
        RunField(sound, cpOut, setT);

        Assert.All(blocks[0], s => Assert.True(s > 0, $"Expected all samples high, got {s}"));
    }

    [Fact]
    public void Block_HiToLo_Transition_SamplesAreCausal()
    {
        // Beeper starts ON (prior field), turns OFF at the midpoint.
        var (sound, cpOut, blocks, setT) = CreateSink();

        cpOut.Write(0x10);         // turn beeper on
        sound.OnFieldComplete();   // beeper=on going into next field
        blocks.Clear();

        RunField(sound, cpOut, setT, [25_000], initialCpout: 0x10);   // beeper was on; toggle OFF at midpoint

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
        var (sound, cpOut, blocks, setT) = CreateSink();

        cpOut.Write(0x10);  // turn beeper on
        sound.Reset();      // must clear state

        RunField(sound, cpOut, setT);   // no further toggles

        Assert.All(blocks[0], s => Assert.Equal(0, s));
    }

    // ─── SaveState / LoadState ─────────────────────────────────────────────────

    [Fact]
    public void SaveLoad_PreservesBeeper_OnState()
    {
        var (sound, cpOut, _, _) = CreateSink();

        cpOut.Write(0x10);   // turn beeper on

        var state = new InMemoryState();
        sound.SaveState(state);
        state.BeginRead();

        var (sound2, cpOut2, blocks2, setT2) = CreateSink();
        sound2.LoadState(state);

        RunField(sound2, cpOut2, setT2);

        Assert.All(blocks2[0], s => Assert.True(s > 0,
            "Loaded beeper-on state should produce all-high samples"));
    }

    [Fact]
    public void SaveLoad_PreservesBeeper_OffState()
    {
        var (sound, _, _, _) = CreateSink();
        // beeper stays off (default)

        var state = new InMemoryState();
        sound.SaveState(state);
        state.BeginRead();

        var (sound2, cpOut2, blocks2, setT2) = CreateSink();
        // turn beeper on in the new device BEFORE loading, so load must override it
        cpOut2.Write(0x10);
        sound2.LoadState(state);

        RunField(sound2, cpOut2, setT2);

        Assert.All(blocks2[0], s => Assert.Equal(0, s));
    }
}

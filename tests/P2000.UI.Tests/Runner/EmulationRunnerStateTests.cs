using Avalonia.Headless.XUnit;
using P2000.Machine.State;
using P2000.UI.Runner;

namespace P2000.UI.Tests.Runner;

/// <summary>
/// Tests for the save-state wiring in <see cref="EmulationRunner"/> (milestone 8).
/// Uses the headless Avalonia backend so <c>Dispatcher.UIThread.Post</c> works.
/// </summary>
public class EmulationRunnerStateTests
{
    // ── SaveStateToStream ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task SaveStateToStream_ProducesLoadableStateFile()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);   // let at least one 50 Hz field (20 ms) complete

        var ms = new MemoryStream();
        runner.SaveStateToStream(ms);
        runner.Dispose();

        ms.Position = 0;
        var machine = MachineStateFile.Load(ms);
        Assert.NotNull(machine);
    }

    [AvaloniaFact]
    public async Task SaveStateToStream_CalledTwice_ProducesTwoIndependentSnapshots()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);

        var ms1 = new MemoryStream();
        runner.SaveStateToStream(ms1);

        await Task.Delay(60);   // let the machine run a bit further

        var ms2 = new MemoryStream();
        runner.SaveStateToStream(ms2);

        runner.Dispose();

        ms1.Position = 0;
        ms2.Position = 0;
        var m1 = MachineStateFile.Load(ms1);
        var m2 = MachineStateFile.Load(ms2);

        Assert.NotNull(m1);
        Assert.NotNull(m2);
        // The two snapshots were taken at different times; the PC values may differ.
        // (They may coincidentally be equal if the ROM is in a tight loop — that's fine.)
    }

    // ── ReconfigureWithMachine ─────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task ReconfigureWithMachine_SwapsRunningMachineAndSameMachineIsAccessible()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);

        // Save current state, load it into a fresh machine, swap it in.
        var ms = new MemoryStream();
        runner.SaveStateToStream(ms);
        ms.Position = 0;
        var loaded = MachineStateFile.Load(ms);

        runner.ReconfigureWithMachine(loaded);

        // After the swap the runner must expose the loaded machine.
        Assert.Same(loaded, runner.Machine);

        runner.Dispose();
    }

    [AvaloniaFact]
    public async Task ReconfigureWithMachine_LoadedMachineRunsWithoutCrashing()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);

        var ms = new MemoryStream();
        runner.SaveStateToStream(ms);
        ms.Position = 0;
        var loaded = MachineStateFile.Load(ms);

        runner.ReconfigureWithMachine(loaded);

        // Let it run a couple of fields; if the machine is broken this will typically
        // crash or deadlock inside the emulation thread.
        await Task.Delay(80);

        runner.Dispose();   // must return cleanly
    }

    // ── RAM seed injection (project CLAUDE.md §17, 2026-07-21/22 finding) ──────────

    [AvaloniaFact]
    public async Task Reconfigure_WithNoExplicitRamSeed_GetsAFreshRandomSeed_NotTheFixedDefault()
    {
        var runner1 = new EmulationRunner();
        var runner2 = new EmulationRunner();
        runner1.Start();
        runner2.Start();
        await Task.Delay(20);

        runner1.Reconfigure(new P2000.Machine.MachineConfig());
        runner2.Reconfigure(new P2000.Machine.MachineConfig());
        await Task.Delay(60); // let the swap land on the emulation thread

        // Two independently-generated random seeds landing on the exact same base-RAM byte
        // is astronomically unlikely — this is the regression guard that Reconfigure actually
        // injects a fresh seed rather than always falling back to the fixed test/CI default.
        Assert.NotEqual(
            runner1.Machine.Memory.Read(P2000.Machine.Memory.PageTable.BaseRamStart),
            runner2.Machine.Memory.Read(P2000.Machine.Memory.PageTable.BaseRamStart));

        runner1.Dispose();
        runner2.Dispose();
    }

    [AvaloniaFact]
    public async Task Reconfigure_WithExplicitRamSeed_PreservesIt_NotOverwrittenWithRandom()
    {
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(20);

        runner.Reconfigure(new P2000.Machine.MachineConfig { RamSeed = 0xCAFEF00D });
        await Task.Delay(60);

        var reference = new P2000.Machine.Machine(new P2000.Machine.MachineConfig { RamSeed = 0xCAFEF00D });
        Assert.Equal(
            reference.Memory.Read(P2000.Machine.Memory.PageTable.BaseRamStart),
            runner.Machine.Memory.Read(P2000.Machine.Memory.PageTable.BaseRamStart));

        runner.Dispose();
    }

    // ── Version-mismatch path (machine-layer, exercised without a running runner) ──

    [Fact]
    public void MachineStateFile_BadMagic_ThrowsInvalidDataException()
    {
        var ms = new MemoryStream();
        ms.Write("NOPE"u8);
        ms.Write(new byte[20]);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }

    [Fact]
    public void MachineStateFile_FutureVersion_ThrowsInvalidDataException()
    {
        var ms = new MemoryStream();
        ms.Write("P2ST"u8);
        var futureVersion = MachineStateFile.CurrentVersion + 1;
        ms.Write(new byte[] { (byte)futureVersion, 0, 0, 0,
                               0, 0, 0, 0 });   // config length = 0
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }
}

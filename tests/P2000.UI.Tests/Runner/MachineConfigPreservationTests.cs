using System.Reflection;
using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.UI.Runner;

namespace P2000.UI.Tests.Runner;

/// <summary>
/// <see cref="EmulationRunner.Reconfigure"/> rebuilds <see cref="MachineConfig"/> by hand to
/// inject a random RAM seed when the caller did not pin one (which is every config the Config
/// window builds). That hand-written field list has drifted twice — <c>CassettePath</c>, and then
/// <c>Modifications</c> in UI milestone 20, which silently un-fitted the 80-column board on every
/// Apply: ticking the checkbox did nothing at all, with no error.
///
/// These tests walk <see cref="MachineConfig"/> by REFLECTION rather than naming its properties,
/// so a property added in future fails here instead of vanishing at runtime.
/// </summary>
public class MachineConfigPreservationTests
{
    /// <summary>A config with every property set to something distinguishable from its default,
    /// and deliberately NO <see cref="MachineConfig.RamSeed"/> — that null is exactly what sends
    /// it down the rebuild path.</summary>
    private static MachineConfig FullyPopulatedWithoutRamSeed() => new()
    {
        Model = MachineModel.P2000T,
        Board = InternalBoard.FloppyRam,
        RamVariant = RamVariant.T102,
        BankCount = 6,
        MonitorRomPath = null,   // a real path would have to exist on disk
        Slot1CartridgePath = null,
        CassettePath = null,
        FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 1, Capacity = 80 } },
        Modifications = new ModificationsConfig
        {
            EightyColumnBoard = true,
            ShowEightyColumnArtifacts = false,
        },
        RamSeed = null,
    };

    [AvaloniaFact]
    public async Task Reconfigure_PreservesEveryConfigProperty_ExceptTheInjectedRamSeed()
    {
        var runner = new EmulationRunner();
        runner.Start();
        try
        {
            var requested = FullyPopulatedWithoutRamSeed();
            runner.Reconfigure(requested);
            await Task.Delay(120);

            var applied = runner.Machine.Config;
            var drifted = new List<string>();

            foreach (var prop in typeof(MachineConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // RamSeed is the ONE property this path is meant to change.
                if (prop.Name is nameof(MachineConfig.RamSeed)) continue;
                // Computed, not stored.
                if (!prop.CanWrite) continue;

                var want = prop.GetValue(requested);
                var got = prop.GetValue(applied);
                if (Equals(want, got)) continue;

                // Most of these are reference types with no ToString override, so printing the
                // values themselves reads as "requested Foo, applied Foo" and helps nobody. Name
                // the property and say plainly what to do about it.
                drifted.Add($"{prop.Name} (add `{prop.Name} = config.{prop.Name},` to " +
                            "EmulationRunner.EnsureRamSeed)");
            }

            Assert.True(drifted.Count == 0,
                "EmulationRunner's hand-written MachineConfig copy dropped: " + string.Join("; ", drifted));
            Assert.NotNull(applied.RamSeed); // the one thing it is supposed to fill in
        }
        finally { runner.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Reconfigure_ActuallyFitsThe80ColumnBoard_TheRegressionThatWasMissed()
    {
        // The concrete failure the reflection test above generalises: this is what the Config
        // window does when the operator ticks "80-column board" and presses Apply.
        var runner = new EmulationRunner();
        runner.Start();
        try
        {
            runner.Reconfigure(new MachineConfig
            {
                Modifications = new ModificationsConfig { EightyColumnBoard = true },
                // No RamSeed — the rebuild path, exactly as the Config window produces it.
            });
            await Task.Delay(120);

            Assert.True(runner.Machine.Config.Modifications.EightyColumnBoard);
            Assert.NotNull(runner.Machine.EightyColumn);
        }
        finally { runner.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Reconfigure_WithAPinnedRamSeed_TakesTheNoRebuildPath_AndAlsoKeepsTheBoard()
    {
        // The other branch: a config that already pins a seed is passed through untouched.
        var runner = new EmulationRunner();
        runner.Start();
        try
        {
            runner.Reconfigure(new MachineConfig
            {
                Modifications = new ModificationsConfig { EightyColumnBoard = true },
                RamSeed = 12345UL,
            });
            await Task.Delay(120);

            Assert.Equal(12345UL, runner.Machine.Config.RamSeed);
            Assert.NotNull(runner.Machine.EightyColumn);
        }
        finally { runner.Dispose(); }
    }
}

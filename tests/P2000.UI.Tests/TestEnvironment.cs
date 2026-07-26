using System.Runtime.CompilerServices;
using P2000.UI.State;
using Xunit;

// AppPreferencesFile.DirectoryOverride is a single shared static (project CLAUDE.md milestone
// 14c's own tests mutate it to get per-test isolation) — disable cross-class parallelization so
// no other test class's EmulationRunner()/ConfigWindowVm construction can race against it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace P2000.UI.Tests;

/// <summary>
/// Isolates the ENTIRE test run from the real per-user app-data folder (project CLAUDE.md
/// milestone 14c). Every <see cref="P2000.UI.Runner.EmulationRunner"/> construction now calls
/// through <see cref="AppPreferencesFile.Load"/> — without this, running the test suite on a
/// developer's own machine could read (or, worse, overwrite via <see cref="AppPreferencesFile.Save"/>)
/// their REAL <c>AppPreferences.json</c>/<c>last-session.cfg</c>. Runs once, before any test,
/// via <see cref="ModuleInitializerAttribute"/>.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    public static void Initialize()
    {
        AppPreferencesFile.DirectoryOverride =
            Path.Combine(Path.GetTempPath(), $"p2000ui-tests-{Guid.NewGuid():N}");
    }
}

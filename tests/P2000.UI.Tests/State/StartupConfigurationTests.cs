using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.State;
using P2000.UI.Runner;
using P2000.UI.State;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.State;

/// <summary>
/// Tests for the startup-configuration feature (project CLAUDE.md milestone 14c; reference doc
/// §3a "RESOLVED — startup configuration"): auto-remember on quit, fail-soft startup, pinning,
/// and the <see cref="ConfigWindowVm.SaveCfgAsync"/> regression guard for the §7 investigation's
/// confirmed gap.
///
/// <b>Isolation:</b> every test uses <see cref="PreferencesDirectoryScope"/> to redirect
/// <see cref="AppPreferencesFile"/> to a throwaway temp directory — see
/// <c>TestEnvironment.cs</c>'s own doc comment for why this matters (every
/// <see cref="EmulationRunner"/> construction calls through <see cref="AppPreferencesFile.Load"/>).
/// </summary>
public class StartupConfigurationTests
{
    /// <summary>Redirects <see cref="AppPreferencesFile.DirectoryOverride"/> to a fresh temp
    /// directory for the lifetime of one test, restoring whatever was there before (the shared
    /// module-initializer directory, not the real app-data folder) on dispose.</summary>
    private sealed class PreferencesDirectoryScope : IDisposable
    {
        private readonly string? _previous;
        public string Path { get; }

        public PreferencesDirectoryScope()
        {
            _previous = AppPreferencesFile.DirectoryOverride;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ms14c-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            AppPreferencesFile.DirectoryOverride = Path;
        }

        public void Dispose()
        {
            AppPreferencesFile.DirectoryOverride = _previous;
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string TempCasPath()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ms14c-cas-{Guid.NewGuid():N}.cas");
        File.WriteAllBytes(path, new byte[1280]);
        return path;
    }

    private static string TempDskPath()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ms14c-dsk-{Guid.NewGuid():N}.dsk");
        File.WriteAllBytes(path, DskImage.CreateBlank(40, 1).GetBytes());
        return path;
    }

    // ---- (b) fresh-install regression --------------------------------------------------------

    [Fact]
    public void FreshInstall_NoPreferencesFile_BootsBare()
    {
        using var scope = new PreferencesDirectoryScope(); // empty — no AppPreferences.json at all

        var runner = new EmulationRunner(); // never Start()ed — not disposed (Dispose requires a started thread)

        Assert.Equal(InternalBoard.None, runner.Machine.Config.Board);
        Assert.False(runner.Machine.Mdcr.HasTape);
    }

    // ---- (a) mount-then-quit-then-relaunch round-trip, both devices --------------------------

    [Fact]
    public void MountDiskAndCassette_QuitThenRelaunch_BothAreBackAutomatically()
    {
        using var scope = new PreferencesDirectoryScope();
        var diskPath = TempDskPath();
        var casPath = TempCasPath();
        try
        {
            var vm = new DisplayWindowVm();
            vm.Runner.Reconfigure(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0 } },
            });
            vm.Runner.Machine.Fdc!.MountDisk(0, new DskImage(diskPath));
            vm.Runner.Machine.Mdcr.InsertTape(File.ReadAllBytes(casPath), casPath);

            vm.Dispose(); // simulated clean app quit -> auto-remember writes last-session.cfg

            var relaunched = new EmulationRunner(); // never Start()ed — not disposed

            Assert.NotNull(relaunched.Machine.Fdc?.GetDisk(0));
            Assert.True(relaunched.Machine.Mdcr.HasTape);
        }
        finally
        {
            File.Delete(diskPath);
            File.Delete(casPath);
        }
    }

    // ---- (e) fail-soft on a corrupt/missing StartupCfgPath -----------------------------------

    [Fact]
    public void StartupCfgPath_PointsAtMissingFile_FallsThroughToBare_NoException()
    {
        using var scope = new PreferencesDirectoryScope();
        AppPreferencesFile.Save(new AppPreferences
        {
            StartupCfgPath = System.IO.Path.Combine(scope.Path, "does-not-exist.cfg"),
        });

        var runner = new EmulationRunner(); // must not throw; never Start()ed — not disposed

        Assert.Equal(InternalBoard.None, runner.Machine.Config.Board);
        Assert.False(runner.Machine.Mdcr.HasTape);
    }

    [Fact]
    public void StartupCfgPath_PointsAtCorruptFile_FallsThroughToBare_NoException()
    {
        using var scope = new PreferencesDirectoryScope();
        var corruptPath = System.IO.Path.Combine(scope.Path, "corrupt.cfg");
        File.WriteAllText(corruptPath, "{ not valid json ");
        AppPreferencesFile.Save(new AppPreferences { StartupCfgPath = corruptPath });

        var runner = new EmulationRunner(); // never Start()ed — not disposed

        Assert.Equal(InternalBoard.None, runner.Machine.Config.Board);
    }

    // ---- (c)/(d) pinning ----------------------------------------------------------------------

    [Fact]
    public void Pinned_ChangeLiveTopologyThenQuit_NextLaunchStillUsesThePinnedFile()
    {
        using var scope = new PreferencesDirectoryScope();
        var pinnedCasPath = TempCasPath();
        var laterDiskPath = TempDskPath();
        try
        {
            // A pinned .cfg exists, pointing at a cassette-only setup.
            var pinnedCfgPath = System.IO.Path.Combine(scope.Path, "pinned.cfg");
            MachineConfigFile.SaveToFile(new MachineConfig { CassettePath = pinnedCasPath }, pinnedCfgPath);
            AppPreferencesFile.Save(new AppPreferences
            {
                StartupCfgPath = pinnedCfgPath,
                StartupCfgIsPinned = true,
            });

            var vm = new DisplayWindowVm();
            // Change live topology to something entirely different from the pinned file.
            vm.Runner.Reconfigure(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0 } },
            });
            vm.Runner.Machine.Fdc!.MountDisk(0, new DskImage(laterDiskPath));

            vm.Dispose(); // pinned -> auto-remember must NOT overwrite the pin

            var prefsAfterQuit = AppPreferencesFile.Load();
            Assert.Equal(pinnedCfgPath, prefsAfterQuit.StartupCfgPath);
            Assert.True(prefsAfterQuit.StartupCfgIsPinned);

            var relaunched = new EmulationRunner(); // never Start()ed — not disposed

            // Still the pinned cassette-only setup, NOT the changed live floppy topology.
            Assert.True(relaunched.Machine.Mdcr.HasTape);
            Assert.Equal(InternalBoard.None, relaunched.Machine.Config.Board);
        }
        finally
        {
            File.Delete(pinnedCasPath);
            File.Delete(laterDiskPath);
        }
    }

    [Fact]
    public void Unpinning_ResumesAutoRememberOnNextQuit()
    {
        using var scope = new PreferencesDirectoryScope();
        var pinnedCasPath = TempCasPath();
        var liveCasPath = TempCasPath();
        try
        {
            var pinnedCfgPath = System.IO.Path.Combine(scope.Path, "pinned.cfg");
            MachineConfigFile.SaveToFile(new MachineConfig { CassettePath = pinnedCasPath }, pinnedCfgPath);
            AppPreferencesFile.Save(new AppPreferences
            {
                StartupCfgPath = pinnedCfgPath,
                StartupCfgIsPinned = true,
            });

            var vm = new DisplayWindowVm();
            var configVm = new ConfigWindowVm(vm.Runner);
            Assert.True(configVm.IsStartupPinned); // reflects the pinned state at construction

            configVm.UnpinStartupConfigCommand.Execute(null);
            Assert.False(configVm.IsStartupPinned);

            vm.Runner.Machine.Mdcr.InsertTape(File.ReadAllBytes(liveCasPath), liveCasPath);
            vm.Dispose(); // now unpinned -> auto-remember should overwrite last-session.cfg

            var prefsAfterQuit = AppPreferencesFile.Load();
            Assert.False(prefsAfterQuit.StartupCfgIsPinned);
            Assert.Equal(AppPreferencesFile.LastSessionCfgPath, prefsAfterQuit.StartupCfgPath);

            var relaunched = new EmulationRunner(); // never Start()ed — not disposed
            Assert.True(relaunched.Machine.Mdcr.HasTape); // the LIVE tape, not the pinned one
        }
        finally
        {
            File.Delete(pinnedCasPath);
            File.Delete(liveCasPath);
        }
    }

    // ---- Pin button ghosted right after Apply (owner report, 2026-07-27) ---------------------

    [Fact]
    public void PinAsStartupConfigCommand_IsAlwaysEnabled_EvenWithNoSavedCfgYet()
    {
        // A machine fresh off Apply (or a freshly-opened Config window) has no LastCfgPath yet —
        // the button must NOT be permanently ghosted in that state (owner report: this was
        // counter-intuitive with no explanation). PinAsStartupConfigAsync itself prompts for a
        // save in that case rather than being disabled.
        using var scope = new PreferencesDirectoryScope();
        var runner = new EmulationRunner(); // never Start()ed — not disposed
        var configVm = new ConfigWindowVm(runner);

        Assert.Null(configVm.LastCfgPath);
        Assert.True(configVm.PinAsStartupConfigCommand.CanExecute(null));
    }

    [Fact]
    public async Task PinAsStartupConfig_SavedCfgAlreadyMatchesLiveConfig_PinsDirectly_NoRePrompt()
    {
        using var scope = new PreferencesDirectoryScope();
        var runner = new EmulationRunner(); // never Start()ed — not disposed
        var configVm = new ConfigWindowVm(runner);

        // Simulates a prior successful Save .cfg (which sets LastCfgPath) without needing a real
        // StorageProvider dialog headlessly — the file's content must be exactly what
        // CaptureCurrentConfig() returns right now (byte-for-byte, including the machine's own
        // randomly-generated RamSeed), same as SaveCfgAsync itself would have written, or the
        // "already matches" check below would (correctly) see a mismatch.
        var cfgPath = System.IO.Path.Combine(scope.Path, "already-saved.cfg");
        MachineConfigFile.SaveToFile(runner.Machine.CaptureCurrentConfig(), cfgPath);
        configVm.LastCfgPath = cfgPath;

        await configVm.PinAsStartupConfigCommand.ExecuteAsync(null);

        Assert.True(configVm.IsStartupPinned);
        var prefs = AppPreferencesFile.Load();
        Assert.True(prefs.StartupCfgIsPinned);
        Assert.Equal(cfgPath, prefs.StartupCfgPath);
    }

    [Fact]
    public async Task PinAsStartupConfig_LiveConfigDivergedFromSavedFile_PromptsReSave_DoesNotPinStaleFile()
    {
        // Owner follow-up report, 2026-07-27: load a .cfg, tweak a field, Apply — LastCfgPath is
        // still set (pointing at the pre-tweak file), but the live machine has moved on. Pinning
        // must not silently pin the now-stale file.
        using var scope = new PreferencesDirectoryScope();
        var runner = new EmulationRunner(); // never Start()ed — not disposed
        var configVm = new ConfigWindowVm(runner);

        var staleCfgPath = System.IO.Path.Combine(scope.Path, "stale.cfg");
        // Deliberately mismatched from the live machine's actual config (different RamVariant),
        // simulating "tweaked and Applied since this file was last saved."
        MachineConfigFile.SaveToFile(new MachineConfig { RamVariant = RamVariant.T54 }, staleCfgPath);
        configVm.LastCfgPath = staleCfgPath;

        // No real StorageProvider in a headless test run, so the implicit re-save this should
        // trigger can't complete — which is itself the point: it must NOT fall through and pin
        // the stale file anyway.
        await configVm.PinAsStartupConfigCommand.ExecuteAsync(null);

        Assert.False(configVm.IsStartupPinned);
        var prefs = AppPreferencesFile.Load();
        Assert.False(prefs.StartupCfgIsPinned);
        Assert.Null(prefs.StartupCfgPath);
    }

    [AvaloniaFact]
    public async Task PinAsStartupConfig_LoadThenApplyWithNoEdits_DoesNotPromptForSave()
    {
        // Owner follow-up report, 2026-07-27: "When I load a config, apply, then unpin, pin it
        // also asks for a save, even though I did not modify the loaded config." Root cause:
        // ConfigWindowVm.BuildConfig() (used by Apply) never carried RamSeed/BankCount forward —
        // neither has a bound UI field — so EVERY Apply passed RamSeed=null to Reconfigure, which
        // then rolled a FRESH random seed even with zero edits, guaranteeing a mismatch against
        // whatever concrete seed the previously-saved file actually had.
        using var scope = new PreferencesDirectoryScope();
        var runner = new EmulationRunner();
        runner.Start();
        await Task.Delay(60);

        // Simulates "the machine is currently running a specific, previously-saved config" —
        // a concrete RamSeed, the way any real Save .cfg produces (never null).
        runner.Reconfigure(new MachineConfig { RamVariant = RamVariant.T54, RamSeed = 0xC0FFEE });
        await Task.Delay(60);

        var savedPath = System.IO.Path.Combine(scope.Path, "saved.cfg");
        MachineConfigFile.SaveToFile(runner.Machine.CaptureCurrentConfig(), savedPath);

        // Opening the Config window against this already-running machine (LoadFromCurrentConfig,
        // called from the constructor) is the headlessly-testable equivalent of "Load .cfg" —
        // both populate the same fields, including the RamSeed/BankCount pass-through this fix
        // adds. LastCfgPath is set directly here since the real Load .cfg dialog needs a
        // StorageProvider this test run doesn't have.
        var configVm = new ConfigWindowVm(runner) { LastCfgPath = savedPath };

        // Apply with NO field edits — must reproduce the exact same config, RamSeed included.
        configVm.ApplyCommand.Execute(null);
        await Task.Delay(60);

        await configVm.PinAsStartupConfigCommand.ExecuteAsync(null);

        Assert.True(configVm.IsStartupPinned); // no re-save prompt needed — the fix under test
        Assert.Equal(0xC0FFEEUL, runner.Machine.Config.RamSeed); // RamSeed survived Apply unchanged
    }

    // ---- (f) SaveCfgAsync regression guard -----------------------------------------------------

    [Fact]
    public void CaptureCurrentConfig_AfterLiveMount_ReflectsTheMountedPaths()
    {
        // ConfigWindowVm.SaveCfgAsync itself needs a real StorageProvider (not available
        // headless — same limitation this suite's other Save/Load-dialog tests already accept,
        // per MemoryWatchVmTests' own doc comment). What's directly testable and IS the actual
        // fix (project CLAUDE.md §18's confirmed investigation): SaveCfgAsync now serializes
        // Machine.CaptureCurrentConfig() instead of BuildConfig()'s always-null bound fields —
        // asserting CaptureCurrentConfig's own output here is the regression guard for exactly
        // that gap (BuildConfig() would have returned ImagePath/CassettePath as null here).
        using var scope = new PreferencesDirectoryScope();
        var diskPath = TempDskPath();
        var casPath = TempCasPath();
        try
        {
            using var runner = new EmulationRunner();
            runner.Start();
            runner.Reconfigure(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 0 } },
            });
            runner.Machine.Fdc!.MountDisk(0, new DskImage(diskPath));
            runner.Machine.Mdcr.InsertTape(File.ReadAllBytes(casPath), casPath);

            var captured = runner.Machine.CaptureCurrentConfig();

            Assert.Equal(diskPath, captured.FloppyDrives[0].ImagePath);
            Assert.Equal(casPath, captured.CassettePath);
        }
        finally
        {
            File.Delete(diskPath);
            File.Delete(casPath);
        }
    }
}

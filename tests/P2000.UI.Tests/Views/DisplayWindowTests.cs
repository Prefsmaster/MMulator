using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.Machine.State;
using P2000.UI.State;
using P2000.UI.ViewModels;
using P2000.UI.Views;

namespace P2000.UI.Tests.Views;

/// <summary>
/// Regression test for a real startup crash (found via owner bug report, 2026-07-27): showing the
/// main window used to crash the whole process whenever the startup-config auto-load carried an
/// UNRESOLVED disk geometry mismatch (project CLAUDE.md milestone 14g's proactive-surfacing
/// feature). Root cause: <c>DisplayWindow.OnDataContextChanged</c> used to call
/// <c>DiskDriveWindowVm.RaiseAnyPendingMismatches()</c> directly — but that method fires
/// <c>ShowGeometryMismatchDialog</c>'s <c>dialog.ShowDialog(this)</c>, and <c>OnDataContextChanged</c>
/// fires SYNCHRONOUSLY inside <c>App.axaml.cs</c>'s <c>new DisplayWindow { DataContext = vm }</c> —
/// before <c>desktop.MainWindow = win</c>, i.e. before this window is ever shown. Avalonia's
/// <c>ShowDialog</c> requires a VISIBLE owner and throws <c>InvalidOperationException</c>
/// otherwise; since the call is <c>async void</c>, that exception was unhandled and crashed the
/// process. Because "Continue mounting as-is" deliberately never clears the underlying mismatch,
/// this reproduced on EVERY subsequent launch — a permanent crash loop until the auto-saved
/// startup <c>.cfg</c> was deleted or hand-edited. Fixed by moving the raise to
/// <c>OnOpened</c> (fires only once the window is actually visible).
/// </summary>
[Trait("Category", "Integration")]
public class DisplayWindowTests
{
    /// <summary>Redirects <see cref="AppPreferencesFile.DirectoryOverride"/> to a fresh temp
    /// directory for the lifetime of one test — same isolation pattern as
    /// <c>StartupConfigurationTests</c>' own scope (kept private/duplicated here rather than
    /// shared, matching how that file already keeps its own copy self-contained).</summary>
    private sealed class PreferencesDirectoryScope : IDisposable
    {
        private readonly string? _previous;
        public string Path { get; }

        public PreferencesDirectoryScope()
        {
            _previous = AppPreferencesFile.DirectoryOverride;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ms-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            AppPreferencesFile.DirectoryOverride = Path;
        }

        public void Dispose()
        {
            AppPreferencesFile.DirectoryOverride = _previous;
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static int LengthFor(int tracks, int sides) =>
        tracks * sides * P2000.Machine.Devices.Fdc.DskImage.SectorsPerTrack *
        P2000.Machine.Devices.Fdc.DskImage.BytesPerSector;

    [AvaloniaFact]
    public void ShowingMainWindow_WithUnresolvedStartupMismatch_DoesNotThrow()
    {
        using var scope = new PreferencesDirectoryScope();
        var tempPath = System.IO.Path.Combine(scope.Path, "mismatched.dsk");
        try
        {
            // 35-track/SS length -> a real Candidate mismatch against the configured 40/Single
            // drive below — reproduces the reported scenario (a short/mismatched image left
            // "as-is" after the geometry-mismatch dialog, never expanded/reconfigured).
            File.WriteAllBytes(tempPath, new byte[LengthFor(35, 1)]);

            var cfgPath = System.IO.Path.Combine(scope.Path, "startup.cfg");
            MachineConfigFile.SaveToFile(new MachineConfig
            {
                Board = InternalBoard.FloppyRam,
                RamVariant = RamVariant.T102,
                FloppyDrives = new[]
                {
                    new FloppyDriveConfig { DriveIndex = 1, Capacity = 40, Sides = DiskSides.Single, ImagePath = tempPath },
                },
            }, cfgPath);
            AppPreferencesFile.Save(new AppPreferences { StartupCfgPath = cfgPath, StartupCfgIsPinned = true });

            // Mirrors App.axaml.cs exactly: construct the VM (auto-loads the startup .cfg, so the
            // mismatch already exists on Upd765 by the time this returns), THEN construct the
            // Window with DataContext set via object initializer (fires OnDataContextChanged
            // synchronously, before Show()) — the exact sequence that used to crash.
            using var vm = new DisplayWindowVm();
            var window = new DisplayWindow { DataContext = vm };

            var exception = Record.Exception(() => window.Show());

            Assert.Null(exception);

            // window.Show() (via OnOpened -> RaiseAnyPendingMismatches) leaves a real, un-closed
            // modal "Disk Geometry Mismatch" dialog owned by this window — close it explicitly
            // before closing the owner. An un-closed dialog left dangling in the visual tree is a
            // plausible source of the cross-test Avalonia-headless flakiness this project's other
            // findings-log entries have repeatedly observed (a LATER, unrelated test's dispatcher
            // reset can end up laying out/rendering this leftover control against a FontManager
            // that's no longer fully set up) — close every window a test creates, not just the one
            // it constructed directly.
            foreach (var owned in window.OwnedWindows.ToArray())
                owned.Close();
            window.Close();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>Regression test for an owner-reported bug (2026-07-31): mounting a mismatched
    /// disk image while BOTH the main window and the Disk Drives window are open raised the
    /// geometry-mismatch dialog TWICE — once via <c>DisplayWindow</c>'s own fallback subscription
    /// to <c>DiskDriveWindowVm.GeometryMismatchDetected</c> (project CLAUDE.md milestone 14g,
    /// meant only for when the Disk Drives satellite window is never opened) and once via
    /// <c>DiskDriveWindow</c>'s own subscription to the SAME shared event (milestone 14e) — since
    /// both windows subscribe unconditionally. This let the user answer the two independent
    /// dialog copies differently. Reproduced and fixed regardless of which window's own action
    /// triggered the mount (Config window's image-picking flow and a direct mount both funnel
    /// through the same <c>DiskDriveVm.MountBytes</c>, raising the identical shared event) —
    /// this test drives the mount directly against the live <c>DiskDriveVm</c>, the same shared
    /// trigger point either UI path ultimately reaches.</summary>
    [AvaloniaFact]
    public void MismatchWhileDiskDriveWindowOpen_RaisesOnlyOneDialog_NotTwo()
    {
        using var vm = new DisplayWindowVm();
        var window = new DisplayWindow { DataContext = vm };
        window.Show();

        // DisplayWindowVm's own constructor already starts Runner — don't call Start() again.
        vm.Runner.Reconfigure(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[] { new FloppyDriveConfig { DriveIndex = 1, Capacity = 40, Sides = DiskSides.Single } },
        });
        vm.DiskVm.RebuildIfMachineChanged();

        vm.OpenDiskDrivesCommand.Execute(null); // opens the Disk Drives satellite window too

        // The Disk Drives window itself is shown owned by the main window (Show(this)) —
        // find it so its OWN owned-window list can be checked separately from the main window's.
        var diskDriveWindow = window.OwnedWindows.OfType<DiskDriveWindow>().Single();

        // 35-track/SS length -> a real Candidate mismatch against the configured 40-track/SS drive.
        var drive = vm.DiskVm.Drives.Single(d => d.DriveIndex == 1);
        drive.MountBytes(new byte[LengthFor(35, 1)], "mismatched");

        // Each window's code-behind shows ITS OWN copy of the mismatch dialog via
        // dialog.ShowDialog(this) — an owned/child window of whichever window created it. Before
        // the fix, BOTH window.OwnedWindows and diskDriveWindow.OwnedWindows would gain a
        // "MMulator — Disk Geometry Mismatch" dialog (two independently-answerable popups); after
        // the fix, only diskDriveWindow's own dialog exists.
        var mainWindowDialogCount = window.OwnedWindows.Count(w => w is not DiskDriveWindow);
        var diskDriveWindowDialogCount = diskDriveWindow.OwnedWindows.Count;

        Assert.Equal(0, mainWindowDialogCount); // the fix: DisplayWindow must stand down
        Assert.Equal(1, diskDriveWindowDialogCount); // the Disk Drives window's own dialog still shows

        // Close every window this test created — the Disk Drives window's own un-closed dialog,
        // then the Disk Drives window itself, then the main window — not just the one closed
        // directly below. See the doc comment on ShowingMainWindow_WithUnresolvedStartupMismatch_
        // DoesNotThrow above for why a dangling dialog left in the visual tree matters here.
        foreach (var dialog in diskDriveWindow.OwnedWindows.ToArray())
            dialog.Close();
        diskDriveWindow.Close();
        window.Close();
    }
}

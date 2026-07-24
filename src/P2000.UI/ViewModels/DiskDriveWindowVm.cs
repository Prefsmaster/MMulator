using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using P2000.UI.Runner;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the Disk Drives window (project CLAUDE.md §14 milestone 14) — the
/// disk analogue of the cassette deck, generalized to N drives. **One window, DRIVE TABS, one
/// tab per configured drive (owner decision, 2026-07-23)** — supersedes the milestone's
/// original "N stacked status rows" draft. Owns one <see cref="DiskDriveVm"/> per drive
/// configured on the CURRENT machine (<c>Runner.Machine.Config.FloppyDrives</c>) — rebuilt
/// whenever a topology change (<c>Reconfigure</c>) swaps in a different machine, since drive
/// COUNT is topology and can genuinely change size, unlike the cassette's always-one-
/// MdcrDevice shape.</summary>
public sealed partial class DiskDriveWindowVm : ObservableObject
{
    private readonly EmulationRunner _runner;
    private P2000.Machine.Machine? _lastMachine;

    public ObservableCollection<DiskDriveVm> Drives { get; } = new();

    /// <summary>The currently-selected tab (two-way bound to the view's <c>TabControl</c>).
    /// Drives which drive a main-window/window-level <c>.dsk</c> drag-drop targets (project
    /// CLAUDE.md §14, 2026-07-23 "DRIVE TABS" decision: "a drop lands on whichever drive's tab
    /// is currently active/focused, exactly like dropping a file onto a specific document tab
    /// in an editor" — resolves the N-drive drag-drop target ambiguity milestone 14 originally
    /// left unbuilt).</summary>
    [ObservableProperty] private DiskDriveVm? _selectedDrive;

    /// <summary>False when the current machine has no floppy+RAM board fitted at all — the
    /// window shows an empty-state message instead of zero rows looking like an error.</summary>
    [ObservableProperty] private bool _hasFloppyBoard;

    /// <summary>Raised when a drive's save/mount error should be surfaced as a dialog.</summary>
    public event Action<string>? ShowMessageRequested;

    public DiskDriveWindowVm(EmulationRunner runner)
    {
        _runner = runner;
        runner.FrameReady += OnFrameReady;
        RebuildIfMachineChanged();
    }

    private void OnFrameReady(uint[] _, bool __, bool[] ___) => RebuildIfMachineChanged();

    private void RebuildIfMachineChanged()
    {
        if (ReferenceEquals(_runner.Machine, _lastMachine)) return;
        _lastMachine = _runner.Machine;

        foreach (var drive in Drives)
        {
            drive.ShowMessageRequested -= OnDriveMessage;
            drive.Detach();
        }
        Drives.Clear();
        SelectedDrive = null;

        var fdc = _runner.Machine.Fdc;
        HasFloppyBoard = fdc is not null;
        if (fdc is null) return;

        foreach (var driveConfig in _runner.Machine.Config.FloppyDrives)
        {
            if (!driveConfig.Enabled) continue;
            var vm = new DiskDriveVm(_runner, driveConfig.DriveIndex, driveConfig.Capacity, driveConfig.Sides);
            vm.ShowMessageRequested += OnDriveMessage;
            Drives.Add(vm);
        }
        SelectedDrive = Drives.Count > 0 ? Drives[0] : null;
    }

    private void OnDriveMessage(string message) => ShowMessageRequested?.Invoke(message);

    public void Detach()
    {
        _runner.FrameReady -= OnFrameReady;
        foreach (var drive in Drives)
        {
            drive.ShowMessageRequested -= OnDriveMessage;
            drive.Detach();
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using P2000.Machine.Devices.Fdc;
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

    /// <summary>Raised when a drive's eject/replace needs the unsaved-changes Discard/Cancel
    /// dialog (§14 milestone 14a) — relayed from whichever <see cref="DiskDriveVm"/> triggered
    /// it, same aggregation pattern as <see cref="ShowMessageRequested"/>.</summary>
    public event Func<string, Task<bool>>? ConfirmDiscardRequested;

    /// <summary>Raised when a drive's mount (live, or a `.cfg`-authored one surfaced for the
    /// first time) produces a real geometry mismatch (project CLAUDE.md milestone 14e) — relayed
    /// from whichever <see cref="DiskDriveVm"/> triggered it, carrying the drive alongside the
    /// mismatch since the view needs to call back into that SPECIFIC drive's
    /// <c>ReconfigureAndRemount</c>/<c>ContinueWithCurrentMount</c>/
    /// <c>ExtendMountedDiskToFullSize</c>/<c>CancelMount</c>.</summary>
    public event Action<DiskDriveVm, DiskGeometryMismatch>? GeometryMismatchDetected;

    /// <summary>Raised when a drive's "Save As" needs the format-choice prompt (project CLAUDE.md
    /// milestone 14f) — relayed from whichever <see cref="DiskDriveVm"/> triggered it, same
    /// aggregation pattern as the other relayed events. The view resolves the task with the
    /// chosen format, or <c>null</c> if the user cancels the format choice. No subscriber (e.g.
    /// the view hasn't attached yet) keeps the drive's CURRENT format, same "no subscriber,
    /// proceed" default <see cref="DiskDriveVm.SaveAsFormatRequested"/> itself falls back to.</summary>
    public event Func<DiskImageFormat, Task<DiskImageFormat?>>? SaveAsFormatRequested;

    public DiskDriveWindowVm(EmulationRunner runner)
    {
        _runner = runner;
        runner.FrameReady += OnFrameReady;
        RebuildIfMachineChanged();
    }

    private void OnFrameReady(uint[] _, bool __, bool[] ___, int ____) => RebuildIfMachineChanged();

    /// <summary>Rebuilds <see cref="Drives"/> against whatever machine <see cref="_runner"/>
    /// currently holds, but ONLY if it's actually a different instance from last time
    /// (<see cref="_lastMachine"/>) — a no-op on every other 50 Hz <see cref="OnFrameReady"/> tick.
    /// Made <c>internal</c> (project CLAUDE.md milestone 14g) so <see cref="ConfigWindowVm.Apply"/>
    /// can force this synchronously right after <c>EmulationRunner.Reconfigure</c> returns, instead
    /// of waiting for the next async <c>FrameReady</c> tick — <c>Reconfigure</c> already blocks
    /// until the swap lands, so by the time <c>Apply</c> resumes, <see cref="_runner"/>'s
    /// <c>Machine</c> genuinely IS a new reference and this rebuild is real, not skipped. Each
    /// freshly-constructed <see cref="DiskDriveVm"/>'s own <c>RaisePendingMismatchIfAny</c> call
    /// below (already-existing ms.14e behavior) is what actually surfaces a `.cfg`-authored
    /// mismatch as a dialog in this case — <see cref="RaiseAnyPendingMismatches"/> is a SEPARATE,
    /// additional mechanism for the one case this doesn't cover (see its own doc comment).</summary>
    internal void RebuildIfMachineChanged()
    {
        if (ReferenceEquals(_runner.Machine, _lastMachine)) return;
        _lastMachine = _runner.Machine;

        foreach (var drive in Drives)
        {
            drive.ShowMessageRequested -= OnDriveMessage;
            drive.ConfirmDiscardRequested -= OnDriveConfirmDiscard;
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
            vm.ConfirmDiscardRequested += OnDriveConfirmDiscard;
            vm.GeometryMismatchDetected += mismatch => GeometryMismatchDetected?.Invoke(vm, mismatch);
            vm.SaveAsFormatRequested += currentFormat =>
                SaveAsFormatRequested?.Invoke(currentFormat) ?? Task.FromResult<DiskImageFormat?>(currentFormat);
            Drives.Add(vm);
            // Subscribed above THEN raised — a .cfg-authored mismatch captured at vm's own
            // construction must not fire before anyone could possibly be listening (project
            // CLAUDE.md milestone 14e).
            vm.RaisePendingMismatchIfAny();
        }
        SelectedDrive = Drives.Count > 0 ? Drives[0] : null;
    }

    /// <summary>Walks the CURRENT <see cref="Drives"/> and freshly re-queries each one's
    /// <c>Upd765.GetMismatch()</c> directly — deliberately bypassing <see cref="DiskDriveVm.PendingMismatch"/>/
    /// <c>RaisePendingMismatchIfAny</c> — raising <see cref="GeometryMismatchDetected"/> for any
    /// real mismatch found (project CLAUDE.md milestone 14g's proactive-surfacing decision).
    ///
    /// <b>Why this exists as a SEPARATE mechanism from the pending-mismatch one:</b> a mismatch
    /// captured at a <see cref="DiskDriveVm"/>'s OWN construction is raised (or silently dropped,
    /// if raised with no subscriber attached yet) exactly once, at construction time — this is
    /// what happens for the STARTUP-config case: <see cref="DiskDriveWindowVm"/> itself is
    /// constructed (and rebuilds <see cref="Drives"/>, consuming every drive's
    /// <c>PendingMismatch</c>) as part of <c>DisplayWindowVm</c>'s OWN constructor, before
    /// <c>DisplayWindow</c>'s code-behind has had a chance to subscribe to
    /// <see cref="GeometryMismatchDetected"/> — so that first raise necessarily fires into a dead
    /// event with nobody listening, and <c>PendingMismatch</c> is already null by the time anyone
    /// could call this. <c>Upd765.GetMismatch()</c> itself is NOT a one-shot/consumed signal
    /// though (machine ms.20d: "the mismatch stays on record for the session") — so a fresh,
    /// direct re-query here is always valid regardless of what already happened (or didn't) via
    /// the pending-mismatch path. Call once, right after subscribing, from
    /// <c>DisplayWindow.OnDataContextChanged</c> — mirrors the same "subscribe THEN raise" ordering
    /// <see cref="RebuildIfMachineChanged"/> already uses per-drive.
    ///
    /// Does NOT call <see cref="RebuildIfMachineChanged"/> first (unlike the Apply path, which
    /// calls that directly instead of this method) — by the time this runs, <see cref="Drives"/>
    /// is already correct for the startup machine, and forcing another rebuild here would
    /// double-fire the SAME mismatch via <see cref="DiskDriveVm.RaisePendingMismatchIfAny"/>'s own
    /// (by-then-subscribed) side effect.</summary>
    public void RaiseAnyPendingMismatches()
    {
        var fdc = _runner.Machine.Fdc;
        if (fdc is null) return;

        foreach (var drive in Drives)
        {
            var mismatch = fdc.GetMismatch(drive.DriveIndex);
            if (mismatch is { Kind: not DiskGeometryMismatchKind.None } m)
                GeometryMismatchDetected?.Invoke(drive, m);
        }
    }

    private void OnDriveMessage(string message) => ShowMessageRequested?.Invoke(message);

    private Task<bool> OnDriveConfirmDiscard(string message) =>
        ConfirmDiscardRequested?.Invoke(message) ?? Task.FromResult(true);

    public void Detach()
    {
        _runner.FrameReady -= OnFrameReady;
        foreach (var drive in Drives)
        {
            drive.ShowMessageRequested -= OnDriveMessage;
            drive.ConfirmDiscardRequested -= OnDriveConfirmDiscard;
            drive.Detach();
        }
    }
}

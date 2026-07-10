using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine.Debug;
using P2000.UI.Runner;
using System.Collections.ObjectModel;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.ViewModels;

/// <summary>
/// Root ViewModel for the debugger satellite window (§10). Purely observer-side:
/// subscribes to <see cref="EmulationRunner.BreakHit"/> (snapshot on pause) and
/// <see cref="EmulationRunner.FrameReady"/> (live memory/VRAM when running).
/// Spawns <see cref="MemoryWatchVm"/> instances for independent memory watch windows.
/// </summary>
public sealed partial class DebuggerWindowVm : ObservableObject, IDisposable
{
    private readonly EmulationRunner _runner;

    // Last corruption snapshot from FrameReady (stable, at field boundary)
    private bool[] _lastCorruption = new bool[40 * 24];

    // Tracks exec breakpoint addresses; updated via SyncBreakpointsToMachine.
    private readonly HashSet<ushort> _execBpSet = new();

    /// <summary>Raised when the VRAM window should be opened (or brought to front).</summary>
    public event Action? OpenVramWindowRequested;

    /// <summary>Raised when a new memory watch should be opened in its own window.</summary>
    public event Action<MemoryWatchVm>? OpenMemoryWatchRequested;

    // ── Child VMs ───────────────────────────────────────────────────────────

    public RegisterFileVm  RegisterFile { get; } = new();
    public VramWindowVm    Vram         { get; } = new();
    public DisassemblyVm   Disassembly  { get; } = new();

    /// <summary>All open memory watch windows (observable so code-behind can react).</summary>
    public ObservableCollection<MemoryWatchVm> MemoryWatches { get; } = new();

    // ── State ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isPaused;
    [ObservableProperty] private string _statusText = "Running";

    // ────────────────────────────────────────────────────────────────────────

    public DebuggerWindowVm(EmulationRunner runner)
    {
        _runner = runner;
        runner.BreakHit   += OnBreakHit;
        runner.FrameReady += OnFrameReady;
    }

    // ── Runner subscriptions ────────────────────────────────────────────────

    // BreakHit fires on the emulation thread — marshal to UI.
    private void OnBreakHit(BreakEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPaused   = true;
            StatusText = e.Kind == BreakpointKind.Step ? "Paused" : $"Break @ {e.Address:X4}";

            // Machine is paused at an instruction boundary — TakeSnapshot() is safe.
            MachineCore m = _runner.Machine;
            var snap = m.TakeSnapshot();

            RegisterFile.Update(snap);
            Disassembly.Refresh(snap.PC, snap.ReadMemory);

            // VRAM: read from snapshot's memory view (live page table, but paused = stable).
            Vram.Update(snap.ReadMemory, m.Video.PanX, _lastCorruption);

            // Memory watches also update from the snapshot.
            foreach (var watch in MemoryWatches)
                UpdateWatch(watch, snap);
        });
    }

    // FrameReady fires on the UI thread (already posted by the runner).
    private void OnFrameReady(uint[] _, bool fieldWasOdd, bool[] corruption)
    {
        // Keep the corruption snapshot current for the paused view.
        _lastCorruption = corruption;

        // When running, update registers/VRAM/memory watches live.
        if (!IsPaused)
        {
            MachineCore m = _runner.Machine;
            RegisterFile.UpdateLive(m.Cpu.Reg, m.Video.FieldTState);

            // Live disassembly: re-decode only when PC changes.
            ushort pc = m.Cpu.Reg.PC;
            if (Disassembly.NeedsRefresh(pc))
                Disassembly.Refresh(pc, m.Memory.Read);

            Vram.Update(m.Memory.Read, m.Video.PanX, corruption);

            foreach (var watch in MemoryWatches)
                watch.Update(m.Memory.Read, FollowBase(watch, m));
        }
        else
        {
            // Refresh the VRAM corruption overlay even while paused
            // (shows corruption from the last completed field).
            Vram.Corruption = (bool[])corruption.Clone();
        }

        // Detect resume: if the machine ran a frame, it's no longer paused.
        if (IsPaused && !_runner.Machine.IsPaused)
        {
            IsPaused   = false;
            StatusText = "Running";
            RegisterFile.Clear();
        }
    }

    // ── Stepping commands (CanExecute = IsPaused) ───────────────────────────

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void StepInto()
    {
        _runner.Machine.Enqueue(new SingleStepCommand());
        _runner.Machine.Enqueue(new RunCommand());
    }

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void StepOver()
    {
        _runner.Machine.Enqueue(new StepOverCommand());
        _runner.Machine.Enqueue(new RunCommand());
    }

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void StepOut()
    {
        _runner.Machine.Enqueue(new StepOutCommand());
        _runner.Machine.Enqueue(new RunCommand());
    }

    [RelayCommand]
    private void RunPause()
    {
        if (IsPaused)
            _runner.Machine.Enqueue(new RunCommand());
        else
            _runner.Machine.Enqueue(new PauseCommand());
    }

    // Notify CanExecute on IsPaused change.
    partial void OnIsPausedChanged(bool value)
    {
        StepIntoCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
    }

    // ── Breakpoint commands ─────────────────────────────────────────────────

    /// <summary>Toggle an exec breakpoint at <paramref name="address"/>.</summary>
    public void ToggleExecBreakpoint(ushort address)
    {
        if (!_execBpSet.Remove(address))
            _execBpSet.Add(address);

        // Keep the disassembly dots in sync.
        Disassembly.BreakpointAddresses.Clear();
        foreach (var a in _execBpSet) Disassembly.BreakpointAddresses.Add(a);
        Disassembly.RefreshBreakpointDots();

        SyncBreakpointsToMachine();
    }

    private void SyncBreakpointsToMachine()
    {
        // Clear all then re-add. Safe because the queue drains atomically at one boundary.
        _runner.Machine.Enqueue(new ClearBreakpointsCommand());
        foreach (var a in _execBpSet)
            _runner.Machine.Enqueue(new AddExecBreakpointCommand(a));
    }

    // ── Satellite window commands ────────────────────────────────────────────

    [RelayCommand]
    private void OpenVramWindow() => OpenVramWindowRequested?.Invoke();

    [RelayCommand]
    private void AddMemoryWatch()
    {
        var watch = new MemoryWatchVm();
        MemoryWatches.Add(watch);
        OpenMemoryWatchRequested?.Invoke(watch);
    }

    [RelayCommand]
    private void RemoveMemoryWatch(MemoryWatchVm watch)
    {
        MemoryWatches.Remove(watch);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void UpdateWatch(MemoryWatchVm watch, MachineSnapshot snap)
    {
        ushort? overrideBase = watch.Follow switch
        {
            "HL" => snap.HL,
            "SP" => snap.SP,
            "BC" => snap.BC,
            "DE" => snap.DE,
            _    => (ushort?)null,
        };
        watch.Update(snap.ReadMemory, overrideBase);
    }

    private static ushort? FollowBase(MemoryWatchVm watch, MachineCore m)
    {
        // Best-effort live register read (not at instruction boundary; minor races are OK).
        try
        {
            return watch.Follow switch
            {
                "HL" => m.Cpu.Reg.HL,
                "SP" => m.Cpu.Reg.SP,
                "BC" => m.Cpu.Reg.BC,
                "DE" => m.Cpu.Reg.DE,
                _    => (ushort?)null,
            };
        }
        catch { return null; }
    }

    // ────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _runner.BreakHit   -= OnBreakHit;
        _runner.FrameReady -= OnFrameReady;
    }
}

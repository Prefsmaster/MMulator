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

    /// <summary>Raised when a new memory watch should be opened in its own window.</summary>
    public event Action<MemoryWatchVm>? OpenMemoryWatchRequested;

    // ── Child VMs ───────────────────────────────────────────────────────────

    public RegisterFileVm  RegisterFile { get; } = new();
    public VramWindowVm    Vram         { get; } = new();

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
            StatusText = "Paused";

            // Machine is paused at an instruction boundary — TakeSnapshot() is safe.
            MachineCore m = _runner.Machine;
            var snap = m.TakeSnapshot();

            RegisterFile.Update(snap);

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

        // When running, update VRAM and memory watches live.
        if (!IsPaused)
        {
            MachineCore m = _runner.Machine;
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

    // ── Commands ────────────────────────────────────────────────────────────

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

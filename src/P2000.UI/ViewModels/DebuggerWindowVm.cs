using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine.Debug;
using P2000.Machine.Memory;
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

    // Viewport width _lastCorruption is indexed by: 40 normally, 80 in 80-column mode.
    private int _lastCorruptionWidth = 40;

    // Tracks exec breakpoint addresses and their optional bank qualifier (project CLAUDE.md §14
    // milestone 17; machine ms.24) — null = unqualified (fires regardless of active bank, the
    // only shape that existed before this milestone). Updated via SyncBreakpointsToMachine.
    private readonly Dictionary<ushort, int?> _execBps = new();

    /// <summary>"Any" (default) — used for the "Bank" qualifier picker shown alongside the
    /// disassembly gutter (project CLAUDE.md §14 milestone 17). Populated with "Bank N" for
    /// each populated bank, mirroring <see cref="MemoryWatchVm.BankOptions"/>'s shape.</summary>
    public const string AnyBankOption = "Any";

    public ObservableCollection<string> BreakpointBankOptions { get; } = new() { AnyBankOption };

    [ObservableProperty] private string _selectedBreakpointBankOption = AnyBankOption;

    /// <summary>True only when the installed card has at least one bank — gates whether the
    /// breakpoint bank-filter picker is shown at all (project CLAUDE.md §14 milestone 17: "outside
    /// the banked region, don't offer the qualifier at all" generalizes to "no banking at all,
    /// don't offer it anywhere").</summary>
    [ObservableProperty] private bool _showBreakpointBankFilter;

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
            RefreshBankFilterOptions(snap.BankCount);
            Disassembly.Refresh(snap.PC, snap.ReadMemory);

            // VRAM: read from snapshot's memory view (live page table, but paused = stable).
            Vram.Update(snap.ReadMemory, m.Video.PanX, _lastCorruption, _lastCorruptionWidth);

            // Memory watches also update from the snapshot.
            foreach (var watch in MemoryWatches)
                UpdateWatch(watch, snap);
        });
    }

    // FrameReady fires on the UI thread (already posted by the runner).
    private void OnFrameReady(uint[] _, bool fieldWasOdd, bool[] corruption, int corruptionWidth)
    {
        // Keep the corruption snapshot current for the paused view.
        _lastCorruption = corruption;
        _lastCorruptionWidth = corruptionWidth;

        // When running, update registers/VRAM/memory watches live.
        if (!IsPaused)
        {
            MachineCore m = _runner.Machine;
            var bankCount = m.Memory.BankCount;
            RegisterFile.UpdateLive(m.Cpu.Reg, m.Video.FieldTState,
                bankCount, bankCount > 0 ? (int?)m.Memory.CurrentBank : null);
            RefreshBankFilterOptions(bankCount);

            // Live disassembly: re-decode only when PC changes.
            ushort pc = m.Cpu.Reg.PC;
            if (Disassembly.NeedsRefresh(pc))
                Disassembly.Refresh(pc, m.Memory.Read);

            Vram.Update(m.Memory.Read, m.Video.PanX, corruption, corruptionWidth);

            foreach (var watch in MemoryWatches)
                watch.Update(m.Memory.Read, FollowBase(watch, m));
        }
        else
        {
            // Refresh the VRAM corruption overlay even while paused
            // (shows corruption from the last completed field).
            Vram.ViewportWidth = corruptionWidth;
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

    /// <summary>Toggle an exec breakpoint at <paramref name="address"/>. When
    /// <paramref name="address"/> falls in the banked window (0xE000-0xFFFF) and the currently
    /// selected breakpoint bank filter (<see cref="SelectedBreakpointBankOption"/>) is a specific
    /// bank, not "Any", the NEW breakpoint is qualified to that bank (project CLAUDE.md §14
    /// milestone 17; machine ms.24) — removing an already-armed breakpoint ignores the current
    /// filter and just removes whatever qualifier it already had.</summary>
    public void ToggleExecBreakpoint(ushort address)
    {
        if (!_execBps.Remove(address))
        {
            int? bank = address is >= PageTable.BankedWindowStart and <= PageTable.BankedWindowEnd
                ? ParseBankOption(SelectedBreakpointBankOption)
                : null;
            _execBps[address] = bank;
        }

        // Keep the disassembly dots in sync.
        Disassembly.BreakpointAddresses.Clear();
        foreach (var a in _execBps.Keys) Disassembly.BreakpointAddresses.Add(a);
        Disassembly.RefreshBreakpointDots();

        SyncBreakpointsToMachine();
    }

    private void SyncBreakpointsToMachine()
    {
        // Clear all then re-add. Safe because the queue drains atomically at one boundary.
        _runner.Machine.Enqueue(new ClearBreakpointsCommand());
        foreach (var (address, bank) in _execBps)
            _runner.Machine.Enqueue(new AddExecBreakpointCommand(address, bank));
    }

    private static int? ParseBankOption(string option) =>
        option == AnyBankOption ? null : int.Parse(option.AsSpan("Bank ".Length));

    /// <summary>Rebuilds <see cref="BreakpointBankOptions"/>/<see cref="ShowBreakpointBankFilter"/>
    /// from the live machine's bank count (project CLAUDE.md §14 milestone 17) — called every
    /// observer tick (alongside the register-file refresh) so it stays correct across a live
    /// topology change, not just at debugger-open.</summary>
    private void RefreshBankFilterOptions(int bankCount)
    {
        ShowBreakpointBankFilter = bankCount > 0;

        if (BreakpointBankOptions.Count != bankCount + 1)
        {
            BreakpointBankOptions.Clear();
            BreakpointBankOptions.Add(AnyBankOption);
            for (var i = 0; i < bankCount; i++) BreakpointBankOptions.Add($"Bank {i}");
        }

        if (!BreakpointBankOptions.Contains(SelectedBreakpointBankOption))
            SelectedBreakpointBankOption = AnyBankOption;
    }

    // ── Satellite window commands ────────────────────────────────────────────

    [RelayCommand]
    private void OpenVramWindow() => OpenVramWindowRequested?.Invoke();

    [RelayCommand]
    private void AddMemoryWatch()
    {
        var watch = new MemoryWatchVm(_runner);
        MemoryWatches.Add(watch);
        OpenMemoryWatchRequested?.Invoke(watch);
    }

    /// <summary>Creates and opens a memory watch pre-configured to a saved range/follow-register
    /// — the <c>.uistate</c> sidecar's restore path (project CLAUDE.md milestone 14b), reusing
    /// the same open-window event <see cref="AddMemoryWatch"/> uses so the view's window-tracking
    /// stays the single code path for both.</summary>
    public MemoryWatchVm RestoreMemoryWatch(ushort baseAddress, int length, string follow)
    {
        var watch = new MemoryWatchVm(_runner) { Follow = follow };
        watch.SetRange(baseAddress, length);
        MemoryWatches.Add(watch);
        OpenMemoryWatchRequested?.Invoke(watch);
        return watch;
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

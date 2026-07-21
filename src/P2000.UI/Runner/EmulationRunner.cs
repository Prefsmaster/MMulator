using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using P2000.Machine;
using P2000.Machine.Debug;
using P2000.Machine.Contention;
using P2000.Machine.Devices;
using P2000.UI.Audio;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.Runner;

/// <summary>
/// Owns a <see cref="MachineCore"/>, advances it on a dedicated background thread, and
/// fires <see cref="FrameReady"/> on the Avalonia UI thread at each 50 Hz field boundary
/// (project CLAUDE.md §3.2a: "UI-owned run-loop host, promotable").
///
/// Threading: emulation runs entirely on the emulation thread. Public reads of
/// <see cref="SpeedPercent"/> and <see cref="Turbo"/> are safe from any thread.
/// <see cref="Reconfigure"/> may be called from the UI thread; it blocks until the swap
/// is acknowledged at the next field boundary (~20 ms max).
/// </summary>
public sealed class EmulationRunner : IDisposable
{
    private static readonly long TicksPerField = Stopwatch.Frequency / 50;  // 20 ms at any clock

    private MachineCore _machine;
    public MachineCore Machine => _machine;

    /// <summary>OpenAL beeper sink. Mute and volume are controlled here.</summary>
    public AudioEngine Audio { get; } = new();

    // Pending machine built by Reconfigure(); swapped in on the emulation thread at the
    // next field boundary (race-free: set before the swap-done semaphore is awaited).
    private volatile MachineCore? _nextMachine;
    private readonly SemaphoreSlim _swapDone = new(0, 1);

    // Pending save-state request: stream set by UI thread, cleared + semaphore released on emulation thread.
    private volatile Stream? _pendingSaveStream;
    private readonly SemaphoreSlim _saveDone = new(0, 1);
    private Exception? _saveException;

    /// <summary>Forwarded from the current machine's <see cref="MachineCore.BreakHit"/>.
    /// Subscribers always see breaks from whichever machine is currently active, even after
    /// a <see cref="Reconfigure"/> swap.</summary>
    public event Action<BreakEvent>? BreakHit;

    /// <summary>Fired on the UI thread at each 50 Hz field boundary.
    /// <c>pixels</c> is a stable BGRA copy of the machine's full-field buffer (928×626,
    /// project CLAUDE.md §17 2026-07-22 full-field change) owned by the runner.
    /// <c>fieldWasOdd</c> is true when the odd field just completed (useful for Progressive /
    /// EvenOnly / OddOnly display modes). <c>corruption</c> is a stable 40×24 snapshot of the
    /// machine's CorruptionOverlay for that field.
    /// All arrays are safe to read until the next <see cref="FrameReady"/> fires.</summary>
    public event Action<uint[], bool, bool[]>? FrameReady;

    /// <summary>Skip the 50 Hz sleep and run as fast as the CPU allows.</summary>
    public volatile bool Turbo;

    /// <summary>Actual render speed as a percentage of the 50 Hz target (0–9999).
    /// Written by the emulation thread; atomically readable from any thread (int = 32-bit).</summary>
    public volatile int SpeedPercent;

    private readonly Thread _thread;
    private volatile bool _running;

    // Key events queued from the UI thread, drained on the emulation thread at each field boundary.
    private readonly ConcurrentQueue<(int Row, int Col, bool Pressed)> _inputQueue = new();

    // Double-buffered presentation frames + corruption snapshots.
    private readonly uint[][] _frameBufs =
    {
        new uint[Video.Width * Video.Height],
        new uint[Video.Width * Video.Height]
    };
    private readonly bool[][] _corruptionBufs =
    {
        new bool[VideoFetchUnit.Columns * Video.CharRows],
        new bool[VideoFetchUnit.Columns * Video.CharRows]
    };
    private int _writeBufIdx;

    // Speed measurement: rolling 1-second window.
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastFieldTicks;
    private long _speedWindowStartTicks;
    private int _speedFieldCount;

    public EmulationRunner()
    {
        _machine = new MachineCore(MakeConfig());
        _machine.Video.FieldComplete += OnFieldComplete;
        _machine.BreakHit += OnBreakHit;
        _machine.Sound.SamplesReady += Audio.EnqueueSamples;
        _thread = new Thread(Run) { IsBackground = true, Name = "Emulation" };
    }

    /// <summary>Rebuilds the machine with <paramref name="config"/> and cold-resets into it.
    /// Blocks the caller (~20 ms max) until the emulation thread acknowledges the swap at the
    /// next field boundary. Safe to call from the UI thread. The cassette is not preserved —
    /// the new machine starts with an empty deck (topology change = reset-to-apply). A real
    /// topology change is a real cold start, so a config with no explicit
    /// <see cref="MachineConfig.RamSeed"/> gets a fresh random one here (project CLAUDE.md §17)
    /// — pass a config with <c>RamSeed</c> already set (e.g. loaded from a <c>.cfg</c> file
    /// that intentionally pins one) to keep that value instead.</summary>
    public void Reconfigure(MachineConfig config)
    {
        if (config.RamSeed is null)
        {
            // MachineConfig is a plain class with init-only properties (not a record), so
            // there's no `with` expression — reconstruct explicitly, copying every other axis.
            config = new MachineConfig
            {
                Model = config.Model,
                Board = config.Board,
                RamVariant = config.RamVariant,
                BankCount = config.BankCount,
                MonitorRomPath = config.MonitorRomPath,
                Slot1CartridgePath = config.Slot1CartridgePath,
                FloppyDiskImagePath = config.FloppyDiskImagePath,
                RamSeed = NewRandomRamSeed(),
            };
        }
        var next = new MachineCore(config);
        next.Video.FieldComplete += OnFieldComplete;
        next.BreakHit += OnBreakHit;
        next.Sound.SamplesReady += Audio.EnqueueSamples;
        _nextMachine = next;   // volatile write — emulation thread picks this up at next field boundary
        _swapDone.Wait(500);   // wait for acknowledgement (should arrive within ~20 ms)
    }

    /// <summary>Swaps in a pre-built machine (e.g. loaded from a .state file) at the next
    /// field boundary on the emulation thread. Same thread-safety contract as
    /// <see cref="Reconfigure(MachineConfig)"/>. The machine's existing cassette/runtime
    /// state is preserved exactly as loaded.</summary>
    public void ReconfigureWithMachine(MachineCore machine)
    {
        machine.Video.FieldComplete += OnFieldComplete;
        machine.BreakHit += OnBreakHit;
        machine.Sound.SamplesReady += Audio.EnqueueSamples;
        _nextMachine = machine;    // volatile write
        _swapDone.Wait(500);
    }

    /// <summary>Saves the current machine state to <paramref name="stream"/> at the next
    /// field boundary on the emulation thread (which guarantees an instruction boundary).
    /// Blocks the caller until the save completes (~20 ms max). Safe to call from the UI thread.
    /// Re-throws any exception raised by the serializer.</summary>
    public void SaveStateToStream(Stream stream)
    {
        _saveException = null;
        _pendingSaveStream = stream;   // volatile write — emulation thread picks this up
        _saveDone.Wait(500);
        if (_saveException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_saveException).Throw();
    }

    // Locate BASIC.bin relative to the executable (assets/ in the repo root).
    private static MachineConfig MakeConfig()
    {
        var ramSeed = NewRandomRamSeed();

        // Trim trailing separator so GetDirectoryName reliably walks up one level per call.
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "assets", "BASIC.bin");
            if (File.Exists(candidate))
                return new MachineConfig { Slot1CartridgePath = candidate, RamSeed = ramSeed };
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        // No BASIC found → bare machine (cassette-wait screen).
        return new MachineConfig { RamSeed = ramSeed };
    }

    /// <summary>Generates a genuinely random 64-bit RAM-fill seed (project CLAUDE.md §17,
    /// 2026-07-21/22 finding). Lives here, outside `P2000.Machine`, deliberately — the core
    /// itself never calls a nondeterministic API (locked decision §2.2); only the UI, which
    /// is not under that constraint, does. A machine built with no seed at all (any test/CI
    /// caller) stays fully deterministic via
    /// <see cref="P2000.Machine.Memory.PageTable.DefaultRamSeed"/>.</summary>
    private static ulong NewRandomRamSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        Random.Shared.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    public void Start()
    {
        _running = true;
        _lastFieldTicks = _speedWindowStartTicks = _sw.ElapsedTicks;
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
        _machine.Video.FieldComplete -= OnFieldComplete;
        _machine.BreakHit -= OnBreakHit;
        _machine.Sound.SamplesReady -= Audio.EnqueueSamples;
        Audio.Dispose();
    }

    /// <summary>Enqueues a key press or release from the UI thread. Applied to the machine's
    /// keyboard matrix at the next field boundary on the emulation thread.</summary>
    public void EnqueueKey(int row, int col, bool pressed)
        => _inputQueue.Enqueue((row, col, pressed));

    /// <summary>Returns a snapshot copy of the most recent rendered frame (BGRA, the machine's
    /// full 928×626 field buffer — project CLAUDE.md §17 2026-07-22 full-field change).
    /// Allocates a new array; safe to call from any thread.</summary>
    public uint[] GetCurrentFrame()
    {
        // The "read" buffer is the one NOT currently being written to.
        var readBuf = _frameBufs[_writeBufIdx ^ 1];
        var copy = new uint[readBuf.Length];
        Array.Copy(readBuf, copy, copy.Length);
        return copy;
    }

    private void Run()
    {
        while (_running)
            _machine.Tick();
    }

    private void OnBreakHit(BreakEvent e) => BreakHit?.Invoke(e);

    // Called on the emulation thread (synchronously inside Machine.Tick()).
    private void OnFieldComplete()
    {
        // ── Save-state request — field boundary is a guaranteed instruction boundary ──
        var saveStream = _pendingSaveStream;
        if (saveStream is not null)
        {
            _pendingSaveStream = null;
            try { P2000.Machine.State.MachineStateFile.Save(_machine, saveStream); }
            catch (Exception ex) { _saveException = ex; }
            _saveDone.Release();
        }

        // ── Machine swap (Reconfigure / ReconfigureWithMachine) ───────────────
        var next = _nextMachine;
        if (next != null)
        {
            _nextMachine = null;
            _machine.Video.FieldComplete -= OnFieldComplete;
            _machine.BreakHit -= OnBreakHit;
            _machine.Sound.SamplesReady -= Audio.EnqueueSamples;
            _machine = next;
            _swapDone.Release();
        }

        // ── Apply queued key events (field boundary — safe point per machine CLAUDE.md §7) ──
        while (_inputQueue.TryDequeue(out var ev))
            _machine.Keyboard.SetKey(ev.Row, ev.Col, ev.Pressed);

        // ── Copy framebuffer + corruption overlay ─────────────────────────────
        // IsOddField has already toggled to the NEXT field's parity at this point,
        // so the field that just completed = !IsOddField.
        bool fieldWasOdd = !_machine.Video.IsOddField;
        var buf          = _frameBufs[_writeBufIdx];
        var corruption   = _corruptionBufs[_writeBufIdx];
        Array.Copy(_machine.Video.Framebuffer,       buf,        buf.Length);
        Array.Copy(_machine.Video.CorruptionOverlay, corruption, corruption.Length);
        _writeBufIdx ^= 1;

        Dispatcher.UIThread.Post(
            () => FrameReady?.Invoke(buf, fieldWasOdd, corruption),
            DispatcherPriority.Render);

        // ── Speed measurement ─────────────────────────────────────────────────
        long now = _sw.ElapsedTicks;
        _speedFieldCount++;
        long windowElapsed = now - _speedWindowStartTicks;
        if (windowElapsed >= Stopwatch.Frequency)   // update once per second
        {
            SpeedPercent = (int)(_speedFieldCount * 100L * Stopwatch.Frequency / (windowElapsed * 50));
            _speedFieldCount = 0;
            _speedWindowStartTicks = now;
        }

        // ── 50 Hz pacing (skipped in turbo) ──────────────────────────────────
        if (!Turbo)
        {
            long targetTicks = _lastFieldTicks + TicksPerField;

            if (now < targetTicks)
            {
                long sleepMs = (targetTicks - now) * 1000 / Stopwatch.Frequency - 1;
                if (sleepMs > 0) Thread.Sleep((int)sleepMs);
                while (_sw.ElapsedTicks < targetTicks) Thread.SpinWait(20);
                _lastFieldTicks = targetTicks;
            }
            else if (now > targetTicks + TicksPerField)
            {
                _lastFieldTicks = now;  // too far behind — reset to avoid spiral
            }
            else
            {
                _lastFieldTicks = targetTicks;
            }
        }
        else
        {
            _lastFieldTicks = now;  // turbo: deadline is always "now"
        }
    }
}

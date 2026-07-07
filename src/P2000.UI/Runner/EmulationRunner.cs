using System.Diagnostics;
using Avalonia.Threading;
using P2000.Machine.Devices;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.Runner;

/// <summary>
/// Owns a <see cref="MachineCore"/>, advances it on a dedicated background thread, and
/// fires <see cref="FrameReady"/> on the Avalonia UI thread at each 50 Hz field boundary
/// (project CLAUDE.md §3.2a: "UI-owned run-loop host, promotable").
///
/// Threading: emulation runs entirely on the emulation thread. Public reads of
/// <see cref="SpeedPercent"/> and <see cref="Turbo"/> are safe from any thread.
/// </summary>
public sealed class EmulationRunner : IDisposable
{
    private static readonly long TicksPerField = Stopwatch.Frequency / 50;  // 20 ms at any clock

    public MachineCore Machine { get; }

    /// <summary>Fired on the UI thread at each 50 Hz field boundary.
    /// The <c>uint[]</c> is a stable BGRA copy (640×480) owned by the runner;
    /// the UI may read it freely until the next <see cref="FrameReady"/> fires.</summary>
    public event Action<uint[]>? FrameReady;

    /// <summary>Skip the 50 Hz sleep and run as fast as the CPU allows.</summary>
    public volatile bool Turbo;

    /// <summary>Actual render speed as a percentage of the 50 Hz target (0–9999).
    /// Written by the emulation thread; atomically readable from any thread (int = 32-bit).</summary>
    public volatile int SpeedPercent;

    private readonly Thread _thread;
    private volatile bool _running;

    // Double-buffered presentation frames.
    private readonly uint[][] _frameBufs =
    {
        new uint[Video.Width * Video.Height],
        new uint[Video.Width * Video.Height]
    };
    private int _writeBufIdx;

    // Speed measurement: rolling 1-second window.
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastFieldTicks;
    private long _speedWindowStartTicks;
    private int _speedFieldCount;

    public EmulationRunner()
    {
        Machine = new MachineCore();
        Machine.Video.FieldComplete += OnFieldComplete;
        _thread = new Thread(Run) { IsBackground = true, Name = "Emulation" };
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
        Machine.Video.FieldComplete -= OnFieldComplete;
    }

    /// <summary>Returns a snapshot copy of the most recent rendered frame (BGRA 640×480).
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
            Machine.Tick();
    }

    // Called on the emulation thread (synchronously inside Machine.Tick()).
    private void OnFieldComplete()
    {
        // ── Copy framebuffer ──────────────────────────────────────────────────
        var buf = _frameBufs[_writeBufIdx];
        Array.Copy(Machine.Video.Framebuffer, buf, buf.Length);
        _writeBufIdx ^= 1;

        Dispatcher.UIThread.Post(() => FrameReady?.Invoke(buf), DispatcherPriority.Render);

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

using System.Diagnostics;
using Avalonia.Threading;
using P2000.Machine.Devices;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.Runner;

/// <summary>
/// Owns a <see cref="Machine"/>, advances it on a dedicated background thread, and
/// fires <see cref="FrameReady"/> on the Avalonia UI thread at each 50 Hz field boundary
/// (project CLAUDE.md §3.2a: "UI-owned run-loop host, promotable").
///
/// Threading contract: emulation runs entirely on the emulation thread. All public
/// mutations (Turbo, Dispose) are safe to call from any thread.
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

    private readonly Thread _thread;
    private volatile bool _running;

    // Double-buffered presentation: emulation writes to _frameBufs[_writeBufIdx],
    // the previous buffer is handed to the UI. Avoids a per-field heap allocation.
    private readonly uint[][] _frameBufs =
    {
        new uint[Video.Width * Video.Height],
        new uint[Video.Width * Video.Height]
    };
    private int _writeBufIdx;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastFieldTicks;

    public EmulationRunner()
    {
        Machine = new MachineCore();
        Machine.Video.FieldComplete += OnFieldComplete;
        _thread = new Thread(Run) { IsBackground = true, Name = "Emulation" };
    }

    /// <summary>Starts the emulation thread. Call once after subscribing to
    /// <see cref="FrameReady"/>.</summary>
    public void Start()
    {
        _running = true;
        _lastFieldTicks = _sw.ElapsedTicks;
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
        Machine.Video.FieldComplete -= OnFieldComplete;
    }

    private void Run()
    {
        while (_running)
            Machine.Tick();
    }

    // Called on the emulation thread (synchronously inside Machine.Tick()).
    private void OnFieldComplete()
    {
        // Copy the framebuffer into the write buffer.
        var buf = _frameBufs[_writeBufIdx];
        Array.Copy(Machine.Video.Framebuffer, buf, buf.Length);
        _writeBufIdx ^= 1;  // alternate so the UI always has the previous buffer stable

        // Hand the copy to the UI thread.
        Dispatcher.UIThread.Post(() => FrameReady?.Invoke(buf), DispatcherPriority.Render);

        // Pace to 50 Hz unless turbo.
        if (!Turbo)
        {
            long targetTicks = _lastFieldTicks + TicksPerField;
            long now = _sw.ElapsedTicks;

            if (now < targetTicks)
            {
                // Sleep for most of the wait, then busy-spin the last ~1 ms for accuracy.
                long sleepMs = (targetTicks - now) * 1000 / Stopwatch.Frequency - 1;
                if (sleepMs > 0) Thread.Sleep((int)sleepMs);
                while (_sw.ElapsedTicks < targetTicks) Thread.SpinWait(20);
                _lastFieldTicks = targetTicks;
            }
            else if (now > targetTicks + TicksPerField)
            {
                // Too far behind; reset deadline to now to avoid a catch-up spiral.
                _lastFieldTicks = now;
            }
            else
            {
                _lastFieldTicks = targetTicks;
            }
        }
    }
}

using System.Collections.Concurrent;
using P2000.Machine.Devices;
using Silk.NET.OpenAL;

namespace P2000.UI.Audio;

/// <summary>
/// OpenAL streaming sink for the 1-bit beeper (project CLAUDE.md §9).
/// Accepts 882-sample PCM blocks from the emulation thread via <see cref="EnqueueSamples"/>
/// and streams them through an OpenAL source on a dedicated background thread.
/// Silently disabled if OpenAL is not available on the host.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int BufferCount   = 4;
    private const int SampleRate    = SoundDevice.SampleRate;
    private const int SamplesPerBuf = SoundDevice.SamplesPerField;

    private readonly ConcurrentQueue<short[]> _queue = new();

    private volatile bool  _running = true;
    private volatile float _volume  = 0.5f;
    private volatile bool  _mute;
    private volatile bool  _gainDirty;

    private readonly Thread _thread;

    // ── Mute / volume ─────────────────────────────────────────────────────────

    /// <summary>Mute the beeper output. Volume is preserved for when un-muted.</summary>
    public bool Mute
    {
        get => _mute;
        set { _mute = value; _gainDirty = true; }
    }

    /// <summary>Beeper volume in the range 0–1 (default 0.5).</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); _gainDirty = true; }
    }

    // ── Construction / disposal ───────────────────────────────────────────────

    public AudioEngine()
    {
        _thread = new Thread(AudioThreadMain) { IsBackground = true, Name = "Audio" };
        _thread.Start();
    }

    /// <summary>Enqueues a copy of <paramref name="samples"/> for playback.
    /// Called from the emulation thread at 50 Hz; returns immediately.</summary>
    public void EnqueueSamples(short[] samples)
    {
        // Copy so the SoundDevice can reuse its internal buffer immediately.
        var copy = new short[samples.Length];
        Array.Copy(samples, copy, samples.Length);
        _queue.Enqueue(copy);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }

    // ── Background audio thread ───────────────────────────────────────────────

    private unsafe void AudioThreadMain()
    {
        AL?        al      = null;
        ALContext? alc     = null;
        Device*    device  = null;
        Context*   context = null;
        uint       source  = 0;
        var        buffers = new uint[BufferCount];
        var        silence = new short[SamplesPerBuf];

        try
        {
            alc = ALContext.GetApi();
            al  = AL.GetApi();

            device = alc.OpenDevice(string.Empty);
            if (device == null) return;

            context = alc.CreateContext(device, null);
            if (context == null) return;

            if (!alc.MakeContextCurrent(context)) return;

            // Generate source (stack-local, safe to address-of in unsafe without fixed)
            al.GenSources(1, &source);

            // Generate buffers (managed array — needs fixed)
            fixed (uint* pBuf = buffers)
                al.GenBuffers(BufferCount, pBuf);

            // Pre-fill each buffer with silence and queue it
            fixed (short* pSilence = silence)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    al.BufferData(buffers[i], BufferFormat.Mono16, pSilence,
                                  SamplesPerBuf * sizeof(short), SampleRate);
                    // Copy the ID to a stack local so we can address it without fixed
                    uint bid = buffers[i];
                    al.SourceQueueBuffers(source, 1, &bid);
                }
            }

            al.SetSourceProperty(source, SourceFloat.Gain, _mute ? 0f : _volume);
            al.SourcePlay(source);

            while (_running)
            {
                if (_gainDirty)
                {
                    al.SetSourceProperty(source, SourceFloat.Gain, _mute ? 0f : _volume);
                    _gainDirty = false;
                }

                // Unqueue processed buffers and refill with new data or silence.
                al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
                for (int i = 0; i < processed; i++)
                {
                    uint freed = 0;
                    al.SourceUnqueueBuffers(source, 1, &freed);

                    short[] data = _queue.TryDequeue(out var pending) ? pending : silence;
                    fixed (short* pData = data)
                    {
                        al.BufferData(freed, BufferFormat.Mono16, pData,
                                      data.Length * sizeof(short), SampleRate);
                    }
                    al.SourceQueueBuffers(source, 1, &freed);
                }

                // Restart if source stopped due to buffer starvation.
                al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
                if (state == (int)SourceState.Stopped)
                    al.SourcePlay(source);

                Thread.Sleep(5);
            }
        }
        catch
        {
            // OpenAL not available or initialization failed — audio stays silent.
        }
        finally
        {
            if (al != null && source != 0)
            {
                al.SourceStop(source);
                al.DeleteSources(1, &source);
                fixed (uint* pBuf = buffers)
                    al.DeleteBuffers(BufferCount, pBuf);
            }
            al?.Dispose();

            if (alc != null && context != null)
            {
                alc.MakeContextCurrent(null);
                alc.DestroyContext(context);
            }
            if (alc != null && device != null)
                alc.CloseDevice(device);
            alc?.Dispose();
        }
    }
}

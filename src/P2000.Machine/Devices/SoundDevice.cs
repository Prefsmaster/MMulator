using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// 1-bit beeper square-wave synthesizer (reference doc §7 Sound, project CLAUDE.md §7).
/// Monitors writes to I/O port 0x50 bit 0 (confirmed — see §17 finding 2026-07-09) and at
/// each 50 Hz field boundary synthesizes an 882-sample 44100 Hz mono PCM block, firing
/// <see cref="SamplesReady"/> for the UI audio sink to consume.
/// </summary>
public sealed class SoundDevice : IDevice
{
    /// <summary>I/O port address for the beeper output (confirmed: port 0x50 bit 0).</summary>
    public const byte Port = 0x50;

    public const int SampleRate      = 44_100;
    public const int SamplesPerField = SampleRate / 50;  // 882 samples @ 50 Hz
    private const int TStatesPerField = 50_000;

    // BEEP line is bit 0 of port 0x50 (confirmed 2026-07-09 — §17 finding).
    private const byte BeeperBit = 0x01;

    private bool _beeperState;

    // (tstate-within-field, new beeper state) transitions recorded during the current field.
    private readonly List<(int TState, bool State)> _transitions = new(16);

    // Delegate for sampling the current field T-state (fed from Video.FieldTState).
    private readonly Func<int> _getFieldTState;

    // Reused output buffer — caller must copy before the next fire.
    private readonly short[] _samples = new short[SamplesPerField];

    /// <summary>Fired at each 50 Hz field boundary with a freshly synthesized 882-sample
    /// mono PCM block (16-bit signed, 44100 Hz). The SoundDevice reuses the same array;
    /// the subscriber must copy the data if it outlives the call.</summary>
    public event Action<short[]>? SamplesReady;

    public SoundDevice(Func<int> getFieldTState)
    {
        _getFieldTState = getFieldTState;
    }

    /// <summary>Called by the port dispatch on every write to port 0x50.
    /// Records a beeper transition if bit 0 changed.</summary>
    public void OnPortWrite(byte value)
    {
        bool newState = (value & BeeperBit) != 0;
        if (newState == _beeperState) return;
        _beeperState = newState;
        _transitions.Add((_getFieldTState(), newState));
    }

    /// <summary>Called by the machine at each 50 Hz field boundary (from the
    /// <c>Video.FieldComplete</c> handler). Synthesizes PCM, fires <see cref="SamplesReady"/>,
    /// then clears the transition log.</summary>
    public void OnFieldComplete()
    {
        Synthesize();
        SamplesReady?.Invoke(_samples);
        _transitions.Clear();
    }

    // Converts the transition log to a PCM block.  For each sample slot the beeper state is
    // determined by finding the last transition whose T-state is at or before the slot's midpoint.
    private void Synthesize()
    {
        const short Amplitude = 8_000;

        // State at the start of the field = opposite of the first transition's target state
        // (the first transition records the flip TO a new value).
        bool current = _transitions.Count > 0 ? !_transitions[0].State : _beeperState;
        int ti = 0;

        for (int i = 0; i < SamplesPerField; i++)
        {
            // T-state at the centre of this sample's time-slot.
            int midTState = (int)((2L * i + 1) * TStatesPerField / (2 * SamplesPerField));
            while (ti < _transitions.Count && _transitions[ti].TState <= midTState)
                current = _transitions[ti++].State;
            _samples[i] = current ? Amplitude : (short)0;
        }
    }

    public void Reset()
    {
        _beeperState = false;
        _transitions.Clear();
    }

    public void SaveState(IStateWriter writer) => writer.WriteBool(_beeperState);
    public void LoadState(IStateReader reader)
    {
        _beeperState = reader.ReadBool();
        _transitions.Clear();  // state is always captured at a field boundary; no pending transitions
    }
}

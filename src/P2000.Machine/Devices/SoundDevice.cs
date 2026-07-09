using P2000.Machine.Io;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// 1-bit beeper square-wave synthesizer (reference doc §7 Sound, project CLAUDE.md §7).
/// Monitors the BEEP line sourced from CPOUT bit 4 (assumed — see §17 finding) and at
/// each 50 Hz field boundary synthesizes an 882-sample 44100 Hz mono PCM block, firing
/// <see cref="SamplesReady"/> for the UI audio sink to consume.
/// </summary>
public sealed class SoundDevice : IDevice
{
    public const int SampleRate     = 44_100;
    public const int SamplesPerField = SampleRate / 50;  // 882 samples @ 50 Hz
    private const int TStatesPerField = 50_000;

    // BEEP is assumed to be bit 4 (0x10) of CPOUT (port 0x10).
    // Reference doc §7 confirms a 1-bit speaker but does not specify which CPOUT bit drives it.
    // Bits 4 and 5 are listed as "unused"; this assumption follows common P2000T emulator practice.
    // Log this assumption in the machine CLAUDE.md §17.
    private const byte BeeperBit = 0x10;

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

    public SoundDevice(CPoutLatch cpOut, Func<int> getFieldTState)
    {
        _getFieldTState = getFieldTState;
        cpOut.Written += OnCpOutWritten;
    }

    private void OnCpOutWritten(byte _, byte current)
    {
        bool newState = (current & BeeperBit) != 0;
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
    public void LoadState(IStateReader reader) => _beeperState = reader.ReadBool();
}

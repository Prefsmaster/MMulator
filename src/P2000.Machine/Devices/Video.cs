using P2000.Machine.Contention;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// The SAA5050 + fetch-timing video device (project CLAUDE.md §7/§9, reference doc §5/§5f).
/// Owns the machine's framebuffer (§3 framebuffer contract: 640×480 BGRA, a SINGLE persistent
/// buffer - the P2000T is interlaced at 50 fields/sec, not 50 progressive frames/sec) and
/// wires the fetch-timing unit (<see cref="VideoFetchUnit"/>, the SAA5020's role) to the
/// character generator (<see cref="Saa5050Generator"/>, the SAA5050's role) exactly along the
/// fetch/generate split `docs/SAA5050-implementation.md` §6 calls for: the fetch unit issues a
/// real VRAM read every column slot on the master clock (the future contention seam,
/// milestone 10); the generator only ever consumes the byte it's handed.
///
/// Each 50 Hz field pass (<see cref="VideoFetchUnit.TStatesPerField"/>) renders ONLY that
/// field's scanlines (even field → even output rows, odd field → odd rows) into the SAME
/// buffer, with NO inter-field clear - the other field's rows are left as they were ~20 ms
/// ago. This reproduces the real interlace "comb" artifact on fast motion and is the
/// project-mandated default (§3: "four display modes... default interlaced/comb" - the other
/// three are a UI-presentation concern, not a machine one, since "the toggle only affects UI
/// presentation"). <see cref="FieldComplete"/> fires every field (50 Hz - drives the
/// interrupt/CTC cadence); <see cref="FrameComplete"/> fires only after the odd field, once
/// every two fields, marking a complete 640×480 image.
/// </summary>
public sealed class Video : IDevice
{
    public const int Width = 640;
    public const int Height = 480;

    /// <summary>Screen buffer is 2 screens wide × 24 rows (reference doc §5): 80 columns per
    /// row, panned by <see cref="PanX"/> to select which 40-wide slice is visible.</summary>
    private const int BufferColumns = 80;

    private readonly PageTable _memory;
    private readonly VideoFetchUnit _fetchUnit = new();
    private readonly Saa5050Generator _generator = new();

    private readonly uint[] _framebuffer = new uint[Width * Height];
    private bool _oddField;

    public Video(PageTable memory)
    {
        _memory = memory;
        _fetchUnit.ColumnFetch += OnColumnFetch;
        _fetchUnit.LineComplete += OnLineComplete;
        _fetchUnit.FieldComplete += OnFieldComplete;
    }

    /// <summary>Upper-left X of the panned 40-column viewport into the 80-column screen
    /// buffer, 0-79 (reference doc §5: "pan the viewport... between 0 and 40"; wrapping past
    /// 79 is harmless bookkeeping since the buffer is a ring of 80 columns). The exact
    /// CPU-facing control that sets this (port vs memory-mapped register) is unconfirmed -
    /// exposed as a plain property for now, same as <c>CprinReader</c>'s ahead of its device
    /// (see milestone 5 findings).</summary>
    public int PanX { get; set; }

    /// <summary>The single persistent framebuffer (project CLAUDE.md §3 framebuffer
    /// contract). Mutated in place, field by field, with no inter-field clear - read it at a
    /// <see cref="FieldComplete"/> boundary for a tear-free (but intentionally comb-able)
    /// snapshot.</summary>
    public uint[] Framebuffer => _framebuffer;

    /// <summary>True while the CURRENTLY RUNNING field is the odd (CRS=true, smoothed) one.</summary>
    public bool IsOddField => _oddField;

    /// <summary>Raised at each 50 Hz field boundary (SAA5020 DEW pulse) - the video VBLANK
    /// interrupt source (project CLAUDE.md §8) fires once per field, not once per frame.</summary>
    public event Action? FieldComplete;

    /// <summary>Raised only after the ODD field completes (`docs/SAA5050-implementation.md`
    /// §5: "FrameComplete (odd-field only)") - once every TWO fields, marking the point where
    /// both interlaced fields have been rendered and the persistent buffer holds a complete
    /// 640×480 image. A future progressive/composited display mode would read on this event
    /// instead of <see cref="FieldComplete"/>.</summary>
    public event Action? FrameComplete;

    public void Reset()
    {
        _fetchUnit.Reset();
        _generator.Reset();
        _oddField = false;
        Array.Clear(_framebuffer);
    }

    /// <summary>Advances the video device by one master-clock T-state (project CLAUDE.md §3
    /// step 1 - called before the CPU steps).</summary>
    public void Tick() => _fetchUnit.Tick();

    private void OnColumnFetch(int column)
    {
        var charRow = _fetchUnit.Line / 10;
        var bufferColumn = (PanX + column) % BufferColumns;
        var address = (ushort)(PageTable.VideoRamStart + charRow * BufferColumns + bufferColumn);
        var data = _memory.Read(address);

        _generator.BeginCell(data, column);

        // Interlaced (project CLAUDE.md §3): this field pass owns only ITS rows (even or odd),
        // not both - the other field's rows are left untouched from ~20 ms ago (the comb).
        var row = _fetchUnit.Line * 2 + (_oddField ? 1 : 0);
        var pixelX = column * 16;
        _generator.RenderField(_framebuffer, row * Width + pixelX, oddField: _oddField);
    }

    private void OnLineComplete() => _generator.EndLine();

    private void OnFieldComplete()
    {
        var completedFieldWasOdd = _oddField; // parity of the field that JUST finished
        _generator.BeginField();
        _oddField = !_oddField;
        FieldComplete?.Invoke();
        if (completedFieldWasOdd)
        {
            FrameComplete?.Invoke();
        }
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteInt32(PanX);
        writer.WriteBool(_oddField);
        _fetchUnit.SaveState(writer);
        _generator.SaveState(writer);
    }

    public void LoadState(IStateReader reader)
    {
        PanX = reader.ReadInt32();
        _oddField = reader.ReadBool();
        _fetchUnit.LoadState(reader);
        _generator.LoadState(reader);
    }
}

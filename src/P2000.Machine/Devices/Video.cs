using P2000.Machine.Contention;
using P2000.Machine.Devices.Saa5050;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// The SAA5050 + fetch-timing video device (project CLAUDE.md §7/§9, reference doc §5/§5f).
/// Owns the machine's framebuffer (§3 framebuffer contract: 640×480 BGRA, double-buffered)
/// and wires the fetch-timing unit (<see cref="VideoFetchUnit"/>, the SAA5020's role) to the
/// character generator (<see cref="Saa5050Generator"/>, the SAA5050's role) exactly along the
/// fetch/generate split `docs/SAA5050-implementation.md` §6 calls for: the fetch unit issues a
/// real VRAM read every column slot on the master clock (the future contention seam,
/// milestone 10); the generator only ever consumes the byte it's handed.
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

    private uint[] _backBuffer = new uint[Width * Height];
    private uint[] _frontBuffer = new uint[Width * Height];

    public Video(PageTable memory)
    {
        _memory = memory;
        _fetchUnit.ColumnFetch += OnColumnFetch;
        _fetchUnit.LineComplete += OnLineComplete;
        _fetchUnit.FrameComplete += OnFrameComplete;
    }

    /// <summary>Upper-left X of the panned 40-column viewport into the 80-column screen
    /// buffer, 0-79 (reference doc §5: "pan the viewport... between 0 and 40"; wrapping past
    /// 79 is harmless bookkeeping since the buffer is a ring of 80 columns). The exact
    /// CPU-facing control that sets this (port vs memory-mapped register) is unconfirmed -
    /// exposed as a plain property for now, same as <c>CprinReader</c>'s ahead of its device
    /// (see milestone 5 findings).</summary>
    public int PanX { get; set; }

    /// <summary>The most recently completed frame (project CLAUDE.md §3 framebuffer
    /// contract). Never mutated mid-render - only swapped in whole on frame completion.</summary>
    public uint[] FrontBuffer => _frontBuffer;

    public void Reset()
    {
        _fetchUnit.Reset();
        _generator.Reset();
        Array.Clear(_backBuffer);
        Array.Clear(_frontBuffer);
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

        var rowBase = _fetchUnit.Line * 2 * Width;
        var pixelX = column * 16;
        _generator.RenderField(_backBuffer, rowBase + pixelX, oddField: false);
        _generator.RenderField(_backBuffer, rowBase + Width + pixelX, oddField: true);
    }

    private void OnLineComplete() => _generator.EndLine();

    private void OnFrameComplete()
    {
        _generator.BeginFrame();
        (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
    }

    public void SaveState(IStateWriter writer)
    {
        writer.WriteInt32(PanX);
        _fetchUnit.SaveState(writer);
        _generator.SaveState(writer);
    }

    public void LoadState(IStateReader reader)
    {
        PanX = reader.ReadInt32();
        _fetchUnit.LoadState(reader);
        _generator.LoadState(reader);
    }
}

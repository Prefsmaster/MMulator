using System.Text;
using P2000.Machine.State;

namespace P2000.Machine.Tests.State;

/// <summary>
/// Minimal in-memory <see cref="IStateWriter"/>/<see cref="IStateReader"/> for device
/// save/load round-trip tests. Not part of the production serialization format (the real
/// `.state` file writer/reader is a later milestone, project CLAUDE.md §11) - just enough
/// to prove a device's <c>SaveState</c> output is exactly what its <c>LoadState</c> expects.
/// </summary>
public sealed class InMemoryState : IStateWriter, IStateReader
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    private BinaryReader? _reader;

    public InMemoryState()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public void WriteByte(byte value) => _writer.Write(value);
    public void WriteBytes(ReadOnlySpan<byte> bytes) => _writer.Write(bytes);
    public void WriteBool(bool value) => _writer.Write(value);
    public void WriteUInt16(ushort value) => _writer.Write(value);
    public void WriteUInt32(uint value) => _writer.Write(value);
    public void WriteUInt64(ulong value) => _writer.Write(value);
    public void WriteInt32(int value) => _writer.Write(value);
    public void WriteString(string value) => _writer.Write(value);

    /// <summary>Switches from write mode to read mode, rewinding to the start. Returns
    /// <c>this</c> so a test can write, then call <c>LoadState(state.BeginRead())</c>.</summary>
    public InMemoryState BeginRead()
    {
        _writer.Flush();
        _stream.Position = 0;
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        return this;
    }

    private BinaryReader Reader =>
        _reader ?? throw new InvalidOperationException("Call BeginRead() before reading.");

    public byte ReadByte() => Reader.ReadByte();
    public void ReadBytes(Span<byte> destination) => Reader.Read(destination);
    public bool ReadBool() => Reader.ReadBoolean();
    public ushort ReadUInt16() => Reader.ReadUInt16();
    public uint ReadUInt32() => Reader.ReadUInt32();
    public ulong ReadUInt64() => Reader.ReadUInt64();
    public int ReadInt32() => Reader.ReadInt32();
    public string ReadString() => Reader.ReadString();
}

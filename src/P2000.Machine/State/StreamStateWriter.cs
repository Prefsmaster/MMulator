using System.Text;

namespace P2000.Machine.State;

/// <summary>
/// Binary <see cref="IStateWriter"/> backed by a <see cref="Stream"/> (project CLAUDE.md
/// §11). The production serializer for <c>.state</c> files — mirrors
/// <see cref="StreamStateReader"/> field-for-field using <see cref="BinaryWriter"/>
/// encoding so round-trips are exact.
/// </summary>
public sealed class StreamStateWriter : IStateWriter, IDisposable
{
    private readonly BinaryWriter _w;

    public StreamStateWriter(Stream stream, bool leaveOpen = false)
    {
        _w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen);
    }

    public void WriteByte(byte value) => _w.Write(value);
    public void WriteBytes(ReadOnlySpan<byte> bytes) => _w.Write(bytes);
    public void WriteBool(bool value) => _w.Write(value);
    public void WriteUInt16(ushort value) => _w.Write(value);
    public void WriteUInt32(uint value) => _w.Write(value);
    public void WriteUInt64(ulong value) => _w.Write(value);
    public void WriteInt32(int value) => _w.Write(value);
    public void WriteString(string value) => _w.Write(value);

    public void Dispose() => _w.Dispose();
}

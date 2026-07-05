using System.Text;

namespace P2000.Machine.State;

/// <summary>
/// Binary <see cref="IStateReader"/> backed by a <see cref="Stream"/> (project CLAUDE.md
/// §11). The production deserializer for <c>.state</c> files — mirrors
/// <see cref="StreamStateWriter"/> field-for-field using <see cref="BinaryReader"/>
/// encoding so round-trips are exact.
/// </summary>
public sealed class StreamStateReader : IStateReader, IDisposable
{
    private readonly BinaryReader _r;

    public StreamStateReader(Stream stream, bool leaveOpen = false)
    {
        _r = new BinaryReader(stream, Encoding.UTF8, leaveOpen);
    }

    public byte ReadByte() => _r.ReadByte();
    public void ReadBytes(Span<byte> destination) => _r.Read(destination);
    public bool ReadBool() => _r.ReadBoolean();
    public ushort ReadUInt16() => _r.ReadUInt16();
    public uint ReadUInt32() => _r.ReadUInt32();
    public ulong ReadUInt64() => _r.ReadUInt64();
    public int ReadInt32() => _r.ReadInt32();
    public string ReadString() => _r.ReadString();

    public void Dispose() => _r.Dispose();
}

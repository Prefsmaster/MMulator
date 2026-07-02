namespace P2000.Machine.State;

/// <summary>
/// Source for a device's runtime-state deserialization walk. Mirrors
/// <see cref="IStateWriter"/> field-for-field, read in the same order they were written.
/// </summary>
public interface IStateReader
{
    byte ReadByte();
    void ReadBytes(Span<byte> destination);
    bool ReadBool();
    ushort ReadUInt16();
    uint ReadUInt32();
    ulong ReadUInt64();
    int ReadInt32();
    string ReadString();
}

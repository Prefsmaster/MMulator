namespace P2000.Machine.State;

/// <summary>
/// Sink for a device's runtime-state serialization walk (root CLAUDE.md §2 rule 4 /
/// project CLAUDE.md §4, §11). Devices write their own fields in a fixed order;
/// <see cref="IStateReader"/> reads them back in the same order.
/// </summary>
public interface IStateWriter
{
    void WriteByte(byte value);
    void WriteBytes(ReadOnlySpan<byte> bytes);
    void WriteBool(bool value);
    void WriteUInt16(ushort value);
    void WriteUInt32(uint value);
    void WriteUInt64(ulong value);
    void WriteInt32(int value);
    void WriteString(string value);
}

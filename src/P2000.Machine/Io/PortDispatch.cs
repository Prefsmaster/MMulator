namespace P2000.Machine.Io;

/// <summary>
/// Routes IORQ reads/writes by 8-bit port address to registered devices (project CLAUDE.md
/// §6, reference doc §5c: the P2000T decodes only A0-A7 for I/O). A single port may have
/// multiple listeners — port 0x10 (CPOUT) and 0x20 (CPRIN) are shared between the keyboard,
/// cassette, and printer. Writes fan out to every listener registered on that port; reads
/// combine each source's contribution by bitwise OR (a source that doesn't drive a bit
/// leaves it 0, so combining is safe as long as each source only sets the bits it owns). A
/// port with no registered read source answers open bus (0xFF), the same presence-probe
/// convention <see cref="Memory.PageTable"/> uses for unpopulated memory.
/// </summary>
public sealed class PortDispatch
{
    public const byte OpenBus = 0xFF;

    private readonly List<Action<byte>>?[] _writeListeners = new List<Action<byte>>?[256];
    private readonly List<Func<byte>>?[] _readSources = new List<Func<byte>>?[256];

    /// <summary>Registers <paramref name="listener"/> to be invoked with the written value
    /// whenever the CPU writes to <paramref name="port"/>. Multiple listeners on the same
    /// port all fire, in registration order.</summary>
    public void RegisterWrite(byte port, Action<byte> listener) =>
        (_writeListeners[port] ??= new List<Action<byte>>()).Add(listener);

    /// <summary>Registers <paramref name="source"/> as a contributor to reads of
    /// <paramref name="port"/>. Multiple sources on the same port are OR-combined.</summary>
    public void RegisterRead(byte port, Func<byte> source) =>
        (_readSources[port] ??= new List<Func<byte>>()).Add(source);

    public void Write(byte port, byte value)
    {
        var listeners = _writeListeners[port];
        if (listeners is null)
        {
            return;
        }

        foreach (var listener in listeners)
        {
            listener(value);
        }
    }

    public byte Read(byte port)
    {
        var sources = _readSources[port];
        if (sources is null || sources.Count == 0)
        {
            return OpenBus;
        }

        byte value = 0;
        foreach (var source in sources)
        {
            value |= source();
        }

        return value;
    }
}

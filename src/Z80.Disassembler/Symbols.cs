namespace Z80.Disassembler;

/// <summary>
/// A minimal, machine-agnostic address/port name table for injection into
/// <see cref="Disassembler.Decode"/> (disassembler CLAUDE.md §7). Per root
/// CLAUDE.md §2, no P2000T (or any machine-specific) names belong in this
/// project — the future P2000.Machine/P2000.UI layer populates an instance of
/// this class (I/O port map, ROM routine labels) and injects its lookup
/// delegates here; this type only provides the mechanism.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<ushort, string> _addresses = new();
    private readonly Dictionary<byte, string> _ports = new();

    public void AddAddress(ushort address, string name) => _addresses[address] = name;

    public void AddPort(byte port, string name) => _ports[port] = name;

    public string? LookupAddress(ushort address) => _addresses.TryGetValue(address, out var name) ? name : null;

    public string? LookupPort(byte port) => _ports.TryGetValue(port, out var name) ? name : null;
}

namespace Z80.Disassembler;

/// <summary>
/// Decodes Z80 machine code into text. A pure, stateless visitor over the same
/// x/y/z/p/q bit-field decomposition the core's <c>Dispatch</c>/<c>Execute</c>
/// methods use (root CLAUDE.md §3), reading the exact same operand orderings from
/// <c>Z80.Core.Z80Tables</c> so it structurally cannot disagree with the core
/// about what an opcode means. <see cref="Decode"/> never touches a live
/// <c>Z80.Core.Z80</c> instance — it only calls the supplied <c>readByte</c>
/// delegate against a memory snapshot (disassembler CLAUDE.md §5).
/// </summary>
public sealed partial class Disassembler
{
    private enum Prefix { None, CB, ED, DD, FD, DDCB, FDCB }

    /// <summary>Decodes the instruction starting at <paramref name="addr"/>.
    /// <paramref name="readByte"/> is called only at addresses this instruction
    /// actually consumes, in order, starting at <paramref name="addr"/> — never
    /// touches a live core, only a memory snapshot. <paramref name="symbolLookup"/>
    /// (address→name) and <paramref name="portName"/> (port→name) are optional;
    /// with neither supplied, operands render as bare hex.</summary>
    public DisasmLine Decode(
        ushort addr,
        Func<ushort, byte> readByte,
        Func<ushort, string?>? symbolLookup = null,
        Func<byte, string?>? portName = null)
    {
        var s = new Session(addr, readByte, symbolLookup, portName);

        var prefix = Prefix.None;
        byte b;
        while (true)
        {
            b = s.Reader.Next();
            if (b == 0xDD) { prefix = Prefix.DD; continue; }
            if (b == 0xFD) { prefix = Prefix.FD; continue; }
            if (b == 0xED)
            {
                // ED always overwrites: any preceding DD/FD chain is a wasted
                // prefix once ED appears, exactly as Z80.cs's RunFetch T4 prefix
                // hand-off does (the DD/FD branch is only taken when the byte is
                // NOT CB/ED; ED unconditionally sets _prefix = Prefix.ED).
                prefix = Prefix.ED;
                b = s.Reader.Next();
                var edText = DecodeEd(b, s);
                return Finish(addr, s, edText);
            }
            if (b == 0xCB)
            {
                if (prefix is Prefix.DD or Prefix.FD)
                {
                    // DDCB/FDCB: displacement byte comes BEFORE the CB-table
                    // opcode byte (disassembler CLAUDE.md §9).
                    var isIx = prefix == Prefix.DD;
                    var d = s.Reader.NextSigned();
                    var cbOp = s.Reader.Next();
                    var ddCbText = DecodeIndexedCb(isIx, d, cbOp);
                    return Finish(addr, s, ddCbText);
                }
                var plainCbOp = s.Reader.Next();
                var cbText = DecodeCb(plainCbOp);
                return Finish(addr, s, cbText);
            }
            break; // ordinary opcode byte (base page, or DD/FD-indexed page)
        }

        var text = prefix switch
        {
            Prefix.None => DecodeBase(b, s),
            Prefix.DD or Prefix.FD => DecodeIndexed(prefix == Prefix.DD, b, s),
            _ => throw new InvalidOperationException($"Unreachable prefix {prefix}."),
        };
        return Finish(addr, s, text);
    }

    private static DisasmLine Finish(ushort addr, Session s, string text) =>
        new(addr, s.Reader.Length, s.Reader.ToArray(), s.Annotate(text));

    /// <summary>Per-<see cref="Decode"/>-call state: the byte cursor plus the
    /// pending symbol/port annotation for whichever operand (at most one per
    /// instruction) is address- or port-shaped.</summary>
    private sealed class Session
    {
        public readonly ushort BaseAddress;
        public readonly ByteReader Reader;
        private readonly Func<ushort, string?>? _symbols;
        private readonly Func<byte, string?>? _ports;
        public ushort? SymbolTarget;
        public byte? PortTarget;

        public Session(ushort addr, Func<ushort, byte> readByte, Func<ushort, string?>? symbols, Func<byte, string?>? ports)
        {
            BaseAddress = addr;
            Reader = new ByteReader(addr, readByte);
            _symbols = symbols;
            _ports = ports;
        }

        public string Annotate(string text)
        {
            if (SymbolTarget is ushort a && _symbols?.Invoke(a) is string name)
                return $"{text} ; {name}";
            if (PortTarget is byte p && _ports?.Invoke(p) is string pname)
                return $"{text} ; {pname}";
            return text;
        }
    }

    /// <summary>Reads bytes sequentially from a fixed start address via the
    /// supplied delegate, accumulating them for <see cref="DisasmLine.Bytes"/>/
    /// <see cref="DisasmLine.Length"/>. Address arithmetic wraps at 64K like the
    /// real address bus.</summary>
    private sealed class ByteReader
    {
        private readonly ushort _start;
        private readonly Func<ushort, byte> _readByte;
        private readonly List<byte> _bytes = new(6);

        public ByteReader(ushort start, Func<ushort, byte> readByte)
        {
            _start = start;
            _readByte = readByte;
        }

        public int Length => _bytes.Count;

        public byte Next()
        {
            var addr = unchecked((ushort)(_start + _bytes.Count));
            var b = _readByte(addr);
            _bytes.Add(b);
            return b;
        }

        public sbyte NextSigned() => unchecked((sbyte)Next());

        public ushort NextWord()
        {
            var lo = Next();
            var hi = Next();
            return (ushort)((hi << 8) | lo);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}

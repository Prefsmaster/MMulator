namespace Z80.Disassembler;

/// <summary>
/// Backward-context alignment for a "disassembly around PC" debugger view
/// (disassembler CLAUDE.md §6). Z80 instructions are 1-4 bytes with no fixed
/// alignment, so decoding backwards from PC is ambiguous — this locates an
/// anchor address at or before PC such that decoding forward from it lands
/// exactly on PC, then decodes forward from there. The line AT PC and everything
/// after is always exact (forward decoding from PC is unambiguous by
/// construction); only the leading line(s) before PC are a heuristic and may be
/// wrong immediately after a data block.
/// </summary>
public static class SyncToPc
{
    /// <summary>Given a target PC, returns a known-good instruction-boundary
    /// address at or before it (e.g. from a project's monitor-ROM disassembly),
    /// or null to fall back to the byte-count heuristic. Pluggable per
    /// disassembler CLAUDE.md §6's "better anchors when available".</summary>
    public delegate ushort? AnchorSource(ushort pc);

    /// <summary>Decodes a window of instructions around <paramref name="pc"/>:
    /// up to <paramref name="linesBefore"/> lines ending exactly at PC, the PC
    /// line itself, then <paramref name="linesAfter"/> lines after it.</summary>
    public static List<DisasmLine> DecodeAround(
        Disassembler disassembler,
        ushort pc,
        Func<ushort, byte> readByte,
        int linesBefore,
        int linesAfter,
        Func<ushort, string?>? symbolLookup = null,
        Func<byte, string?>? portName = null,
        AnchorSource? anchorSource = null,
        int minLookback = 8,
        int maxLookback = 16)
    {
        var anchor = anchorSource?.Invoke(pc)
            ?? FindHeuristicAnchor(disassembler, pc, readByte, minLookback, maxLookback);

        var before = new List<DisasmLine>();
        var addr = anchor;
        while (addr != pc)
        {
            var line = disassembler.Decode(addr, readByte, symbolLookup, portName);
            before.Add(line);
            var next = unchecked((ushort)(addr + line.Length));
            if (Overshoots(addr, next, pc)) { addr = pc; break; } // heuristic failed to hit PC: bail to PC directly
            addr = next;
        }
        if (before.Count > linesBefore)
            before = before.GetRange(before.Count - linesBefore, linesBefore);

        var result = new List<DisasmLine>(before);
        var cur = pc;
        for (var i = 0; i <= linesAfter; i++)
        {
            var line = disassembler.Decode(cur, readByte, symbolLookup, portName);
            result.Add(line);
            cur = unchecked((ushort)(cur + line.Length));
        }
        return result;
    }

    private static ushort FindHeuristicAnchor(
        Disassembler disassembler, ushort pc, Func<ushort, byte> readByte, int minLookback, int maxLookback)
    {
        for (var k = minLookback; k <= maxLookback; k++)
        {
            var candidate = unchecked((ushort)(pc - k));
            if (BoundaryHitsPc(disassembler, candidate, pc, readByte))
                return candidate;
        }
        return unchecked((ushort)(pc - minLookback)); // best-effort: may misdecode a leading line
    }

    /// <summary>Decodes forward from <paramref name="start"/>, returning true only
    /// if a boundary lands exactly on <paramref name="pc"/> (never overshooting
    /// past it) — disassembler CLAUDE.md §6's "pick the alignment whose
    /// boundaries hit PC".</summary>
    private static bool BoundaryHitsPc(Disassembler disassembler, ushort start, ushort pc, Func<ushort, byte> readByte)
    {
        var addr = start;
        for (var i = 0; i < 32 && addr != pc; i++)
        {
            var line = disassembler.Decode(addr, readByte);
            var next = unchecked((ushort)(addr + line.Length));
            if (Overshoots(addr, next, pc)) return false;
            addr = next;
        }
        return addr == pc;
    }

    private static bool Overshoots(ushort before, ushort after, ushort pc) => before < pc && after > pc;
}

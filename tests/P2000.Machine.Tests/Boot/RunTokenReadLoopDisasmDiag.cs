using Xunit.Abstractions;
using Z80.Disassembler;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part G (owner follow-up, 2026-08-04) — disassembles the BASIC-side (cartridge ROM,
/// <c>Basic-24.bin</c>) code around the two confirmed repeated call sites found by
/// <c>BasicReadLoopCallSiteDiag.cs</c>: 0x32A8 (F_READ, C=0x14) and 0x32D0 (F_DMAOFF, C=0x1A),
/// each called 14 times during <c>RUN"VOLORG"</c>. This static dump backs the findings-log
/// write-up of the loop structure:
///
/// - <c>0x323A</c>: the loop-driving routine. Reads a pointer from fixed cell <c>0x63A3</c>,
///   checks a 2-byte "bytes remaining in current 256-byte buffer" counter at
///   <c>[pointer+0x26..0x27]</c>; if nonzero, decrements it and returns one byte from a computed
///   buffer position (a byte-by-byte program scanner, confirmed via live trace in
///   <c>RecordCounterLiveTraceDiag.cs</c> to run 13 full 256-byte cycles before stopping). If that
///   counter is already zero, falls through to check a SECOND 2-byte counter at
///   <c>[pointer+0x24..0x25]</c> (<c>0x326C</c>) — if that is ALSO zero, exits the whole loop
///   (<c>0x3279</c>, <c>SCF; LD A,1Ah; RET</c>); otherwise calls <c>0x327F</c> to issue one more
///   real DMAOFF+READ pair via <c>0x32A8</c>/<c>0x32D0</c>.
/// - <c>0x3273</c> (within the 0x323C-area caller): <c>CALL 327Fh</c> then
///   <c>JP NZ,323Ch</c> — the actual repeat-or-stop branch, based on the flags <c>327F</c>'s own
///   tail (<c>32B0</c>: <c>DEC A</c> on the F_READ result, comparing against the standard CP/M
///   EOF value 1) leaves set.
/// - <c>0x63A3</c> itself is set ONCE, at <c>0x37AD</c> (inside LOAD's own setup, confirmed
///   previously at roughly 0x376F-0x3830), from <c>(0x63B1)</c> — a separate cell not yet traced
///   to ITS OWN origin.
///
/// Still open: the exact initial value and semantics of the SECOND counter
/// (<c>[pointer+0x24..0x25]</c>), which is what actually governs the loop's real exit condition
/// once the byte-buffer counter empties.
/// </summary>
public class RunTokenReadLoopDisasmDiag
{
    private readonly ITestOutputHelper _output;
    public RunTokenReadLoopDisasmDiag(ITestOutputHelper output) => _output = output;

    private const ushort CartridgeStart = 0x1000;

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "MMulator.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("repo root not found");
    }

    private static List<DisasmLine> DecodeRange(Disassembler disasm, Func<ushort, byte> readByte, ushort from, ushort to)
    {
        var lines = new List<DisasmLine>();
        var pc = from;
        while (pc < to)
        {
            var line = disasm.Decode(pc, readByte);
            lines.Add(line);
            pc = unchecked((ushort)(pc + line.Length));
        }
        return lines;
    }

    [Fact]
    public void Disassemble_RunTokenReadLoopAndLoadSetup()
    {
        var repoRoot = FindRepoRoot();
        var romPath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var rom = File.ReadAllBytes(romPath);

        var disasm = new Disassembler();
        byte ReadByte(ushort a)
        {
            var offset = a - CartridgeStart;
            return offset >= 0 && offset < rom.Length ? rom[offset] : (byte)0xFF;
        }

        var loopLines = DecodeRange(disasm, ReadByte, 0x31C0, 0x3330);
        _output.WriteLine($"=== Read loop region 0x31C0-0x3330, {loopLines.Count} instructions ===");
        foreach (var l in loopLines)
        {
            var bytes = string.Join(' ', Array.ConvertAll(l.Bytes, b => b.ToString("X2")));
            _output.WriteLine($"{l.Address:X4}: {bytes,-14} {l.Text}");
        }

        var setupLines = DecodeRange(disasm, ReadByte, 0x3760, 0x3830);
        _output.WriteLine($"=== LOAD setup region 0x3760-0x3830, {setupLines.Count} instructions ===");
        foreach (var l in setupLines)
        {
            var bytes = string.Join(' ', Array.ConvertAll(l.Bytes, b => b.ToString("X2")));
            _output.WriteLine($"{l.Address:X4}: {bytes,-14} {l.Text}");
        }

        // Pin the confirmed shape of the two repeated call sites and the loop-back branch, so a
        // future ROM/tooling change doesn't silently drift without failing this test.
        var readCallLine = loopLines.Single(l => l.Address == 0x32A8);
        Assert.Contains("6205", readCallLine.Text);
        var dmaoffCallLine = loopLines.Single(l => l.Address == 0x32D0);
        Assert.Contains("6205", dmaoffCallLine.Text);
        var loopBackLine = loopLines.Single(l => l.Address == 0x3276);
        Assert.Contains("323C", loopBackLine.Text);
        var pointerInitLine = setupLines.Single(l => l.Address == 0x37AD);
        Assert.Contains("63A3", pointerInitLine.Text);
    }
}

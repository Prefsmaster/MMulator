using Xunit.Abstractions;
using Z80.Disassembler;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Diagnostic-only (not a permanent regression test): disassembles real Disk BASIC token
/// entry points directly out of <c>Basic-24.bin</c> (confirmed 16K-only ROM, mapped linearly
/// at CPU 0x1000-0x4FFF per <see cref="P2000.Machine.Slots.Slot1Cartridge"/>) using the
/// project's own <see cref="Disassembler"/> — 2026-08-02 "Disk I/O error" investigation
/// (cc-bugfix-prompt-9). Part A used this to find SYSTEM/RESET/FILES's token-handler shape
/// (each just loads a PDOS function code into C and does <c>CALL 0x6205</c>, no flag logic of
/// its own — see <c>DiskBasicDiskLoadedDisasmDiag</c> for the actual flag-check wrapper, which
/// lives in disk-loaded RAM, not here). Part B extends this to LOAD's full token handler,
/// looking for every <c>CALL 0x6205</c> and the PDOS function code (register C) loaded just
/// before it, to find which function(s) LOAD/RUN use and whether any of them fall in the
/// wrapper's confirmed "always error" class set.
/// </summary>
public class DiskBasicDisasmDiag
{
    private readonly ITestOutputHelper _output;
    public DiskBasicDisasmDiag(ITestOutputHelper output) => _output = output;

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

    private const ushort CartridgeStart = 0x1000;

    private void DumpFrom(byte[] rom, ushort address, int lines, string label)
    {
        _output.WriteLine($"=== {label} @ 0x{address:X4} ===");
        var disasm = new Disassembler();
        byte ReadByte(ushort a)
        {
            var offset = a - CartridgeStart;
            return offset >= 0 && offset < rom.Length ? rom[offset] : (byte)0xFF;
        }

        var pc = address;
        for (var i = 0; i < lines; i++)
        {
            var line = disasm.Decode(pc, ReadByte);
            var bytes = string.Join(' ', Array.ConvertAll(line.Bytes, b => b.ToString("X2")));
            _output.WriteLine($"{line.Address:X4}: {bytes,-12} {line.Text}");
            pc = unchecked((ushort)(pc + line.Length));
        }
    }

    [Fact]
    public void Disassemble_SYSTEM_RESET_FILES_LOAD_TokenEntryPoints()
    {
        var repoRoot = FindRepoRoot();
        var romPath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var rom = File.ReadAllBytes(romPath);
        Assert.Equal(16384, rom.Length);

        DumpFrom(rom, 0x34D2, 60, "SYSTEM token entry (&H34D2)");
        DumpFrom(rom, 0x3500, 40, "RESET token entry (&H3500)");
        DumpFrom(rom, 0x3543, 40, "FILES token entry (&H3543)");
        DumpFrom(rom, 0x376F, 60, "LOAD token entry (&H376F)");
    }

    /// <summary>Part B: LOAD's full token handler, from &amp;H376F all the way to its end (the
    /// next token's entry point, &amp;H3830 MERGE, per the Adresboekje's own token table —
    /// bounds the dump so it doesn't run into MERGE's own code and misattribute it to LOAD).
    /// Scans the decoded text for every <c>CALL 6205h</c> (the confirmed PDOS invocation) and
    /// reports the address, plus a few preceding lines so the loaded C (function code) value is
    /// visible.</summary>
    [Fact]
    public void Disassemble_LOAD_FullTokenHandler_FindEveryPdosCall()
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

        var lines = new List<DisasmLine>();
        var pc = (ushort)0x376F;
        while (pc < 0x3830)
        {
            var line = disasm.Decode(pc, ReadByte);
            lines.Add(line);
            pc = unchecked((ushort)(pc + line.Length));
        }

        _output.WriteLine($"=== LOAD full token handler (&H376F-&H3830), {lines.Count} instructions ===");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var bytes = string.Join(' ', Array.ConvertAll(l.Bytes, b => b.ToString("X2")));
            _output.WriteLine($"{l.Address:X4}: {bytes,-12} {l.Text}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== CALL 6205h occurrences (PDOS invocations), with preceding context ===");
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Text.Contains("6205", StringComparison.OrdinalIgnoreCase)) continue;
            _output.WriteLine($"--- PDOS call at 0x{lines[i].Address:X4} ---");
            for (var j = Math.Max(0, i - 6); j <= i; j++)
                _output.WriteLine($"  {lines[j].Address:X4}: {lines[j].Text}");
        }
    }

    /// <summary>Scans the ENTIRE cartridge ROM (0x1000-0x4FFF) for every <c>CALL 6205h</c> site
    /// (the confirmed PDOS invocation), a straightforward linear decode from the cartridge start
    /// — accepting the small risk of transient misalignment through embedded data/string
    /// regions, since a false "6205h" text match from misdecoded data is exceedingly unlikely.
    /// For each real hit, reports the preceding several lines so the loaded C (PDOS function
    /// code) value is visible, to build a full map of which functions this ROM actually uses.</summary>
    [Fact]
    public void Disassemble_WholeCartridge_FindEveryPdosCallSite()
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

        var lines = new List<DisasmLine>();
        var pc = CartridgeStart;
        while (pc < 0x5000)
        {
            var line = disasm.Decode(pc, ReadByte);
            lines.Add(line);
            var next = unchecked((ushort)(pc + line.Length));
            if (next <= pc) break; // guard against a zero-length/overflow decode near the top
            pc = next;
        }

        _output.WriteLine($"=== Whole-cartridge PDOS call sites ({lines.Count} instructions scanned) ===");
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Text.Contains("6205", StringComparison.OrdinalIgnoreCase)) continue;
            _output.WriteLine($"--- PDOS call at 0x{lines[i].Address:X4} ---");
            for (var j = Math.Max(0, i - 5); j <= i; j++)
                _output.WriteLine($"  {lines[j].Address:X4}: {lines[j].Text}");
        }
    }
}

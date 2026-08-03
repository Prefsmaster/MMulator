using P2000.Machine.Devices.Fdc;
using Xunit.Abstractions;
using Z80.Disassembler;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Diagnostic-only: reconstructs the ~8K "missing interpreter chunk" Disk BASIC loads from the
/// boot floppy's tracks 3-5 into RAM at &amp;H6200 (per the Adresboekje memory map, see
/// <c>docs/Adresboekje-DiskBASIC-parsed.md</c>), by reading those physical tracks directly out of
/// <c>diskbasic_1.6uk.dsk</c> via <see cref="DskImage.ReadSector"/> (same technique the boot-time
/// loader itself uses — a plain sequential 16-sector/track read, matching the confirmed "READ A
/// TRACK" R=1..EOT=16 shape, no logical/interleaved reordering). Cylinder numbering: the
/// Adresboekje's own 1-based "track N" convention maps to <c>DskImage</c>'s 0-based cylinder as
/// N-1 (consistent with this project's own confirmed "track 1/2" boot-check = cylinders 0/1).
///
/// Sanity check built in: the Adresboekje names &amp;H693D-&amp;H696C as the literal text
/// "PHILIPS DISK BASIC ......" — if the reconstructed buffer contains that string at the right
/// offset, the track mapping is confirmed correct before trusting anything disassembled from it.
/// </summary>
public class DiskBasicDiskLoadedDisasmDiag
{
    private readonly ITestOutputHelper _output;
    public DiskBasicDiskLoadedDisasmDiag(ITestOutputHelper output) => _output = output;

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

    private const ushort LoadedChunkStart = 0x6200;

    [Fact]
    public void ReconstructLoadedChunk_ContainsPhilipsDiskBasicText_AtExpectedOffset()
    {
        var repoRoot = FindRepoRoot();
        var diskPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");
        var disk = new DskImage(diskPath);

        var chunk = new List<byte>();
        for (var cylinder = 2; cylinder <= 4; cylinder++) // tracks 3,4,5 (1-based) => cylinders 2-4
            for (var sector = 1; sector <= 16; sector++)
                chunk.AddRange(disk.ReadSector(cylinder, head: 0, sector).ToArray());

        var buffer = chunk.ToArray();
        _output.WriteLine($"Reconstructed chunk length: {buffer.Length} bytes (0x{LoadedChunkStart:X4}-0x{LoadedChunkStart + buffer.Length - 1:X4})");

        var textOffset = 0x693D - LoadedChunkStart;
        var textBytes = buffer.AsSpan(textOffset, 20).ToArray();
        var text = System.Text.Encoding.ASCII.GetString(textBytes);
        _output.WriteLine($"Bytes at 0x693D (offset 0x{textOffset:X4}): {string.Join(' ', Array.ConvertAll(textBytes, b => b.ToString("X2")))}");
        _output.WriteLine($"As ASCII: \"{text}\"");

        Assert.Contains("PHILIPS", text.ToUpperInvariant());
    }

    private void DumpFrom(byte[] buffer, ushort address, int lines, string label)
    {
        _output.WriteLine($"=== {label} @ 0x{address:X4} ===");
        var disasm = new Disassembler();
        byte ReadByte(ushort a)
        {
            var offset = a - LoadedChunkStart;
            return offset >= 0 && offset < buffer.Length ? buffer[offset] : (byte)0xFF;
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
    public void Disassemble_PdosEntryAndStartupRoutine_AroundTheDiskIoErrorFlag()
    {
        var repoRoot = FindRepoRoot();
        var diskPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");
        var disk = new DskImage(diskPath);

        var chunk = new List<byte>();
        for (var cylinder = 2; cylinder <= 4; cylinder++)
            for (var sector = 1; sector <= 16; sector++)
                chunk.AddRange(disk.ReadSector(cylinder, head: 0, sector).ToArray());
        var buffer = chunk.ToArray();

        // &H6934: JP 696D -- calls PDOS (per Adresboekje, only ever jumped to from &H6205 in ROM)
        DumpFrom(buffer, 0x6934, 10, "PDOS call jump (&H6934)");
        // &H696D onward -- the actual PDOS body
        DumpFrom(buffer, 0x696D, 120, "PDOS body (&H696D)");
    }
}

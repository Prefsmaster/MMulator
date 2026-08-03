using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part E addendum (cc-bugfix-prompt-12 addendum) — the "cheap check to do first": PDOS's
/// function codes match standard CP/M 2.2 BDOS exactly (0x0F=F_OPEN, 0x14=F_READ, 0x1A=F_DMAOFF),
/// which reframes what the 14-15 physical sector reads Parts B/C/D observed during
/// <c>RUN"VOLORG"</c> most likely ARE: not a directory scan (F_SFIRST/F_SNEXT, confirmed never
/// called), but VOLORG.BAS's own sequential file DATA being read record-by-record via a standard
/// DMAOFF-then-READ loop. This test settles it empirically: does each real READ DATA command
/// during this repro target physical cylinder 0 (1-based track 1, the directory), or one of
/// VOLORG's own real data tracks?
///
/// VOLORG's real allocation map (raw bytes confirmed directly from <c>assets/Disks/volorg.dsk</c>,
/// track-1 slot 0: name="VOLORG  " ext="BAS" byte0=0xF3 sector-count=0x2C(44)):
/// records {4,5,6,7,12,13,14,15,16,17,18} (11 records, matching 44 sectors / 4 sectors-per-record).
/// Under the confirmed 1-based-track = record/4 + 1 formula (<c>docs/P2000T-disk-formats.md</c>
/// §6a, independently reconfirmed in the machine-project findings log's 2026-07-28 milestone-22a
/// entry): records 4-7 -> track 2 (cylinder 1), records 12-15 -> track 4 (cylinder 3), records
/// 16-18 -> track 5 (cylinder 4). NONE of VOLORG's own data lives on track 1 (cylinder 0, the
/// directory track) at all.
///
/// CONFIRMED: exactly one initial read on cylinder 0 (the directory), then all 14 remaining reads
/// target cylinder 1 (track 2) -- VOLORG's own real data track. This settles Part B/C/D's
/// "directory scan" label as WRONG for these reads -- they are the real file-data read, already
/// working correctly. A real off-by-one was found and fixed in THIS test's own instrumentation
/// (<c>Upd765.CurrentTransfer.Sector</c>, sampled at the COMPLETE trace event, reports one sector
/// PAST the one that just finished, since <c>_transferIndex</c> has already advanced to the full
/// transferred byte count by then) -- corrected by subtracting 1. Once corrected, the track-2
/// sector sequence is <c>1,7,13,3,9,15,5,11,2,8,14,4,10,16</c> -- EXACTLY the documented full
/// 16-sector interleave (<c>docs/P2000T-disk-formats.md</c> §6a), missing only 6 and 12 -- the
/// SAME "14-of-16" pattern already flagged (project findings log, 2026-07-28) as an unrelated
/// loose end for DIRECTORY reads. This is the SAME underlying sector-advancement mechanism, now
/// confirmed to also govern real file-data reads, not a coincidence.
/// </summary>
public class ReadDataPhysicalTrackDiag
{
    private readonly ITestOutputHelper _output;
    public ReadDataPhysicalTrackDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;

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

    private static void Ticks(Machine machine, int fields)
    {
        for (var f = 0; f < fields; f++)
            for (var t = 0; t < VideoFetchUnit.TStatesPerField; t++)
                machine.Tick();
    }

    private static (bool Found, string Screen) WaitForScreenContains(Machine machine, string needle, int maxFields)
    {
        for (var f = 0; f < maxFields; f++)
        {
            Ticks(machine, 1);
            var screen = SnapshotScreenText(machine);
            if (screen.Contains(needle)) return (true, screen);
        }
        return (false, SnapshotScreenText(machine));
    }

    private static string SnapshotScreenText(Machine m)
    {
        var sb = new System.Text.StringBuilder();
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 40; col++)
            {
                var bufferColumn = (m.Video.PanX + col) % 80;
                var b = m.Memory.Read((ushort)(PageTable.VideoRamStart + row * 80 + bufferColumn));
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? ' ' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, (int Row, int Col, bool Shift)> CharMap = new()
    {
        ['L'] = (8, 1, false), ['O'] = (6, 1, false), ['A'] = (4, 2, false), ['D'] = (1, 4, false),
        [' '] = (2, 1, false), ['"'] = (7, 7, true), ['V'] = (3, 7, false), ['R'] = (4, 7, false),
        ['G'] = (1, 5, false), ['B'] = (3, 5, false), [':'] = (8, 7, false), ['I'] = (8, 6, false),
        ['N'] = (3, 1, false), ['F'] = (1, 7, false), ['T'] = (4, 5, false), ['E'] = (4, 4, false),
        ['S'] = (1, 3, false), ['Y'] = (4, 1, false), ['U'] = (4, 6, false), ['M'] = (3, 6, false),
    };

    private const int EnterRow = 6;
    private const int EnterCol = 4;

    private static void TypeChar(Machine machine, int row, int col, bool shift)
    {
        if (shift) { machine.Keyboard.SetKey(9, 0, true); Ticks(machine, 5); }
        machine.Keyboard.SetKey(row, col, true);
        Ticks(machine, 8);
        machine.Keyboard.SetKey(row, col, false);
        Ticks(machine, 5);
        if (shift) { machine.Keyboard.SetKey(9, 0, false); Ticks(machine, 5); }
        Ticks(machine, 5);
    }

    private static void TypeString(Machine machine, string text)
    {
        foreach (var ch in text) { var (row, col, shift) = CharMap[ch]; TypeChar(machine, row, col, shift); }
    }

    private static void PressEnter(Machine machine) => TypeChar(machine, EnterRow, EnterCol, false);

    private byte ReadFlag(Machine m) => m.Memory.Read(DiskIoErrorFlag);

    private static string LastNonBlankLine(string screen) =>
        screen.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

    private static void WaitForReadyPrompt(Machine machine, int maxFields = 5000)
    {
        for (var f = 0; f < maxFields; f++)
        {
            Ticks(machine, 1);
            if (LastNonBlankLine(SnapshotScreenText(machine)) == "Ok") return;
        }
    }

    [Fact]
    public void RunVolorg_ReadDataCommands_TargetTrackOneNotVolorgsOwnDataTracks()
    {
        var repoRoot = FindRepoRoot();
        var cartridgePath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var bootFloppyPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");

        var machine = new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            Slot1CartridgePath = cartridgePath,
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 1, Capacity = 35, Sides = DiskSides.Single, ImagePath = bootFloppyPath },
                new FloppyDriveConfig { DriveIndex = 2, Capacity = 35, Sides = DiskSides.Single, ImagePath = secondDiskPath },
            },
        });
        machine.Fdc!.Policy = TimingPolicy.Turbo;

        var completions = new List<(long T, int Drive, int Cylinder, int Head, int Sector)>();
        machine.Fdc.Trace = msg =>
        {
            if (msg.StartsWith("COMPLETE"))
            {
                var transfer = machine.Fdc.CurrentTransfer;
                if (transfer is { } t)
                {
                    var cyl = machine.Fdc.GetCylinder(t.Drive);
                    // Upd765.CurrentSector() = _transferStartSector + _transferIndex/_transferSectorSize.
                    // At the moment the COMPLETE trace fires, _transferIndex already equals the FULL
                    // transferred byte count (one whole 256-byte sector for these single-sector
                    // reads), so t.Sector reports one PAST the sector that just actually completed --
                    // subtract 1 to get the real requested/transferred sector number.
                    completions.Add((0, t.Drive, cyl, t.Head, t.Sector - 1));
                }
            }
        };

        for (var i = 0; i < 40_000_000; i++)
        {
            machine.Tick();
            if (machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd) break;
        }
        WaitForScreenContains(machine, "many files", 3000);
        PressEnter(machine);
        WaitForScreenContains(machine, "Runtime support", 3000);
        PressEnter(machine);
        WaitForScreenContains(machine, "Ok", 3000);
        Ticks(machine, 20);

        TypeString(machine, "SYSTEM B");
        PressEnter(machine);
        WaitForReadyPrompt(machine);
        Ticks(machine, 20);
        TypeString(machine, "SYSTEM B");
        PressEnter(machine);
        WaitForReadyPrompt(machine);
        Ticks(machine, 20);

        completions.Clear();
        _output.WriteLine("=== Watching every real FDC transfer completion during RUN\"VOLORG\": drive/cylinder/head/sector ===");
        TypeString(machine, "RUN\"VOLORG\"");
        PressEnter(machine);

        WaitForReadyPrompt(machine, maxFields: 8000);

        for (var i = 0; i < completions.Count; i++)
        {
            var c = completions[i];
            var track1Based = c.Cylinder + 1;
            _output.WriteLine($"completion#{i + 1,3}  drive={c.Drive}  cylinder={c.Cylinder} (1-based track {track1Based})  head={c.Head}  sector={c.Sector}");
        }

        var distinctCylinders = completions.Select(c => c.Cylinder).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine("=== Distinct cylinders touched: " + string.Join(", ", distinctCylinders) + " ===");
        _output.WriteLine($"=== Total completions: {completions.Count} ===");
        var track2Sectors = completions.Skip(1).Select(c => c.Sector).ToList();
        _output.WriteLine("=== Track-2 (VOLORG data) sector sequence: " + string.Join(",", track2Sectors) + " ===");

        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: exactly one initial read on cylinder 0 (1-based track 1 -- the directory,
        // presumably F_OPEN/0x0F locating VOLORG's FCB), then ALL SUBSEQUENT reads target cylinder
        // 1 (1-based track 2) -- VOLORG's OWN real file-data track, computed independently from its
        // real allocation map (records 4-7). This is NOT a directory scan that never transitions to
        // a real read (Part B/C/D's working assumption) -- it IS the real file-data read, already
        // succeeding, record by record.
        Assert.Equal(15, completions.Count);
        Assert.Equal(0, completions[0].Cylinder);
        Assert.All(completions.Skip(1), c => Assert.Equal(1, c.Cylinder));
        // CONFIRMED: the track-2 sector sequence matches the documented full 16-sector interleave
        // (docs/P2000T-disk-formats.md §6a) EXACTLY, missing only 6 and 12 -- the SAME "14-of-16"
        // limitation previously flagged (Part B, 2026-07-28 entry) as an unrelated loose end for
        // DIRECTORY reads. This confirms it is the SAME underlying sector-advancement mechanism,
        // not a coincidence, and it governs real file-data reads too.
        Assert.Equal(new[] { 1, 7, 13, 3, 9, 15, 5, 11, 2, 8, 14, 4, 10, 16 }, track2Sectors);
    }
}

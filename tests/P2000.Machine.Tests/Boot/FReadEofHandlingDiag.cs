using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part E addendum (cc-bugfix-prompt-12 addendum), item 2: capture CP/M's CR (current record,
/// FCB offset+0x20) and RC (record count, FCB offset+0x0F) at every real <c>sub_f137h</c>
/// (0x14/F_READ) entry, plus the actual EOF-equivalent result (<c>lf582h</c>) at return, to
/// determine whether F_READ ever legitimately signals EOF for VOLORG's own FCB, and if so what
/// happens immediately afterward -- clean EOF handling, or a fall-through into the busy-wait.
///
/// Static disassembly of <c>sub_f137h</c> (<c>docs/PDOS_wip.asm</c>, read but not edited):
/// compares CR against RC (<c>sub_ec39h</c> reads both); if CR &lt; RC, jumps straight to issuing
/// the next physical read (<c>lf15fh</c> -&gt; ... -&gt; <c>lf170h</c> -&gt; <c>Seek_to_track</c>/
/// <c>sub_e8b3h</c>); if CR &gt;= RC, unconditionally sets <c>lf582h = 1</c> (the EOF-equivalent
/// result that flows into the actual return value via <c>sub_f2fdh</c>'s own <c>lf3fah</c>
/// epilogue) and returns immediately without reading anything further.
///
/// CONFIRMED, decisively: CR advances from 0 to only 13 across the entire attempt (27 sub_f137h
/// entries observed, RC constant at 0x2C=44 the whole time) -- CR NEVER reaches RC. CP/M's own
/// standard EOF condition is NEVER triggered for this repro. The busy-wait/"Disk I/O error" that
/// eventually fires is NOT a consequence of end-of-file being mishandled -- there IS no EOF here.
/// The real stopping point is confirmed elsewhere (<c>ReadDataPhysicalTrackDiag.cs</c>) to be the
/// same "14-of-16 sectors" physical-sector-advancement limit already flagged as an unrelated loose
/// end in Part B (2026-07-28 entry) for directory reads -- now confirmed to also govern real
/// file-data reads. Once that limit is hit, no further FDC command is ever issued (regardless of
/// CR/RC), and execution falls into the busy-wait exactly as Parts B/C/D established.
/// </summary>
public class FReadEofHandlingDiag
{
    private readonly ITestOutputHelper _output;
    public FReadEofHandlingDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Handler_0x14_Entry_SubF137h = 0xF137;
    private const ushort CurrentFcbPointerCell_0xf579 = 0xF579;
    private const ushort ResultFlag_lf582h = 0xF582;

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

    private static ushort ReadFcbAddr(Machine machine, ushort fcbPointerCell)
    {
        var lo = machine.Memory.Read(fcbPointerCell);
        var hi = machine.Memory.Read((ushort)(fcbPointerCell + 1));
        return (ushort)((hi << 8) | lo);
    }

    [Fact]
    public void RunVolorg_CrNeverReachesRc_StandardCpmEofIsNeverTriggered()
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

        TypeString(machine, "RUN\"VOLORG\"");
        PressEnter(machine);

        _output.WriteLine("=== Watching every sub_f137h (0x14/F_READ) entry: CR, RC, and the return-flag lf582h ===");
        ushort? lastPc = null;
        var entryCount = 0;
        var maxCr = 0;
        var rcValuesSeen = new HashSet<int>();
        var eofFlagEverSetDuringScan = false;

        for (long t = 0; t < 20_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (pc == Handler_0x14_Entry_SubF137h && pc != lastPc)
            {
                entryCount++;
                var fcbAddr = ReadFcbAddr(machine, CurrentFcbPointerCell_0xf579);
                var cr = machine.Memory.Read((ushort)(fcbAddr + 0x20));
                var rc = machine.Memory.Read((ushort)(fcbAddr + 0x0F));
                var resultBefore = machine.Memory.Read(ResultFlag_lf582h);
                if (fcbAddr != 0)
                {
                    maxCr = Math.Max(maxCr, cr);
                    rcValuesSeen.Add(rc);
                    if (resultBefore != 0) eofFlagEverSetDuringScan = true;
                }
                _output.WriteLine($"F_READ-entry#{entryCount,3} t={t,10}  FCB=0x{fcbAddr:X4}  CR=0x{cr:X2}({cr})  RC=0x{rc:X2}({rc})  lf582h-before=0x{resultBefore:X2}");
            }

            lastPc = pc;
        }

        _output.WriteLine($"=== Total F_READ (sub_f137h) entries observed: {entryCount} ===");
        var finalFcbAddr = ReadFcbAddr(machine, CurrentFcbPointerCell_0xf579);
        var finalCr = machine.Memory.Read((ushort)(finalFcbAddr + 0x20));
        var finalRc = machine.Memory.Read((ushort)(finalFcbAddr + 0x0F));
        var finalResult = machine.Memory.Read(ResultFlag_lf582h);
        _output.WriteLine($"=== Final state: FCB=0x{finalFcbAddr:X4} CR=0x{finalCr:X2}({finalCr}) RC=0x{finalRc:X2}({finalRc}) lf582h=0x{finalResult:X2} ===");
        _output.WriteLine($"=== Max CR observed while FCB pointer was valid: {maxCr} ===");
        _output.WriteLine($"=== RC values observed while FCB pointer was valid: {string.Join(",", rcValuesSeen)} ===");
        _output.WriteLine($"=== Was the EOF-equivalent flag (lf582h) ever set during the scan: {eofFlagEverSetDuringScan} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: RC stays constant at 44 the whole time (VOLORG's own real record count,
        // never a stale/corrupt value), and CR only ever reaches 13 -- nowhere near RC. CP/M's own
        // EOF condition (CR >= RC) is NEVER triggered for this repro.
        Assert.True(entryCount > 0);
        Assert.Equal(new[] { 44 }, rcValuesSeen.ToArray());
        Assert.True(maxCr < 44, $"expected CR to never reach RC=44 in this repro; observed max CR={maxCr}");
        Assert.False(eofFlagEverSetDuringScan, "expected the EOF-equivalent flag never to be set while the FCB pointer was valid -- the busy-wait is not an EOF-handling bug");
    }
}

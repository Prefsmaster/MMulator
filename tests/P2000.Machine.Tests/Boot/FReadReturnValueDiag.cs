using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part H (cc-bugfix-prompt-14) — decisive check: the second loop counter
/// (<c>[pointer+0x24..25]</c>) never changes during the whole <c>RUN"VOLORG"</c> attempt (stays
/// at 256 throughout, confirmed via <c>SecondLoopCounterLiveTraceDiag.cs</c>'s corrected trace),
/// so it cannot be what triggers <c>0x326C</c>'s own zero-check exit path.
///
/// This checks the other plausible source of a graceful exit: register A, F_READ's own return
/// value, right after <c>CALL 6205h</c> returns at 0x32A8 (landing at 0x32AB). Working through
/// the actual branch polarity at 0x32B0-0x3276 (not just "A==1 causes some exit" as a first
/// glance might suggest): <c>32B0: DEC A</c> only sets Z if A WAS exactly 1; since A is confirmed
/// ALWAYS 0 here, <c>DEC A</c> gives 0xFF (Z clear), so the jump to 32BA is NOT taken -- it falls
/// through to <c>LD DE,0100h</c>, and the resulting <c>OR</c> at 32C2-32C3 leaves A nonzero (NZ),
/// meaning <c>3276: JP NZ,323Ch</c> IS taken -- i.e., A=0 makes the loop CONTINUE, not exit. So
/// even if this were the real exit check, A=0 (confirmed here on every real return) could never
/// trigger it; only A=1 (never observed) would.
/// </summary>
public class FReadReturnValueDiag
{
    private readonly ITestOutputHelper _output;
    public FReadReturnValueDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort FReadCallSite_0x32A8 = 0x32A8;
    private const ushort FReadReturnAddr_0x32AB = 0x32AB;

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

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own name/count (\"only 13 of 14 " +
        "returns observed\") pinned the CONFIRMED BUG's own symptom -- the 14th F_READ call's own " +
        "CALL 6205h never returning. Part I fixed the root cause (Upd765.DeferNaturalCompletion) " +
        "-- every F_READ call now returns normally, and VOLORG.BAS loads and runs successfully. " +
        "See CLAUDE.md's Part I entry and FourteenthOperationRedirectDiag.cs. Retained, skipped, " +
        "for historical/investigative record only.")]
    public void RunVolorg_FReadAlwaysReturnsZero_Never1_AndOnly13Of14ReturnsAreObserved()
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

        _output.WriteLine("=== Watching register A at every return from CALL 6205h (F_READ) at 0x32A8 ===");
        ushort? lastPc = null;
        var results = new List<(long T, byte A)>();

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == FReadReturnAddr_0x32AB && pc != lastPc)
            {
                var a = machine.Cpu.Reg.A;
                results.Add((t, a));
                _output.WriteLine($"F_READ-return#{results.Count,3}  t={t,10}  A=0x{a:X2}({a})");
            }
            lastPc = pc;
        }

        _output.WriteLine($"=== Total F_READ returns observed at 0x32AB: {results.Count} ===");
        _output.WriteLine("=== Distinct A values seen: " + string.Join(", ", results.Select(r => r.A).Distinct().OrderBy(x => x).Select(v => $"0x{v:X2}")) + " ===");
        _output.WriteLine($"=== Was A==1 (standard CP/M EOF) ever observed: {results.Any(r => r.A == 1)} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: A is always 0 (standard CP/M success) on every observed F_READ return, never
        // 1 (EOF). Combined with the disproven branch-polarity reasoning above, this rules out
        // the EOF-check path as the loop's real exit mechanism. Also notable, reconciled in the
        // findings-log entry: only 13 genuine cycles' worth of returns are ever observed here
        // (matching Part G's own confirmed 13 byte-scan cycles), even though Part E confirmed 14
        // real physical disk completions -- the 14th real F_READ call's own CALL 6205h never
        // returns to this call site at all.
        // 27 = 13 genuine returns doubled by a PC-fetch-timing artifact plus one unpaired --
        // the same class of false-positive-PC-hit gotcha already found twice elsewhere in this
        // investigation (Part C/E). The genuine count is 13, not 14 or 27; kept as an exact
        // regression pin since the doubling pattern itself is deterministic for this repro.
        Assert.Equal(27, results.Count);
        Assert.All(results, r => Assert.Equal(0, r.A));
        Assert.False(results.Any(r => r.A == 1));
    }
}

using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part H (cc-bugfix-prompt-14) — traces the SECOND loop counter at
/// <c>[pointer+0x24..0x25]</c> (<c>pointer</c> = the value at fixed cell <c>0x63A3</c>, itself set
/// once at <c>0x37AD</c>), checked at <c>0x326C</c> only once the first, byte-scan counter
/// (<c>[pointer+0x26..0x27]</c>, Part G) empties. Originally hypothesized as the check that
/// actually decides whether the read loop exits (<c>0x3279</c>) or fetches another disk sector
/// (<c>0x327F</c>).
///
/// CONFIRMED, and DISPROVES that hypothesis: this counter is set ONCE, to 256, right when
/// <c>0x63A3</c> is first set, and NEVER CHANGES for the rest of the loop -- it stays at 256 at
/// every single one of 3329 entries to <c>0x323A</c>, including the very last one. It cannot be
/// what governs the loop's real termination, since <c>0x326C</c>'s own check requires it to reach
/// zero. Combined with <c>FReadReturnValueDiag.cs</c> (F_READ's own return value is always 0,
/// never the standard EOF value 1) and <c>LoopExitPathDiag.cs</c> (the OTHER candidate check,
/// <c>[pointer+0]==3</c>, never fires either -- <c>[pointer+0]</c> stays at 1 throughout), ALL
/// THREE plausible graceful-exit mechanisms in this loop's own body are ruled out. See the
/// findings-log entry for the actual reconciled mechanism: the loop does not gracefully exit at
/// all -- it hangs on the 14th real F_READ call, which physically completes at the FDC level
/// (Part E) but never returns to this BASIC-side call site.
/// </summary>
public class SecondLoopCounterLiveTraceDiag
{
    private readonly ITestOutputHelper _output;
    public SecondLoopCounterLiveTraceDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort PointerCell_0x63A3 = 0x63A3;
    private const ushort SecondCounterOffset = 0x24;
    private const ushort FirstCounterOffset = 0x26;
    private const ushort LoopDriverEntry_0x323A = 0x323A;

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

    private static ushort ReadWord(Machine m, ushort addr) =>
        (ushort)((m.Memory.Read((ushort)(addr + 1)) << 8) | m.Memory.Read(addr));

    [Fact]
    public void RunVolorg_SecondCounterStaysAt256_CannotGovernLoopExit()
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

        _output.WriteLine("=== Watching every entry to 0x323A: read pointer(0x63A3) + both counters fresh each time, matching Part G's own proven approach ===");
        ushort? lastPc = null;
        ushort? lastPointer = null;
        ushort? lastSecondCounter = null;
        var loopEntries = new List<(long T, ushort Pointer, ushort Second, ushort First)>();

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (pc == LoopDriverEntry_0x323A && pc != lastPc)
            {
                var p = ReadWord(machine, PointerCell_0x63A3);
                var secondVal = ReadWord(machine, (ushort)(p + SecondCounterOffset));
                var firstVal = ReadWord(machine, (ushort)(p + FirstCounterOffset));
                loopEntries.Add((t, p, secondVal, firstVal));
                if (p != lastPointer)
                {
                    _output.WriteLine($"t={t,10}  pointer(0x63A3)=0x{p:X4}  (changed from {(lastPointer.HasValue ? $"0x{lastPointer.Value:X4}" : "null")})");
                    lastPointer = p;
                }
                if (secondVal != lastSecondCounter)
                {
                    _output.WriteLine($"t={t,10}  pointer=0x{p:X4}  [pointer+0x24..25]=0x{secondVal:X4}({secondVal})  [pointer+0x26..27]=0x{firstVal:X4}({firstVal})  (second changed from {(lastSecondCounter.HasValue ? lastSecondCounter.Value.ToString() : "null")})");
                    lastSecondCounter = secondVal;
                }
            }

            lastPc = pc;
        }

        _output.WriteLine($"=== Total entries to 0x323A: {loopEntries.Count} ===");
        if (loopEntries.Count > 0)
        {
            _output.WriteLine($"=== [pointer+0x24..25] at FIRST 0x323A entry: {loopEntries[0].Second} ===");
            _output.WriteLine($"=== [pointer+0x24..25] at LAST 0x323A entry: {loopEntries[^1].Second} ===");
        }

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: the second counter is set once (to 256) and NEVER changes for the rest of
        // the loop -- it cannot be what governs the loop's real exit condition. See
        // LoopExitPathDiag.cs and FReadReturnValueDiag.cs for the other two hypothesized exit
        // paths, both also disproven, and the findings-log entry for the actual reconciled
        // mechanism (the loop doesn't gracefully exit at all -- it hangs on the 14th real F_READ
        // call, which never returns).
        Assert.True(loopEntries.Count > 0);
        Assert.All(loopEntries, e => Assert.Equal(256, e.Second));
    }
}

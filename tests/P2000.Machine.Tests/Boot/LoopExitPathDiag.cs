using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part H (cc-bugfix-prompt-14) — both previously-hypothesized exit paths from the read loop are
/// now DISPROVEN by direct trace: the second counter (<c>[pointer+0x24..25]</c>) never changes
/// from 256 (<c>SecondLoopCounterLiveTraceDiag.cs</c>), and F_READ's own return value (register A
/// at 0x32AB) is always 0, never the standard CP/M EOF value 1 (<c>FReadReturnValueDiag.cs</c>) --
/// which, worked through the actual branch polarity at 0x32B0/0x3276, means the loop-back branch
/// at 0x3276 (<c>JP NZ,323Ch</c>) is ALWAYS taken when A=0, i.e., this path can only ever
/// continue the loop, never exit it. Yet the loop demonstrably does stop after 13 cycles.
///
/// This checks the ONE remaining unexamined branch in <c>0x323A</c>'s own body: its very first
/// real check, <c>0x3240: CP 03h; 3242: JP Z,8996h</c> -- comparing the first byte of whatever
/// <c>pointer</c> references against 3, jumping to a completely different ROM address (0x8996,
/// not yet identified) if it matches. Traces <c>[pointer+0]</c> directly at every 0x323A entry,
/// and watches for a genuine PC transition to 0x8996.
/// </summary>
public class LoopExitPathDiag
{
    private readonly ITestOutputHelper _output;
    public LoopExitPathDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort PointerCell_0x63A3 = 0x63A3;
    private const ushort LoopDriverEntry_0x323A = 0x323A;
    private const ushort AltExitTarget_0x8996 = 0x8996;

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

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own exact loop-entry count " +
        "(3329) was pinned against the CONFIRMED BUG's behavior (RUN\"VOLORG\" hanging after 14 " +
        "reads). Part I fixed the root cause (Upd765.DeferNaturalCompletion) -- VOLORG.BAS now " +
        "loads and runs successfully, changing how many times this loop is entered. See " +
        "CLAUDE.md's Part I entry and FourteenthOperationRedirectDiag.cs. Retained, skipped, for " +
        "historical/investigative record only.")]
    public void RunVolorg_AltExitCheckNeverFires_PointerFirstByteStaysAt1()
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

        _output.WriteLine("=== Watching [pointer+0] at every 0x323A entry, and any genuine transition to 0x8996 ===");
        ushort? lastPc = null;
        byte? lastFirstByte = null;
        var alt8996Hits = 0;
        var loopEntries = 0;

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (pc == LoopDriverEntry_0x323A && pc != lastPc)
            {
                loopEntries++;
                var p = ReadWord(machine, PointerCell_0x63A3);
                var firstByte = machine.Memory.Read(p);
                if (firstByte != lastFirstByte)
                {
                    _output.WriteLine($"t={t,10}  pointer=0x{p:X4}  [pointer+0]=0x{firstByte:X2}({firstByte})  (changed from {(lastFirstByte.HasValue ? $"0x{lastFirstByte.Value:X2}" : "null")})");
                    lastFirstByte = firstByte;
                }
            }

            if (pc == AltExitTarget_0x8996 && pc != lastPc)
            {
                alt8996Hits++;
                _output.WriteLine($"  *** genuine PC transition to 0x8996 (alt exit target) at t={t} ***");
            }

            lastPc = pc;
        }

        _output.WriteLine($"=== Total 0x323A entries: {loopEntries} ===");
        _output.WriteLine($"=== Total genuine transitions to 0x8996: {alt8996Hits} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: [pointer+0] stays at exactly 1 for the whole loop (never becomes 3), and PC
        // never genuinely transitions to 0x8996. This alt-exit check never fires either -- the
        // third and last plausible graceful-exit mechanism in this loop's own body, ruled out.
        Assert.Equal(3329, loopEntries);
        Assert.Equal(0, alt8996Hits);
    }
}

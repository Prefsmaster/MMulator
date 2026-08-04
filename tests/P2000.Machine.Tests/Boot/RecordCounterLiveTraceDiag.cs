using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part G (owner follow-up, 2026-08-04) — live-traces the BASIC-side counter found by
/// disassembling the RUN token's own read loop (<c>RunTokenReadLoopDisasmDiag.cs</c>): a 2-byte
/// value at <c>[(0x63A3) + 0x26]</c> (where <c>0x63A3</c> itself is a pointer, set once at
/// <c>0x37AD</c> from <c>(0x63B1)</c>, right before the read loop begins at <c>0x37BD</c>).
///
/// CORRECTS an initial working hypothesis: this is NOT a "records remaining" counter decremented
/// once per disk sector. Live tracing shows it decrements ONCE PER BYTE, cycling 256-&gt;0
/// repeatedly (0x323A is entered ~3300 times total, far more than the 14 real disk reads) --
/// i.e., BASIC is byte-scanning the loaded program through a 256-byte sliding buffer, refilled
/// via a real disk read only once each 256-byte cycle empties. CONFIRMED: exactly 13 full
/// 256-byte cycles occur before the loop stops (not 14) -- the counter's own zero crossing is not
/// what directly explains "14 disk reads"; a SEPARATE 2-byte counter at
/// <c>[pointer+0x24..0x25]</c> (checked only once this byte-buffer counter is empty, at 0x326C)
/// governs the loop's real exit and has NOT yet been live-traced.
/// </summary>
public class RecordCounterLiveTraceDiag
{
    private readonly ITestOutputHelper _output;
    public RecordCounterLiveTraceDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort PointerCell_0x63A3 = 0x63A3;
    private const ushort CounterOffset = 0x26;
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

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own count (\"13 full cycles then " +
        "stops\") pinned the CONFIRMED BUG's own symptom. Part I fixed the root cause " +
        "(Upd765.DeferNaturalCompletion) -- VOLORG.BAS now loads and runs successfully, so the " +
        "loop runs many more than 13 cycles. See CLAUDE.md's Part I entry and " +
        "FourteenthOperationRedirectDiag.cs. Retained, skipped, for historical/investigative " +
        "record only.")]
    public void RunVolorg_ByteBufferCounter_Runs13FullCyclesThenStops()
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

        _output.WriteLine("=== Watching entries to 0x323A (loop driver), reading the pointer + counter each time ===");
        ushort? lastPc = null;
        ushort? lastCounterValue = null;
        var observations = new List<(long T, ushort Pointer, ushort CounterAddr, ushort CounterValue)>();

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == LoopDriverEntry_0x323A && pc != lastPc)
            {
                var pointer = ReadWord(machine, PointerCell_0x63A3);
                var counterAddr = (ushort)(pointer + CounterOffset);
                var counterValue = ReadWord(machine, counterAddr);
                observations.Add((t, pointer, counterAddr, counterValue));
                if (counterValue != lastCounterValue)
                {
                    _output.WriteLine($"t={t,10}  pointer(0x63A3)=0x{pointer:X4}  counterAddr=0x{counterAddr:X4}  counterValue=0x{counterValue:X4}({counterValue})");
                    lastCounterValue = counterValue;
                }
            }
            lastPc = pc;
        }

        _output.WriteLine($"=== Total entries to 0x323A: {observations.Count} ===");
        if (observations.Count > 0)
        {
            _output.WriteLine($"=== First observed counter value: {observations[0].CounterValue} ===");
            _output.WriteLine($"=== Last observed counter value: {observations[^1].CounterValue} ===");
        }

        // A "cycle" is a run of strictly-decreasing counter values ending at 0, followed by a
        // jump back up (the buffer being refilled by a real disk read). Count cycles by counting
        // 0->nonzero transitions in the deduplicated value sequence, plus the final cycle if it
        // ends the whole sequence at exactly 0.
        var distinctValues = observations.Select(o => o.CounterValue).Distinct().ToList();
        var wasReduced = new List<ushort>();
        ushort? prev = null;
        foreach (var o in observations)
        {
            if (o.CounterValue != prev) wasReduced.Add(o.CounterValue);
            prev = o.CounterValue;
        }
        var cycleStarts = 0;
        for (var i = 1; i < wasReduced.Count; i++)
        {
            if (wasReduced[i] > wasReduced[i - 1]) cycleStarts++;
        }
        var totalCycles = cycleStarts + 1; // the initial cycle plus each restart
        _output.WriteLine($"=== Total full 256-byte scan cycles: {totalCycles} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: the counter decrements once per BYTE (not once per sector), starts at 256,
        // and runs through exactly 13 full 256-byte cycles (3328 bytes total) before the loop
        // stops -- the LAST observed value is exactly 0, not a partial cycle. This corrects the
        // original "records remaining, decremented per sector" hypothesis.
        Assert.Equal(256, observations[0].CounterValue);
        Assert.Equal(0, observations[^1].CounterValue);
        Assert.Equal(13, totalCycles);
    }
}

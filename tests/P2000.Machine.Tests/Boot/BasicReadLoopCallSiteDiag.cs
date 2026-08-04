using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part G (owner follow-up, 2026-08-04) — continuing past Part F's confirmation that PDOS's own
/// sector-advancement code is passive and uncapped: finds the exact BASIC-side call site(s) that
/// issue each of the 14 real 0x1A(F_DMAOFF)/0x14(F_READ) pairs during <c>RUN"VOLORG"</c>, to
/// locate the loop that decides to stop after the 14th pair.
///
/// BASIC's own fixed entry point into PDOS is <c>0x6205</c> (per
/// <c>docs/PDOS-notes-for-annotation.md</c> §1: <c>CALL &amp;H6205 -&gt; JP 6934 -&gt; JP 696D -&gt;
/// JP 0005h</c> -&gt; lands at <c>CPM_entry_point</c>, 0xE000). This watches every genuine CALL to
/// 0x6205 and records the return address -- i.e., exactly which BASIC-side instruction issued
/// each PDOS invocation -- to identify the calling loop's own address range.
///
/// CONFIRMED: exactly 3 fixed call sites, all in the CARTRIDGE ROM (<c>Basic-24.bin</c>), NOT the
/// disk-loaded chunk: 0x3487 (F_OPEN, once), 0x32A8 (F_READ, 14x), 0x32D0 (F_DMAOFF, 14x). See
/// <c>RunTokenReadLoopDisasmDiag.cs</c> for the disassembly of the loop these two repeated call
/// sites belong to.
/// </summary>
public class BasicReadLoopCallSiteDiag
{
    private readonly ITestOutputHelper _output;
    public BasicReadLoopCallSiteDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Basic_PdosEntry_0x6205 = 0x6205;

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

    private sealed record Entry(long T, ushort ReturnAddr, ushort CallSite, byte C);

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own exact call counts (F_READ/" +
        "F_DMAOFF x14) pinned the CONFIRMED BUG's own symptom -- RUN\"VOLORG\" stopping after 14 " +
        "reads. Part I fixed the root cause (Upd765.DeferNaturalCompletion) -- VOLORG.BAS now " +
        "loads and runs successfully with far more than 14 F_READ/F_DMAOFF calls. See CLAUDE.md's " +
        "Part I entry and FourteenthOperationRedirectDiag.cs. Retained, skipped, for historical/" +
        "investigative record only.")]
    public void RunVolorg_ThreeFixedCartridgeRomCallSitesIssueAllPdosCalls()
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

        _output.WriteLine("=== Watching every genuine CALL to 0x6205 (BASIC's own fixed PDOS entry point) ===");
        ushort? lastPc = null;
        var entries = new List<Entry>();

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == Basic_PdosEntry_0x6205 && pc != lastPc)
            {
                var sp = machine.Cpu.Reg.SP;
                var retLo = machine.Memory.Read(sp);
                var retHi = machine.Memory.Read((ushort)(sp + 1));
                var ret = (ushort)((retHi << 8) | retLo);
                var callSite = (ushort)(ret - 3);
                var b0 = machine.Memory.Read(callSite);
                var b1 = machine.Memory.Read((ushort)(callSite + 1));
                var b2 = machine.Memory.Read((ushort)(callSite + 2));
                if (b0 == 0xCD && b1 == 0x05 && b2 == 0x62)
                {
                    entries.Add(new Entry(t, ret, callSite, machine.Cpu.Reg.C));
                }
            }
            lastPc = pc;
        }

        _output.WriteLine($"=== Total genuine calls to 0x6205: {entries.Count} ===");
        foreach (var e in entries)
        {
            _output.WriteLine($"  t={e.T,10}  callSite=0x{e.CallSite:X4}  return=0x{e.ReturnAddr:X4}  C=0x{e.C:X2}");
        }
        var distinctCallSites = entries.Select(e => e.CallSite).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine("=== Distinct BASIC-side call sites: " + string.Join(", ", distinctCallSites.Select(a => $"0x{a:X4}")) + " ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: exactly 3 fixed BASIC-side (cartridge ROM) call sites into 0x6205 for the
        // whole RUN"VOLORG" attempt -- 0x3487 (C=0x0F/F_OPEN, once), 0x32A8 (C=0x14/F_READ, 14x),
        // 0x32D0 (C=0x1A/F_DMAOFF, 14x). This is the exact origin of every PDOS invocation during
        // this repro, all within the cartridge ROM (Basic-24.bin), not the disk-loaded chunk.
        Assert.Equal(29, entries.Count);
        Assert.Equal(new ushort[] { 0x32A8, 0x32D0, 0x3487 }, distinctCallSites);
        Assert.Equal(1, entries.Count(e => e.CallSite == 0x3487 && e.C == 0x0F));
        Assert.Equal(14, entries.Count(e => e.CallSite == 0x32A8 && e.C == 0x14));
        Assert.Equal(14, entries.Count(e => e.CallSite == 0x32D0 && e.C == 0x1A));
    }
}

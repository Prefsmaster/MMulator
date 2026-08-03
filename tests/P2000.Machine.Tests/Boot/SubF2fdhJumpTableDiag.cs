using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part D (cc-bugfix-prompt-11) — a direct follow-up to Part C's own caveat: Part C's account of
/// <c>le12dh</c> → <c>le149h</c> → <c>sub_e705h</c> → <c>sub_f2fdh</c> as "the path 0x1A/0x14
/// take" was cited from <c>docs/PDOS_wip.asm</c>'s static byte patterns, NOT confirmed by watching
/// the PC actually walk that path during a real <c>RUN"VOLORG"</c> repro. This test closes that
/// gap with the same live-execution-trace rigor already applied to the dispatcher and
/// <c>sub_e943h</c> (<c>DispatchFunctionCodeTraceDiag.cs</c>/<c>Sube943hCallerDiag.cs</c>).
///
/// CONFIRMED (see the dated findings-log entry for the full write-up): execution DOES reach
/// <c>sub_f2fdh</c> (0xF2FD), verified via the genuine-CALL-bytes check (not a bare PC match --
/// the same discipline <c>Sube943hCallerDiag.cs</c>'s own RET-fetch-artifact gotcha demands): 30
/// genuine calls for a real <c>RUN"VOLORG"</c> attempt.
///
/// This CORRECTS Part C's own speculative account, which assumed 0x14/0x1A shared a single
/// handler: they do NOT. Three distinct routing codes reach here (0x0F ×1, 0x14 ×14, 0x1A ×15 --
/// a superset of Part C's own 29-entry CPM_entry_point trace, meaning at least one call here
/// bypasses BASIC's own top-level dispatch entirely), and each lands on its OWN distinct
/// jump-table target, computed as <c>lf307h + 2*code</c> (0xF307-based): 0x0F -> 0xF370, 0x14 ->
/// 0xF3A0, 0x1A -> 0xF3CA. Every one of the 30 computed targets was independently reconfirmed by
/// directly observing the CPU's own PC actually reach that address after the jump (not just a
/// static memory read of the table entry).
/// </summary>
public class SubF2fdhJumpTableDiag
{
    private readonly ITestOutputHelper _output;
    public SubF2fdhJumpTableDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Sub_f2fdh = 0xF2FD;
    private const ushort JumpTableBase_lf307h = 0xF307;
    private const ushort JpHl_Instruction = 0xF320;
    private const ushort RoutingCodeCell_0xf578 = 0xF578;

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

    private sealed record Entry(long T, byte Code, ushort TableSlot, ushort TableTarget, ushort? ObservedJumpTarget);

    [Fact]
    public void RunVolorg_ReachesSubF2fdh_WithThreeDistinctCodesEachLandingOnItsOwnTarget()
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

        _output.WriteLine("=== Watching for GENUINE entries to sub_f2fdh (0xF2FD) via a real CALL, ===");
        _output.WriteLine("=== then cross-checking the jump-table target two ways: memory read + observed PC jump ===");

        ushort? lastPc = null;
        var entries = new List<Entry>();
        // Track, for a short window after each genuine sub_f2fdh entry, whether PC is later
        // observed to equal the computed jump-table target (independent confirmation beyond the
        // static memory read).
        var pendingChecks = new List<(int Index, long ExpireAtT, ushort ExpectedTarget)>();

        for (long t = 0; t < 20_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (pc == Sub_f2fdh && pc != lastPc)
            {
                var sp = machine.Cpu.Reg.SP;
                var returnAddrLo = machine.Memory.Read(sp);
                var returnAddrHi = machine.Memory.Read((ushort)(sp + 1));
                var returnAddr = (ushort)((returnAddrHi << 8) | returnAddrLo);
                var callSite = (ushort)(returnAddr - 3);
                var b0 = machine.Memory.Read(callSite);
                var b1 = machine.Memory.Read((ushort)(callSite + 1));
                var b2 = machine.Memory.Read((ushort)(callSite + 2));
                var isGenuineCall = b0 == 0xCD && b1 == 0xFD && b2 == 0xF2;

                if (isGenuineCall)
                {
                    var code = machine.Memory.Read(RoutingCodeCell_0xf578);
                    var tableSlot = (ushort)(JumpTableBase_lf307h + 2 * code);
                    var lo = machine.Memory.Read(tableSlot);
                    var hi = machine.Memory.Read((ushort)(tableSlot + 1));
                    var tableTarget = (ushort)((hi << 8) | lo);

                    entries.Add(new Entry(t, code, tableSlot, tableTarget, null));
                    pendingChecks.Add((entries.Count - 1, t + 2000, tableTarget));

                    _output.WriteLine($"entry#{entries.Count,3} t={t,10}  code=0x{code:X2}  table-slot=0x{tableSlot:X4}  table-target=0x{tableTarget:X4}");
                }
            }

            if (pendingChecks.Count > 0)
            {
                for (var i = pendingChecks.Count - 1; i >= 0; i--)
                {
                    var (index, expireAtT, expectedTarget) = pendingChecks[i];
                    if (pc == expectedTarget)
                    {
                        entries[index] = entries[index] with { ObservedJumpTarget = pc };
                        pendingChecks.RemoveAt(i);
                    }
                    else if (t >= expireAtT)
                    {
                        pendingChecks.RemoveAt(i);
                    }
                }
            }

            lastPc = pc;
        }

        _output.WriteLine($"=== Total genuine entries to sub_f2fdh: {entries.Count} ===");
        var distinctCodes = entries.Select(e => e.Code).Distinct().OrderBy(x => x).ToList();
        var distinctTargets = entries.Select(e => e.TableTarget).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine("=== Distinct routing codes seen at sub_f2fdh: " + string.Join(", ", distinctCodes.Select(c => $"0x{c:X2}")) + " ===");
        _output.WriteLine("=== Distinct jump-table targets landed on: " + string.Join(", ", distinctTargets.Select(t2 => $"0x{t2:X4}")) + " ===");
        var confirmedCount = entries.Count(e => e.ObservedJumpTarget.HasValue);
        _output.WriteLine($"=== Entries where PC was independently observed to reach the computed target: {confirmedCount}/{entries.Count} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: execution DOES reach sub_f2fdh (not "zero, unreached").
        Assert.True(entries.Count > 0, "expected RUN\"VOLORG\" to reach sub_f2fdh at least once");
        // CONFIRMED: every code reaching sub_f2fdh is one of the three actually observed here --
        // 0x0F, 0x14, 0x1A. This is a SUPERSET of Part C's own dispatcher-level trace (which only
        // watched CPM_entry_point, 0xE000, and saw exactly these three codes) -- consistent, but
        // NOT the same call count: this test found 30 genuine sub_f2fdh entries against Part C's
        // own 29 CPM_entry_point entries, meaning at least one call here does not originate from
        // BASIC's own top-level PDOS invocation (most likely sub_e706h's alternate entry point,
        // called directly by PDOS's own internal code) -- flagged, not explained away.
        Assert.All(distinctCodes, c => Assert.Contains(c, new byte[] { 0x0F, 0x14, 0x1A }));
        // CORRECTS Part C's own speculative account ("both routed function codes land on the same
        // jump-table target") -- confirmed here to be FALSE. Each of the three codes lands on its
        // OWN distinct target: exactly 3 distinct targets for 3 distinct codes, not 1 shared
        // handler.
        Assert.Equal(3, distinctTargets.Count);
        Assert.Equal(3, distinctCodes.Count);
        // CONFIRMED via independent PC observation, not just the static memory read -- every
        // single genuine entry's computed target was later actually reached by the CPU.
        Assert.Equal(entries.Count, confirmedCount);
    }
}

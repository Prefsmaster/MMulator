using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part C (cc-bugfix-prompt-10) — CONFIRMED at the instruction level: of the four call sites in
/// <c>docs/PDOS_wip.asm</c> reaching <c>sub_e943h</c> (0xE943 — the confirmed "set the wrapper's
/// always-error class + disable the FDC" routine) — 0xE0EE (the function-0x0F "FCB extension ==
/// 'BAS'?" check), 0xE978/0xE97D (<c>channel_time_out</c>, reached from
/// <c>busy_wait_for_interrupt</c>'s own natural 65536-iteration timeout — the mechanism Part B
/// already traced via T-state/PC-range evidence), and 0xEA6B (<c>lea6ah</c>, the third
/// interrupt-vector payload) — a live <c>RUN"VOLORG"</c> attempt hits EXACTLY ONE of them, exactly
/// once: 0xE978. This precisely reconfirms Part B's own conclusion with call-site-level ground
/// truth and rules out the 0x0F/"BAS" branch and the <c>lea6ah</c> path as the cause.
///
/// GOTCHA found and fixed while building this (kept in the code as a guard): naively checking
/// <c>PC == 0xE943</c> alone gives 30 FALSE POSITIVES for every 1 real call in this exact repro.
/// <c>sub_e937h</c>'s own <c>ret</c> instruction sits at 0xE942, immediately before
/// <c>sub_e943h</c>'s label — Z80 cores bump PC past a fetched single-byte opcode before that
/// opcode's own semantics (here, the stack pop a RET performs) complete, so PC transiently reads
/// 0xE943 for several T-states on every return from <c>sub_e937h</c>, with SP still pointing at
/// the not-yet-popped return address (which is why the raw stack read initially looked like calls
/// from 0xE8F7/0xE96F — both real, but unrelated, call sites for
/// <c>Get_7_disk_status_bytes</c>/<c>sub_e937h</c>, not for <c>sub_e943h</c> at all). Fixed by
/// verifying the actual 3 bytes at <c>returnAddr-3</c> decode to <c>CD 43 E9</c> (a genuine
/// <c>CALL 0xE943</c>) before counting a hit.
/// </summary>
public class Sube943hCallerDiag
{
    private readonly ITestOutputHelper _output;
    public Sube943hCallerDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Sub_e943h = 0xE943;

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

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test pinned the CONFIRMED BUG's own " +
        "symptom (busy_wait_for_interrupt's natural timeout calling sub_e943h exactly once, via " +
        "channel_time_out). Part I fixed the root cause (Upd765.DeferNaturalCompletion) -- " +
        "RUN\"VOLORG\" now loads and runs VOLORG.BAS successfully, with no timeout and no call to " +
        "sub_e943h at all. See CLAUDE.md's Part I entry and FourteenthOperationRedirectDiag.cs. " +
        "Retained, skipped, for historical/investigative record only.")]
    public void RunVolorg_ReachesSubE943hExactlyOnce_ViaChannelTimeOutOnly()
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

        _output.WriteLine("=== Watching every GENUINE entry to sub_e943h (0xE943) via a real CALL instruction ===");
        _output.WriteLine("=== (PC==0xE943 alone is a false-positive trap: sub_e937h's own RET sits at 0xE942, ===");
        _output.WriteLine("===  and Z80 cores bump PC past a fetched opcode before RET's pop completes, so PC ===");
        _output.WriteLine("===  transiently reads 0xE943 on every return from sub_e937h. Verify via the actual ===");
        _output.WriteLine("===  CALL bytes (CD 43 E9) at returnAddr-3, not PC alone.) ===");
        ushort? lastPc = null;
        var falsePositiveCount = 0;
        var genuineCallSites = new List<ushort>();

        for (long t = 0; t < 20_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == Sub_e943h && pc != lastPc)
            {
                var sp = machine.Cpu.Reg.SP;
                var returnAddrLo = machine.Memory.Read(sp);
                var returnAddrHi = machine.Memory.Read((ushort)(sp + 1));
                var returnAddr = (ushort)((returnAddrHi << 8) | returnAddrLo);
                var callSite = (ushort)(returnAddr - 3);
                var b0 = machine.Memory.Read(callSite);
                var b1 = machine.Memory.Read((ushort)(callSite + 1));
                var b2 = machine.Memory.Read((ushort)(callSite + 2));
                var isGenuineCall = b0 == 0xCD && b1 == 0x43 && b2 == 0xE9;
                if (isGenuineCall)
                {
                    genuineCallSites.Add(callSite);
                    _output.WriteLine($"call#{genuineCallSites.Count} t={t,12}  return-address=0x{returnAddr:X4}  (REAL CALL site 0x{callSite:X4})  flag(6091)=0x{ReadFlag(machine):X2}  bank={machine.Memory.CurrentBank}");
                }
                else
                {
                    falsePositiveCount++;
                    if (falsePositiveCount <= 3 || falsePositiveCount % 500 == 0)
                        _output.WriteLine($"  (false-positive #{falsePositiveCount} at t={t}: PC==0xE943 but bytes at 0x{callSite:X4} are {b0:X2} {b1:X2} {b2:X2}, not CD 43 E9 -- RET-fetch artifact, ignored)");
                }
            }
            lastPc = pc;
        }
        _output.WriteLine($"=== Total false-positive PC==0xE943 hits (RET-fetch artifact, not real calls): {falsePositiveCount} ===");
        _output.WriteLine($"=== Total GENUINE calls to sub_e943h: {genuineCallSites.Count} ===");

        // CONFIRMED: exactly one genuine call, from channel_time_out (0xE978) -- the busy-wait
        // timeout path Part B already traced via T-state evidence. Neither the 0x0F/"BAS" branch
        // (0xE0EE) nor the lea6ah third path (0xEA6B) ever fires for this repro.
        Assert.Single(genuineCallSites);
        Assert.Equal((ushort)0xE978, genuineCallSites[0]);

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));
    }
}

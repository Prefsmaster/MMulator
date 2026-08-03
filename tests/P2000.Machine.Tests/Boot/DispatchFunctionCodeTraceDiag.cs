using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part C (cc-bugfix-prompt-10), continuing past <c>Sube943hCallerDiag</c>'s confirmation that
/// <c>sub_e943h</c> fires exactly once, via <c>channel_time_out</c> (0xE978), reached from
/// <c>busy_wait_for_interrupt</c>'s own natural 65536-iteration timeout (matching Part B's
/// finding exactly, at the instruction level).
///
/// This watches every entry to the PDOS dispatcher itself (<c>CPM_entry_point</c>, 0xE000) and
/// logs the function code BASIC passes in (register C — NOT A; the dispatcher's own first
/// instructions do <c>ld a,c</c> AFTER this test's entry point, so A isn't populated yet at the
/// exact moment PC==0xE000) for a real <c>RUN"VOLORG"</c> attempt.
///
/// CONFIRMED: only codes 0x0F/0x1A/0x14 are EVER dispatched, across the entire attempt — 0x39 (a
/// candidate "convert an FCB allocation-map record into a real read track" handler,
/// <c>docs/PDOS_wip.asm</c> line ~2229/0xE229 — reads a value via <c>(ix_pointer)</c>, converts it
/// via <c>sub_eb23h</c>, stores the result into <c>RW_cmd_track</c>) never fires. Both 0x1A and
/// 0x14 route to the SAME handler (<c>sub_e705h</c> → <c>sub_f2fdh</c>, 0xF2FD) — a SECOND,
/// internal jump table indexed by the identical function code, computed as
/// <c>lf307h + 2*code</c>. This is PDOS's actual FCB-compare/decision engine; its dozen-plus case
/// handlers (<c>sub_f068h</c>, <c>sub_f09bh</c>, <c>sub_f137h</c>, <c>sub_f186h</c>,
/// <c>sub_f0adh</c>, <c>sub_f045h</c>, <c>sub_ef3dh</c>, <c>sub_eeadh</c>, <c>sub_ef57h</c>,
/// <c>sub_ecd5h</c>, <c>sub_f24ch</c>, <c>sub_f2c2h</c>, <c>sub_f2d4h</c>) are NOT yet named or
/// disassembled in <c>docs/PDOS_wip.asm</c> — none of these addresses are from the owner's file;
/// they're this investigation's own raw disassembly, unread past identifying their existence and
/// the jump-table shape. Whatever decides "FCB found, transition to a real file-data read" lives
/// inside this jump table (or is never reached because BASIC's OWN LOAD/RUN driver — a separate,
/// disk-loaded code region, not PDOS's bank-1 driver — never asks PDOS to do anything past the
/// scan at all). Disambiguating those two possibilities needs disassembling this jump table's
/// C=0x14/C=0x1A targets, which is a substantial follow-on task, not completed in this pass.
/// </summary>
public class DispatchFunctionCodeTraceDiag
{
    private readonly ITestOutputHelper _output;
    public DispatchFunctionCodeTraceDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Dispatcher_CPM_entry_point = 0xE000;

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
    public void RunVolorg_DispatcherNeverReceivesAnyCodeOutsideTheDirectoryScanSet()
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

        // Real calling convention: BASIC passes the function code in register C, and
        // CPM_entry_point's own first few instructions do "ld a,c" AFTER ix_pointer is saved --
        // so the function code must be read from C (the low byte of BC), not A (which at the
        // exact moment PC==0xE000 still holds whatever A was before the call).
        _output.WriteLine("=== Watching every entry to CPM_entry_point (0xE000), logging function code C ===");
        ushort? lastPc = null;
        var entries = new List<(long T, byte C, ushort Bc)>();

        for (long t = 0; t < 10_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == Dispatcher_CPM_entry_point && pc != lastPc)
            {
                entries.Add((t, machine.Cpu.Reg.C, machine.Cpu.Reg.BC));
            }
            lastPc = pc;
        }

        _output.WriteLine($"=== Total dispatcher entries observed: {entries.Count} ===");
        foreach (var (t, c, bc) in entries)
        {
            _output.WriteLine($"t={t,10}  C=0x{c:X2} ({c,3})  BC=0x{bc:X4}");
        }

        var distinctCodes = entries.Select(e => e.C).Distinct().OrderBy(x => x).ToList();
        _output.WriteLine("=== Distinct function codes seen: " + string.Join(", ", distinctCodes.Select(code => $"0x{code:X2}")) + " ===");
        _output.WriteLine($"=== Was 0x39 ever dispatched? {distinctCodes.Contains((byte)0x39)} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");

        // CONFIRMED: PDOS's own dispatcher is NEVER asked to do anything beyond directory-scan
        // codes for the whole RUN"VOLORG" attempt -- 0x39 (the candidate "convert an FCB
        // allocation-map record into a real read track" handler) never fires, and no code outside
        // {0x0F, 0x1A, 0x14} ever appears. Whatever decides "FCB found, now read the file's data"
        // never runs at all -- it isn't a PDOS-dispatch-level decision that fails, PDOS is simply
        // never asked. The gap is upstream, in BASIC's own LOAD/RUN driver code.
        Assert.True(entries.Count > 0, "expected the directory scan to dispatch at least one PDOS call");
        Assert.All(distinctCodes, code => Assert.Contains(code, new byte[] { 0x0F, 0x1A, 0x14 }));
        Assert.DoesNotContain((byte)0x39, distinctCodes);
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));
    }
}

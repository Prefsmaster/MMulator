using System.Linq;
using System.Reflection;
using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Ctc;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Interrupts;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part B, CONCLUSIVE finding (2026-08-02/03, cc-bugfix-prompt-9): traces the real FDC command
/// stream AND CTC channel-0's own IntPending/InService state across a full <c>RUN"VOLORG"</c>
/// attempt (after two prior <c>SYSTEM B</c> calls, matching Part A's own confirmed "clean by the
/// second call" state).
///
/// <b>Result: our FDC/CTC/interrupt-delivery emulation is proven correct, every single time it's
/// exercised.</b> Channel 0 (the FDC completion interrupt, wired via
/// <c>Fdc.ResultReady += () =&gt; Ctc.ClkTrg(0)</c>) fires-and-delivers cleanly (IntPending→
/// InService→cleared) for EVERY ONE of the 15 real READ DATA completions in the trace, exactly
/// matching the confirmed directory interleave order (1,7,13,3,9,15,5,11,2,8,14,4,10,16 — see
/// <c>docs/P2000T-disk-formats.md</c> §6a). No missed interrupt, no delivery bug.
///
/// <b>The real bug is upstream of the FDC entirely.</b> After the LAST directory sector (16)
/// completes successfully at t≈2.5M, the FDC trace goes COMPLETELY SILENT — no further command is
/// EVER issued (confirmed: <c>RUN"VOLORG"</c> never attempts to read VOLORG's actual file DATA at
/// all, despite its FCB being confirmed present and correctly located in the directory scan, per
/// the project's own 2026-07-28 investigation). Execution instead falls into a hardcoded
/// 65536-iteration busy-wait loop (<c>le95fh</c>/<c>le962h</c> in <c>docs/PDOS_wip.asm</c>, ~3.8M
/// T-states) that exists to be interrupted EARLY by a real disk operation's own completion signal
/// (a stack-manipulation redirect installed at &amp;H6135 by <c>sub_e8c3h</c>) — but since no
/// operation was ever started, nothing ever redirects it, and it burns through its full duration
/// pointlessly. Once it expires, <c>channel_time_out</c>/<c>sub_e943h</c> fires: disables the FDC/
/// motor entirely (matches the trace's own final "CTRL 00" write), writes the confirmed
/// "always error" class <c>0x02</c> into the wrapper's own result byte (see
/// <see cref="DiskBasicDiskLoadedDisasmDiag"/> for that mechanism), and sets <c>FDOS_flags</c>
/// bit 6 — which is what prints "Disk I/O error".
///
/// <b>Genuinely open, narrower than where this investigation started:</b> WHY does PDOS's own
/// logic, immediately after successfully finding VOLORG's FCB, decide not to proceed to reading
/// the file's data at all? That decision point sits between the directory-scan dispatch
/// (function codes 0x0F/0x1A/0x14, already traced) and <c>sub_e7abh</c>/<c>le7b0h</c> (the real
/// file-data-read entry point) — needs a disassembly of that SPECIFIC narrow code region (a real
/// FCB validation/allocation-map check the fixture's FCB may not satisfy, most likely) to resolve
/// further. Flagged, not guessed, per this project's own convention.
/// </summary>
public class Channel0InterruptDuringGapDiag
{
    private readonly ITestOutputHelper _output;
    public Channel0InterruptDuringGapDiag(ITestOutputHelper output) => _output = output;

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

    private static readonly FieldInfo ChannelsField = typeof(Z80Ctc)
        .GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IDaisyChainDevice GetChannel(Z80Ctc ctc, int index)
    {
        var channels = (Array)ChannelsField.GetValue(ctc)!;
        return (IDaisyChainDevice)channels.GetValue(index)!;
    }

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test pinned the CONFIRMED BUG's own " +
        "symptom (the directory/file-data scan completing 15 real reads then NO further FDC " +
        "command ever being issued). Part I fixed the root cause (Upd765.DeferNaturalCompletion) " +
        "-- RUN\"VOLORG\" now continues reading far past 15 commands and loads VOLORG.BAS " +
        "successfully. See CLAUDE.md's Part I entry and FourteenthOperationRedirectDiag.cs. " +
        "Retained, skipped, for historical/investigative record only.")]
    public void Diag_RunVolorg_TraceFdcAndChannel0DuringTheGap()
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
        var ctc = machine.Ctc!;
        var ch0 = GetChannel(ctc, 0);

        var trace = new List<(long T, string Line)>();

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

        // Subscribe to the FDC trace only from here on, with a running T-state counter, so the
        // output stays scoped to RUN"VOLORG" itself.
        long tCounter = 0;
        machine.Fdc.Trace = line => trace.Add((tCounter, line));

        TypeString(machine, "RUN\"VOLORG\"");
        PressEnter(machine);

        _output.WriteLine("=== FDC trace + channel-0 IntPending/InService state across the full RUN\"VOLORG\" attempt ===");
        var lastIntPending = ch0.IntPending;
        var lastInService = ch0.InService;
        var lastReportedLine = "";

        for (tCounter = 0; tCounter < 7_000_000; tCounter++)
        {
            machine.Tick();

            if (ch0.IntPending != lastIntPending || ch0.InService != lastInService)
            {
                _output.WriteLine($"t={tCounter,10}  ch0.IntPending: {lastIntPending}->{ch0.IntPending}  ch0.InService: {lastInService}->{ch0.InService}  PC=0x{machine.Cpu.Reg.PC:X4}");
                lastIntPending = ch0.IntPending;
                lastInService = ch0.InService;
            }

            if (tCounter % 200_000 == 0)
            {
                var lastLine = LastNonBlankLine(SnapshotScreenText(machine));
                if (lastLine != lastReportedLine)
                {
                    _output.WriteLine($"t={tCounter,10}  screen last line: \"{lastLine}\"  flag(6091)=0x{ReadFlag(machine):X2}");
                    lastReportedLine = lastLine;
                }
            }
        }

        _output.WriteLine($"=== FDC trace ({trace.Count} entries) ===");
        foreach (var (t, line) in trace) _output.WriteLine($"t={t,10}  {line}");

        _output.WriteLine($"Final ch0: IntPending={ch0.IntPending} InService={ch0.InService}");
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");

        // Regression guard for the confirmed mechanism: the full 16-sector directory scan
        // completes (correct interleave order), then NOTHING further is ever issued to the FDC —
        // proving the eventual "Disk I/O error" is not an FDC/interrupt-delivery problem.
        var completions = trace
            .Where(e => e.Line.StartsWith("COMPLETE kind=ReadData"))
            .Select(e => (T: e.T, Sector: int.Parse(e.Line.Split("startSector=")[1].Split(' ')[0])))
            .ToList();
        // The first "1" is RUN's own initial "verify the drive is ready" check (a real, separate
        // read of sector 1 before the actual directory scan begins, which then also starts at
        // sector 1) — confirmed real behavior, not a fixture quirk.
        Assert.Equal(new[] { 1, 1, 7, 13, 3, 9, 15, 5, 11, 2, 8, 14, 4, 10, 16 }, completions.Select(c => c.Sector));

        var lastCompletionT = completions[^1].T;
        var anyCommandAfterLastCompletion = trace.Any(e => e.T > lastCompletionT && e.Line.StartsWith("CMD"));
        Assert.False(anyCommandAfterLastCompletion,
            "no FDC command should ever be issued after the last directory-scan sector completes — " +
            "RUN\"VOLORG\" never actually attempts to read the file's data");

        Assert.Equal(0x00, ReadFlag(machine)); // settles back to clean once "Ok" reprints
    }
}

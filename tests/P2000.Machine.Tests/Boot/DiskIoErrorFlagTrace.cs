using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Step 0 of the 2026-08-02 "Disk I/O error" investigation (cc-bugfix-prompt-9): instruments
/// &amp;H6091 (the Adresboekje's own named "Flag for Disk I/O error", see
/// <c>docs/Adresboekje-DiskBASIC-parsed.md</c> and reference doc §5d's 2026-08-02 entry) directly,
/// live, across the owner's own exact manual repro sequence — RESET, SYSTEM B (x2), FILES,
/// RUN"VOLORG" (x2), FILES again — recording the flag's value AND the actual screen text before/
/// after every step.
///
/// <b>Result: every owner-observed symptom reproduces exactly, byte-for-byte, once the screen
/// reader itself was fixed (see below) — RESET and the first SYSTEM B both print "Disk I/O
/// error" then "Ok"; the second SYSTEM B prints "Ok" alone; FILES lists both real files
/// correctly and then ALSO prints a trailing "Disk I/O error" (the owner's own "newest data
/// point"); both RUN"VOLORG" attempts fail identically, fresh, every time.</b> This confirms the
/// mechanism found by disassembly (see <c>DiskBasicDiskLoadedDisasmDiag</c>) is real PDOS
/// behavior, not an emulator defect — Part A's "stale-retry" symptom is fully explained and
/// requires no fix (see the project findings log).
///
/// <b>A real bug WAS found along the way, in this test's OWN tooling (and
/// <c>PdosLoadSaveRepro.cs</c>'s identical, pre-existing helper): the original
/// <c>SnapshotScreenText</c> read video memory with a 40-byte row stride. The real layout is 80
/// bytes/row with <see cref="P2000.Machine.Devices.Video.PanX"/>-based windowing (confirmed via
/// a raw VRAM hex dump — see <c>VramLayoutDiag</c>), matching <c>Video.cs</c>'s own
/// <c>OnColumnFetch</c>. The 40-stride version only ever exposed the FIRST 12 of 24 real screen
/// rows and, for any message under 40 characters, coincidentally still looked "readable" (real
/// content on every other line, blank padding in between) — which is exactly what caused an
/// earlier pass of this same investigation to wrongly conclude "SYSTEM B hangs and prints
/// nothing forever." Fixed here; not yet ported back to <c>PdosLoadSaveRepro.cs</c>, flagged
/// separately.</b>
///
/// &amp;H6091 sits in monitor RAM (&amp;H6000-&amp;H61FF per the Adresboekje), NOT inside the
/// range loaded from the system disk's tracks 3-5 (&amp;H6200-&amp;H8A90) — so it's directly
/// readable via <see cref="Machine.Memory"/> at any point with no disassembly or system-disk
/// fixture dependency at all.
/// </summary>
public class DiskIoErrorFlagTrace
{
    private readonly ITestOutputHelper _output;

    public DiskIoErrorFlagTrace(ITestOutputHelper output)
    {
        _output = output;
    }

    private const ushort DiskIoErrorFlag = 0x6091;

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "MMulator.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (MMulator.sln not found walking up from " +
            AppContext.BaseDirectory + ").");
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

    /// <summary>CORRECTED stride: VRAM is 80 bytes/row with PanX-based windowing
    /// (<c>Video.cs</c>'s own <c>BufferColumns=80</c>/<c>OnColumnFetch</c>), confirmed via a raw
    /// VRAM hex dump — NOT the 40-byte stride <c>PdosLoadSaveRepro.cs</c> (and this file's own
    /// earlier draft) used. That formula only coincidentally looked right (it happens to land on
    /// real content for even logical rows and blank padding for odd ones, for any message under
    /// 40 chars), while actually only ever exposing the FIRST 12 of 24 real screen rows — silently
    /// hiding anything printed further down (exactly what caused the false "SYSTEM B hangs,
    /// prints nothing" conclusion earlier in this investigation).</summary>
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

    // (row, col, shift) per character, sourced from src/P2000.UI/Input/KeyMap.cs.
    private static readonly Dictionary<char, (int Row, int Col, bool Shift)> CharMap = new()
    {
        ['L'] = (8, 1, false),
        ['O'] = (6, 1, false),
        ['A'] = (4, 2, false),
        ['D'] = (1, 4, false),
        [' '] = (2, 1, false),
        ['"'] = (7, 7, true),
        ['V'] = (3, 7, false),
        ['R'] = (4, 7, false),
        ['G'] = (1, 5, false),
        ['B'] = (3, 5, false),
        [':'] = (8, 7, false),
        ['I'] = (8, 6, false),
        ['N'] = (3, 1, false),
        ['F'] = (1, 7, false),
        ['T'] = (4, 5, false),
        ['E'] = (4, 4, false),
        ['S'] = (1, 3, false),
        ['Y'] = (4, 1, false),
        ['U'] = (4, 6, false),
        ['M'] = (3, 6, false),
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
        foreach (var ch in text)
        {
            var (row, col, shift) = CharMap[ch];
            TypeChar(machine, row, col, shift);
        }
    }

    private static void PressEnter(Machine machine) => TypeChar(machine, EnterRow, EnterCol, false);

    private Machine BootToReadyPrompt(string secondDiskPath, out List<string> trace)
    {
        var repoRoot = FindRepoRoot();
        var cartridgePath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var bootFloppyPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");

        Assert.True(File.Exists(cartridgePath), $"missing fixture: {cartridgePath}");
        Assert.True(File.Exists(bootFloppyPath), $"missing fixture: {bootFloppyPath}");
        Assert.True(File.Exists(secondDiskPath), $"missing fixture: {secondDiskPath}");

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

        var localTrace = new List<string>();
        machine.Fdc.Trace = line => localTrace.Add(line);
        trace = localTrace;

        RunUntil(machine, () => machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "boot never reached SLOT1 — disk-boot gate didn't fire");

        var (foundFiles, _) = WaitForScreenContains(machine, "many files", maxFields: 3000);
        Assert.True(foundFiles, "Disk BASIC's 'How many files?' prompt never appeared");
        PressEnter(machine);

        var (foundRuntime, _) = WaitForScreenContains(machine, "Runtime support", maxFields: 3000);
        Assert.True(foundRuntime, "Disk BASIC's 'Runtime support?' prompt never appeared");
        PressEnter(machine);

        var (foundReady, _) = WaitForScreenContains(machine, "Ok", maxFields: 3000);
        Assert.True(foundReady, "Disk BASIC never reached its ready prompt");
        Ticks(machine, 20);

        return machine;
    }

    private static void RunUntil(Machine machine, Func<bool> condition, string failMessage, int limit = 40_000_000)
    {
        for (var i = 0; i < limit; i++)
        {
            machine.Tick();
            if (condition()) return;
        }
        Assert.Fail($"{failMessage} (ran {limit:N0} T-states)");
    }

    private byte ReadFlag(Machine m) => m.Memory.Read(DiskIoErrorFlag);

    private static string LastNonBlankLine(string screen) =>
        screen.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

    /// <summary>Waits until the LAST non-blank screen line is exactly "Ok" — every direct-mode
    /// command here ends with a fresh ready prompt as the final thing printed, whether it
    /// succeeded or errored (e.g. RESET's own "Disk I/O error" is followed by "Ok" too), so this
    /// is a reliable, command-shape-independent completion signal. Two other approaches were
    /// tried and rejected first: a fixed tick budget (different commands take wildly different
    /// real time — SYSTEM/FILES's own bank-switch-heavy internals don't complete in the same
    /// budget a directory scan does) and a screen/FDC-trace idleness detector (real commands go
    /// silent on BOTH fronts for long stretches in the MIDDLE of still-running work, so "nothing
    /// changed for N fields" repeatedly produced a false "done" reading).</summary>
    private static void WaitForReadyPrompt(Machine machine, int maxFields = 5000)
    {
        // Tick first, then check — the screen may still show a STALE "Ok" from the prompt
        // that was on-screen before this command was even typed; ticking first guarantees we
        // never read that stale value as "done" before the new command has had a chance to run.
        for (var f = 0; f < maxFields; f++)
        {
            Ticks(machine, 1);
            if (LastNonBlankLine(SnapshotScreenText(machine)) == "Ok") return;
        }
    }

    private void Step(Machine machine, string label, string? typeAndEnter, List<(string Label, byte Before, byte After, string Screen)> timeline, List<string> trace)
    {
        var before = ReadFlag(machine);
        var traceCountBefore = trace.Count;
        if (typeAndEnter is not null)
        {
            TypeString(machine, typeAndEnter);
            PressEnter(machine);
        }
        WaitForReadyPrompt(machine);
        Ticks(machine, 20); // small trailing margin
        var after = ReadFlag(machine);
        var screen = SnapshotScreenText(machine);
        timeline.Add((label, before, after, screen));
        _output.WriteLine($"=== {label}: flag 0x{before:X2} -> 0x{after:X2} ===");
        _output.WriteLine(screen);
        _output.WriteLine($"--- FDC trace during \"{label}\" ({trace.Count - traceCountBefore} entries) ---");
        for (var i = traceCountBefore; i < trace.Count; i++) _output.WriteLine(trace[i]);
    }

    [Fact]
    public void Repro_RESET_SYSTEMB_FILES_RUNVOLORG_TracesDiskIoErrorFlagAtEveryStep()
    {
        var repoRoot = FindRepoRoot();
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");
        var machine = BootToReadyPrompt(secondDiskPath, out var trace);

        var timeline = new List<(string Label, byte Before, byte After, string Screen)>();

        Step(machine, "RESET", "RESET", timeline, trace);
        Step(machine, "SYSTEM B (1st)", "SYSTEM B", timeline, trace);
        Step(machine, "SYSTEM B (2nd)", "SYSTEM B", timeline, trace);
        Step(machine, "FILES (after SYSTEM B)", "FILES", timeline, trace);
        Step(machine, "RUN\"VOLORG\" (1st)", "RUN\"VOLORG\"", timeline, trace);
        Step(machine, "RUN\"VOLORG\" (2nd)", "RUN\"VOLORG\"", timeline, trace);
        Step(machine, "FILES (after RUN)", "FILES", timeline, trace);

        _output.WriteLine("=== Timeline summary ===");
        foreach (var (label, before, after, screen) in timeline)
        {
            var lastLine = screen.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "";
            _output.WriteLine($"{label,-28} flag 0x{before:X2} -> 0x{after:X2}   last non-blank screen line: \"{lastLine.TrimEnd()}\"");
        }

        // Regression guard: the confirmed, reproduced-exactly owner symptom pattern.
        Assert.Equal(0x02, timeline[0].After); // RESET sets the flag (system disk has no FCB index)
        Assert.Equal(0x02, timeline[1].After); // SYSTEM B (1st) — flag stays set
        Assert.Contains("Disk I/O error", timeline[1].Screen);
        Assert.Equal(0x00, timeline[2].After); // SYSTEM B (2nd) — clean
        var secondSystemBOffset = timeline[2].Screen.LastIndexOf("SYSTEM B", StringComparison.Ordinal);
        Assert.DoesNotContain("Disk I/O error", timeline[2].Screen[(secondSystemBOffset + 1)..]);
        // FILES lists both real files correctly AND still prints a trailing error — the owner's
        // own "newest data point" (project findings log), not a name-lookup failure.
        Assert.Contains("VOLORG", timeline[3].Screen);
        Assert.Contains("VOLINFO", timeline[3].Screen);
        Assert.Equal(0x02, timeline[3].After);
        // Both RUN"VOLORG" attempts fail fresh, identically — rules out "stale flag" for this one.
        Assert.Equal(0x00, timeline[4].After);
        Assert.Contains("Disk I/O error", timeline[4].Screen);
        Assert.Equal(0x00, timeline[5].After);
        Assert.Contains("Disk I/O error", timeline[5].Screen);
    }
}

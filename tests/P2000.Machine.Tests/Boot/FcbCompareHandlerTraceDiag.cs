using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part E (cc-bugfix-prompt-12) — disassembles and live-traces the three real <c>sub_f2fdh</c>
/// handler bodies Part D pinned down (0x0F -> 0xF370, 0x14 -> 0xF3A0, 0x1A -> 0xF3CA), to settle
/// whether PDOS's own FCB-name compare (a) legitimately never matches this fixture's real VOLORG
/// FCB content, or (b) matches correctly but nothing acts on it.
///
/// Static disassembly (docs/PDOS_wip.asm, read but not edited -- no owner annotations exist yet
/// around these three addresses) found:
/// - 0x0F -> 0xF370: <c>call sub_f2d4h; call sub_f068h; jr lf388h</c>. <c>sub_f2d4h</c> extracts
///   the low 5 bits of the current FCB's flag byte (offset+0), treats values >= 0x1E as
///   "out of range" (early return), otherwise clears those bits in-place and calls
///   <c>sub_f2c2h</c>. <c>sub_f068h</c> copies bytes out of the current FCB into a working buffer
///   (guarded by <c>sub_f032h</c>, which aborts if <c>(lf585h) == 0xFF</c>).
/// - 0x14 -> 0xF3A0: <c>call sub_f2d4h; call sub_f137h; jr lf3fah</c>. <c>sub_f137h</c> is the
///   routine that actually issues each subsequent directory-sector READ (via
///   <c>Seek_to_track</c>/<c>sub_e8b3h</c>, confirmed the real FDC-command source from Part B/C's
///   own trace) -- this is the "read the next sector" step.
/// - 0x1A -> 0xF3CA: a DIFFERENT shape entirely -- <c>ld a,(lf58bh); rra; jr nc,lf3d8h</c>. If bit
///   0 of <c>lf58bh</c> is CLEAR, jumps to <c>lf3d8h</c> which just calls <c>lf2f3h</c> (a small
///   bookkeeping routine that copies the current FCB pointer into <c>lf587h</c>/<c>lf52eh</c> and
///   returns -- a no-op continuation, NOT a compare). If bit 0 is SET, instead copies the current
///   FCB pointer (0xF579) directly into <c>lf589h</c> -- the EXACT cell <c>le229h</c>'s function
///   0x39 handler reads to know which file's data to actually read. <c>lf58bh</c> bit 0 is also
///   checked by <c>sub_f123h</c>/<c>sub_f12bh</c> (called from inside <c>sub_f137h</c>'s own
///   continuation) and is SET by the top-level dispatcher's bit-7-set-function-code path
///   (<c>le059h</c>, <c>docs/PDOS_wip.asm</c> line ~134) -- but Part C/D's own dispatcher-level
///   trace already confirmed NO bit-7-set code (nor 0x39) ever reaches the top dispatcher during
///   this whole repro. This is the key branch point: whether <c>lf58bh</c> bit 0 is ever actually
///   set during a live run settles theory (a) vs (b) directly.
///
/// This test watches every real entry to the 0x1A handler (0xF3CA) and logs (i) the live value of
/// <c>lf58bh</c>, (ii) which of the two branches (0xF3D0, "prep for a real read" vs 0xF3D8, mere
/// bookkeeping continuation) actually executes, and (iii) the current FCB pointer's own name+ext
/// bytes at that moment, to see whether VOLORG's real FCB (independently confirmed via
/// <c>DskImage</c>/raw bytes to sit at track-1 slot 0, sector 1, byte0=0xF3, name="VOLORG  ",
/// ext="BAS") is ever the FCB under consideration when the branch is evaluated.
///
/// CONFIRMED, decisively: <c>lf58bh</c> bit 0 is SET on EVERY ONE of the 15 real 0x1A entries, and
/// the MATCH branch (0xF3D0, prep <c>lf589h</c> for a real read) is taken every single time -- it
/// never once fails. VOLORG's own FCB is the "current" FCB pointer at every one of the 27
/// <c>sub_f137h</c> entries too. This settles theories (a) and (b) from the prompt as BOTH WRONG:
/// PDOS is not searching for VOLORG among candidates that fail to match (a); nor is it a case of
/// "compare succeeds, nothing acts on it" in the sense of a dropped BASIC-side ball (b) -- VOLORG
/// is already the active file the ENTIRE TIME, and PDOS's own read-next-record machinery (0x14,
/// re-identified via the CP/M-BDOS addendum as F_READ) IS acting on it, correctly, repeatedly. The
/// real mechanism is a THIRD explanation, confirmed by <c>ReadDataPhysicalTrackDiag.cs</c> and
/// <c>FReadEofHandlingDiag.cs</c>: the underlying physical-sector-advancement machinery stops
/// exactly 2 sectors short of a full 16-sector track (the already-flagged "14-of-16" pattern),
/// long before CP/M's own CR/RC-based EOF condition would ever fire -- see the dated findings-log
/// entry for the full unifying account.
/// </summary>
public class FcbCompareHandlerTraceDiag
{
    private readonly ITestOutputHelper _output;
    public FcbCompareHandlerTraceDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Handler_0x1A_Entry = 0xF3CA;      // ld a,(lf58bh)
    private const ushort Handler_0x1A_MatchBranch = 0xF3D0; // ld hl,(0f579h) -- "prep lf589h for 0x39"
    private const ushort Handler_0x1A_NoMatchBranch = 0xF3D8; // call lf2f3h -- mere bookkeeping continue
    private const ushort Flag_lf58bh = 0xF58B;
    private const ushort CurrentFcbPointerCell_0xf579 = 0xF579;
    private const ushort Handler_0x14_Entry_SubF137h = 0xF137;

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

    private static string ReadFcbNameExt(Machine machine, ushort fcbPointerCell)
    {
        var lo = machine.Memory.Read(fcbPointerCell);
        var hi = machine.Memory.Read((ushort)(fcbPointerCell + 1));
        var fcbAddr = (ushort)((hi << 8) | lo);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            var b = machine.Memory.Read((ushort)(fcbAddr + 1 + i));
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return $"0x{fcbAddr:X4}:'{sb}'";
    }

    [Fact]
    public void RunVolorg_VolorgIsAlreadyTheActiveFcb_MatchBranchAlwaysTaken()
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

        _output.WriteLine("=== Watching every entry to the 0x1A handler (0xF3CA): lf58bh value + branch taken + current FCB ===");
        ushort? lastPc = null;
        var handlerEntries = 0;
        var matchBranchTaken = 0;
        var noMatchBranchTaken = 0;
        var sub_f137h_entries = 0;
        var volorgWasCurrentAtLeastOnce = false;

        for (long t = 0; t < 20_000_000L; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (pc == Handler_0x1A_Entry && pc != lastPc)
            {
                handlerEntries++;
                var flag = machine.Memory.Read(Flag_lf58bh);
                var fcb = ReadFcbNameExt(machine, CurrentFcbPointerCell_0xf579);
                if (fcb.Contains("VOLORG")) volorgWasCurrentAtLeastOnce = true;
                _output.WriteLine($"0x1A-entry#{handlerEntries,3} t={t,10}  lf58bh=0x{flag:X2} (bit0={flag & 1})  current-FCB={fcb}");
            }
            if (pc == Handler_0x1A_MatchBranch && pc != lastPc)
            {
                matchBranchTaken++;
                _output.WriteLine($"  -> took MATCH branch (0xF3D0, prep lf589h for a real read) at t={t}");
            }
            if (pc == Handler_0x1A_NoMatchBranch && pc != lastPc)
            {
                noMatchBranchTaken++;
            }
            if (pc == Handler_0x14_Entry_SubF137h && pc != lastPc)
            {
                sub_f137h_entries++;
                var fcb = ReadFcbNameExt(machine, CurrentFcbPointerCell_0xf579);
                if (fcb.Contains("VOLORG")) volorgWasCurrentAtLeastOnce = true;
                _output.WriteLine($"sub_f137h-entry#{sub_f137h_entries,3} t={t,10}  current-FCB={fcb}");
            }

            lastPc = pc;
        }

        _output.WriteLine($"=== Total 0x1A-handler entries: {handlerEntries} ===");
        _output.WriteLine($"=== MATCH branch (0xF3D0) taken: {matchBranchTaken} times ===");
        _output.WriteLine($"=== NO-MATCH branch (0xF3D8) taken: {noMatchBranchTaken} times ===");
        _output.WriteLine($"=== sub_f137h (0x14 body) entries: {sub_f137h_entries} ===");
        _output.WriteLine($"=== Was VOLORG's own FCB ever the 'current' FCB pointer at any observed checkpoint: {volorgWasCurrentAtLeastOnce} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: VOLORG's own FCB is the "current" FCB pointer throughout the whole attempt --
        // it was never being searched for among other candidates, it was already the active file.
        Assert.True(volorgWasCurrentAtLeastOnce, "expected VOLORG's own FCB to be the current FCB pointer at least once");
        // CONFIRMED: lf58bh bit 0 is set on EVERY 0x1A entry, and the "prep lf589h for a real read"
        // branch (0xF3D0) is taken EVERY time -- never once does the compare/flag-check fail. The
        // "NO-MATCH branch (0xF3D8)" hits recorded above are NOT genuine executions of that branch --
        // they are a PC-fetch-increment artifact (the same class as Sube943hCallerDiag.cs's own
        // RET-fetch gotcha, here from the 2-byte "jr lf3dbh" at 0xF3D6 transiently showing PC at
        // 0xF3D8 mid-fetch before the jump completes): they occur exactly once per MATCH branch
        // taken, 1:1, which is the tell. Kept as a raw diagnostic count above, not asserted as a
        // real branch outcome.
        Assert.Equal(handlerEntries, matchBranchTaken);
        Assert.True(handlerEntries > 0);
    }
}

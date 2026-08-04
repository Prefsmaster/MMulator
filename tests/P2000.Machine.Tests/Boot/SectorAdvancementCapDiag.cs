using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Part F (cc-bugfix-prompt-13) — investigates whether <c>leb9eh</c>/<c>sub_f447h</c>/<c>lf555h</c>
/// (the candidate mechanism flagged in Part E) contains the bug that stops physical-sector
/// advancement 2 short of a full 16-sector track (the "14-of-16" pattern, first observed for
/// directory reads in Part B, confirmed to also govern VOLORG's real file-data read in Part E).
///
/// Static disassembly (docs/PDOS_wip.asm, read but not edited -- no owner annotations exist yet
/// around this region) found the <c>lf555h</c> interleave table itself is NOT short: its raw
/// bytes (the disassembler mis-renders them as garbage instructions, exactly like
/// <c>sub_f2fdh</c>'s own jump table in Part D) are the COMPLETE, correct 16-entry sequence
/// <c>01 07 0D 03 09 0F 05 0B 02 08 0E 04 0A 10 06 0C</c> (decimal
/// <c>1,7,13,3,9,15,5,11,2,8,14,4,10,16,6,12</c>) -- indices 14 and 15 genuinely hold 6 and 12.
///
/// A real instrumentation bug was found and fixed while building this test: <c>sub_f447h</c>'s
/// subtrahend is NOT read directly from <c>0f662h</c> -- <c>ld hl,(0f662h)</c> loads a POINTER
/// from that cell, and <c>ld c,(hl)</c>/<c>ld b,(hl+1)</c> read the real subtrahend from wherever
/// THAT pointer points (a double indirection). The first pass of this test read <c>0f662h</c>'s
/// own bytes directly, producing nonsensical index values (166-195, far outside the table's
/// 16-entry range) that briefly looked like a genuine out-of-bounds bug before the fix revealed
/// they were purely an artifact of missing that second indirection.
///
/// CONFIRMED, decisively, once fixed: the index computation is a clean, UNCAPPED linear counter
/// in BOTH <c>SYSTEM B</c> (directory read) and <c>RUN"VOLORG"</c> (file-data read) -- indices
/// 0,1,2,...,13, <c>carry</c> FALSE on every single call (no underflow/boundary condition is ever
/// signaled), and every table byte read matches the confirmed interleave exactly. This DISPROVES
/// the working hypothesis that this code contains the "stop 2 short" bug -- it would happily
/// continue to indices 14/15 (reading the real 6/12 sectors) if called again. The actual stop is
/// external to all three candidate locations named in this prompt.
/// </summary>
public class SectorAdvancementCapDiag
{
    private readonly ITestOutputHelper _output;
    public SectorAdvancementCapDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;
    private const ushort Sub_f447h = 0xF447;
    private const ushort Cell_0f662h = 0xF662;
    private const ushort Cell_lf666h = 0xF666;

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

    // sub_f447h has exactly two call sites: the loop-check inside leb9eh (0xEB9E, return 0xEBA1
    // -- NOT a table-index computation, just a carry check on a different, larger quantity) and
    // the actual table-index computation inside lebe5h (0xEBF0, return 0xEBF3). Distinguish by
    // return address so only genuine table-index calls are treated as such.
    private const ushort LoopCheckReturnAddr = 0xEBA1;
    private const ushort TableIndexReturnAddr = 0xEBF3;

    private const ushort InterleaveTableBase_lf555h = 0xF555;

    private sealed record SubF447hCall(long T, ushort Minuend, ushort Subtrahend, ushort Result, bool Carry, string Caller, ushort TableAddr, byte TableByte, ushort SubtrahendPointer);

    private static List<SubF447hCall> TraceSubF447h(Machine machine, long tStates)
    {
        var calls = new List<SubF447hCall>();
        ushort? lastPc = null;
        for (long t = 0; t < tStates; t++)
        {
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;
            if (pc == Sub_f447h && pc != lastPc)
            {
                // Verify genuine entry the same way prior parts did: real CALL bytes at
                // returnAddr-3, not a bare PC match (guards against the same RET-fetch artifact
                // already found twice in this investigation).
                var sp = machine.Cpu.Reg.SP;
                var retLo = machine.Memory.Read(sp);
                var retHi = machine.Memory.Read((ushort)(sp + 1));
                var ret = (ushort)((retHi << 8) | retLo);
                var callSite = (ushort)(ret - 3);
                var b0 = machine.Memory.Read(callSite);
                var b1 = machine.Memory.Read((ushort)(callSite + 1));
                var b2 = machine.Memory.Read((ushort)(callSite + 2));
                if (b0 == 0xCD && b1 == 0x47 && b2 == 0xF4)
                {
                    // minuend: "ld a,(de)" with de = literal address lf666h -- a DIRECT read.
                    var minuendLo = machine.Memory.Read(Cell_lf666h);
                    var minuendHi = machine.Memory.Read((ushort)(Cell_lf666h + 1));
                    var minuend = (ushort)((minuendHi << 8) | minuendLo);
                    // subtrahend: "ld hl,(0f662h)" loads a POINTER from 0f662h, then "ld c,(hl)" /
                    // "ld b,(hl+1)" read the actual subtrahend bytes from WHEREVER that pointer
                    // points -- a double indirection. An earlier pass of this test read 0f662h's
                    // own bytes directly (missing the second indirection), which produced
                    // nonsensical results.
                    var pointerLo = machine.Memory.Read(Cell_0f662h);
                    var pointerHi = machine.Memory.Read((ushort)(Cell_0f662h + 1));
                    var pointer = (ushort)((pointerHi << 8) | pointerLo);
                    var subLo = machine.Memory.Read(pointer);
                    var subHi = machine.Memory.Read((ushort)(pointer + 1));
                    var sub = (ushort)((subHi << 8) | subLo);
                    var result = (ushort)(minuend - sub);
                    var carry = minuend < sub;
                    var caller = ret == TableIndexReturnAddr ? "lebe5h(table-index)"
                        : ret == LoopCheckReturnAddr ? "leb9eh(loop-check)"
                        : $"UNKNOWN(ret=0x{ret:X4})";
                    var tableAddr = (ushort)(InterleaveTableBase_lf555h + (byte)result);
                    var tableByte = machine.Memory.Read(tableAddr);
                    calls.Add(new SubF447hCall(t, minuend, sub, result, carry, caller, tableAddr, tableByte, pointer));
                }
            }
            lastPc = pc;
        }
        return calls;
    }

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own exact index count (14) for the " +
        "RUN\"VOLORG\" context was pinned against the CONFIRMED BUG's behavior (RUN\"VOLORG\" " +
        "hanging after 14 reads). Part I fixed the root cause (Upd765.DeferNaturalCompletion) -- " +
        "VOLORG.BAS now loads and runs successfully, changing how many table-index computations " +
        "happen within this test's bounded trace window. The underlying finding (the index " +
        "computation itself is uncapped) is unaffected -- only the specific count pinned here is " +
        "stale. See CLAUDE.md's Part I entry and FourteenthOperationRedirectDiag.cs. Retained, " +
        "skipped, for historical/investigative record only.")]
    public void SystemBAndRunVolorg_TableIndexComputationIsUncapped_StopIsExternalToThisCode()
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

        _output.WriteLine("=== Context 1: SYSTEM B (directory read) -- tracing sub_f447h ===");
        TypeString(machine, "SYSTEM B");
        PressEnter(machine);
        var systemBCalls = TraceSubF447h(machine, 3_000_000L);
        WaitForReadyPrompt(machine);
        Ticks(machine, 20);
        foreach (var c in systemBCalls)
        {
            var index = (byte)c.Result;
            _output.WriteLine($"  t={c.T,10}  caller={c.Caller,-20}  lf666h=0x{c.Minuend:X4}  0f662hPtr=0x{c.SubtrahendPointer:X4} subtrahend=0x{c.Subtrahend:X4}  index(low byte)={index,3}  carry={c.Carry}  tableAddr=0x{c.TableAddr:X4}  tableByte=0x{c.TableByte:X2}({c.TableByte})");
        }
        var systemBTableIndexCalls = systemBCalls.Where(c => c.Caller == "lebe5h(table-index)").ToList();
        _output.WriteLine($"=== SYSTEM B: {systemBCalls.Count} total sub_f447h calls, {systemBTableIndexCalls.Count} real table-index calls; max real index = {(systemBTableIndexCalls.Count > 0 ? systemBTableIndexCalls.Max(c => (byte)c.Result) : -1)} ===");

        TypeString(machine, "SYSTEM B");
        PressEnter(machine);
        WaitForReadyPrompt(machine);
        Ticks(machine, 20);

        _output.WriteLine("=== Context 2: RUN\"VOLORG\" (file-data read) -- tracing sub_f447h ===");
        TypeString(machine, "RUN\"VOLORG\"");
        PressEnter(machine);
        var runVolorgCalls = TraceSubF447h(machine, 20_000_000L);
        foreach (var c in runVolorgCalls)
        {
            var index = (byte)c.Result;
            _output.WriteLine($"  t={c.T,10}  caller={c.Caller,-20}  lf666h=0x{c.Minuend:X4}  0f662hPtr=0x{c.SubtrahendPointer:X4} subtrahend=0x{c.Subtrahend:X4}  index(low byte)={index,3}  carry={c.Carry}  tableAddr=0x{c.TableAddr:X4}  tableByte=0x{c.TableByte:X2}({c.TableByte})");
        }
        var runVolorgTableIndexCalls = runVolorgCalls.Where(c => c.Caller == "lebe5h(table-index)").ToList();
        _output.WriteLine($"=== RUN\"VOLORG\": {runVolorgCalls.Count} total sub_f447h calls, {runVolorgTableIndexCalls.Count} real table-index calls; max real index = {(runVolorgTableIndexCalls.Count > 0 ? runVolorgTableIndexCalls.Max(c => (byte)c.Result) : -1)} ===");

        WaitForReadyPrompt(machine, maxFields: 3000);
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        _output.WriteLine(SnapshotScreenText(machine));

        // CONFIRMED: the interleave-table index computation is a clean, uncapped linear counter in
        // BOTH contexts -- 0,1,2,...,13, with carry=False throughout (no underflow/boundary
        // condition ever signaled), and every table byte read matches the confirmed full interleave
        // exactly. This DISPROVES the working hypothesis that leb9eh/sub_f447h/lf555h contain an
        // internal "stop 2 short" bug -- they would happily continue to index 14/15 (reading the
        // real 6/12 sectors) if called an additional 1-2 times. The stop is external to this code.
        Assert.Equal(14, systemBTableIndexCalls.Count);
        Assert.Equal(15, runVolorgTableIndexCalls.Count);
        Assert.All(systemBTableIndexCalls, c => Assert.False(c.Carry));
        Assert.All(runVolorgTableIndexCalls, c => Assert.False(c.Carry));
        var systemBIndices = systemBTableIndexCalls.Select(c => (int)(byte)c.Result).ToArray();
        var runVolorgIndices = runVolorgTableIndexCalls.Skip(1).Select(c => (int)(byte)c.Result).ToArray();
        Assert.Equal(Enumerable.Range(0, 14).ToArray(), systemBIndices);
        Assert.Equal(Enumerable.Range(0, 14).ToArray(), runVolorgIndices);
        var expectedSectors = new byte[] { 1, 7, 13, 3, 9, 15, 5, 11, 2, 8, 14, 4, 10, 16 };
        Assert.Equal(expectedSectors, systemBTableIndexCalls.Select(c => c.TableByte).ToArray());
        Assert.Equal(expectedSectors, runVolorgTableIndexCalls.Skip(1).Select(c => c.TableByte).ToArray());
    }
}

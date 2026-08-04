using System.Linq;
using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Diagnostic repro for the owner-reported bug (reference doc §5d, "TRACKED, not fixed
/// (owner-reported, 2026-07-28)"): every post-boot LOAD/SAVE fails with "Disk I/O error" once
/// real Philips Disk BASIC 1.6 UK is running, even though the ROM's own fixed 2-track
/// <c>getdos</c> boot read succeeds. Drives the real repro end-to-end (real cartridge, real boot
/// floppy, real second disk, real BASIC, real keyboard-typed command) with <see cref="Upd765.Trace"/>
/// wired up, dumping the full FDC command/result trace plus the screen text via
/// <see cref="ITestOutputHelper"/> so the trace can be inspected directly
/// (`dotnet test --filter` + read stdout) without attaching a debugger.
///
/// <b>Investigation outcome (2026-07-28) — three real, confirmed <see cref="Upd765"/> bugs found
/// and fixed via this trace, all only reachable through the TC (Terminal Count)-forced early
/// transfer completion Disk BASIC's resident LOAD driver actually uses (request a wide EOT
/// window, take only the sector(s) wanted, then TC-abort the rest — a real, legitimate technique
/// never previously exercised by anything else in this project):</b>
/// <list type="number">
/// <item><c>LastSectorResultFields</c> reported the EOT window's tail sector instead of the
/// sector actually transferred (fixed: bound by <c>_transferIndex</c>, not
/// <c>_transferBuffer.Length</c>).</item>
/// <item><c>CompleteTransfer</c>'s ST0 always reported <c>0x00</c> regardless of which drive/head
/// was addressed, instead of the datasheet-standard drive/head bits (fixed).</item>
/// <item>TC-forced completion fired <see cref="Upd765.ResultReady"/> SYNCHRONOUSLY inside the
/// triggering <c>WriteControl</c> call — the same "lost wakeup" bug class already found and fixed
/// for SEEK/RECALIBRATE (<see cref="Upd765.MinimumLostWakeupGuardTStates"/>), now confirmed to
/// also apply here (fixed: deferred via the same guard).</item>
/// </list>
/// Fixing all three turned the FDC's behavior from "identically retries the SAME sector forever,
/// then fails" into "issues a real, correctly-parameterized, complete scan across all 16
/// directory sectors" — a dramatic, verified improvement (see the 4 new regression tests in
/// <c>Upd765Tests.cs</c>). <b>The LOAD still does not fully succeed in this exact repro</b>: the
/// scan visits every directory sector (including the one holding the target file's real,
/// byte-verified FCB) and still reports "Disk I/O error" at the end. Genuinely stuck past this
/// point without a disassembly of Philips Disk BASIC's own resident driver — the trace proves the
/// FDC's command/status/data are all now datasheet-correct, so whatever the driver checks next
/// (a checksum? a specific probe order/count it expects? something about how it recognizes a
/// name match against the FCB bytes?) can't be determined from behavior alone. A disassembly
/// would need to show exactly what the driver does with the 256 bytes it reads from each
/// candidate sector to resolve this further.
///
/// <b>SAVE side-by-side (2026-07-28, owner's own follow-up question):</b>
/// <see cref="Boot_ThenSaveTest_TraceFdcCommandsAndScreenOutput"/> runs <c>SAVE "B:TEST"</c>
/// against the identical booted machine. Result: it reads directory track1/sector1 EXACTLY ONCE
/// (same command shape, same now-fixed-correct status/data as LOAD's reads), then reports "Disk
/// I/O error" immediately — no SENSE DRIVE STATUS (no write-protect check ever runs) and no
/// WRITE DATA is EVER attempted.
///
/// <b>Three-way SAVE comparison (2026-07-28, owner's own further follow-up — "I tried a save on
/// a clean disk... the error took a little longer to appear"), fully explaining the shape
/// difference but NOT the remaining root cause:</b>
/// <list type="bullet">
/// <item><see cref="Boot_ThenSaveTest_TraceFdcCommandsAndScreenOutput"/> (real <c>volorg.dsk</c>,
/// whose VOLORG entry's FCB happens to start with <c>0xF3</c>): reads sector 1 ONCE, fails
/// immediately.</item>
/// <item><see cref="Boot_ThenSaveTest_OnCleanBlankDisk_TraceFdcCommandsAndScreenOutput"/> (a
/// genuinely blank/unformatted disk, all-zero, no <c>0xF3</c> anywhere): scans all 16 sectors, in
/// the EXACT same order LOAD's own search uses, THEN fails — confirming the owner's "took longer"
/// observation was real, not an illusion.</item>
/// <item><see cref="Boot_ThenSaveTest_OnVolorgWithoutThe0xF3Byte_TraceFdcCommandsAndScreenOutput"/>
/// (real <c>volorg.dsk</c> bytes, UNCHANGED except patching that one byte 0xF3→0x00 — VOLORG's FCB
/// is STILL fully occupied/non-zero otherwise): ALSO scans all 16 sectors like the blank disk,
/// not just 1 — isolating the variable decisively. It is the <c>0xF3</c> BYTE VALUE specifically
/// (not "occupied vs. empty" directory content) that makes SAVE short-circuit after one read.</item>
/// </list>
/// This is very plausibly Disk BASIC's OWN legitimate design, not an emulator bug at all: `0xF3`
/// at this exact track1/sector1/offset0 location is the SAME byte the ROM's own <c>getdos</c>
/// checks to recognize a PDOS system disk (reference doc §5d) — real Disk BASIC's SAVE command
/// plausibly refuses to write to what it believes is a system disk, exactly like a real DOS
/// protecting its own boot media from being clobbered by a naive SAVE. VOLORG's own FCB colliding
/// with that exact byte value is the SAME "one genuine ambiguity in the format" already flagged
/// in `docs/P2000T-disk-formats.md` §7 item 8 (milestone 22a) — not a new finding, just a new
/// consequence of it observed here. <b>Critically, this does NOT explain the remaining bug</b>:
/// the blank disk (no `0xF3` anywhere) and the patched `volorg.dsk` (no `0xF3` anywhere) BOTH
/// still end in "Disk I/O error" after their full 16-sector scan — so the `0xF3` check only
/// governs HOW MANY sectors get scanned before failing, not WHETHER the command ultimately
/// succeeds. Whatever the still-unresolved root cause is, it sits downstream of (or is unrelated
/// to) this `0xF3` gate, and remains exactly as open as before this comparison — still nothing a
/// disassembly-free trace can pin down further.
/// </summary>
public class PdosLoadSaveRepro
{
    private readonly ITestOutputHelper _output;

    public PdosLoadSaveRepro(ITestOutputHelper output)
    {
        _output = output;
    }

    private const int BootTickLimit = 40_000_000;

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

    private static void RunUntil(Machine machine, Func<bool> condition, string failMessage, int limit = BootTickLimit)
    {
        for (var i = 0; i < limit; i++)
        {
            machine.Tick();
            if (condition()) return;
        }
        Assert.Fail($"{failMessage} (ran {limit:N0} T-states)");
    }

    /// <summary>Polls the screen (one field at a time) until it contains <paramref name="needle"/>
    /// or <paramref name="maxFields"/> is exhausted. Returns the last-seen screen text either way
    /// (never throws) so the caller can log it for diagnosis regardless of outcome.</summary>
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
        // 40x24 P2000T text screen. VRAM bytes are the raw glyph codes BASIC/monitor writes;
        // printable-ASCII range renders 1:1 for our purposes (see MatrixCharacterOutputTests.cs).
        var sb = new System.Text.StringBuilder();
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 40; col++)
            {
                var b = m.Memory.Read((ushort)(PageTable.VideoRamStart + row * 40 + col));
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? ' ' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // (row, col, shift) per character, sourced from src/P2000.UI/Input/KeyMap.cs (ground-truth
    // matrix table) + HostKeyTranslator.cs's independent quote confirmation.
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
        Ticks(machine, 5); // minimum field gap before the next key (KeyboardScanTimingTests finding)
    }

    private static void TypeString(Machine machine, string text)
    {
        foreach (var ch in text)
        {
            var (row, col, shift) = CharMap[ch];
            TypeChar(machine, row, col, shift);
        }
    }

    private static void PressEnter(Machine machine)
    {
        TypeChar(machine, EnterRow, EnterCol, false);
    }

    /// <summary>Boots the real repro machine (real cartridge + real boot floppy in drive 1) all
    /// the way through Disk BASIC's cold-start prompts to the ready ("Ok") prompt, wiring up an
    /// FDC trace as it goes. Shared by every test in this file so each only has to mount its own
    /// second-drive media and type its own command afterward.
    /// <paramref name="secondDiskPath"/>: a real fixture file for drive 2, mounted at construction
    /// (the LOAD/SAVE-against-an-existing-disk repros). <paramref name="blankDrive2"/>: when true
    /// (mutually exclusive with <paramref name="secondDiskPath"/>), drive 2 starts EMPTY at
    /// construction and a genuinely clean/unformatted <see cref="DskImage.CreateBlank"/> image
    /// (all-zero, no directory) is mounted live afterward — the owner's own 2026-07-28 follow-up
    /// scenario ("I tried a save on a clean disk").</summary>
    private Machine BootToReadyPrompt(string? secondDiskPath, out List<string> trace, bool blankDrive2 = false, byte[]? liveDrive2Bytes = null)
    {
        var repoRoot = FindRepoRoot();
        var cartridgePath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var bootFloppyPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");

        Assert.True(File.Exists(cartridgePath), $"missing fixture: {cartridgePath}");
        Assert.True(File.Exists(bootFloppyPath), $"missing fixture: {bootFloppyPath}");
        var modes = new[] { secondDiskPath is not null, blankDrive2, liveDrive2Bytes is not null };
        Assert.Equal(1, modes.Count(m => m));
        if (secondDiskPath is not null) Assert.True(File.Exists(secondDiskPath), $"missing fixture: {secondDiskPath}");

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
        if (blankDrive2) machine.Fdc.MountDisk(2, DskImage.CreateBlank(tracks: 35, sides: 1));
        if (liveDrive2Bytes is not null) machine.Fdc.MountDisk(2, new DskImage(liveDrive2Bytes));

        var localTrace = new List<string>();
        machine.Fdc.Trace = line => localTrace.Add(line);
        trace = localTrace;

        // ---- Boot ----
        RunUntil(machine, () => machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "boot never reached SLOT1 — disk-boot gate didn't fire");

        _output.WriteLine($"=== FDC trace right after getdos ({localTrace.Count} entries) ===");
        foreach (var line in localTrace) _output.WriteLine(line);

        // Disk BASIC 1.6's own cold-start sequence prompts for buffer count, then runtime
        // support, BEFORE reaching the ready prompt — answer both with their defaults (bare
        // Enter) rather than assuming a fixed tick count gets us straight to "Ok".
        var (foundFiles, screenFiles) = WaitForScreenContains(machine, "many files", maxFields: 3000);
        _output.WriteLine($"=== Screen when 'many files' prompt {(foundFiles ? "appeared" : "NEVER appeared")} ===");
        _output.WriteLine(screenFiles);
        Assert.True(foundFiles, "Disk BASIC's 'How many files?' prompt never appeared — boot didn't reach the expected startup sequence");
        PressEnter(machine);

        var (foundRuntime, screenRuntime) = WaitForScreenContains(machine, "Runtime support", maxFields: 3000);
        _output.WriteLine($"=== Screen when 'Runtime support' prompt {(foundRuntime ? "appeared" : "NEVER appeared")} ===");
        _output.WriteLine(screenRuntime);
        Assert.True(foundRuntime, "Disk BASIC's 'Runtime support?' prompt never appeared after answering the buffer-count prompt");
        PressEnter(machine);

        var (foundReady, screenReady) = WaitForScreenContains(machine, "Ok", maxFields: 3000);
        _output.WriteLine($"=== Screen when ready prompt {(foundReady ? "appeared" : "NEVER appeared")} ===");
        _output.WriteLine(screenReady);
        Assert.True(foundReady, "Disk BASIC never reached its ready ('bytes free') prompt after answering both startup prompts");
        Ticks(machine, 20); // let the prompt fully settle before typing

        return machine;
    }

    [Fact(Skip = "SUPERSEDED (2026-08-04, Part I): this test's own script assumed the CONFIRMED " +
        "BUG (Upd765's natural end-of-buffer completion racing PDOS's own semi-DMA polling loop " +
        "on a track's last sector) that made RUN\"VOLORG\" hang and report \"Disk I/O error\". " +
        "Part I fixed the root cause (Upd765.DeferNaturalCompletion) -- VOLORG.BAS now loads and " +
        "runs successfully (its own real menu, \"P 2000 DISK UTILITY\", confirmed on screen), " +
        "invalidating the specific counts/text this test pinned. See CLAUDE.md's Part I entry and " +
        "FourteenthOperationRedirectDiag.cs (the regression guard for this exact bug class). " +
        "Retained, skipped, for historical/investigative record only.")]
    public void Boot_ThenLoadVolorg_TraceFdcCommandsAndScreenOutput()
    {
        var repoRoot = FindRepoRoot();
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");
        var machine = BootToReadyPrompt(secondDiskPath, out var trace);
        var readyTraceCount = trace.Count;

        // ---- Type LOAD "B:VOLORG" + Enter ----
        // "B:" (docs/VolOrg manual.md's own confirmed `RUN "B:VOLORG"` syntax) explicitly
        // targets the second configured drive (internal index 2, where volorg.dsk is actually
        // mounted in this test) — plain "LOAD "VOLORG"" with no drive letter defaults to the
        // boot/system drive (internal index 1), whose own directory legitimately does NOT
        // contain VOLORG in this test's fixture setup, which would produce a genuine (and
        // uninteresting) file-not-found outcome rather than exercising the reported bug.
        TypeString(machine, "LOAD \"B:VOLORG\"");
        _output.WriteLine("=== Screen after typing (before Enter) ===");
        _output.WriteLine(SnapshotScreenText(machine));
        PressEnter(machine);

        // Let the command run: seeks, directory lookup, sector reads, and (per the repro) the
        // error path all need real T-states. Turbo makes transfers instant but the ROM/DOS's own
        // busy-loops and settle delays are real code either way.
        Ticks(machine, 600);

        _output.WriteLine("=== Screen after LOAD \"VOLORG\" + Enter ===");
        _output.WriteLine(SnapshotScreenText(machine));

        _output.WriteLine($"=== FDC trace after LOAD (total {trace.Count} entries, {trace.Count - readyTraceCount} new since ready prompt) ===");
        for (var i = readyTraceCount; i < trace.Count; i++) _output.WriteLine(trace[i]);

        // Regression guard for the three fixes above: before them, the driver retried the exact
        // SAME sector (R=1) identically 14-28 times before giving up. Confirm that specific
        // failure mode is gone — every READ DATA command issued during this LOAD must target a
        // DISTINCT sector (a real, advancing directory scan), never repeat one.
        var sectorsRead = new List<byte>();
        for (var i = readyTraceCount; i < trace.Count; i++)
        {
            if (!trace[i].StartsWith("CMD 46 ")) continue;
            var startSectorHex = trace[i].Split(' ')[5]; // cmd,HD/US,C,H,R,N,... — R is index 5
            sectorsRead.Add(Convert.ToByte(startSectorHex, 16));
        }
        Assert.NotEmpty(sectorsRead); // the LOAD must have actually attempted a READ DATA at all
        Assert.Equal(sectorsRead.Count, sectorsRead.Distinct().Count());

        // NOT asserted: that the LOAD ultimately succeeds. It still reports "Disk I/O error" here
        // even though the FDC-level trace now looks entirely correct (see class doc comment) —
        // an open question this test's own trace can't resolve further without a disassembly of
        // Philips Disk BASIC's resident driver.
    }

    /// <summary>Companion to the LOAD repro above (owner's own suggestion, 2026-07-28): SAVE is a
    /// genuinely different code path — it must find/allocate a FREE FCB slot and free data
    /// sectors, rather than search for an existing name match — so its trace could reveal whether
    /// the still-open LOAD failure is name-comparison-specific or something more structural (e.g.
    /// a write-protect gate, a free-space scan) shared by both commands. Uses an empty/default
    /// BASIC program (no program entered first) — if that turns out to matter, real Disk BASIC
    /// would report something distinctly different from "Disk I/O error" for "nothing to save".</summary>
    [Fact]
    public void Boot_ThenSaveTest_TraceFdcCommandsAndScreenOutput()
    {
        var repoRoot = FindRepoRoot();
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");
        var machine = BootToReadyPrompt(secondDiskPath, out var trace);
        var readyTraceCount = trace.Count;

        // ---- Type SAVE "B:TEST" + Enter ----
        TypeString(machine, "SAVE \"B:TEST\"");
        _output.WriteLine("=== Screen after typing (before Enter) ===");
        _output.WriteLine(SnapshotScreenText(machine));
        PressEnter(machine);

        Ticks(machine, 600);

        _output.WriteLine("=== Screen after SAVE \"B:TEST\" + Enter ===");
        _output.WriteLine(SnapshotScreenText(machine));

        _output.WriteLine($"=== FDC trace after SAVE (total {trace.Count} entries, {trace.Count - readyTraceCount} new since ready prompt) ===");
        for (var i = readyTraceCount; i < trace.Count; i++) _output.WriteLine(trace[i]);

        // No fixed assertion — this test exists purely to produce the trace above for inspection,
        // same spirit as the LOAD repro before its own regression assertion was added.
    }

    /// <summary>Owner's own 2026-07-28 follow-up: "I tried a save on a clean disk. also got an
    /// disk I/O error, but the error took (it seems) a little longer to appear." Same SAVE
    /// command, same drive geometry, but drive 2 gets a genuinely clean/unformatted
    /// <see cref="DskImage.CreateBlank"/> image (all-zero, no directory at all — the "New (blank)
    /// disk" case, reference doc §5d/§3a) instead of a real fixture already holding two files.
    /// A generous tick budget (2500 fields vs. the 600 used against <c>volorg.dsk</c>) covers the
    /// "takes longer" observation without assuming a specific cause for it up front.</summary>
    [Fact]
    public void Boot_ThenSaveTest_OnCleanBlankDisk_TraceFdcCommandsAndScreenOutput()
    {
        var machine = BootToReadyPrompt(secondDiskPath: null, out var trace, blankDrive2: true);
        var readyTraceCount = trace.Count;

        // ---- Type SAVE "B:TEST" + Enter ----
        TypeString(machine, "SAVE \"B:TEST\"");
        _output.WriteLine("=== Screen after typing (before Enter) ===");
        _output.WriteLine(SnapshotScreenText(machine));
        PressEnter(machine);

        Ticks(machine, 2500);

        _output.WriteLine("=== Screen after SAVE \"B:TEST\" + Enter (clean disk) ===");
        _output.WriteLine(SnapshotScreenText(machine));

        _output.WriteLine($"=== FDC trace after SAVE on a clean disk (total {trace.Count} entries, {trace.Count - readyTraceCount} new since ready prompt) ===");
        for (var i = readyTraceCount; i < trace.Count; i++) _output.WriteLine(trace[i]);

        // No fixed assertion — diagnostic only, same spirit as the other SAVE trace above.
    }

    /// <summary>Follow-up experiment to the two SAVE traces above: <c>volorg.dsk</c> (SAVE stops
    /// after ONE read) and a genuinely blank disk (SAVE scans all 16 sectors, same order LOAD's
    /// own search uses) differ in two ways at once — occupied vs. all-zero FCB content, AND
    /// VOLORG's own FCB happening to start with <c>0xF3</c> (the same byte value as the ROM's
    /// PDOS-system-disk signature, milestone 22a's own confirmed disambiguation case). This
    /// isolates which one actually matters: patches ONLY that one byte in a copy of
    /// <c>volorg.dsk</c> (0xF3 → 0x00, otherwise byte-identical — VOLORG's FCB is still fully
    /// occupied/non-zero) and re-runs SAVE. If the scan STILL stops after one read, occupied
    /// content itself is what matters (the 0xF3 coincidence is irrelevant, as already shown for
    /// LOAD). If the scan now runs all 16 sectors like the blank-disk case, the 0xF3 byte
    /// specifically — not "occupied vs. empty" — is what SAVE's own logic reacts to.</summary>
    [Fact]
    public void Boot_ThenSaveTest_OnVolorgWithoutThe0xF3Byte_TraceFdcCommandsAndScreenOutput()
    {
        var repoRoot = FindRepoRoot();
        var originalPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");
        var patched = File.ReadAllBytes(originalPath);
        Assert.Equal(0xF3, patched[0]); // sanity: this is really the byte we think it is
        patched[0] = 0x00;

        var machine = BootToReadyPrompt(secondDiskPath: null, out var trace, liveDrive2Bytes: patched);
        var readyTraceCount = trace.Count;

        TypeString(machine, "SAVE \"B:TEST\"");
        PressEnter(machine);
        Ticks(machine, 2500);

        _output.WriteLine("=== Screen after SAVE \"B:TEST\" + Enter (volorg.dsk, 0xF3 patched to 0x00) ===");
        _output.WriteLine(SnapshotScreenText(machine));

        _output.WriteLine($"=== FDC trace (total {trace.Count} entries, {trace.Count - readyTraceCount} new since ready prompt) ===");
        for (var i = readyTraceCount; i < trace.Count; i++) _output.WriteLine(trace[i]);

        var sectorsRead = new List<byte>();
        for (var i = readyTraceCount; i < trace.Count; i++)
        {
            if (!trace[i].StartsWith("CMD 46 ")) continue;
            sectorsRead.Add(Convert.ToByte(trace[i].Split(' ')[5], 16));
        }
        _output.WriteLine($"=== Sectors read: {string.Join(',', sectorsRead)} ===");

        // No fixed assertion — diagnostic only, this test exists to compare its sector-read count
        // against the other two SAVE traces above (1 for real volorg.dsk, 16 for a blank disk).
    }

    /// <summary>Searches the CURRENTLY selected bank's full 64K address space, plus every OTHER
    /// bank's own content in the 0xE000-0xFFFF banked window, for <paramref name="pattern"/> —
    /// diagnostic-only (owner's own 2026-07-28 banking hypothesis): finds out WHERE, if anywhere,
    /// the FDC-delivered directory bytes actually landed in RAM, and whether that location sits
    /// inside the same banked window the DOS driver's own code occupies. Restores the original
    /// bank selection before returning (read-only from the caller's perspective).</summary>
    /// <summary>Dumps the first <paramref name="count"/> bytes of a given bank's OWN content at
    /// <see cref="PageTable.BankedWindowStart"/>, restoring the original bank selection
    /// afterward — diagnostic-only, direct confirmation that a bank is really populated/distinct
    /// (not open-bus, not accidentally aliased to another bank), independent of searching for any
    /// specific text pattern.</summary>
    private static string DumpBank(Machine machine, byte bank, int count)
    {
        var original = machine.Memory.CurrentBank;
        machine.Memory.SelectBank(bank);
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = machine.Memory.Read((ushort)(PageTable.BankedWindowStart + i));
        machine.Memory.SelectBank(original);
        return string.Join(' ', Array.ConvertAll(bytes, b => b.ToString("X2")));
    }

    private static List<string> FindPattern(Machine machine, byte[] pattern)
    {
        var matches = new List<string>();
        var originalBank = machine.Memory.CurrentBank;

        for (var addr = 0; addr <= 0x10000 - pattern.Length; addr++)
        {
            var found = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (machine.Memory.Read((ushort)(addr + i)) != pattern[i]) { found = false; break; }
            }
            if (found) matches.Add($"current bank ({originalBank:X2}) @ 0x{addr:X4}");
        }

        for (byte b = 0; b < 6; b++)
        {
            if (b == originalBank) continue; // already covered above
            machine.Memory.SelectBank(b);
            for (var addr = PageTable.BankedWindowStart; addr <= PageTable.BankedWindowEnd - pattern.Length + 1; addr++)
            {
                var found = true;
                for (var i = 0; i < pattern.Length; i++)
                {
                    if (machine.Memory.Read((ushort)(addr + i)) != pattern[i]) { found = false; break; }
                }
                if (found) matches.Add($"bank {b:X2} @ 0x{addr:X4}");
            }
        }

        machine.Memory.SelectBank(originalBank);
        return matches;
    }

    /// <summary>Owner's own follow-up hypothesis (2026-07-28): "PDOS buffers the directory (in
    /// RAM) ... could it be that something is not working well in that caching area? Basic24
    /// needs at least a switchable bank." Confirmed first (see the trace in the tests above):
    /// after the directory scan, ZERO further FDC activity occurs before "Disk I/O error" prints —
    /// the failing check is evaluated ENTIRELY from data already in RAM, consistent with a
    /// directory CACHE rather than a live per-sector search. This test traces every bank-select
    /// write (<see cref="PageTable.BankSelected"/>) during the SAVE attempt against the real
    /// (unpatched) <c>volorg.dsk</c> — so the read stops after track1/sector1 alone (the 0xF3
    /// short-circuit already isolated above) — and searches every bank for the "VOLORG" text the
    /// FDC is known to have delivered (<c>transferIndex=256</c>, confirmed correct content) to see
    /// where, if anywhere, it was actually stored.
    ///
    /// <b>Owner's direct follow-up (2026-07-28): "Did you check that anything landed in banked
    /// memory and that the banks are present, and that the switches have the desired effect? The
    /// boot ROM doesn't perform extensive banked memory tests..." — a fair challenge, since the
    /// original pass only searched for the delivered directory bytes, it didn't verify the banking
    /// mechanism itself. Three things checked, all confirming the banking mechanism itself is
    /// sound (though NOT ruling out something more subtle a disassembly might still reveal):</b>
    /// <list type="number">
    /// <item><b>Real coverage gap closed:</b> every PRIOR banking test only ever verified banks 0
    /// and 1 (`BankedWindow_SelectBank_SwitchesToAnIsolatedBank`) — banks 2-5 had literally never
    /// been checked for isolation. New `PageTableTests.BankedWindow_AllSixT102Banks_AreMutuallyIsolated`
    /// closes this: writes a distinct marker to all 6 T102 banks and confirms all 6 persist
    /// independently. Passes.</item>
    /// <item><b>Banks are genuinely present, populated, and distinct in THIS live machine, not
    /// just in isolated `PageTableTests` construction:</b> dumping all 6 banks' own first 16 bytes
    /// at 0xE000 shows bank 1 holding recognizable, plausible Z80 machine code
    /// (`F3 ED 5E DD 22 32 F5 FB DB 00 3C 20 FB F3 ED 73` = DI / IM2 / LD (nn),IX / LD (nn),A / EI /
    /// IN A,(0) / INC A / JR NZ,-3 / DI / LD (nn),SP — a sensible ISR-setup preamble, consistent
    /// with this being exactly the DOS driver code `getdos` loaded into bank 1 at boot), banks 2-5
    /// showing DIFFERENT, still-untouched pseudo-random power-on noise (allocated correctly, simply
    /// never written by this particular PDOS/Basic-24 session), and bank 0 reading all-zero
    /// (plausibly Disk BASIC's own cleared extended-program-workspace use of that bank, not a disk
    /// buffer — orthogonal to the disk driver entirely). Nothing here looks like open-bus,
    /// aliasing, or cross-bank corruption.</item>
    /// <item><b>Switches DO have real, observable effect during the live SAVE attempt:</b> exactly
    /// 12 real bank-select writes occur (alternating 0x01/0x00) while attempting to save just one
    /// sector's worth of directory data — consistent with BASIC's own code (running with bank 0
    /// selected) repeatedly calling INTO DOS driver subroutines (living in bank 1) and switching
    /// back after each one, a real, observable cross-bank call pattern, not a single call. (A prior
    /// draft of this exact test miscounted this at first — its own `DumpBank` helper temporarily
    /// re-selects banks to peek at their content, and briefly logged those instrumentation-induced
    /// switches into the SAME log meant for the driver's own — fixed by dumping the "before" banks
    /// BEFORE subscribing to the trace, and unsubscribing before the "after" dump.)</item>
    /// </list>
    /// <b>What this does and doesn't settle:</b> the banking MECHANISM itself (isolation,
    /// persistence, real effect on addressing) checks out — this doesn't look like a `PageTable`
    /// bug. It does NOT rule out a more specific timing/ordering issue in how disk I/O interacts
    /// with a bank switch (e.g., a switch happening one instruction earlier/later than a real chip
    /// would allow) — exactly the kind of thing only a disassembly could confirm one way or the
    /// other; the trace evidence available here is consistent with "the banking half of this is
    /// fine" but can't prove a negative beyond that.</summary>
    [Fact]
    public void Boot_ThenSaveTest_TracesBankSelectionAndSearchesMemoryForDeliveredDirectoryBytes()
    {
        var repoRoot = FindRepoRoot();
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");
        var machine = BootToReadyPrompt(secondDiskPath, out var trace);
        var readyTraceCount = trace.Count;

        _output.WriteLine($"=== Bank selected right before typing SAVE: 0x{machine.Memory.CurrentBank:X2} ===");

        // Owner's own follow-up (2026-07-28): confirm banks are genuinely present/populated and
        // distinct in THIS exact live machine (not just PageTableTests' isolated construction) —
        // dump all 6 banks' own first 16 bytes at 0xE000, before touching SAVE OR subscribing to
        // BankSelected (DumpBank itself calls SelectBank to peek at each bank, which would
        // otherwise pollute the bank-switch log below with our own instrumentation's switches,
        // not just the driver's real ones — a mistake made and caught during this same pass).
        _output.WriteLine("=== All 6 banks' first 16 bytes at 0xE000, BEFORE typing SAVE ===");
        for (byte b = 0; b < 6; b++) _output.WriteLine($"  bank {b}: {DumpBank(machine, b, 16)}");

        var bankLog = new List<(int TraceIndex, byte Bank)>();
        void OnBankSelected(byte b) => bankLog.Add((trace.Count, b));
        machine.Memory.BankSelected += OnBankSelected;

        TypeString(machine, "SAVE \"B:TEST\"");
        PressEnter(machine);
        Ticks(machine, 600);

        machine.Memory.BankSelected -= OnBankSelected; // stop logging BEFORE our own dump's switches

        _output.WriteLine("=== All 6 banks' first 16 bytes at 0xE000, AFTER SAVE failed ===");
        for (byte b = 0; b < 6; b++) _output.WriteLine($"  bank {b}: {DumpBank(machine, b, 16)}");

        _output.WriteLine("=== Screen after SAVE \"B:TEST\" + Enter ===");
        _output.WriteLine(SnapshotScreenText(machine));

        _output.WriteLine($"=== FDC trace ({trace.Count - readyTraceCount} new since ready prompt) ===");
        for (var i = readyTraceCount; i < trace.Count; i++) _output.WriteLine(trace[i]);

        _output.WriteLine($"=== Bank-select writes during this window ({bankLog.Count}) ===");
        foreach (var (idx, bank) in bankLog) _output.WriteLine($"  at FDC-trace-index {idx}: bank -> 0x{bank:X2}");
        _output.WriteLine($"=== Bank selected right after the command completed: 0x{machine.Memory.CurrentBank:X2} ===");

        // "VOLORG  BAS" as it appears in the FCB: name (space-padded to 8) + extension, starting
        // right after the 0xF3 flag byte — confirmed real bytes from the dump earlier in this
        // investigation (56 4F 4C 4F 52 47 20 20 42 41 53 = "VOLORG  BAS").
        var pattern = new byte[] { 0x56, 0x4F, 0x4C, 0x4F, 0x52, 0x47, 0x20, 0x20, 0x42, 0x41, 0x53 };
        var matches = FindPattern(machine, pattern);
        _output.WriteLine($"=== \"VOLORG  BAS\" found in RAM at: {(matches.Count == 0 ? "NOWHERE" : string.Join(", ", matches))} ===");

        // No fixed assertion — purely diagnostic, to see whether the delivered directory bytes
        // are cached anywhere findable and whether any bank switch happens around the transfer.
    }
}

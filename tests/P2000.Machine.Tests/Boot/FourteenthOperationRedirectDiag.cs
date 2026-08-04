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
/// Part I (cc-bugfix-prompt-15) — traces the 14th disk operation's completion-to-return path
/// precisely, reconciling Part B's own "channel 0 fires and delivers correctly for all 15
/// completions" claim with Part H's own "the 14th CALL 6205h never returns" finding.
///
/// The redirect mechanism itself (read directly from <c>docs/PDOS_wip.asm</c>, not re-derived):
/// PDOS's F_READ handler (<c>sub_f137h</c>, 0xF137, reached from the jump-table target 0xF3A0)
/// checks CR&lt;RC, then (via <c>lf170h</c>, 0xF170) calls <c>Seek_to_track</c> then
/// <c>sub_e8b3h</c> (0xE8B3) to issue the physical READ DATA command. <c>sub_e8b3h</c> calls
/// <c>sub_e8c0h</c> (0xE8C0, <c>ld hl,le916h</c>) which falls into
/// <c>issue_Disk_read_command</c>/<c>issue_Disk_write_command</c> (0xE8C3) — THIS is the routine
/// the owner's prompt calls "sub_e8c3h": at 0xE8C8 it does <c>ld (06135h),hl</c>, patching the
/// 2-byte JP-target operand embedded in a small ISR trampoline PDOS copies into RAM at 0x6130
/// once, at startup (<c>Set_time_out_comm_irq</c>). CTC channel 0's IM2 vector ALWAYS points at
/// this fixed 0x6130 trampoline (never changes); what changes per-call is the JP target patched
/// at 0x6130+5 (=0x6135) — for a real physical read via <c>sub_e8b3h</c>, that target is always
/// <c>le916h</c> (0xE916), which does <c>pop hl</c> (discards the busy-wait's own interrupted
/// return address) / <c>ld hl,le8f7h</c> / falls into <c>End_RW_action</c> (pushes le8f7h, EI,
/// RETI) — i.e. RETI "returns" not into the busy-wait loop but into <c>le8f7h</c> (0xE8F7),
/// which re-checks drive status (<c>Get_7_disk_status_bytes</c>/<c>sub_e937h</c>) and eventually
/// calls <c>sub_e96fh</c> (0xE96F), which itself either returns cleanly (drive matched) or falls
/// into <c>channel_time_out</c> (0xE978, calling <c>sub_e943h</c> — the "always error" routine).
/// <c>busy_wait_for_interrupt</c> (0xE95F) has its OWN natural 65536-iteration timeout that,
/// if reached WITHOUT ever being redirected away by the interrupt above, falls through to
/// <c>jr channel_time_out</c> at 0xE96D — this is the alternate, non-interrupt-driven path to
/// the exact same <c>channel_time_out</c>/<c>sub_e943h</c> call site Part C's own trace found
/// (0xE978), meaning a bare "which address called sub_e943h" trace (as Part C's own
/// <see cref="Sube943hCallerDiag"/> did) CANNOT distinguish "busy-wait naturally timed out,
/// interrupt never redirected" from "interrupt redirected fine, but sub_e96fh's own drive check
/// then failed and fell through to channel_time_out anyway". This test distinguishes them
/// directly by watching whether PC ever GENUINELY reaches 0xE916 (redirect landing) for the 14th
/// read cycle specifically, versus reaching 0xE96D (busy-wait's own natural-timeout fallthrough).
///
/// <b>CONFIRMED ROOT CAUSE (Part I, cc-bugfix-prompt-15):</b> the redirect target at 0x6135 is
/// genuinely <c>le916h</c> for every one of the 15 physical reads (an earlier pass of this test
/// mis-read a STALE value at 0x6135 by sampling one instruction too early, before the 16-T-state
/// <c>LD (nn),HL</c> write had actually committed -- fixed by sampling later, at 0xE8D2). Channel
/// 0 fires and delivers correctly for the 14th completion too. The decisive difference is WHAT
/// WAS INTERRUPTED: for reads 1-13, the interrupt's own int-ack-pushed return address is always
/// 0xE969 (a clean instruction boundary inside <c>busy_wait_for_interrupt</c>'s own idle loop) --
/// but for the 14th (last) read, it is 0x6150 (a clean instruction boundary INSIDE
/// <c>dsk_in_loop</c>, PDOS's own semi-DMA byte-transfer polling loop, between its own
/// <c>dec e</c> and the following <c>jp nz</c>). Cross-referencing <c>Upd765</c>'s own COMPLETE
/// trace explains why: PDOS's FDC command always requests a wide EOT window (EOT=16, fixed) while
/// its software polling loop (<c>dsk_in_loop</c>, governed by E=1) only ever consumes exactly ONE
/// sector (confirmed: <c>transferIndex</c> is 256 at every single completion) -- so for every
/// read except the last, <c>_transferIndex</c> (256) stays well short of the nominal
/// <c>_transferBuffer.Length</c> (up to 4096, since <c>sectorCount = EOT-R+1</c>), and completion
/// only happens because <c>dsk_io_done</c> (PDOS's own polling-loop exit) explicitly writes the
/// FDC's TC (terminal count) bit -- which this emulator deliberately defers by
/// <c>MinimumLostWakeupGuardTStates</c> (200 T-states, a real, intentional fix from 2026-07-28 for
/// an already-diagnosed "lost wakeup" race), giving the CPU's own return-to-busy-wait path a safe
/// head start. But for the LAST sector of the track (R=16), the nominal window collapses to
/// EXACTLY one sector -- matching what the software polls -- so the transfer instead completes
/// via <c>Upd765.ReadData</c>'s own NATURAL, perfectly SYNCHRONOUS end-of-buffer check, with NO
/// equivalent settle delay. The interrupt fires immediately, catching the CPU still inside
/// <c>dsk_in_loop</c> rather than already idling in <c>busy_wait_for_interrupt</c>. PDOS's own
/// redirect handler (<c>le916h</c>) unconditionally discards whatever return context was
/// interrupted -- for every other read this harmlessly discards a throwaway busy-wait resume
/// point, but here it discards <c>dsk_in_loop</c>'s own resume point instead. The surviving,
/// untouched STACK FRAME one level further down (<c>read_disk_bytes</c>'s own real call-return
/// address, pointing at <c>le7b8h</c>'s trailing <c>jp busy_wait_for_interrupt</c>) then gets
/// popped by <c>le8f7h</c>'s own unrelated <c>ret</c> -- so execution accidentally re-enters
/// <c>busy_wait_for_interrupt</c> FRESH, waits for a SECOND interrupt that will never come (the
/// FDC has nothing left to signal), and genuinely times out ~3.8M T-states later (confirmed: the
/// one genuine BC==0 exhaustion in this trace lands at t=6,304,191, exactly 3,802,541 T-states
/// after the 14th completion at t=2,501,650 -- matching the "~3.8M T-states" figure already cited
/// in the project's own Part B findings entry).
///
/// This is very likely a genuine EMULATOR TIMING GAP, not a PDOS/BASIC bug and not necessarily a
/// real-hardware fault: the TC-forced completion path already got an explicit settle-delay fix
/// for exactly this class of race (2026-07-28); the natural/synchronous end-of-buffer completion
/// path in <c>Upd765.ReadData</c>/<c>WriteData</c> never received the analogous treatment,
/// because no prior test exercised a transfer whose natural end coincides with what the driving
/// software's own polling loop consumes. Real silicon's own completion-to-INT-line propagation is
/// very unlikely to be perfectly zero-latency the way this synchronous C# check is -- which would
/// reconcile the owner's real-P2000M data point (no error on real hardware) without requiring
/// PDOS's own read protocol (request-wide-window-then-partial-consume-plus-explicit-TC) to be
/// considered fragile or wrong in itself.
///
/// <b>FIXED (2026-08-04, same pass):</b> <c>Upd765.ReadData</c>/<c>WriteData</c>'s natural
/// end-of-buffer completion now defers via the SAME <c>MinimumLostWakeupGuardTStates</c> guard the
/// TC-forced path already had (<see cref="P2000.Machine.Devices.Fdc.Upd765"/>'s new
/// <c>DeferNaturalCompletion</c>, <c>PendingAction.NaturalCompletion</c>), removing the asymmetry.
/// <b>Confirmed end-to-end:</b> `RUN"VOLORG"` now loads and runs VOLORG.BAS successfully --
/// its own real menu ("P 2000 P 2000") renders on screen, with no "Disk I/O error" anywhere
/// across RESET/SYSTEM B/FILES/RUN"VOLORG". This test itself was updated (below) from asserting
/// the bug's own exact symptom (15 reads then a timeout) to asserting the fix's own invariant:
/// every redirect landing's popped return address is now uniformly 0xE969 (busy_wait's own idle
/// loop), never 0x6150 (mid-transfer), and no natural busy-wait timeout occurs at all within the
/// trace window.
/// </summary>
public class FourteenthOperationRedirectDiag
{
    private readonly ITestOutputHelper _output;
    public FourteenthOperationRedirectDiag(ITestOutputHelper output) => _output = output;

    private const ushort DiskIoErrorFlag = 0x6091;

    // PDOS bank-1 addresses, all confirmed via the "l<hex>h"/"sub_<hex>h" naming convention
    // (these disassembler-generated labels ARE their own hex addresses) cross-checked against
    // the file's own ";eXXX raw-byte" comments at several anchor points -- see this class's own
    // doc comment above for the full derivation chain.
    private const ushort SubE8b3h_IssuePhysicalRead = 0xE8B3;
    private const ushort PatchRedirectInstr_0xE8C8 = 0xE8C8; // ld (06135h),hl
    // NOTE: 0xE8CB (the instruction right after the 3-byte/16-T-state "ld (06135h),hl") is TOO
    // EARLY to read the freshly-patched value -- this project's cycle-stepped core can show PC at
    // the next instruction's address before a multi-T-state instruction's own memory WRITE has
    // actually committed (same class of timing subtlety already hit for RET elsewhere in this
    // investigation, here affecting a 16-T-state LD (nn),HL rather than a RET). Read several
    // instructions later instead, at 0xE8D2 (right before "call send_disk_command"), by which
    // point the write is unambiguously long done.
    private const ushort AfterPatchSettled_0xE8D2 = 0xE8D2;
    private const ushort Le916h_RedirectLanding = 0xE916;
    private const ushort Le8f7h_PostRedirectCheck = 0xE8F7;
    private const ushort SubE96fh_DriveRecheck = 0xE96F;
    private const ushort ChannelTimeOut = 0xE978;
    private const ushort BusyWaitNaturalTimeoutFallthrough = 0xE96D; // jr channel_time_out (65536 iterations exhausted)
    private const ushort RedirectTarget_0x6135 = 0x6135;

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

    private static readonly FieldInfo ChannelsField = typeof(Z80Ctc)
        .GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static IDaisyChainDevice GetChannel(Z80Ctc ctc, int index)
    {
        var channels = (Array)ChannelsField.GetValue(ctc)!;
        return (IDaisyChainDevice)channels.GetValue(index)!;
    }

    private enum EventKind { FdcComplete, IssuePhysicalRead, PatchInstalled, RedirectLanding, PostRedirectCheck, DriveRecheck, NaturalTimeout, Ch0IntPending, Ch0InService, Ch0Cleared }

    [Fact]
    public void RunVolorg_TracesThe14thOperationsRedirectPathPrecisely()
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

        var fdcCompletions = new List<(long T, string Line)>();
        machine.Fdc.Trace = line =>
        {
            if (line.StartsWith("COMPLETE kind=ReadData"))
                fdcCompletions.Add((tCounterRef, line));
        };

        TypeString(machine, "RUN\"VOLORG\"");
        PressEnter(machine);

        _output.WriteLine("=== Tracing the redirect chain for every physical read issued during RUN\"VOLORG\" ===");

        var events = new List<(long T, EventKind Kind, string Detail)>();
        ushort? lastPc = null;
        var lastIntPending = ch0.IntPending;
        var lastInService = ch0.InService;
        var naturalTimeoutRawHits = 0;
        long tCounter;
        tCounterRef = 0;

        for (tCounter = 0; tCounter < 10_000_000L; tCounter++)
        {
            tCounterRef = tCounter;
            machine.Tick();
            var pc = machine.Cpu.Reg.PC;

            if (ch0.IntPending != lastIntPending || ch0.InService != lastInService)
            {
                if (ch0.IntPending && !lastIntPending) events.Add((tCounter, EventKind.Ch0IntPending, ""));
                if (ch0.InService && !lastInService) events.Add((tCounter, EventKind.Ch0InService, ""));
                if (!ch0.InService && lastInService) events.Add((tCounter, EventKind.Ch0Cleared, ""));
                lastIntPending = ch0.IntPending;
                lastInService = ch0.InService;
            }

            if (pc != lastPc)
            {
                switch (pc)
                {
                    case SubE8b3h_IssuePhysicalRead:
                        events.Add((tCounter, EventKind.IssuePhysicalRead, ""));
                        break;
                    case AfterPatchSettled_0xE8D2:
                        {
                            var target = ReadWord(machine, RedirectTarget_0x6135);
                            events.Add((tCounter, EventKind.PatchInstalled, $"0x6135=0x{target:X4}"));
                            break;
                        }
                    case Le916h_RedirectLanding:
                        {
                            var sp = machine.Cpu.Reg.SP;
                            var poppedAddr = ReadWord(machine, sp);
                            events.Add((tCounter, EventKind.RedirectLanding, $"SP=0x{sp:X4} about-to-pop=0x{poppedAddr:X4}"));
                            break;
                        }
                    case Le8f7h_PostRedirectCheck:
                        events.Add((tCounter, EventKind.PostRedirectCheck, ""));
                        break;
                    case SubE96fh_DriveRecheck:
                        events.Add((tCounter, EventKind.DriveRecheck, ""));
                        break;
                    case BusyWaitNaturalTimeoutFallthrough:
                        {
                            // le962h's own "jr nz,le962h" (E96B-E96C) is a 2-byte JR; per this
                            // investigation's established gotcha (Parts C/E), PC can transiently
                            // show the NEXT-sequential address (E96D) mid-decode even when the
                            // branch IS taken (looping back), not just on a genuine fallthrough.
                            // Only count/log a REAL exhaustion: BC==0 (the "or c" zero-check that
                            // gates the fallthrough) must actually hold.
                            var bc = machine.Cpu.Reg.BC;
                            if (bc == 0)
                                events.Add((tCounter, EventKind.NaturalTimeout, $"BC=0x{bc:X4} (genuine exhaustion)"));
                            naturalTimeoutRawHits++;
                            break;
                        }
                }
            }
            lastPc = pc;
        }

        _output.WriteLine($"=== FDC COMPLETE (ReadData) events: {fdcCompletions.Count} ===");
        foreach (var (t, line) in fdcCompletions) _output.WriteLine($"  FDC-COMPLETE t={t,10}  {line}");
        _output.WriteLine($"=== Raw (unfiltered) 0xE96D hits: {naturalTimeoutRawHits} (expect ~1 per busy-wait loop iteration -- JR-decode artifact, not real) ===");
        _output.WriteLine("=== Event trace (key addresses, NaturalTimeout filtered to genuine exhaustions only) ===");
        foreach (var (t, kind, detail) in events)
            _output.WriteLine($"t={t,10}  {kind,-20}  {detail}");

        var issueCount = events.Count(e => e.Kind == EventKind.IssuePhysicalRead);
        var patchCount = events.Count(e => e.Kind == EventKind.PatchInstalled);
        var redirectLandingCount = events.Count(e => e.Kind == EventKind.RedirectLanding);
        var postRedirectCount = events.Count(e => e.Kind == EventKind.PostRedirectCheck);
        var driveRecheckCount = events.Count(e => e.Kind == EventKind.DriveRecheck);
        var naturalTimeoutCount = events.Count(e => e.Kind == EventKind.NaturalTimeout);
        var ch0IntPendingCount = events.Count(e => e.Kind == EventKind.Ch0IntPending);
        var ch0InServiceCount = events.Count(e => e.Kind == EventKind.Ch0InService);
        var ch0ClearedCount = events.Count(e => e.Kind == EventKind.Ch0Cleared);

        _output.WriteLine($"=== sub_e8b3h (issue physical read) entries: {issueCount} ===");
        _output.WriteLine($"=== 0x6135 patch-installed entries: {patchCount} ===");
        _output.WriteLine($"=== le916h (redirect landing) entries: {redirectLandingCount} ===");
        _output.WriteLine($"=== le8f7h (post-redirect status check) entries: {postRedirectCount} ===");
        _output.WriteLine($"=== sub_e96fh (drive recheck) entries: {driveRecheckCount} ===");
        _output.WriteLine($"=== GENUINE 0xE96D natural-timeout exhaustions: {naturalTimeoutCount} ===");
        _output.WriteLine($"=== ch0 IntPending set: {ch0IntPendingCount}  InService set: {ch0InServiceCount}  Cleared: {ch0ClearedCount} ===");

        // VOLORG.BAS now genuinely loads and runs its own real menu program (fixed, per this
        // class's own doc comment) rather than hanging -- give it a bounded extra window to finish
        // rendering that menu before snapshotting the screen (the 10M-T-state trace loop above ends
        // mid-render on some runs; this is just draining the last bit of a real, finite render, not
        // waiting on anything indefinite).
        var (foundMenu, screenAfterWait) = WaitForScreenContains(machine, "P 2000", maxFields: 500);
        _output.WriteLine($"Menu text found within the extra wait window: {foundMenu}");
        _output.WriteLine($"Final flag(6091)=0x{ReadFlag(machine):X2}");
        _output.WriteLine("Final screen:");
        var finalScreen = screenAfterWait;
        _output.WriteLine(finalScreen);

        // FIXED (2026-08-04, same pass): every physical read issues via sub_e8b3h with 0x6135
        // genuinely patched to le916h -- the redirect itself was never mis-armed, before or after
        // the fix. VOLORG.BAS now reads far more than the old 15-completion ceiling (its own real
        // 44-record file, plus whatever FILES/SYSTEM B already did before RUN"VOLORG").
        Assert.True(issueCount >= 15, $"expected at least 15 physical reads, got {issueCount}");
        Assert.Equal(issueCount, patchCount);
        Assert.Equal(issueCount, redirectLandingCount);

        // THE FIX'S OWN INVARIANT, replacing the old bug-specific assertion: EVERY redirect
        // landing's int-ack-pushed return address is now uniformly 0xE969 (busy_wait_for_
        // interrupt's own idle loop) -- never 0x6150 (PDOS's semi-DMA polling loop, dsk_in_loop),
        // which was the root of the whole "Disk I/O error" investigation (see this class's own
        // doc comment). DeferNaturalCompletion() removes the asymmetry that let the interrupt for
        // a track's last sector race ahead of PDOS's own software bookkeeping.
        var poppedAddresses = events.Where(e => e.Kind == EventKind.RedirectLanding)
            .Select(e => ushort.Parse(e.Detail.Split("about-to-pop=0x")[1], System.Globalization.NumberStyles.HexNumber))
            .ToList();
        Assert.Equal(issueCount, poppedAddresses.Count);
        Assert.All(poppedAddresses, addr => Assert.Equal((ushort)0xE969, addr));
        Assert.DoesNotContain((ushort)0x6150, poppedAddresses);

        // FIXED: no genuine busy-wait exhaustion occurs at all now -- every completion, including
        // ones whose nominal bufferLength collapses to exactly one sector (the specific case that
        // used to race ahead), now correctly reaches busy_wait_for_interrupt before its own
        // interrupt fires.
        Assert.Equal(0, naturalTimeoutCount);

        // FIXED, confirmed end-to-end: VOLORG.BAS is a real menu program ("P 2000 P 2000")
        // and it now renders its own menu on screen -- no "Disk I/O error" anywhere.
        Assert.DoesNotContain("Disk I/O error", finalScreen);
        Assert.Contains("P 2000", finalScreen);
    }

    // Backing field for the FDC trace callback (captured by ref via a field since the lambda is
    // registered before the loop's own local t-counter exists).
    private long tCounterRef;
}

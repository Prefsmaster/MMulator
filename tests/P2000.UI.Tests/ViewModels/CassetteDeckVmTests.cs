using System.Text;
using CommunityToolkit.Mvvm.Input;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="CassetteDeckVm"/>'s "New (blank) tape" + Save/Save-as wiring (§14
/// milestone 13). The StorageProvider-driven halves of <c>SaveAsync</c>/<c>SaveAsAsync</c>/
/// <c>MountAsync</c> are not unit-tested here — same limitation noted for
/// <c>MemoryWatchVmTests</c>/<c>DisplayWindowVm</c>: a headless test run has no real desktop
/// <c>TopLevel</c>, so the file picker never fires. What IS tested: the VM's state transitions
/// (<c>HasTape</c>/<c>TapeLabel</c>/<c>IsWriteProtected</c>), the Save/SaveAs `CanExecute`
/// wiring, and — via the underlying <c>MdcrDevice</c> the VM drives — that "New (blank) tape"
/// never observes CIP go absent when swapping over an already-mounted tape.
/// </summary>
public class CassetteDeckVmTests
{
    private static CassetteDeckVm NewVm() => new(new EmulationRunner());

    private static byte[] OneCasRecord() => new byte[1280];

    [Fact]
    public void Initial_NoTape_SaveAndSaveAsDisabled()
    {
        var vm = NewVm();
        Assert.False(vm.HasTape);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
        Assert.False(vm.EjectCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_SetsHasTapeTrue_Unbacked_Writable()
    {
        var vm = NewVm();

        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(vm.HasTape);
        Assert.Equal("(blank tape)", vm.TapeLabel);
        Assert.False(vm.IsWriteProtected);
        Assert.Empty(vm.Programs);
    }

    [Fact]
    public void NewBlankTape_EnablesSaveAndSaveAs()
    {
        var vm = NewVm();
        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.SaveAsCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_MdcrHasTapeAndCipPresent()
    {
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);

        vm.NewBlankTapeCommand.Execute(null);

        Assert.True(runner.Machine.Mdcr.HasTape);
        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // CIP clear = present
    }

    [Fact]
    public void MountBytes_NoProtectByte_DefaultsWritable()
    {
        // Regression check for the reported "always write-protected" symptom (§14.13a test
        // (a)): the old code hardcoded writeProtect:true on every file-loaded mount. Now
        // protect is read from the file itself (record offset 0x50 bit 0) — a plain record
        // with no protect byte set must default writable.
        var vm = NewVm();

        vm.MountBytes(OneCasRecord(), "GHOSTHUNT");

        Assert.True(vm.HasTape);
        Assert.Equal("GHOSTHUNT", vm.TapeLabel);
        Assert.False(vm.IsWriteProtected);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void MountBytes_WithProtectByteSet_IsProtected()
    {
        var vm = NewVm();
        var cas = OneCasRecord();
        cas[0x50] |= 0x01;

        vm.MountBytes(cas, "PROTECTEDPROG");

        Assert.True(vm.IsWriteProtected);
    }

    [Fact]
    public void IsWriteProtected_ToggleLive_FlipsMachineWen_WithoutTouchingCip()
    {
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);
        vm.NewBlankTapeCommand.Execute(null);
        Assert.False(vm.IsWriteProtected);

        vm.IsWriteProtected = true;

        Assert.True(runner.Machine.Mdcr.IsWriteProtected);
        Assert.Equal(0x08, runner.Machine.Mdcr.ReadStatus() & 0x08); // WEN set
        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // CIP still present

        vm.IsWriteProtected = false;
        Assert.False(runner.Machine.Mdcr.IsWriteProtected);
    }

    [Fact]
    public void IsWriteProtected_DisabledWithNoTapeMounted()
    {
        var vm = NewVm();
        Assert.False(vm.HasTape);
        // Setting it with no tape must not throw (SetWriteProtected no-ops with no tape).
        vm.IsWriteProtected = true;
    }

    [Fact]
    public void ToggleWriteProtectCommand_DisabledWithNoTape_EnabledOnceMounted()
    {
        var vm = NewVm();
        Assert.False(vm.ToggleWriteProtectCommand.CanExecute(null));

        vm.NewBlankTapeCommand.Execute(null);
        Assert.True(vm.ToggleWriteProtectCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleWriteProtectCommand_FlipsIsWriteProtected_AndTheMachine()
    {
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);
        vm.NewBlankTapeCommand.Execute(null);
        Assert.False(vm.IsWriteProtected);

        vm.ToggleWriteProtectCommand.Execute(null);
        Assert.True(vm.IsWriteProtected);
        Assert.True(runner.Machine.Mdcr.IsWriteProtected);

        vm.ToggleWriteProtectCommand.Execute(null);
        Assert.False(vm.IsWriteProtected);
        Assert.False(runner.Machine.Mdcr.IsWriteProtected);
    }

    [Fact]
    public void Eject_ClearsHasTapeAndDisablesSave()
    {
        var vm = NewVm();
        vm.NewBlankTapeCommand.Execute(null);
        Assert.True(vm.HasTape);

        vm.EjectCommand.Execute(null);

        Assert.False(vm.HasTape);
        Assert.Equal("No cassette", vm.TapeLabel);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SaveAsCommand.CanExecute(null));
    }

    [Fact]
    public void NewBlankTape_OverAlreadyMountedTape_CipStaysPresentThroughout()
    {
        // "One CIP transition, not two" (§14 milestone 13 test (e)): swapping straight from a
        // mounted tape to a blank one must never pass through the "no cassette" state.
        var runner = new EmulationRunner();
        var vm = new CassetteDeckVm(runner);

        vm.MountBytes(OneCasRecord(), "SOMEPROG");
        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // present

        vm.NewBlankTapeCommand.Execute(null);

        Assert.Equal(0x00, runner.Machine.Mdcr.ReadStatus() & 0x10); // still present
        Assert.True(vm.HasTape);
        Assert.Equal("(blank tape)", vm.TapeLabel);
    }

    // Note: the directory-refresh-on-motor-stop fix (RefreshDirectoryFromLiveTape, wired from
    // OnFrameReady) is not unit-tested here. A live Machine actively runs the embedded ROM,
    // which drives CPOUT itself (keyboard scan, its own CIP-triggered auto-load attempt on a
    // freshly-mounted tape) — any CPOUT value forced from a test gets overwritten by ordinary
    // ROM execution within the same field, on the runner's thread or not, so there's no clean
    // way to synthesize a "motor on then off" edge without driving a real CSAVE/CLOAD through
    // BASIC. Same gap already exists for this class's HasTape 5 Hz resync, also untested here.
    // Verified instead by manual live-app testing (owner-confirmed, 2026-07-14).

    [Fact]
    public void ParseDirectory_BlockCount_UsesCeilingDivisionNotFloor()
    {
        // Owner-reported bug: 5673 occupied bytes showed 5 blocks (should be 6); 40 occupied
        // bytes showed 0 blocks (should be 1). Byte 0x1F ("block counter") was tried as a
        // direct source but is empirically always zero in real .cas files (owner inspection,
        // 2026-07-14) — not usable. "Occupied" (0x02-0x03) isn't always block-aligned either,
        // so this must be a CEILING division (matches the ROM's own get_length_blocks,
        // Cassette.asm): floor division (the original code) undercounted whenever occupied
        // wasn't an exact multiple of 1024.
        var vm = NewVm();
        vm.MountBytes(BuildDirectoryEntry("K", occupied: 5673, size: 5673), "K");
        Assert.Equal("6", LastToken(vm.Programs[0]));

        var vm2 = NewVm();
        vm2.MountBytes(BuildDirectoryEntry("J", occupied: 40, size: 40), "J");
        Assert.Equal("1", LastToken(vm2.Programs[0]));
    }

    [Fact]
    public void ParseDirectory_BlockCount_ExactMultipleOf1024_Unaffected()
    {
        // Ceiling division must not change behavior for already block-aligned sizes
        // (e.g. a full 1024-byte block must still show exactly 1, not round up to 2).
        var vm = NewVm();
        vm.MountBytes(BuildDirectoryEntry("FULL", occupied: 1024, size: 1024), "FULL");
        Assert.Equal("1", LastToken(vm.Programs[0]));
    }

    private static string LastToken(string row) =>
        row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

    /// <summary>Builds a single-record .cas image with a directory header at the confirmed
    /// offsets (project CLAUDE.md §14.4 addendum): name 06-0D/17-1E, ext 0E-10, creator 11,
    /// size (file bytes) 04-05, occupied (space reserved on tape) 02-03.</summary>
    private static byte[] BuildDirectoryEntry(string name, int occupied, int size)
    {
        var cas = new byte[1280];
        const int hdr = 0x30;
        cas[hdr + 2] = (byte)(occupied & 0xFF);
        cas[hdr + 3] = (byte)((occupied >> 8) & 0xFF);
        cas[hdr + 4] = (byte)(size & 0xFF);
        cas[hdr + 5] = (byte)((size >> 8) & 0xFF);
        Encoding.ASCII.GetBytes(name.PadRight(8)).CopyTo(cas, hdr + 6);
        Encoding.ASCII.GetBytes("BAS").CopyTo(cas, hdr + 14);
        cas[hdr + 17] = (byte)'B';
        Encoding.ASCII.GetBytes("        ").CopyTo(cas, hdr + 23);
        return cas;
    }

    [Fact]
    public void DirectoryHeader_IsFormattedColumnRow()
    {
        Assert.Contains("Filename", CassetteDeckVm.DirectoryHeader);
        Assert.Contains("Ext", CassetteDeckVm.DirectoryHeader);
    }
}

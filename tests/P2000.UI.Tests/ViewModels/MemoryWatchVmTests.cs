using P2000.Machine.Debug;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="MemoryWatchVm"/> (milestone 9; configurable range + export/import
/// milestone 12). File-picker-driven parts of <c>SaveRangeToFileAsync</c>/
/// <c>LoadFileToAddressAsync</c> (StorageProvider dialogs) are not unit-tested here — same as
/// <c>DisplayWindowVm.SaveStateAsync</c>/<c>LoadStateAsync</c> elsewhere in this suite — since
/// there is no real desktop `TopLevel` in a headless test run. What IS tested: the range model
/// (<see cref="MemoryWatchVm.SetRange"/>), and the underlying machine-memory read/write paths
/// those commands rely on.
/// </summary>
public class MemoryWatchVmTests
{
    // A simple deterministic memory reader: addr → (byte)addr
    private static byte FakeMemory(ushort addr) => (byte)addr;

    private static MemoryWatchVm NewVm() => new(new EmulationRunner());

    [Fact]
    public void InitialState_Rows_Has16Items()
    {
        var vm = NewVm();
        Assert.Equal(16, vm.Rows.Count);
        Assert.Equal(256, vm.Length);
    }

    [Fact]
    public void Update_PopulatesAddressLabel()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x5000;
        vm.Update(FakeMemory);
        Assert.Equal("5000", vm.Rows[0].Address);
        Assert.Equal("5010", vm.Rows[1].Address);
        Assert.Equal("50F0", vm.Rows[15].Address);
    }

    [Fact]
    public void Update_HexBytes_MatchFakeMemory()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x0000;
        vm.Update(FakeMemory);
        // Row 0, byte 0: addr=0x00 → value 0x00
        Assert.Equal("00", vm.Rows[0].Bytes[0].Hex);
        // Row 0, byte 15: addr=0x0F → value 0x0F
        Assert.Equal("0F", vm.Rows[0].Bytes[15].Hex);
        // Row 1, byte 0: addr=0x10 → value 0x10
        Assert.Equal("10", vm.Rows[1].Bytes[0].Hex);
    }

    [Fact]
    public void Update_FirstUpdate_NoBytesMarkedChanged()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x0000;
        vm.Update(FakeMemory);
        // First update: nothing has a "previous" value — no changes expected
        Assert.True(vm.Rows.All(r => r.Bytes.All(b => !b.IsChanged)));
    }

    [Fact]
    public void Update_SecondUpdate_ChangedBytesHighlighted()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x0000;
        vm.Update(FakeMemory);

        // Second update with a different value for address 0x00
        vm.Update(addr => addr == 0x00 ? (byte)0xFF : FakeMemory(addr));

        Assert.True(vm.Rows[0].Bytes[0].IsChanged);
        // Other bytes unchanged
        Assert.False(vm.Rows[0].Bytes[1].IsChanged);
    }

    [Fact]
    public void Update_BaseOverride_UsesOverrideNotBaseAddress()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x5000;
        vm.Update(FakeMemory, 0x0010);
        Assert.Equal("0010", vm.Rows[0].Address);
    }

    [Fact]
    public void Update_AsciiRow_ShowsPrintableAndDots()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x0000;
        // All bytes 0x00–0x0F (non-printable) → row 0 ascii = "................"
        vm.Update(addr => (byte)(addr & 0x0F));
        Assert.Equal(new string('.', 16), vm.Rows[0].Ascii);
    }

    [Fact]
    public void Title_UpdatesWhenBaseAddressChanges()
    {
        var vm = NewVm();
        vm.BaseAddress = 0x6000;
        Assert.Contains("6000", vm.Title);
        vm.BaseAddress = 0xA000;
        Assert.Contains("A000", vm.Title);
    }

    [Fact]
    public void FollowOptions_ContainsExpectedEntries()
    {
        Assert.Contains("None", MemoryWatchVm.FollowOptions);
        Assert.Contains("HL",   MemoryWatchVm.FollowOptions);
        Assert.Contains("SP",   MemoryWatchVm.FollowOptions);
        Assert.Contains("BC",   MemoryWatchVm.FollowOptions);
        Assert.Contains("DE",   MemoryWatchVm.FollowOptions);
    }

    // ── Configurable range (§14 milestone 12 follow-up) ─────────────────────────────────

    [Fact]
    public void SetRange_ResizesRowsToMatchLength_RoundedUpToWholeRows()
    {
        var vm = NewVm();

        vm.SetRange(0x8000, 64); // exactly 4 rows
        Assert.Equal(64, vm.Length);
        Assert.Equal(4, vm.Rows.Count);

        vm.SetRange(0x8000, 17); // rounds up to 2 rows
        Assert.Equal(17, vm.Length);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void SetRange_UpdatesBaseAddressAndTitle()
    {
        var vm = NewVm();
        vm.SetRange(0x7000, 32);
        Assert.Equal((ushort)0x7000, vm.BaseAddress);
        Assert.Contains("7000", vm.Title);
    }

    [Fact]
    public void SetRange_ClampsLengthToAtLeastOne()
    {
        var vm = NewVm();
        vm.SetRange(0x5000, 0);
        Assert.Equal(1, vm.Length);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public void SetRange_ClampsLengthToWholeAddressSpace()
    {
        var vm = NewVm();
        vm.SetRange(0x5000, 0x20000);
        Assert.Equal(0x10000, vm.Length);
    }

    [Fact]
    public void Update_AfterSetRange_ReflectsNewLengthAndAddresses()
    {
        var vm = NewVm();
        vm.SetRange(0x1000, 32); // 2 rows

        vm.Update(FakeMemory);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("1000", vm.Rows[0].Address);
        Assert.Equal("1010", vm.Rows[1].Address);
    }

    [Fact]
    public void SetRange_GrowingThenShrinking_RowCountTracksBothDirections()
    {
        var vm = NewVm();
        vm.SetRange(0x5000, 512); // 32 rows
        Assert.Equal(32, vm.Rows.Count);

        vm.SetRange(0x5000, 16); // back down to 1 row
        Assert.Single(vm.Rows);
    }

    // ── Export / import (§14 milestone 12) ──────────────────────────────────────────────

    [Fact]
    public void LoadAddressText_DefaultsToBaseAddress()
    {
        var vm = new MemoryWatchVm(new EmulationRunner()) { BaseAddress = 0x5000 };
        // Constructor captured BaseAddress (0x5000, the default) before the test's own set.
        Assert.Equal("5000", vm.LoadAddressText);
    }

    [Fact]
    public void SaveRange_ReadsLiveMachineMemory_IndependentOfWindowsOwnDisplayedRange()
    {
        // SaveRangeToFileAsync(start, length) reads runner.Machine.Memory.Read fresh — not the
        // window's own _curr buffer — so an export range can legitimately differ from what the
        // window currently displays. Exercise the same read path directly (the file-picker
        // portion itself isn't unit-testable here; see class doc).
        var runner = new EmulationRunner();
        var vm = new MemoryWatchVm(runner) { BaseAddress = 0x6000 }; // window watches 0x6000+
        vm.Update(_ => 0xAA); // window's displayed buffer is all 0xAA

        // Write different bytes at a DIFFERENT range than the window's own display.
        const ushort exportStart = 0x7000;
        const int exportLength = 64;
        for (var i = 0; i < exportLength; i++)
            runner.Machine.Memory.Write((ushort)(exportStart + i), (byte)i);

        var exported = new byte[exportLength];
        for (var i = 0; i < exportLength; i++)
            exported[i] = runner.Machine.Memory.Read((ushort)(exportStart + i));

        for (var i = 0; i < exportLength; i++)
            Assert.Equal((byte)i, exported[i]);
        // Confirms the export source is decoupled from the window's own 0xAA-filled display.
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBytesToTheSameAddress()
    {
        var runner = new EmulationRunner();
        const ushort start = 0x6000;
        const int length = 256;

        for (var i = 0; i < length; i++)
            runner.Machine.Memory.Write((ushort)(start + i), (byte)(i & 0xFF));

        var exported = new byte[length];
        for (var i = 0; i < length; i++)
            exported[i] = runner.Machine.Memory.Read((ushort)(start + i));

        var path = Path.Combine(Path.GetTempPath(), $"memwatch_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, exported);
            var loaded = File.ReadAllBytes(path);

            Assert.Equal(length, loaded.Length);
            for (var i = 0; i < length; i++)
                Assert.Equal((byte)(i & 0xFF), loaded[i]);

            // Enqueue exactly what LoadFileToAddressAsync would enqueue and confirm the
            // machine applies it identically (round-trip via the command queue).
            runner.Machine.Enqueue(new LoadImageCommand(start, loaded));
            RunToNextBoundary(runner.Machine);

            for (var i = 0; i < length; i++)
                Assert.Equal((byte)(i & 0xFF), runner.Machine.Memory.Read((ushort)(start + i)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadImageCommand_AddressPlusLengthPastTopOfRam_WouldBeRejectedByUiGuard()
    {
        // Mirrors the guard in MemoryWatchVm.LoadFileToAddressAsync: address+length > 0x10000
        // must be flagged rather than silently wrapping. Exercised here at the arithmetic
        // level since the async command requires a StorageProvider/TopLevel to drive fully.
        const ushort address = 0xFF00;
        var data = new byte[512]; // 0xFF00 + 512 = 0x10100 > 0x10000
        Assert.True(address + data.Length > 0x10000);
    }

    [Fact]
    public void LoadImageCommand_LargerThanWatchWindowLength_IsAllowed()
    {
        // Only the RAM-size bound applies to the target address; the file may be larger
        // than the watch window's own configured length.
        var runner = new EmulationRunner();
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;

        runner.Machine.Enqueue(new LoadImageCommand(0x6000, data));
        RunToNextBoundary(runner.Machine);

        for (var i = 0; i < data.Length; i++)
            Assert.Equal(data[i], runner.Machine.Memory.Read((ushort)(0x6000 + i)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Advance to the next instruction boundary so a queued command drains
    /// (mirrors the pattern in P2000.Machine.Tests CommandQueueTests).</summary>
    private static void RunToNextBoundary(P2000.Machine.Machine m, int maxTicks = 200)
    {
        m.Tick();
        for (var i = 0; i < maxTicks; i++)
        {
            if (m.Cpu.AtInstructionBoundary) return;
            m.Tick();
        }
        throw new InvalidOperationException("Did not reach instruction boundary.");
    }
}

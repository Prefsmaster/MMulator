using P2000.Machine;
using P2000.UI.ViewModels;
using MachineCore = P2000.Machine.Machine;

namespace P2000.UI.Tests.ViewModels;

/// <summary>Tests for <see cref="MemoryWatchVm"/> (milestone 9).</summary>
public class MemoryWatchVmTests
{
    // A simple deterministic memory reader: addr → (byte)addr
    private static byte FakeMemory(ushort addr) => (byte)addr;

    [Fact]
    public void InitialState_Rows_Has16Items()
    {
        var vm = new MemoryWatchVm();
        Assert.Equal(16, vm.Rows.Length);
    }

    [Fact]
    public void Update_PopulatesAddressLabel()
    {
        var vm = new MemoryWatchVm { BaseAddress = 0x5000 };
        vm.Update(FakeMemory);
        Assert.Equal("5000", vm.Rows[0].Address);
        Assert.Equal("5010", vm.Rows[1].Address);
        Assert.Equal("50F0", vm.Rows[15].Address);
    }

    [Fact]
    public void Update_HexBytes_MatchFakeMemory()
    {
        var vm = new MemoryWatchVm { BaseAddress = 0x0000 };
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
        var vm = new MemoryWatchVm { BaseAddress = 0x0000 };
        vm.Update(FakeMemory);
        // First update: nothing has a "previous" value — no changes expected
        Assert.True(vm.Rows.All(r => r.Bytes.All(b => !b.IsChanged)));
    }

    [Fact]
    public void Update_SecondUpdate_ChangedBytesHighlighted()
    {
        var vm = new MemoryWatchVm { BaseAddress = 0x0000 };
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
        var vm = new MemoryWatchVm { BaseAddress = 0x5000 };
        vm.Update(FakeMemory, 0x0010);
        Assert.Equal("0010", vm.Rows[0].Address);
    }

    [Fact]
    public void Update_AsciiRow_ShowsPrintableAndDots()
    {
        var vm = new MemoryWatchVm { BaseAddress = 0x0000 };
        // All bytes 0x00–0x0F (non-printable) → row 0 ascii = "................"
        vm.Update(addr => (byte)(addr & 0x0F));
        Assert.Equal(new string('.', 16), vm.Rows[0].Ascii);
    }

    [Fact]
    public void Title_UpdatesWhenBaseAddressChanges()
    {
        var vm = new MemoryWatchVm { BaseAddress = 0x6000 };
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
}

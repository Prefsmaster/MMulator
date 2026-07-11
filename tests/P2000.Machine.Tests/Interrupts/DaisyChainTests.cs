using P2000.Machine.Devices.Ctc;
using P2000.Machine.Interrupts;

namespace P2000.Machine.Tests.Interrupts;

/// <summary>
/// Unit tests for <see cref="DaisyChain"/> priority arbitration + IEI/IEO blocking + RETI
/// unblocking (project CLAUDE.md §13 milestone 17, test (d)) — driven against real
/// <see cref="Z80Ctc"/> channels since that's the only registrant so far.
/// </summary>
public class DaisyChainTests
{
    private static (Z80Ctc ctc, DaisyChain chain) BuildArmedChip()
    {
        var ctc = new Z80Ctc();
        var chain = new DaisyChain();
        foreach (var device in ctc.DaisyChainDevices)
            chain.Register(device);

        // ch0 and ch1: counter mode, TC=1, INTEN — trivial to arm via ClkTrg.
        ctc.WritePort(0, 0xC5); ctc.WritePort(0, 0x01);
        ctc.WritePort(1, 0xC5); ctc.WritePort(1, 0x01);
        return (ctc, chain);
    }

    [Fact]
    public void NoDevicesPending_IntPendingFalse()
    {
        var (_, chain) = BuildArmedChip();
        Assert.False(chain.IntPending);
    }

    [Fact]
    public void HigherPriorityDevice_WinsArbitration()
    {
        var (ctc, chain) = BuildArmedChip();
        ctc.ClkTrg(0); // ch0 pending
        ctc.ClkTrg(1); // ch1 pending too

        Assert.True(chain.IntPending);
        chain.Acknowledge();

        Assert.True(ctc.DaisyChainDevices[0].InService);
        Assert.False(ctc.DaisyChainDevices[1].InService);
    }

    [Fact]
    public void InServiceDevice_BlocksLowerPriorityFromBeingSeen()
    {
        var (ctc, chain) = BuildArmedChip();
        ctc.ClkTrg(0);
        ctc.ClkTrg(1);

        chain.Acknowledge(); // ch0 now in service

        // ch1 is still genuinely pending underneath, but the chain must not surface it while
        // ch0 (higher priority) is in service — mirrors the real IEI/IEO cascade.
        Assert.True(ctc.DaisyChainDevices[1].IntPending);
        Assert.False(chain.IntPending);
    }

    [Fact]
    public void Reti_ClearsHighestPriorityInService_ThenLowerPriorityUnblocks()
    {
        var (ctc, chain) = BuildArmedChip();
        ctc.ClkTrg(0);
        ctc.ClkTrg(1);
        chain.Acknowledge(); // ch0 in service, ch1 blocked

        chain.OnReti();

        Assert.False(ctc.DaisyChainDevices[0].InService);
        Assert.True(chain.IntPending); // ch1 now visible

        var vector = chain.Acknowledge();
        Assert.True(ctc.DaisyChainDevices[1].InService);
        Assert.Equal((byte)((0 & 0xF8) | (1 << 1)), vector); // default vector base 0 -> ch1 = 0x02
    }

    [Fact]
    public void Acknowledge_WithNothingPending_ReturnsPassiveByte()
    {
        var (_, chain) = BuildArmedChip();
        Assert.Equal(0xFF, chain.Acknowledge());
    }

    [Fact]
    public void OnReti_WithNothingInService_IsNoOp()
    {
        var (ctc, chain) = BuildArmedChip();
        chain.OnReti(); // must not throw
        Assert.False(ctc.DaisyChainDevices[0].InService);
    }
}

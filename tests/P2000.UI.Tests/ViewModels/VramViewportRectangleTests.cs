using Avalonia.Headless.XUnit;
using P2000.Machine;
using P2000.Machine.Devices;
using P2000.UI.Runner;
using P2000.UI.ViewModels;

namespace P2000.UI.Tests.ViewModels;

/// <summary>
/// The VRAM window's viewport rectangle must track the machine's live video control register
/// (machine milestone 26): it moves with the pan set by <c>OUT 0x30</c>, and widens to the full
/// 80 columns in 80-column mode, where the pan register is held cleared in hardware.
///
/// Owner report, 2026-08-04 — these exist because the behaviour was reported as not working and
/// nothing covered the VM path end-to-end from a real port write.
/// </summary>
public class VramViewportRectangleTests
{
    /// <summary>Long enough for at least one 50 Hz FrameReady tick to reach the debugger VM.</summary>
    private const int FrameSettleMs = 120;

    [AvaloniaFact]
    public async Task ViewportRectangle_FollowsAPanWrittenThroughThePort()
    {
        var runner = new EmulationRunner();
        var debugger = new DebuggerWindowVm(runner);
        runner.Start();
        try
        {
            await Task.Delay(FrameSettleMs);
            Assert.Equal(0, debugger.Vram.PanX);

            runner.Machine.Ports.Write(Video.ControlPortFirst, 40); // OUT 48,40
            await Task.Delay(FrameSettleMs);

            Assert.Equal(40, runner.Machine.Video.PanX);
            Assert.Equal(40, debugger.Vram.PanX);
            Assert.Equal(40, debugger.Vram.ViewportWidth); // still a 40-wide viewport
        }
        finally { runner.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ViewportRectangle_TracksASmallPan_NotJustTheExtremes()
    {
        // The owner's own repro value.
        var runner = new EmulationRunner();
        var debugger = new DebuggerWindowVm(runner);
        runner.Start();
        try
        {
            await Task.Delay(FrameSettleMs);
            runner.Machine.Ports.Write(Video.ControlPortFirst, 3); // OUT 48,3
            await Task.Delay(FrameSettleMs);

            Assert.Equal(3, debugger.Vram.PanX);
        }
        finally { runner.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ViewportRectangle_CoversTheFullWidthInEightyColumnMode()
    {
        var runner = new EmulationRunner();
        var debugger = new DebuggerWindowVm(runner);
        // Start BEFORE Reconfigure: the swap is applied by the emulation thread at a field
        // boundary, so a Reconfigure issued while the thread is stopped never lands (it just
        // times out its 500 ms acknowledgement wait). Real usage always has the runner started
        // well before the config window can Apply, so this is a test-ordering rule, not a bug —
        // but it fails silently, which is worth knowing.
        runner.Start();
        try
        {
            runner.Reconfigure(new MachineConfig
            {
                Modifications = new ModificationsConfig { EightyColumnBoard = true },
            });
            await Task.Delay(FrameSettleMs);
            Assert.Equal(40, debugger.Vram.ViewportWidth);
            // Discriminator: has the reconfigure actually swapped in the fitted machine? If not,
            // the port write below lands on a bare machine and silently does nothing.
            Assert.NotNull(runner.Machine.EightyColumn);

            runner.Machine.Ports.Write(EightyColumnBoard.ModePort, 0x01); // OUT 0,1
            await Task.Delay(FrameSettleMs);

            Assert.Equal(80, debugger.Vram.ViewportWidth);
            Assert.Equal(0, debugger.Vram.PanX); // held cleared in hardware while there

            runner.Machine.Ports.Write(EightyColumnBoard.ModePort, 0x00); // OUT 0,0
            await Task.Delay(FrameSettleMs);

            Assert.Equal(40, debugger.Vram.ViewportWidth);
        }
        finally { runner.Dispose(); }
    }
}

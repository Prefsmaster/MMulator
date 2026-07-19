using P2000.Machine.Contention;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Integration regression test for a real ROM-timing quirk found while diagnosing an
/// owner-reported bug in `P2000.UI`'s Standard-Host keyboard mode (project
/// `P2000.UI/CLAUDE.md` §14.3a, 2026-07-20 finding): releasing an already-held Shift
/// crosspoint and pressing a different key in the exact same instant (same field) is NOT
/// read as "unshifted" by the monitor ROM's keyboard scan — it still reads as shifted. A
/// genuine one-field gap between the release and the press is required. This test exercises
/// the real booted BASIC + `KeyboardDevice.SetKey` directly (no `P2000.UI` involved) so a
/// future change to the monitor ROM, the keyboard scan, or the video/field timing that
/// silently removes the need for this gap — or reintroduces the bug some other way — fails
/// loudly here rather than only being caught by a live owner report again.
/// </summary>
public class KeyboardScanTimingTests
{
    private const int TickLimit = 5_000_000;

    [Fact]
    public void ShiftReleaseAndKeyPress_NeedARealFieldGap_ToRegisterAsUnshifted()
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });

        RunUntil(machine, () =>
            machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "didn't enter BASIC");
        Ticks(machine, 100); // let BASIC print its banner and reach the "Ok" prompt

        // Hold Shift for a while (a real hold, as a physical keypress would be), then release
        // it and — after a genuine one-field gap — press the '@' position (6,7) unshifted.
        machine.Keyboard.SetKey(9, 0, true);
        Ticks(machine, 20);
        machine.Keyboard.SetKey(9, 0, false);
        Ticks(machine, 1); // the field-boundary gap this test protects
        machine.Keyboard.SetKey(6, 7, true);
        Ticks(machine, 4);
        machine.Keyboard.SetKey(6, 7, false);
        Ticks(machine, 20);

        Assert.Equal((byte)'@', EchoedChar(machine));
    }

    [Fact]
    public void ShiftReleaseAndKeyPress_WithNoGap_StillReadsAsShifted()
    {
        // Documents the bug this test suite now guards against: with ZERO gap between the
        // release and the press, the ROM reads it as shifted (produces the umlaut/diaeresis
        // dead key at (6,7) shifted, not '@'). If this ever starts passing with '@' instead,
        // the ROM/timing model changed in a way that makes `HostKeyTranslator`'s force-off
        // gap (`P2000.UI/Input/HostKeyTranslator.cs`) unnecessary — worth a deliberate look,
        // not a silent removal.
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });

        RunUntil(machine, () =>
            machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "didn't enter BASIC");
        Ticks(machine, 100);

        machine.Keyboard.SetKey(9, 0, true);
        Ticks(machine, 20);
        machine.Keyboard.SetKey(9, 0, false); // no gap before the next line
        machine.Keyboard.SetKey(6, 7, true);
        Ticks(machine, 4);
        machine.Keyboard.SetKey(6, 7, false);
        Ticks(machine, 20);

        Assert.NotEqual((byte)'@', EchoedChar(machine));
    }

    /// <summary>Reads the one VRAM byte that differs from a space/cursor-glyph baseline at
    /// the start of the input line (row 7 in this boot's fixed prompt position) — i.e. the
    /// character BASIC just echoed for the direct-mode input.</summary>
    private static byte EchoedChar(Machine m)
    {
        const int row = 7;
        for (var col = 0; col < 80; col++)
        {
            var b = m.Memory.Read((ushort)(PageTable.VideoRamStart + row * 80 + col));
            if (b is not (0x00 or 0x80 or 0x20)) return b; // skip cursor glyph / blank / space
        }
        Assert.Fail("No echoed character found on the input row.");
        return 0;
    }

    private static void Ticks(Machine machine, int fields)
    {
        for (var f = 0; f < fields; f++)
            for (var t = 0; t < VideoFetchUnit.TStatesPerField; t++)
                machine.Tick();
    }

    private static void RunUntil(Machine machine, Func<bool> condition, string failMessage)
    {
        for (var i = 0; i < TickLimit; i++)
        {
            machine.Tick();
            if (condition()) return;
        }
        Assert.Fail($"{failMessage} (ran {TickLimit:N0} T-states)");
    }

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
}

using P2000.Machine.Contention;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Regression guard for the keyboard-matrix ground truth used throughout `P2000.UI/Input`
/// (project `P2000.UI/CLAUDE.md` §14.3a, §18 2026-07-20 findings). The owner-supplied photo
/// transcription got several non-alphanumeric positions wrong — this presses each confirmed
/// position directly on a real booted BASIC and checks the exact byte that lands in VRAM, so a
/// future change to the monitor ROM, BASIC, or the keyboard/video timing that silently
/// resurrects one of these mismatches fails here rather than only via a live owner report.
///
/// IMPORTANT METHODOLOGY NOTE: an intermediate theory (unshifted keycode = row×8+col, shifted =
/// that + 72, cross-checked against BASIC's own keycode-to-ASCII table at Z80 address 6164)
/// held for several cells but is NOT universally reliable — it wrongly predicted Shift+3 would
/// show '#' instead of the correct '£'. Every assertion below was independently confirmed by
/// pressing the real matrix position and reading the real VRAM byte back, never by the table
/// extrapolation alone. Do not "correct" any of these from the table formula without doing the
/// same direct test first.
/// </summary>
public class MatrixCharacterOutputTests
{
    private const int TickLimit = 5_000_000;

    [Theory]
    // ── Positions where the photo transcription turned out correct (regression guard) ──
    [InlineData(0, 4, false, 0x33)]   // D3 unshifted: '3'
    [InlineData(0, 4, true,  0x23)]   // D3 shifted: '£' (Saa5050Font remap of 0x23) — NOT '#'
    [InlineData(2, 4, false, 0x5F)]   // "#/°" key unshifted: '#' (0x5F IS literally '#' in this font)
    // ── Positions where the photo transcription was wrong (see §18 for the corrections) ──
    [InlineData(6, 7, false, 0x40)]   // "@/¨" key unshifted: '@'
    [InlineData(6, 7, true,  0x5E)]   // shifted: renders as an UP ARROW glyph, not ¨ (umlaut)
    [InlineData(7, 4, false, 0x5D)]   // "][" key unshifted: renders as RIGHT ARROW, not ']'
    [InlineData(7, 4, true,  0x5B)]   // shifted: renders as LEFT ARROW, not '['
    [InlineData(8, 4, false, 0x7B)]   // accent-aigu-labeled key unshifted: renders as ¼, not ´
    [InlineData(8, 4, true,  0x7D)]   // shifted: renders as ¾, not `
    [InlineData(5, 2, false, 0x2B)]   // numpad "+/x" key unshifted: '+'
    [InlineData(5, 2, true,  0x2A)]   // shifted: '*', not the letter 'x'
    [InlineData(5, 3, false, 0x2D)]   // numpad "-/:" key unshifted: '-'
    [InlineData(5, 3, true,  0x7E)]   // shifted: renders as Divide (÷), not ':'
    public void KeyPress_ProducesExpectedVramByte(int row, int col, bool shift, byte expectedByte)
    {
        var machine = BootToBasic();
        var before = SnapshotVram(machine);

        if (shift) { machine.Keyboard.SetKey(9, 0, true); Ticks(machine, 5); }
        machine.Keyboard.SetKey(row, col, true);
        Ticks(machine, 8);
        machine.Keyboard.SetKey(row, col, false);
        Ticks(machine, 5);
        if (shift) { machine.Keyboard.SetKey(9, 0, false); Ticks(machine, 5); }
        Ticks(machine, 20);

        var after = SnapshotVram(machine);
        byte? changed = null;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i]) continue;
            if (after[i] != 0x80) changed = after[i]; // 0x80 is the cursor glyph moving on
        }

        Assert.NotNull(changed);
        Assert.Equal(expectedByte, changed!.Value);
    }

    [Theory]
    [InlineData(6, 0)]  // numpad "9/M" key shifted: does not echo a character into the input line
    public void KeyPress_Shifted_DoesNotEchoIntoInputLine(int row, int col)
    {
        // Only checks the input row (7 — where BASIC's direct-mode cursor sits at this boot
        // point) doesn't gain a new printable character. NOT a claim that the key does nothing
        // at all: numpad "5" shifted (8,2) was tried here too and turned out to trigger some
        // other screen-level side effect (looks like a redraw touching row 0's banner text) —
        // genuinely interesting but outside what this test needs to pin down, so it's not
        // asserted here; see P2000.UI/CLAUDE.md §18 for the raw finding if it needs revisiting.
        var machine = BootToBasic();
        const int inputRow = 7;
        var before = SnapshotVram(machine);

        machine.Keyboard.SetKey(9, 0, true);
        Ticks(machine, 5);
        machine.Keyboard.SetKey(row, col, true);
        Ticks(machine, 8);
        machine.Keyboard.SetKey(row, col, false);
        Ticks(machine, 5);
        machine.Keyboard.SetKey(9, 0, false);
        Ticks(machine, 20);

        var after = SnapshotVram(machine);
        for (var col2 = 0; col2 < 80; col2++)
        {
            var i = inputRow * 80 + col2;
            if (before[i] == after[i]) continue;
            Assert.True(after[i] == 0x80 || after[i] is < 0x20 or >= 0x7F,
                $"Expected no new printable character on the input row, but col {col2} changed to 0x{after[i]:X2}");
        }
    }

    private static Machine BootToBasic()
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });
        RunUntil(machine, () =>
            machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "didn't enter BASIC");
        Ticks(machine, 100); // let BASIC print its banner and reach the "Ok" prompt
        return machine;
    }

    private static byte[] SnapshotVram(Machine m)
    {
        var buf = new byte[80 * 24];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = m.Memory.Read((ushort)(PageTable.VideoRamStart + i));
        return buf;
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

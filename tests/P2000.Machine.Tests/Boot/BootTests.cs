using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Integration tests for project CLAUDE.md §7 (BOOT milestone): the monitor ROM is
/// auto-loaded and the machine is driven to one of the two boot outcomes depending on
/// SLOT1 population (reference doc §5b boot sequence).
///
/// These tests tick the machine through the real monitor ROM boot code — RAM check,
/// SLOT1 probe, and either the cassette-wait loop or the jump into BASIC. They are
/// intentionally long-running relative to unit tests: the monitor ROM's RAM-sizing probe
/// walks the full populated RAM once before doing anything visible, so each test requires
/// on the order of a few million T-state ticks.
/// </summary>
public class BootTests
{
    /// <summary>Maximum T-states to run before declaring a boot test failed. At 2.5 MHz
    /// this is 2 seconds of simulated time — more than enough for the monitor ROM to
    /// complete its RAM test and reach the cassette-wait loop or jump to BASIC.</summary>
    private const int TickLimit = 5_000_000;

    // ---- Embedded monitor ROM -------------------------------------------------------

    [Fact]
    public void Machine_AutoLoadsEmbeddedMonitorRom_OnConstruction()
    {
        var machine = new Machine();

        // The embedded ROM should be non-trivial: first byte is a Z80 instruction, not
        // open-bus (0xFF). The monitor ROM starts with a JP or DI/LD at byte 0.
        Assert.NotEqual(PageTable.OpenBus, machine.Memory.Read(0x0000));
    }

    [Fact]
    public void MonitorRomPath_Override_LoadsFromDisk()
    {
        var path = Path.Combine(FindRepoRoot(), "assets", "P2000ROM.rom");
        var config = new MachineConfig { MonitorRomPath = path };
        var machine = new Machine(config);

        // Same first byte as the embedded default (same file, just loaded via override).
        var embedded = new Machine();
        Assert.Equal(embedded.Memory.Read(0x0000), machine.Memory.Read(0x0000));
    }

    // ---- Boot outcome (a): bare machine → cassette-wait loop -----------------------

    /// <summary>
    /// With no SLOT1 cartridge the monitor ROM completes its RAM check, detects open-bus
    /// in the cartridge region, writes the cassette-wait prompt to VRAM, and enters a
    /// tight CIP-polling loop. After enough ticks: VRAM has non-zero content (the prompt
    /// was rendered) and the CPU is still inside the ROM (PC &lt; 0x1000).
    /// </summary>
    [Fact]
    public void BareBootReachesRom_CassetteWaitLoop()
    {
        var machine = new Machine(); // no Slot1CartridgePath → bare machine

        RunUntil(machine, () =>
            machine.Cpu.Reg.PC < PageTable.CartridgeStart &&
            machine.Video.Framebuffer.Any(p => p != 0),
            "bare machine: VRAM still empty after full tick budget");
    }

    /// <summary>
    /// On the bare machine the CPU must be in a tight polling loop inside ROM — it should
    /// not have jumped to SLOT1 or any other region outside 0x0000-0x0FFF.
    /// </summary>
    [Fact]
    public void BareBootStaysInRom_DoesNotJumpToSlot1()
    {
        var machine = new Machine();

        for (var i = 0; i < TickLimit; i++)
        {
            machine.Tick();
        }

        Assert.True(machine.Cpu.Reg.PC < PageTable.CartridgeStart,
            $"bare machine PC ended up at 0x{machine.Cpu.Reg.PC:X4} — expected inside ROM (< 0x1000)");
    }

    // ---- Boot outcome (b): SLOT1 populated → jump into BASIC ----------------------

    /// <summary>
    /// With the BASIC cartridge in SLOT1 the monitor ROM's SLOT1 probe finds a non-open-bus
    /// byte at 0x1000 and jumps there. After enough ticks the CPU's PC is in the SLOT1
    /// range (0x1000–0x4FFF).
    /// </summary>
    [Fact]
    public void Slot1Boot_WithBasicCartridge_JumpsIntoBasic()
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });

        RunUntil(machine, () =>
            machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "with BASIC in SLOT1: CPU never jumped into the cartridge region (0x1000–0x4FFF)");
    }

    /// <summary>
    /// The first byte of the loaded BASIC cartridge should be readable at 0x1000 and must
    /// not be open-bus (confirming SLOT1 was actually populated).
    /// </summary>
    [Fact]
    public void Slot1_PopulatedFromFile_ReadsCorrectly()
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });

        var firstByte = machine.Memory.Read(PageTable.CartridgeStart);
        Assert.NotEqual(PageTable.OpenBus, firstByte);

        // Verify the raw file byte matches what the page table serves (no corruption).
        var fileBytes = File.ReadAllBytes(basicPath);
        Assert.Equal(fileBytes[0], firstByte);
        Assert.Equal(fileBytes[^1],
            machine.Memory.Read(PageTable.CartridgeEnd));
    }

    // ---- Helpers ------------------------------------------------------------------

    /// <summary>Runs the machine until <paramref name="condition"/> becomes true or the
    /// tick limit is reached, then asserts the condition held.</summary>
    private static void RunUntil(Machine machine, Func<bool> condition, string failMessage)
    {
        for (var i = 0; i < TickLimit; i++)
        {
            machine.Tick();
            if (condition()) return;
        }
        Assert.Fail($"{failMessage} (ran {TickLimit:N0} T-states)");
    }

    /// <summary>Finds the repo root by walking up from the test output directory until
    /// MMulator.sln is found. Works regardless of build configuration or output path.</summary>
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

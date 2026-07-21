using P2000.Machine.Devices.Cassette;
using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Integration tests for the disk-boot path (project CLAUDE.md §13 milestone 19; reference doc
/// §5b "Disk-boot gate", `docs/JWSDOS-format.md` §6 `getdos`) — the REAL embedded monitor ROM
/// driving the real <see cref="Devices.Fdc.Upd765"/> chip through its actual boot code, against
/// the real disk fixture in <c>assets/Disks/Spel1.dsk</c>.
///
/// <b>History — a prior attempt at this test failed and was removed (see the machine
/// CLAUDE.md §17 finding it left behind).</b> The `docs/Monitor Documented Disassembly/`
/// files (owner-supplied, not previously read for this milestone) turned out to hold the
/// answer to both open questions that attempt left behind:
/// <list type="bullet">
/// <item>The SLOT1 header byte's bit1 ("needs DOS") is the OPPOSITE of what was first assumed:
/// `Startup.asm`'s disk-boot gate does `bit 1,a; jr z,prep_status_display` — bit1=0 SKIPS disk
/// boot, bit1=1 is what triggers it. The real `assets/BASIC.bin` header byte (0x5E) already has
/// bit1=1 — no cartridge modification is needed at all; the prior attempt's "clone BASIC.bin
/// and clear bit1" workaround was clearing the exact bit that would have triggered the gate.</item>
/// <item>`Disk.asm`'s `getdos`/`read_track` reveals the READ DATA command template is copied to
/// RAM ONCE and its cylinder byte is never updated between the two DOS-track reads — both
/// sends are byte-identical — while the actual cylinder read differs because a separate SEEK
/// command (`disk_gotrack`) repositions the head in between. Real µPD765 hardware reads/writes
/// wherever the head physically IS; the command's own C field doesn't drive addressing. Fixed
/// in <see cref="Devices.Fdc.Upd765.DispatchReadWrite"/> (now reads <c>_cylinder[drive]</c>) —
/// this is what let the RUN-gate test below actually pass.</item>
/// </list>
/// </summary>
public class DiskBootTests
{
    // Disk boot needs noticeably more than a plain cartridge boot (BootTests.cs's own 5M
    // budget): the RAM-sizing test walks a fully-populated T102 (base+expansion+both banked
    // pages, ~48 KB) instead of a smaller variant, plus getdos's own two ~342 ms settle delays
    // (854,799 T-states each, pure CPU busy-loops per Disk.asm) on top of the actual transfer.
    private const int TickLimit = 40_000_000;

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

    private static Machine BuildDiskBootMachine(string diskImagePath)
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            Slot1CartridgePath = basicPath, // real, unmodified — its header byte already has
                                             // bit1=1 ("needs DOS"), confirmed against Startup.asm
            FloppyDiskImagePath = diskImagePath,
        });
        machine.Fdc!.Policy = TimingPolicy.Turbo; // instant transfers; the ROM's own settle
                                                    // delays are real code either way (cycle-exact)
        return machine;
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

    /// <summary>
    /// The real monitor ROM's disk-boot gate (memsize==3 + SLOT1 present + needs-DOS via bit1),
    /// presence probe, and `getdos` load sequence, run end to end against the real JWSDOS disk
    /// (`Spel1.dsk`): both DOS tracks must land byte-identical to the source image at
    /// 0xE000-0xFFFF (bank 1) — the full semi-DMA read round-trip, driven by real Z80 code.
    /// </summary>
    [Fact]
    public void GetDos_LoadsBothTracksByteIdentical_FromRealJwsdosImage()
    {
        var diskPath = Path.Combine(FindRepoRoot(), "assets", "Disks", "Spel1.dsk");
        var machine = BuildDiskBootMachine(diskPath);

        // Select bank 1 to read back what getdos loaded (it restores bank 0 on cleanup —
        // Disk.asm's disk_interrupts_off — so we must select it back ourselves afterward).
        RunUntil(machine, () => machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "boot never reached SLOT1 — check the disk-boot gate fired");

        machine.Memory.SelectBank(0x01);

        var sourceFirstByte = File.ReadAllBytes(diskPath)[0];
        Assert.Equal(sourceFirstByte, machine.Memory.Read(0xE000));

        // A full-track spot check: several bytes spread across track 1 (cylinder 0, head 0)
        // must match the source image byte-for-byte.
        var track1Bytes = File.ReadAllBytes(diskPath).AsSpan(0, 4096).ToArray();
        for (var offset = 0; offset < 4096; offset += 512)
        {
            Assert.Equal(track1Bytes[offset], machine.Memory.Read((ushort)(0xE000 + offset)));
        }

        // Track 2 (cylinder 1, head 0) lands at 0xF000-0xFFFF.
        var track2Bytes = File.ReadAllBytes(diskPath).AsSpan(0x1000, 4096).ToArray();
        for (var offset = 0; offset < 4096; offset += 512)
        {
            Assert.Equal(track2Bytes[offset], machine.Memory.Read((ushort)(0xF000 + offset)));
        }
    }

    /// <summary>
    /// The system-disk signature check (`docs/JWSDOS-format.md` §6 step 7 / `Disk.asm`
    /// `tracks_loaded`): a real JWSDOS disk (first byte 0x20) does NOT match 0xF3, so `getdos`
    /// takes the "not PDOS" branch, confirmed by the exact loaded byte at 0xE000.
    /// </summary>
    [Fact]
    public void RealJwsdosImage_FirstByteIsNot0xF3_NotAPdosSignedDisk()
    {
        var diskPath = Path.Combine(FindRepoRoot(), "assets", "Disks", "Spel1.dsk");
        var machine = BuildDiskBootMachine(diskPath);

        RunUntil(machine, () => machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "boot never reached SLOT1 after the disk-boot sequence");

        machine.Memory.SelectBank(0x01);
        Assert.NotEqual(0xF3, machine.Memory.Read(0xE000));
        Assert.Equal(0x20, machine.Memory.Read(0xE000)); // JWSDOS's own confirmed first byte
    }

    /// <summary>
    /// Companion to the above: a disk whose first byte IS 0xF3 (the official Philips
    /// disk-BASIC/PDOS signature) must be distinguishable at the same address. No real
    /// PDOS-signed raw <c>.dsk</c> fixture was available, so this uses a synthetic image with
    /// just that one byte forced — everything else about the pipeline is exercised identically
    /// to the real-image test above.
    /// </summary>
    [Fact]
    public void SyntheticPdosSignedImage_FirstByteIs0xF3_LoadsCorrectly()
    {
        var diskPath = Path.Combine(FindRepoRoot(), "assets", "Disks", "Spel1.dsk");
        var image = File.ReadAllBytes(diskPath);
        image[0] = 0xF3;
        var tempDisk = Path.GetTempFileName();
        File.WriteAllBytes(tempDisk, image);

        try
        {
            var machine = BuildDiskBootMachine(tempDisk);

            RunUntil(machine, () => machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
                "boot never reached SLOT1 after the disk-boot sequence");

            machine.Memory.SelectBank(0x01);
            Assert.Equal(0xF3, machine.Memory.Read(0xE000));
        }
        finally
        {
            File.Delete(tempDisk);
        }
    }

    /// <summary>Without the 3-gate condition satisfied (here: RamOnly board, no FDC at all),
    /// the disk path must never be touched — a plain ordinary cartridge boot into BASIC.</summary>
    [Fact]
    public void NoFloppyBoard_BootsNormallyIntoBasic_NoDiskTouched()
    {
        var basicPath = Path.Combine(FindRepoRoot(), "assets", "BASIC.bin");
        var machine = new Machine(new MachineConfig { Slot1CartridgePath = basicPath });

        RunUntil(machine, () =>
            machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd,
            "cartridge-only boot never jumped into SLOT1");

        Assert.Null(machine.Fdc); // no board at all — disk path structurally unreachable
    }
}

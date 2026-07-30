using P2000.Machine.Memory;

namespace P2000.Machine.Tests.Memory;

public class PageTableTests
{
    private static PageTable Create(MachineConfig? config = null) => new(config ?? new MachineConfig());

    // ---- ROM (0x0000-0x0FFF) ---------------------------------------------------------

    [Fact]
    public void Rom_AutoLoadsEmbeddedMonitorRom_OnConstruction()
    {
        var pageTable = Create();

        // The embedded ROM is non-trivial: the first byte is a real Z80 instruction, not
        // open-bus (0xFF) or zero. LoadRom() can still overwrite it for test fixtures.
        Assert.NotEqual(PageTable.OpenBus, pageTable.Read(0x0000));
        Assert.NotEqual(0x00, pageTable.Read(0x0000));
    }

    [Fact]
    public void Rom_LoadRom_ReadsLoadedBytes()
    {
        var pageTable = Create();
        var rom = new byte[] { 0xC3, 0x00, 0x10 }; // JP 0x1000

        pageTable.LoadRom(rom);

        Assert.Equal(0xC3, pageTable.Read(0x0000));
        Assert.Equal(0x00, pageTable.Read(0x0001));
        Assert.Equal(0x10, pageTable.Read(0x0002));
    }

    [Fact]
    public void Rom_IsReadOnly_WritesAreDiscarded()
    {
        var pageTable = Create();
        pageTable.LoadRom(new byte[] { 0xAA });

        pageTable.Write(0x0000, 0x55);

        Assert.Equal(0xAA, pageTable.Read(0x0000));
    }

    [Fact]
    public void Rom_LoadRom_TooLarge_Throws()
    {
        var pageTable = Create();
        var oversized = new byte[PageTable.RomEnd - PageTable.RomStart + 2];

        Assert.Throws<ArgumentException>(() => pageTable.LoadRom(oversized));
    }

    // ---- SLOT1 cartridge region (0x1000-0x4FFF) — always open bus this milestone ------

    [Fact]
    public void Cartridge_IsOpenBus_BareByDefault()
    {
        var pageTable = Create();

        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeStart));
        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.CartridgeEnd));
    }

    [Fact]
    public void Cartridge_WritesAreDiscarded()
    {
        var pageTable = Create();

        pageTable.Write(0x2000, 0x42);

        Assert.Equal(PageTable.OpenBus, pageTable.Read(0x2000));
    }

    // ---- Video RAM (0x5000-0x5FFF), model-specific shape ------------------------------

    [Fact]
    public void VideoRam_T_ReadWrite_WithinConfirmedRange()
    {
        var pageTable = Create(new MachineConfig { Model = MachineModel.P2000T });

        pageTable.Write(PageTable.VideoRamStart, 0x11);
        pageTable.Write(0x57FF, 0x22);

        Assert.Equal(0x11, pageTable.Read(PageTable.VideoRamStart));
        Assert.Equal(0x22, pageTable.Read(0x57FF));
    }

    [Fact]
    public void VideoRam_T_UnusedGap_IsOpenBus()
    {
        var pageTable = Create(new MachineConfig { Model = MachineModel.P2000T });

        pageTable.Write(0x5800, 0x99); // gap is open bus - write must be discarded too

        Assert.Equal(PageTable.OpenBus, pageTable.Read(0x5800));
        Assert.Equal(PageTable.OpenBus, pageTable.Read(0x5FFF));
    }

    [Fact]
    public void VideoRam_M_SpansFullFourKilobytes()
    {
        var pageTable = Create(new MachineConfig { Model = MachineModel.P2000M });

        pageTable.Write(0x5800, 0x99); // open-bus gap on T is real VRAM on M

        Assert.Equal(0x99, pageTable.Read(0x5800));
        pageTable.Write(0x5FFF, 0x77);
        Assert.Equal(0x77, pageTable.Read(0x5FFF));
    }

    // ---- Base RAM (0x6000-0x9FFF) — always populated, every variant -------------------

    [Theory]
    [InlineData(RamVariant.T38)]
    [InlineData(RamVariant.T54)]
    [InlineData(RamVariant.T102)]
    public void BaseRam_AlwaysPopulated_ReadWrite(RamVariant variant)
    {
        var pageTable = Create(new MachineConfig { RamVariant = variant });

        pageTable.Write(PageTable.BaseRamStart, 0x33);
        pageTable.Write(PageTable.BaseRamEnd, 0x44);

        Assert.Equal(0x33, pageTable.Read(PageTable.BaseRamStart));
        Assert.Equal(0x44, pageTable.Read(PageTable.BaseRamEnd));
    }

    // ---- Expansion RAM (0xA000-0xDFFF) — T54/T102 only ---------------------------------

    [Fact]
    public void ExpansionRam_T38_IsOpenBus()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T38 });

        pageTable.Write(PageTable.ExpansionRamStart, 0x55);

        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.ExpansionRamStart));
    }

    [Theory]
    [InlineData(RamVariant.T54)]
    [InlineData(RamVariant.T102)]
    public void ExpansionRam_Populated_ReadWrite(RamVariant variant)
    {
        var pageTable = Create(new MachineConfig { RamVariant = variant });

        pageTable.Write(PageTable.ExpansionRamStart, 0x66);
        pageTable.Write(PageTable.ExpansionRamEnd, 0x77);

        Assert.Equal(0x66, pageTable.Read(PageTable.ExpansionRamStart));
        Assert.Equal(0x77, pageTable.Read(PageTable.ExpansionRamEnd));
    }

    // ---- Banked window (0xE000-0xFFFF) via port 0x94 -----------------------------------

    [Theory]
    [InlineData(RamVariant.T38)]
    [InlineData(RamVariant.T54)]
    public void BankedWindow_UnbankedVariant_IsOpenBus(RamVariant variant)
    {
        var pageTable = Create(new MachineConfig { RamVariant = variant });

        pageTable.Write(PageTable.BankedWindowStart, 0x12);

        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void BankedWindow_T102_DefaultsToBankZero_ReadWrite()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });

        pageTable.Write(PageTable.BankedWindowStart, 0xAB);

        Assert.Equal(0xAB, pageTable.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void BankedWindow_SelectBank_SwitchesToAnIsolatedBank()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });

        pageTable.SelectBank(0);
        pageTable.Write(PageTable.BankedWindowStart, 0x01);

        pageTable.SelectBank(1);
        pageTable.Write(PageTable.BankedWindowStart, 0x02);

        pageTable.SelectBank(0);
        Assert.Equal(0x01, pageTable.Read(PageTable.BankedWindowStart));

        pageTable.SelectBank(1);
        Assert.Equal(0x02, pageTable.Read(PageTable.BankedWindowStart));
    }

    /// <summary>Regression guard for a real coverage gap found 2026-07-28 (reference doc §5d's
    /// "Disk I/O error" investigation — the owner asked directly whether ALL configured banks,
    /// not just 0/1, had ever been verified isolated): every prior banking test only ever
    /// exercised banks 0 and 1. T102 configures 6 banks (0-5); this writes a distinct marker byte
    /// to EVERY one of them and confirms all 6 persist independently — a bug isolating only
    /// SOME higher-index bank (an off-by-one in the allocation loop, say) would have passed every
    /// existing test here.</summary>
    [Fact]
    public void BankedWindow_AllSixT102Banks_AreMutuallyIsolated()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });

        for (byte b = 0; b < 6; b++)
        {
            pageTable.SelectBank(b);
            pageTable.Write(PageTable.BankedWindowStart, (byte)(0x10 + b));
            pageTable.Write(PageTable.BankedWindowEnd, (byte)(0x20 + b)); // both ends of the window
        }

        for (byte b = 0; b < 6; b++)
        {
            pageTable.SelectBank(b);
            Assert.Equal((byte)(0x10 + b), pageTable.Read(PageTable.BankedWindowStart));
            Assert.Equal((byte)(0x20 + b), pageTable.Read(PageTable.BankedWindowEnd));
        }
    }

    [Fact]
    public void BankedWindow_IndexAtOrBeyondBankCount_IsOpenBus()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 }); // 6 banks (0-5)

        pageTable.SelectBank(6);

        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void BankedWindow_BankIndexIsStoredUnmasked_NotClampedToPopulatedCount()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T38, BankCount = 10 });

        pageTable.SelectBank(9);
        pageTable.Write(PageTable.BankedWindowStart, 0xEE);
        Assert.Equal(0xEE, pageTable.Read(PageTable.BankedWindowStart));

        pageTable.SelectBank(200); // far beyond the configured 10 banks - open bus, not masked into range
        Assert.Equal(PageTable.OpenBus, pageTable.Read(PageTable.BankedWindowStart));
    }

    // ---- Debugger per-bank access (project CLAUDE.md §13 milestone 24) ----------------

    [Fact]
    public void BankCount_Unbanked_IsZero()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T38 });

        Assert.Equal(0, pageTable.BankCount);
    }

    [Fact]
    public void BankCount_T102_IsSix()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });

        Assert.Equal(6, pageTable.BankCount);
    }

    [Fact]
    public void BankCount_HomebrewOverride_ReflectsExplicitBankCount()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T38, BankCount = 10 });

        Assert.Equal(10, pageTable.BankCount);
    }

    [Fact]
    public void GetBankRaw_ReturnsBankNsBytes_RegardlessOfWhichBankIsCurrentlyActive()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });
        for (byte b = 0; b < 6; b++)
        {
            pageTable.SelectBank(b);
            pageTable.Write(PageTable.BankedWindowStart, (byte)(0x30 + b));
        }
        pageTable.SelectBank(3); // leave some OTHER bank live-active

        for (byte b = 0; b < 6; b++)
        {
            var raw = pageTable.GetBankRaw(b);
            Assert.Equal((byte)(0x30 + b), raw[0]);
        }
    }

    [Fact]
    public void GetBankRaw_SwitchingActiveBankLive_DoesNotChangeWhatASpecificGetBankRawReturns()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });
        pageTable.SelectBank(2);
        pageTable.Write(PageTable.BankedWindowStart, 0x42);

        var before = pageTable.GetBankRaw(2);

        pageTable.SelectBank(0); // switch away — bank 2 is no longer live-active
        pageTable.Write(PageTable.BankedWindowStart, 0xFF); // mutate whatever's NOW active (bank 0)

        var after = pageTable.GetBankRaw(2);

        Assert.Equal(0x42, before[0]);
        Assert.Equal(0x42, after[0]); // bank 2's own bytes are untouched by the switch + other bank's write
    }

    [Fact]
    public void GetBankRaw_ReturnsADefensiveCopy_NotTheLiveBackingArray()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });
        pageTable.SelectBank(0);
        pageTable.Write(PageTable.BankedWindowStart, 0x11);

        var raw = pageTable.GetBankRaw(0);
        raw[0] = 0x99; // mutate the RETURNED copy

        Assert.Equal(0x11, pageTable.Read(PageTable.BankedWindowStart)); // live core unaffected
    }

    [Fact]
    public void GetBankRaw_OutOfRangeIndex_Throws()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 }); // 6 banks (0-5)

        Assert.Throws<ArgumentOutOfRangeException>(() => pageTable.GetBankRaw(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => pageTable.GetBankRaw(-1));
    }

    [Fact]
    public void GetBankRaw_NoBankingAtAll_AnyIndexThrows()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T38 }); // 0 banks

        Assert.Throws<ArgumentOutOfRangeException>(() => pageTable.GetBankRaw(0));
    }

    // ---- FillRam (project CLAUDE.md §17, 2026-07-21/22 finding: RAM powers up non-zero) ------

    [Fact]
    public void FillRam_ProducesNonZeroContent_InAllPopulatedRegions()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 }); // base+expansion+banks

        pageTable.FillRam(PageTable.DefaultRamSeed);

        Assert.NotEqual(0x00, pageTable.Read(PageTable.VideoRamStart));
        Assert.NotEqual(0x00, pageTable.Read(PageTable.BaseRamStart));
        Assert.NotEqual(0x00, pageTable.Read(PageTable.ExpansionRamStart));
        pageTable.SelectBank(0);
        Assert.NotEqual(0x00, pageTable.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void FillRam_SameSeed_ProducesByteIdenticalContent()
    {
        var a = Create(new MachineConfig { RamVariant = RamVariant.T102 });
        var b = Create(new MachineConfig { RamVariant = RamVariant.T102 });

        a.FillRam(0x1234);
        b.FillRam(0x1234);

        for (ushort addr = PageTable.BaseRamStart; addr < PageTable.BaseRamStart + 64; addr++)
        {
            Assert.Equal(a.Read(addr), b.Read(addr));
        }
    }

    [Fact]
    public void FillRam_DifferentSeeds_ProduceDifferentContent()
    {
        var a = Create();
        var b = Create();

        a.FillRam(0x1111);
        b.FillRam(0x2222);

        var different = false;
        for (ushort addr = PageTable.BaseRamStart; addr < PageTable.BaseRamStart + 64; addr++)
        {
            if (a.Read(addr) != b.Read(addr)) { different = true; break; }
        }
        Assert.True(different, "Two different seeds produced byte-identical RAM content.");
    }

    [Fact]
    public void FillRam_SeedZero_StillProducesNonZeroContent()
    {
        // xorshift64* has a fixed point at 0 (stays 0 forever) — FillRam must guard this so an
        // explicit seed of exactly 0 doesn't silently degrade to an all-zero fill.
        var pageTable = Create();

        pageTable.FillRam(0);

        Assert.NotEqual(0x00, pageTable.Read(PageTable.BaseRamStart));
    }

    [Fact]
    public void FillRam_ResetsBankIndexToZero()
    {
        var pageTable = Create(new MachineConfig { RamVariant = RamVariant.T102 });
        pageTable.SelectBank(3);

        pageTable.FillRam(PageTable.DefaultRamSeed);
        pageTable.Write(PageTable.BankedWindowStart, 0x77); // writes wherever bank index now is

        pageTable.SelectBank(0);
        Assert.Equal(0x77, pageTable.Read(PageTable.BankedWindowStart));
    }
}

using System.Linq;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;

namespace P2000.Machine.Tests.Devices.Fdc;

/// <summary>
/// Tests against the REAL disk fixtures the owner supplied in <c>assets/Disks/</c>
/// (<c>Spel1.dsk</c>, <c>jws-sytem.dsk</c>, plus <c>empty-jws.dsk</c>/<c>hires_demo.dsk</c>) —
/// project CLAUDE.md §13 milestone 19's fixture-dependent test group, run at the
/// <see cref="Upd765"/>/<see cref="DskImage"/> level rather than by driving the real monitor
/// ROM's boot sequence end to end.
///
/// <b>Scope note (why not a full real-ROM `getdos` boot test):</b> a full boot-driven RUN-gate
/// test was attempted first (constructing a `FloppyRam`/T102 machine with a needs-DOS cartridge
/// and ticking through the real embedded ROM's boot code). It did not reach a state where
/// <c>getdos</c> visibly ran — bank 1 stayed at its pre-load zero content after boot completed.
/// The 3-gate condition and command bytes are ROM-disassembly-CONFIRMED per the reference doc,
/// but the exact SLOT1 cartridge-presence/needs-DOS validation the ROM performs turned out to
/// need more than the header-byte bits alone (an all-zero synthetic cartridge was never
/// recognized as present/executable at all, for reasons not sourced in either doc) — reproducing
/// the full pipeline needs either a real needs-DOS 24K-disk-BASIC cartridge image (not available)
/// or a disassembly-level trace neither doc provides. Flagged rather than forced; these tests
/// instead exercise the SAME real disk data through the already-verified <see cref="Upd765"/>
/// command surface directly.
/// </summary>
public class RealFixtureTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "MMulator.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private static string DiskPath(string filename) =>
        Path.Combine(FindRepoRoot(), "assets", "Disks", filename);

    // ---- Geometry auto-detect ------------------------------------------------------------------

    [Fact]
    public void Spel1Dsk_GeometryAutoDetect_Is40Track_DoubleSided()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        Assert.Equal(40, disk.Tracks);
        Assert.Equal(2, disk.Sides);
    }

    // ---- Directory browse: exact real content, stale cluster excluded ---------------------------

    /// <summary>The confirmed 18 real filenames on <c>Spel1.dsk</c>'s active side-1 directory
    /// (raw 0x1800-0x1FFF), in on-disk order (<c>docs/JWSDOS-format.md</c> §2/§4).</summary>
    private static readonly string[] ExpectedActiveFilenames =
    {
        "Tralieenspel", "klemvast", "Elevatie", "Risk", "Space Misson", "Cijferdans",
        "Info Bat.S.", "Battle star", "Toernooi", "Doolhofspel", "rij sim",
        "Doolhof 3 dim.", "JACKPOT", "Jackpot", "AUTORUN", "Letter-invaders",
        "Grotvliergers", "BABA",
    };

    /// <summary>The confirmed 20 stale filenames at raw 0x1000-0x17FF (format doc §2/§7 item 3)
    /// — real, struct-shaped data, but NOT this disk's active catalog; must never surface.</summary>
    private static readonly string[] StaleClusterFilenames =
    {
        "Fraxxon + scores", "Centipede", "Androide-nim", "Race-track", "Car Race",
        "racen 2.1", "Lady Bug", "Space Atack", "Brick-Wall", "brick-Wall II",
        "RAce-circuit", "Handicap race", "Speelpleis", "Valbal", "Space fight",
        "Fight in space", "Eendenjacht", "Kleiduivschiet", "Mens-e-j-niet", "Superlaser",
    };

    [Fact]
    public void Spel1Dsk_Directory_ReturnsExactly18RealEntries_InOrder()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var entries = disk.ReadDirectory();

        Assert.Equal(ExpectedActiveFilenames.Length, entries.Count);
        for (var i = 0; i < ExpectedActiveFilenames.Length; i++)
        {
            Assert.Equal(ExpectedActiveFilenames[i], entries[i].Filename);
            Assert.Equal("BAS", entries[i].Extension);
            Assert.Equal((byte)'B', entries[i].FileType);
        }
    }

    [Fact]
    public void Spel1Dsk_Directory_AutorunEntry_HasConfirmedTransferAddress()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var autorun = disk.ReadDirectory().Single(e => e.Filename == "AUTORUN");
        Assert.Equal(0x7000, autorun.TransferAddress);
        Assert.Equal(2744, autorun.FileLength);
    }

    [Fact]
    public void Spel1Dsk_Directory_NeverIncludesTheStaleClusterEntries()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var entries = disk.ReadDirectory();

        foreach (var staleName in StaleClusterFilenames)
        {
            Assert.DoesNotContain(entries, e => e.Filename == staleName);
        }
    }

    /// <summary>Validation identity confirmed in the format doc (§4): for every real entry,
    /// the sector span exactly accounts for the file length in 256-byte sectors.</summary>
    [Fact]
    public void Spel1Dsk_Directory_AllEntries_SectorSpanMatchesFileLength()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        foreach (var e in disk.ReadDirectory())
        {
            var expectedSpan = (int)Math.Ceiling(e.FileLength / 256.0);
            var actualSpan = e.EndSector - e.StartSector + 1;
            Assert.Equal(expectedSpan, actualSpan);
        }
    }

    // ---- Empty-track fixture ----------------------------------------------------------------

    [Fact]
    public void JwsSytemDsk_AllZeroTrack2_DirectoryIsEmpty_NotAnError()
    {
        var disk = new DskImage(DiskPath("jws-sytem.dsk"));
        Assert.Empty(disk.ReadDirectory());
    }

    [Fact]
    public void Spel1Dsk_And_JwsSytemDsk_ShareByteIdenticalTrack1AndLabel()
    {
        var spel1 = File.ReadAllBytes(DiskPath("Spel1.dsk"));
        var jwsSytem = File.ReadAllBytes(DiskPath("jws-sytem.dsk"));
        Assert.Equal(spel1.AsSpan(0, 0x1000).ToArray(), jwsSytem.AsSpan(0, 0x1000).ToArray());
    }

    // ---- Full semi-DMA read round-trip against a real image (Upd765 level) --------------------

    [Fact]
    public void Upd765_ReadData_AgainstRealSpel1Dsk_MatchesRawFileBytes_FirstTrack()
    {
        var path = DiskPath("Spel1.dsk");
        var disk = new DskImage(path);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // READ DATA: unit=0, cylinder=0, head=0, sector=1, N=1 (256B), EOT=16 (whole track).
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[4096];
        for (var i = 0; i < read.Length; i++)
        {
            Assert.Equal(0x01, fdc.ReadControl());
            read[i] = fdc.ReadData();
        }

        var expected = File.ReadAllBytes(path).AsSpan(0, 4096).ToArray();
        Assert.Equal(expected, read);

        // The byte getdos's own system-disk-signature check reads (docs/JWSDOS-format.md §6
        // step 7): a real JWSDOS disk is 0x20, not the PDOS/official-disk-BASIC 0xF3.
        Assert.Equal(0x20, read[0]);
        Assert.NotEqual(0xF3, read[0]);
    }

    [Fact]
    public void Upd765_ReadData_AgainstRealSpel1Dsk_MatchesRawFileBytes_SecondTrack()
    {
        var path = DiskPath("Spel1.dsk");
        var disk = new DskImage(path);
        var fdc = new Upd765 { Policy = TimingPolicy.Turbo };
        fdc.MountDisk(0, disk);

        // Cylinder 1 ("track 2" in getdos's own naming) — the SECOND DOS track load.
        fdc.WriteData(0x42);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01); // cylinder = 1
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        fdc.WriteData(0x01);
        fdc.WriteData(0x10);
        fdc.WriteData(0x00);
        fdc.WriteData(0x00);

        var read = new byte[4096];
        for (var i = 0; i < read.Length; i++)
        {
            Assert.Equal(0x01, fdc.ReadControl());
            read[i] = fdc.ReadData();
        }

        var expected = File.ReadAllBytes(path).AsSpan(0x1000, 4096).ToArray();
        Assert.Equal(expected, read);
    }
}

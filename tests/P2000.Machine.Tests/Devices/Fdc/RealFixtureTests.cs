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
/// <b>Scope note:</b> a full real-ROM-driven `getdos` RUN-gate boot test now exists —
/// see <c>tests/P2000.Machine.Tests/Boot/DiskBootTests.cs</c> — once
/// `docs/Monitor Documented Disassembly/` (`Startup.asm`/`Disk.asm`) resolved the SLOT1
/// header-bit polarity and the FDC cylinder-tracking/RESET-guard/Turbo-timing bugs that had
/// blocked it (see machine CLAUDE.md §17). These tests remain at the chip/board level on
/// purpose — they pin the SAME real disk data against the <see cref="Upd765"/> command surface
/// directly, independent of the ROM boot path.
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

    // ---- CHS->offset geometry mapping, pinned against real raw bytes (bug investigation,
    // CLAUDE.md §17 2026-07-30) ------------------------------------------------------------------

    [Fact]
    public void Spel1Dsk_ReadSectorCylinder1Head0Sector9_MatchesRawFileBytesAtOffset0x1800()
    {
        // Independent ground truth: read the raw file bytes directly (no DskImage/SectorOffset
        // involved) and confirm ReadSector's CHS addressing agrees. Also directly confirms the
        // owner's alternative "cylinder-major, side-minor" convention does NOT hold for this real
        // disk: under that convention (cylinder=1, head=0, sector=9) would land at raw offset
        // 0x2800, not 0x1800, and the bytes there are demonstrably different (see the raw-string
        // search finding in this entry's own findings-log write-up: "Tralieenspel"/"BABA" sit at
        // 0x1800, not 0x2800).
        var path = DiskPath("Spel1.dsk");
        var raw = File.ReadAllBytes(path);
        var expected = raw.AsSpan(0x1800, 256).ToArray();

        var disk = new DskImage(path);
        var actual = disk.ReadSector(cylinder: 1, head: 0, sector: 9).ToArray();

        Assert.Equal(expected, actual);
        // Sanity: this is genuinely the active side-1 directory's first sector, not blank filler.
        Assert.Contains("Tralieenspel", System.Text.Encoding.ASCII.GetString(actual));
    }

    // ---- Directory browse: exact real content, stale cluster excluded ---------------------------

    /// <summary>The confirmed 18 real filenames on <c>Spel1.dsk</c>'s active side-1 directory
    /// (raw 0x1800-0x1FFF), in on-disk order (<c>docs/P2000T-disk-formats.md</c> §2/§4).</summary>
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

    // ---- Directory-format auto-detection (project CLAUDE.md §13 milestone 22) -------------------

    [Fact]
    public void Spel1Dsk_DetectDirectoryFormat_ReturnsJwsdos()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        Assert.Equal(DiskDirectoryFormat.Jwsdos, disk.DetectDirectoryFormat());
    }

    [Fact]
    public void JwsSytemDsk_DetectDirectoryFormat_ReturnsUnknown()
    {
        // CHANGED (machine milestone 23): jws-sytem.dsk's real JWSDOS directory region (raw
        // 0x1800) is legitimately empty (see the test just below) — that's now equally consistent
        // with a blank PDOS working disk, so it no longer defaults to Jwsdos. Falls through to
        // Unknown, same as any other all-empty directory region.
        // NOT IsDirectoryRegionBlank(), though — track 1 (PDOS's own FCB region, raw 0x0000) holds
        // this real disk's genuine JWSDOS boot code, not all-zero data, so only the JWSDOS
        // directory offset is empty here, not both formats' regions.
        var disk = new DskImage(DiskPath("jws-sytem.dsk"));
        Assert.Equal(DiskDirectoryFormat.Unknown, disk.DetectDirectoryFormat());
        Assert.False(disk.IsDirectoryRegionBlank());
    }

    [Fact]
    public void VolorgDsk_DetectDirectoryFormat_ReturnsPdosWorking()
    {
        // A real PDOS working-disk fixture: no JWSDOS label, and the bytes sitting at JWSDOS's
        // directory offset (0x1800) are arbitrary binary data, not plausible filenames — correctly
        // NOT Jwsdos. Milestone 22a's disambiguation validates track 1's own first FCB slot
        // (VOLORG, which legitimately carries the 0xF3 flag value at position 1 — the exact real
        // case this disambiguation exists for, docs/P2000T-disk-formats.md §7 item 8) and finds it
        // plausible, so this is PdosWorking — no longer Unknown now that milestone 22a is implemented.
        var disk = new DskImage(DiskPath("volorg.dsk"));
        Assert.Equal(DiskDirectoryFormat.PdosWorking, disk.DetectDirectoryFormat());
    }

    [Fact]
    public void DiskBasicDsk_DetectDirectoryFormat_ReturnsPdosSystem_NotFalsePositiveDirectory()
    {
        // A real official Philips "Disk BASIC 24K" system disk (owner-supplied): track 1 offset 0
        // is the genuine 0xF3 boot signature, and the REST of that first FCB-shaped slot is real
        // Z80 boot code, not a plausible filename/allocation-map — the disambiguation logic
        // (docs/P2000T-disk-formats.md §7 item 8) exists precisely to get this case right instead
        // of reporting a false-positive PdosWorking directory.
        var disk = new DskImage(DiskPath("diskbasic_1.6uk.dsk"));
        Assert.Equal(DiskDirectoryFormat.PdosSystem, disk.DetectDirectoryFormat());
    }

    // ---- PDOS FCB directory parse (project CLAUDE.md §13 milestone 22a; docs/P2000T-disk-formats.md
    // §6a) -------------------------------------------------------------------------------------------

    [Fact]
    public void VolorgDsk_ReadPdosDirectory_ReturnsBothRealEntries_InOrder()
    {
        var disk = new DskImage(DiskPath("volorg.dsk"));
        var entries = disk.ReadPdosDirectory();

        Assert.Equal(2, entries.Count);
        Assert.Equal("VOLORG", entries[0].Name);
        Assert.Equal("BAS", entries[0].Extension);
        Assert.Equal("VOLINFO", entries[1].Name);
        Assert.Equal("BAS", entries[1].Extension);
    }

    [Fact]
    public void VolorgDsk_ReadPdosDirectory_Volorg_HasConfirmedSizeAndTrackRange()
    {
        // docs/P2000T-disk-formats.md §6a: VOLORG's real allocation map is
        // [04,05,06,07,0C,0D,0E,0F,10,11,12] (11 records, exact-fit sector count 0x2C=44) — the
        // 0xF3-flagged-but-still-valid case this whole disambiguation feature exists for.
        var disk = new DskImage(DiskPath("volorg.dsk"));
        var volorg = disk.ReadPdosDirectory().Single(e => e.Name == "VOLORG");

        Assert.Equal(44 * 256, volorg.FileLength);
        Assert.Equal(2, volorg.StartTrack); // first record 0x04 -> track (4/4)+1 = 2
        Assert.Equal(5, volorg.EndTrack);   // last record 0x12=18 -> track (18/4)+1 = 5
        Assert.Equal(44, volorg.TotalSectors); // 11 records x 4, exact fit
    }

    [Fact]
    public void VolorgDsk_ReadPdosDirectory_Volinfo_HasConfirmedSizeAndTrackRange()
    {
        // docs/P2000T-disk-formats.md §6a/§7 item 8: VOLINFO's real allocation map is
        // [08,09,0A,0B] (4 records, track 3 exactly — independently confirmed via the real
        // interleave-reconstruction finding, "VOLINFO.BAS (track 3, records 8-11)"), sector count
        // 0x0E=14 with 2 sectors' slack (16 allocated, 14 real).
        var disk = new DskImage(DiskPath("volorg.dsk"));
        var volinfo = disk.ReadPdosDirectory().Single(e => e.Name == "VOLINFO");

        Assert.Equal(14 * 256, volinfo.FileLength);
        Assert.Equal(3, volinfo.StartTrack); // first record 0x08=8 -> track (8/4)+1 = 3
        Assert.Equal(3, volinfo.EndTrack);   // last record 0x0B=11 -> track (11/4)+1 = 3
        Assert.Equal(16, volinfo.TotalSectors); // 4 records x 4
    }

    // ---- Side / start-end sector fields (project CLAUDE.md §13 milestone 22) ---------------------

    [Fact]
    public void Spel1Dsk_Directory_AllEntries_HaveConfirmedSideValue()
    {
        // docs/P2000T-disk-formats.md §4: every one of Spel1.dsk's real active-directory entries
        // reads DE_head=1 (side 2) — a confirmed real per-disk value, not the 0 originally
        // (mis)reported in an earlier pass of that doc.
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var entries = disk.ReadDirectory();

        Assert.Equal(18, entries.Count);
        Assert.All(entries, e => Assert.Equal(1, e.Head));
    }

    [Fact]
    public void Spel1Dsk_Directory_AutorunEntry_HasConfirmedStartEndSector()
    {
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var autorun = disk.ReadDirectory().Single(e => e.Filename == "AUTORUN");

        Assert.Equal(622, autorun.StartSector);
        Assert.Equal(632, autorun.EndSector);
    }

    // ---- Raw sector-1 read for the fallback dump view (project CLAUDE.md §13 milestone 22b) ------
    // No new API needed — DskImage.ReadSector already reads raw bytes off the mounted image with
    // no FDC/command-sequence semantics, and its out-of-range/short-mount behavior already matches
    // milestone 20d's 0x00 fill-byte convention (see DskImageTests.cs's own coverage of that). These
    // tests pin the SPECIFIC "sector 1" call the UI fallback view (milestone 15b) makes.

    [Fact]
    public void DiskBasicDsk_ReadSector_Track1Sector1_MatchesRawFileBytesExactly()
    {
        var path = DiskPath("diskbasic_1.6uk.dsk");
        var disk = new DskImage(path);

        var sector1 = disk.ReadSector(cylinder: 0, head: 0, sector: 1).ToArray();

        var expected = File.ReadAllBytes(path).AsSpan(0, DskImage.BytesPerSector).ToArray();
        Assert.Equal(expected, sector1);
        Assert.Equal(0xF3, sector1[0]); // the real PDOS system-disk boot signature
    }

    [Fact]
    public void VolorgDsk_ReadSector_Track1Sector1_MatchesRawFileBytesExactly()
    {
        var path = DiskPath("volorg.dsk");
        var disk = new DskImage(path);

        var sector1 = disk.ReadSector(cylinder: 0, head: 0, sector: 1).ToArray();

        var expected = File.ReadAllBytes(path).AsSpan(0, DskImage.BytesPerSector).ToArray();
        Assert.Equal(expected, sector1);
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

        // READ A TRACK (real ROM byte 0x42 — project CLAUDE.md §17 2026-07-24 opcode-identity
        // finding): unit=0, cylinder=0, head=0, R=1(ignored), N=1 (256B), EOT=16 (whole track).
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

        // The byte getdos's own system-disk-signature check reads (docs/P2000T-disk-formats.md §6
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

        // SEEK to cylinder 1 first — matches the real ROM driver's own sequence (getdos calls
        // disk_gotrack/SEEK between track reads; READ A TRACK addresses wherever the head
        // physically is, not its own hardcoded cylinder byte — see Upd765.DispatchDataCommand).
        fdc.WriteData(0x0F);
        fdc.WriteData(0x00);
        fdc.WriteData(0x01);
        for (var i = 0; i < 300; i++) fdc.Tick(); // let the (deferred) seek actually complete

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

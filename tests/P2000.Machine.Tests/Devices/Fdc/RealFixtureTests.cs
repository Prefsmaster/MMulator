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
    public void JwsSytemDsk_ReadSectorCylinder1Head0_MatchesRawFileBytesAtOffset0x2000_NotSideMajorOffset0x1000()
    {
        // Ground truth pinned against real bytes (no DskImage/SectorOffset involved on the
        // "expected" side): getdos's real second-track read is (cylinder=1, head=0) — the
        // monitor ROM has no double-sided support (owner, 2026-07-30 bug-investigation finding,
        // project CLAUDE.md §17). Under the CORRECTED cylinder-major layout that lands on raw
        // 0x2000, not raw 0x1000 (the disproven side-major reading's prediction — genuinely
        // blank on this real disk).
        var path = DiskPath("jws-sytem.dsk");
        var raw = File.ReadAllBytes(path);
        var expected = raw.AsSpan(0x2000, 256).ToArray();

        var disk = new DskImage(path);
        var actual = disk.ReadSector(cylinder: 1, head: 0, sector: 1).ToArray();

        Assert.Equal(expected, actual);
        // Sanity: real, non-trivial Z80 code, not the blank filler that sits at raw 0x1000 on
        // this specific disk.
        Assert.False(actual.All(b => b == 0x00));
    }

    // ---- Directory browse: exact real content, BOTH sides (project CLAUDE.md §17, 2026-07-31
    // fix — ReadDirectory() previously only ever surfaced side 2, reading a hardcoded 0x1800
    // offset that was neither side's real location) -----------------------------------------------

    /// <summary>The confirmed 18 real filenames on <c>Spel1.dsk</c>'s side-2 directory (raw
    /// <c>0x3000</c>-<c>0x37FF</c>, <c>dir_side2_prep</c>'s confirmed real target), in on-disk
    /// order (<c>docs/P2000T-disk-formats.md</c> §2/§4).</summary>
    private static readonly string[] Side2RealFilenames =
    {
        "Tralieenspel", "klemvast", "Elevatie", "Risk", "Space Misson", "Cijferdans",
        "Info Bat.S.", "Battle star", "Toernooi", "Doolhofspel", "rij sim",
        "Doolhof 3 dim.", "JACKPOT", "Jackpot", "AUTORUN", "Letter-invaders",
        "Grotvliergers", "BABA",
    };

    /// <summary>The confirmed 20 real filenames on <c>Spel1.dsk</c>'s side-1 directory (raw
    /// <c>0x2800</c>-<c>0x2FFF</c>, <c>dir_side1_prep</c>'s confirmed real target) — previously
    /// mislabeled a "stale cluster from another disk" (a real mislabeling this project carried
    /// for a long time, corrected 2026-07-31): every entry here carries a self-consistent
    /// <c>DE_head=0</c>, exactly matching a genuine side-1 catalog written by JWSDOS's own
    /// ordinary <c>save_directory</c> path, same as side 2's.</summary>
    private static readonly string[] Side1RealFilenames =
    {
        "Fraxxon + scores", "Centipede", "Androide-nim", "Race-track", "Car Race",
        "racen 2.1", "Lady Bug", "Space Atack", "Brick-Wall", "brick-Wall II",
        "RAce-circuit", "Handicap race", "Speelpleis", "Valbal", "Space fight",
        "Fight in space", "Eendenjacht", "Kleiduivschiet", "Mens-e-j-niet", "Superlaser",
    };

    [Fact]
    public void Spel1Dsk_Directory_ReturnsBothSides_Side1ThenSide2_38EntriesTotal()
    {
        // The core regression case for the 2026-07-31 fix — the first time this specific "both
        // sides on one real disk" scenario has ever been exercised end to end (the old code only
        // ever surfaced one side, so this combination was structurally untestable before).
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var entries = disk.ReadDirectory();

        Assert.Equal(Side1RealFilenames.Length + Side2RealFilenames.Length, entries.Count);

        for (var i = 0; i < Side1RealFilenames.Length; i++)
        {
            Assert.Equal(Side1RealFilenames[i], entries[i].Filename);
            Assert.Equal("BAS", entries[i].Extension);
            Assert.Equal(0, entries[i].Head); // read from the head-0 region — must match
        }

        var side2Start = Side1RealFilenames.Length;
        for (var i = 0; i < Side2RealFilenames.Length; i++)
        {
            var e = entries[side2Start + i];
            Assert.Equal(Side2RealFilenames[i], e.Filename);
            Assert.Equal("BAS", e.Extension);
            Assert.Equal((byte)'B', e.FileType);
            Assert.Equal(1, e.Head); // read from the head-1 region — must match
        }
    }

    [Fact]
    public void Spel1Dsk_Directory_AutorunEntry_HasConfirmedTransferAddress()
    {
        // CORRECTED (2026-07-31 fix): the previously-"confirmed" 0x7000 came from reading the
        // 0x1800 duplicate/near-match region, not AUTORUN's own real location (raw 0x3000). The
        // duplicate differs from the real content by exactly one byte (project CLAUDE.md §17,
        // 2026-07-31 audit) — AUTORUN's own transfer-address LOW byte is precisely that one byte,
        // which is why this is the only field this fix changes for this specific entry.
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var autorun = disk.ReadDirectory().Single(e => e.Filename == "AUTORUN");
        Assert.Equal(0x6547, autorun.TransferAddress);
        Assert.Equal(2744, autorun.FileLength);
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
    public void JwsSytemDsk_DetectDirectoryFormat_ReturnsJwsdos()
    {
        // CORRECTED (2026-07-31 fix, supersedes the old "...ReturnsUnknown" expectation): the
        // old hardcoded 0x1800 offset was genuinely empty on this disk, but that was never
        // jws-sytem.dsk's real directory location — its actual side-1 directory (raw 0x2800,
        // dir_side1_prep's confirmed target) holds a real, well-formed 14-entry catalog (the
        // disk's own utility programs — "JWS Systeem Disk", "Format", "AUTORUN", "Tetris", etc.).
        // Side 2 (raw 0x3000) is genuinely empty on this disk — not investigated further.
        var disk = new DskImage(DiskPath("jws-sytem.dsk"));
        Assert.Equal(DiskDirectoryFormat.Jwsdos, disk.DetectDirectoryFormat());
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
        // docs/P2000T-disk-formats.md §4: EVERY entry's own embedded Head byte is self-consistent
        // with the physical region it was actually read from (project CLAUDE.md §17, 2026-07-31
        // fix's own test requirement — no cross-wiring between the two reads) — side 1's 20 real
        // entries all read Head=0, side 2's 18 real entries all read Head=1.
        var disk = new DskImage(DiskPath("Spel1.dsk"));
        var entries = disk.ReadDirectory();

        Assert.Equal(38, entries.Count);
        Assert.All(entries.Take(Side1RealFilenames.Length), e => Assert.Equal(0, e.Head));
        Assert.All(entries.Skip(Side1RealFilenames.Length), e => Assert.Equal(1, e.Head));
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

    // ---- jws-sytem.dsk's real directory (project CLAUDE.md §17, 2026-07-31 fix) -------------

    /// <summary>The confirmed 14 real filenames on <c>jws-sytem.dsk</c>'s side-1 directory (raw
    /// <c>0x2800</c>-<c>0x2FFF</c>) — this system disk's own utility programs, previously
    /// entirely missed since the old hardcoded 0x1800 offset was genuinely empty for THIS disk.
    /// Side 2 (raw <c>0x3000</c>) is genuinely empty on this disk.</summary>
    private static readonly string[] JwsSytemSide1RealFilenames =
    {
        "JWS Systeem Disk", "Format", "AUTORUN", "Disk-report 2.1", "Disk-duplicator",
        "Disk Inhoud Spec", "Multi-file Copy", "Back-updata 1.1", "Disk Util.3 in 1",
        "Diskzoeker", "Edit 40", "Edit 80", "Filecopy 1.4", "Tetris",
    };

    [Fact]
    public void JwsSytemDsk_Directory_ReturnsSide1sRealCatalog_NotEmpty()
    {
        // CORRECTED (2026-07-31 fix, supersedes the old "...DirectoryIsEmpty..." expectation):
        // this disk's real directory was never empty — the old hardcoded 0x1800 offset just
        // happened to be empty for this specific disk, which is a genuinely different byte
        // range than side 1's actual location (raw 0x2800).
        var disk = new DskImage(DiskPath("jws-sytem.dsk"));
        var entries = disk.ReadDirectory();

        Assert.Equal(JwsSytemSide1RealFilenames.Length, entries.Count); // side 2 is genuinely empty
        for (var i = 0; i < JwsSytemSide1RealFilenames.Length; i++)
        {
            Assert.Equal(JwsSytemSide1RealFilenames[i], entries[i].Filename);
            Assert.Equal(0, entries[i].Head);
        }
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

        // Cylinder-major layout (project CLAUDE.md §17, 2026-07-30 correction): cylinder 1/head 0
        // lands at raw 0x2000, not 0x1000 — the disproven side-major reading's prediction.
        var expected = File.ReadAllBytes(path).AsSpan(0x2000, 4096).ToArray();
        Assert.Equal(expected, read);
    }
}

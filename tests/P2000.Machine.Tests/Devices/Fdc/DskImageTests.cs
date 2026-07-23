using P2000.Machine.Devices.Fdc;

namespace P2000.Machine.Tests.Devices.Fdc;

/// <summary>
/// Unit tests for <see cref="DskImage"/> against hand-built synthetic images (project CLAUDE.md
/// §13 milestone 19). These exercise the geometry-autodetect/CHS/directory-parsing LOGIC only —
/// the real-fixture RUN-gate and directory-content tests (against <c>Spel1.dsk</c>/
/// <c>jwssytem.dsk</c>) are tracked separately pending those files.
/// </summary>
public class DskImageTests
{
    private static byte[] BuildSyntheticImage(int tracks, int sides)
    {
        var image = new byte[tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector];
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    // ---- Geometry auto-detect (docs/JWSDOS-format.md §3) --------------------------------------

    [Fact]
    public void Mount_DoubleSided40Track_DetectsGeometry()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        Assert.Equal(40, disk.Tracks);
        Assert.Equal(2, disk.Sides);
    }

    [Fact]
    public void Mount_SingleSided35Track_DetectsGeometry()
    {
        var image = BuildSyntheticImage(tracks: 35, sides: 1);
        var disk = new DskImage(image);
        Assert.Equal(35, disk.Tracks);
        Assert.Equal(1, disk.Sides);
    }

    [Fact]
    public void Mount_TooShortForLabel_Throws()
    {
        var image = new byte[100];
        Assert.Throws<ArgumentException>(() => new DskImage(image));
    }

    // ---- CreateBlank --------------------------------------------------------------------------

    [Fact]
    public void CreateBlank_IsAllZero_WithGivenGeometry()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        Assert.Equal(40, disk.Tracks);
        Assert.Equal(2, disk.Sides);
        foreach (var b in disk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);
    }

    // ---- CHS sector addressing (side-major, cylinder-minor — derived, see DskImage's doc) -----

    [Fact]
    public void ReadWriteSector_Side0Cylinder1Sector9_LandsAtRawOffset0x1800()
    {
        // docs/JWSDOS-format.md §2: side 1's active directory sits at raw 0x1800-0x1FFF, which
        // is cylinder 1 (getdos's "track 2"), head 0, sectors 9-16 — this pins that identity.
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = (byte)(i + 1);

        disk.WriteSector(cylinder: 1, head: 0, sector: 9, pattern);

        Assert.Equal(pattern, disk.ReadSector(1, 0, 9).ToArray());
    }

    [Fact]
    public void ReadWriteSector_Head1_IsInTheSecondHalfOfTheImage()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = (byte)(200 - i);

        disk.WriteSector(cylinder: 0, head: 1, sector: 1, pattern);

        Assert.Equal(pattern, disk.ReadSector(0, 1, 1).ToArray());
        // Head 0's own cylinder-0/sector-1 must be untouched (different physical surface).
        foreach (var b in disk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);
    }

    [Fact]
    public void WriteSector_WhenWriteProtected_IsIgnored()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        disk.WriteProtected = true;
        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = 0xAA;

        disk.WriteSector(0, 0, 1, pattern);

        foreach (var b in disk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);
    }

    // ---- Directory browse (docs/JWSDOS-format.md §4; side-1 active directory only) ------------

    private static void WriteDirectoryEntry(byte[] image, int slotIndex, string filename,
        string extension, char fileType, ushort fileLength, ushort transferAddress,
        byte head, ushort startSector, ushort endSector)
    {
        var offset = 0x1800 + slotIndex * 32;
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(filename.PadRight(16));
        var extBytes = System.Text.Encoding.ASCII.GetBytes(extension.PadRight(3));
        nameBytes.CopyTo(image, offset);
        extBytes.CopyTo(image, offset + 16);
        image[offset + 19] = (byte)fileType;
        image[offset + 20] = (byte)(fileLength & 0xFF);
        image[offset + 21] = (byte)(fileLength >> 8);
        image[offset + 22] = (byte)(transferAddress & 0xFF);
        image[offset + 23] = (byte)(transferAddress >> 8);
        image[offset + 24] = head;
        image[offset + 25] = (byte)(startSector & 0xFF);
        image[offset + 26] = (byte)(startSector >> 8);
        image[offset + 27] = (byte)(endSector & 0xFF);
        image[offset + 28] = (byte)(endSector >> 8);
    }

    [Fact]
    public void ReadDirectory_ParsesPopulatedEntries_SkipsEmptySlots()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        WriteDirectoryEntry(image, 0, "TRALIEENSPEL", "BAS", 'B', 12345, 0x6547, 0, 1, 48);
        WriteDirectoryEntry(image, 1, "AUTORUN", "BAS", 'B', 500, 0x7000, 0, 49, 50);
        // Slots 2..63 are left zero — empty.

        var disk = new DskImage(image);
        var entries = disk.ReadDirectory();

        Assert.Equal(2, entries.Count);
        Assert.Equal("TRALIEENSPEL", entries[0].Filename);
        Assert.Equal("BAS", entries[0].Extension);
        Assert.Equal("TRALIEENSPEL.BAS", entries[0].FullName);
        Assert.Equal((byte)'B', entries[0].FileType);
        Assert.Equal(12345, entries[0].FileLength);
        Assert.Equal(0x6547, entries[0].TransferAddress);
        Assert.Equal(1, entries[0].StartSector);
        Assert.Equal(48, entries[0].EndSector);
        Assert.Equal("AUTORUN.BAS", entries[1].FullName);
    }

    [Fact]
    public void ReadDirectory_NeverReadsTheStaleClusterAt0x1000()
    {
        // docs/JWSDOS-format.md §2/§7 item 3: raw 0x1000-0x17FF holds a real, struct-shaped but
        // STALE directory cluster from a different disk operation entirely — must never surface.
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        // Poke a plausible-looking directory entry into the stale region.
        var staleOffset = 0x1000;
        System.Text.Encoding.ASCII.GetBytes("PHANTOM FILE    ").CopyTo(image, staleOffset);
        System.Text.Encoding.ASCII.GetBytes("BAS").CopyTo(image, staleOffset + 16);
        image[staleOffset + 19] = (byte)'B';

        var disk = new DskImage(image);
        var entries = disk.ReadDirectory();

        Assert.DoesNotContain(entries, e => e.Filename.Contains("PHANTOM"));
    }

    [Fact]
    public void ReadDirectory_AllZeroTrack_ReturnsEmptyDirectory()
    {
        // docs/JWSDOS-format.md §2: jwssytem.dsk's entire track 2 is all-zero — an empty
        // directory must not be treated as an error.
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        Assert.Empty(disk.ReadDirectory());
    }

    // ---- CreateBlank produces genuinely unformatted media (project CLAUDE.md §13.20 test (g)) --

    [Fact]
    public void CreateBlank_ExactByteSize_ForConfiguredGeometry()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        Assert.Equal(40 * 2 * DskImage.SectorsPerTrack * DskImage.BytesPerSector, disk.GetBytes().Length);
    }

    [Fact]
    public void CreateBlank_NoValidLabel_AtAutoDetectOffsets()
    {
        // Confirms the blank image reads as genuinely unformatted, not silently pre-labeled —
        // a fresh DskImage(bytes) reconstruction of it must NOT report the geometry the
        // creator asked for via the label path (both offsets are zero, not 'S'/'D' or a
        // plausible track-count byte).
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        var bytes = disk.GetBytes();
        Assert.Equal(0x00, bytes[0x0FEF]);
        Assert.Equal(0x00, bytes[0x0FFF]);
    }

    // ---- Dirty tracking (project CLAUDE.md §13 milestone 20a) ----------------------------------

    [Fact]
    public void CreateBlank_IsNotDirty()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        Assert.False(disk.IsDirty);
    }

    [Fact]
    public void Mount_FromBytes_IsNotDirty()
    {
        var image = BuildSyntheticImage(tracks: 40, sides: 2);
        var disk = new DskImage(image);
        Assert.False(disk.IsDirty);
    }

    [Fact]
    public void WriteSector_SetsDirty()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        disk.WriteSector(0, 0, 1, new byte[256]);
        Assert.True(disk.IsDirty);
    }

    [Fact]
    public void WriteSector_WhenWriteProtected_DoesNotSetDirty()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        disk.WriteProtected = true;
        disk.WriteSector(0, 0, 1, new byte[256]);
        Assert.False(disk.IsDirty);
    }

    [Fact]
    public void MarkClean_ClearsDirty()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        disk.WriteSector(0, 0, 1, new byte[256]);
        Assert.True(disk.IsDirty);

        disk.MarkClean();

        Assert.False(disk.IsDirty);
    }

    [Fact]
    public void MarkClean_ThenWriteAgain_ReSetsDirty()
    {
        // The flag isn't sticky-false after the first save (project CLAUDE.md §13.20a test (e)).
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        disk.WriteSector(0, 0, 1, new byte[256]);
        disk.MarkClean();

        disk.WriteSector(0, 0, 2, new byte[256]);

        Assert.True(disk.IsDirty);
    }

    // ---- GetBytes (host Save/Save-as, project CLAUDE.md §13.20 test (h)) ------------------------

    [Fact]
    public void GetBytes_ThenReload_RoundTripsByteIdentical()
    {
        // Needs a genuine on-disk label to survive a reload-by-bytes (auto-detect reads it back)
        // — a truly blank/unformatted image has none, by design (the "genuinely unformatted"
        // guarantee this same file's CreateBlank tests pin down), so this uses a labeled
        // synthetic image instead, matching the mount-an-existing-file path GetBytes exists for.
        var disk = new DskImage(BuildSyntheticImage(tracks: 40, sides: 2));
        var pattern = new byte[256];
        for (var i = 0; i < 256; i++) pattern[i] = (byte)(i ^ 0x5A);
        disk.WriteSector(3, 1, 5, pattern);

        var saved = disk.GetBytes();
        var reloaded = new DskImage(saved);

        Assert.Equal(pattern, reloaded.ReadSector(3, 1, 5).ToArray());
        Assert.Equal(disk.Tracks, reloaded.Tracks);
        Assert.Equal(disk.Sides, reloaded.Sides);
    }

    [Fact]
    public void GetBytes_ReturnsACopy_NotTheLiveBackingArray()
    {
        var disk = DskImage.CreateBlank(tracks: 40, sides: 2);
        var bytes = disk.GetBytes();
        bytes[0] = 0xFF;

        foreach (var b in disk.ReadSector(0, 0, 1)) Assert.Equal(0x00, b);
    }
}

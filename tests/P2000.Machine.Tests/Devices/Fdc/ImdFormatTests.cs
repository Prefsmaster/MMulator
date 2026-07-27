using P2000.Machine.Devices.Fdc;

namespace P2000.Machine.Tests.Devices.Fdc;

/// <summary>
/// Unit tests for IMD (ImageDisk) read/write support (project CLAUDE.md milestone 21) — both the
/// internal <see cref="ImdFormat"/> parser/serializer and its wiring through <see cref="DskImage"/>'s
/// public <c>Mount</c>/<c>GetImdBytes</c>/<c>Format</c> surface, per the milestone's own 5-item
/// test list.
/// </summary>
public class ImdFormatTests
{
    private static byte[] BuildDskBytes(int tracks, int sides, byte fill = 0x00)
    {
        var image = new byte[tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector];
        if (fill != 0x00) Array.Fill(image, fill);
        image[0x0FEF] = (byte)(sides == 2 ? 'D' : 'S');
        image[0x0FFF] = (byte)(tracks + 1);
        return image;
    }

    // ---- (a) round-trip byte-identical, including a real (non-sequential) sector-order map ----

    [Fact]
    public void RoundTrip_ImdWithNonSequentialOrderMap_IsByteIdentical()
    {
        const int tracks = 2;
        const int sides = 2;
        var data = BuildDskBytes(tracks, sides, fill: 0xAA);

        // A real captured disk's interleave rarely matches logical order — reverse it on one
        // track to prove Mount/GetImdBytes actually preserve the map rather than silently
        // normalizing to sequential.
        var reversedMap = new byte[DskImage.SectorsPerTrack];
        for (var i = 0; i < reversedMap.Length; i++) reversedMap[i] = (byte)(DskImage.SectorsPerTrack - i);
        var sequentialMap = new byte[DskImage.SectorsPerTrack];
        for (var i = 0; i < sequentialMap.Length; i++) sequentialMap[i] = (byte)(i + 1);

        var orderMaps = new Dictionary<(int Cylinder, int Head), byte[]>
        {
            [(0, 0)] = reversedMap,
            [(0, 1)] = sequentialMap,
            [(1, 0)] = sequentialMap,
            [(1, 1)] = sequentialMap,
        };

        var originalImdBytes = ImdFormat.Write(data, tracks, sides, orderMaps);

        var (disk, mismatch) = DskImage.Mount(originalImdBytes, configuredTracks: tracks, configuredSides: sides);
        Assert.Equal(DiskGeometryMismatchKind.None, mismatch.Kind);
        Assert.Equal(DiskImageFormat.Imd, disk.Format);

        var resavedBytes = disk.GetImdBytes();

        Assert.Equal(originalImdBytes, resavedBytes);
    }

    // ---- (b) "all sectors this value" compression marker reads as a fully-populated track ------

    [Fact]
    public void Read_CompressedSectorMarker_FillsWholeSectorWithTheGivenValue()
    {
        const byte fillByte = 0xE5;
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("IMD 1.18: test fixture\r\n"));
        bytes.Add(0x1A);

        // One track: cylinder 0, head 0, 16 sectors, size code 1 (256 B), sequential order map.
        bytes.Add(5); // mode
        bytes.Add(0); // cylinder
        bytes.Add(0); // head byte (no maps)
        bytes.Add(DskImage.SectorsPerTrack); // sector count
        bytes.Add(1); // size code -> 256 B
        for (byte s = 1; s <= DskImage.SectorsPerTrack; s++) bytes.Add(s); // sequential numbering map

        for (var i = 0; i < DskImage.SectorsPerTrack; i++)
        {
            bytes.Add(2); // type 2 = compressed, normal
            bytes.Add(fillByte);
        }

        var (data, tracks, sides, _) = ImdFormat.Read(bytes.ToArray());
        Assert.Equal(1, tracks);
        Assert.Equal(1, sides);
        Assert.Equal(tracks * sides * DskImage.SectorsPerTrack * DskImage.BytesPerSector, data.Length);
        foreach (var b in data) Assert.Equal(fillByte, b);
    }

    // ---- (c) writing a DskImage built from a plain .dsk mount produces a valid, sequential IMD -

    [Fact]
    public void GetImdBytes_FromDskMountedImage_ProducesValidImd_WithSequentialOrderMaps()
    {
        const int tracks = 3;
        const int sides = 2;
        var dskBytes = BuildDskBytes(tracks, sides, fill: 0x7E);

        var (disk, mismatch) = DskImage.Mount(dskBytes, configuredTracks: tracks, configuredSides: sides);
        Assert.Equal(DiskGeometryMismatchKind.None, mismatch.Kind);
        Assert.Equal(DiskImageFormat.Dsk, disk.Format);

        var imdBytes = disk.GetImdBytes();
        Assert.True(ImdFormat.IsImdFile(imdBytes));

        var (readBack, readTracks, readSides, orderMaps) = ImdFormat.Read(imdBytes);
        Assert.Equal(tracks, readTracks);
        Assert.Equal(sides, readSides);
        Assert.Equal(disk.GetBytes(), readBack);

        Assert.Equal(tracks * sides, orderMaps.Count);
        foreach (var map in orderMaps.Values)
        {
            for (var i = 0; i < map.Length; i++) Assert.Equal((byte)(i + 1), map[i]);
        }
    }

    // ---- (d) DskImage.Format correctly reports .dsk vs. IMD after each mount path --------------

    [Fact]
    public void Mount_RawDsk_ReportsDskFormat()
    {
        var dskBytes = BuildDskBytes(tracks: 40, sides: 2);
        var (disk, _) = DskImage.Mount(dskBytes, configuredTracks: 40, configuredSides: 2);
        Assert.Equal(DiskImageFormat.Dsk, disk.Format);
    }

    [Fact]
    public void Mount_ImdFile_ReportsImdFormat()
    {
        var data = BuildDskBytes(tracks: 1, sides: 1);
        var imdBytes = ImdFormat.Write(data, tracks: 1, sides: 1, orderMaps: null);
        var (disk, _) = DskImage.Mount(imdBytes, configuredTracks: 1, configuredSides: 1);
        Assert.Equal(DiskImageFormat.Imd, disk.Format);
    }

    // ---- (e) IMD mounting is fully deterministic — ms.20d's mismatch machinery never runs -------

    [Fact]
    public void Mount_ImdFile_NeverReportsMismatch_EvenWhenConfiguredGeometryDiffers()
    {
        const int actualTracks = 40;
        const int actualSides = 2;
        var data = BuildDskBytes(actualTracks, actualSides);
        var imdBytes = ImdFormat.Write(data, actualTracks, actualSides, orderMaps: null);

        // Deliberately wrong configured geometry — a raw .dsk mount with this mismatch would hit
        // ms.20d's Candidate/NoCandidate machinery; an IMD mount must not.
        var (disk, mismatch) = DskImage.Mount(imdBytes, configuredTracks: 35, configuredSides: 1);

        Assert.Equal(DiskGeometryMismatchKind.None, mismatch.Kind);
        Assert.Equal(actualTracks, disk.Tracks);
        Assert.Equal(actualSides, disk.Sides);
    }

    // ---- Extra coverage: sector-size validation, geometry derivation ---------------------------

    [Fact]
    public void Read_NonStandardSectorSize_ThrowsInvalidDataException()
    {
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("IMD 1.18: bad sector size\r\n"));
        bytes.Add(0x1A);
        bytes.Add(5); // mode
        bytes.Add(0); // cylinder
        bytes.Add(0); // head byte
        bytes.Add(8); // sector count
        bytes.Add(3); // size code 3 -> 1024 B, not this project's 256 B

        Assert.Throws<InvalidDataException>(() => ImdFormat.Read(bytes.ToArray()));
    }

    [Fact]
    public void IsImdFile_RawDskBytes_ReturnsFalse()
    {
        var dskBytes = BuildDskBytes(tracks: 40, sides: 2);
        Assert.False(ImdFormat.IsImdFile(dskBytes));
    }

    [Fact]
    public void IsImdFile_ImdBytes_ReturnsTrue()
    {
        var data = BuildDskBytes(tracks: 1, sides: 1);
        var imdBytes = ImdFormat.Write(data, tracks: 1, sides: 1, orderMaps: null);
        Assert.True(ImdFormat.IsImdFile(imdBytes));
    }
}

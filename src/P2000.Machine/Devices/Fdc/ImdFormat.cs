namespace P2000.Machine.Devices.Fdc;

/// <summary>
/// IMD (ImageDisk) binary format reader/writer (project CLAUDE.md milestone 21; reference doc
/// §3a "RESOLVED — adopt IMD... as the emulator's native/preferred disk container"). Spec facts
/// below were extracted from MAME's <c>imd_dsk.cpp</c> (the primary published spec PDF at
/// <c>oldcomputers-ddns.org</c> was unreachable while building this — <c>ECONNREFUSED</c> —
/// MAME's own parser was used as the authoritative fallback source, cited in the reference doc
/// block).
///
/// <b>File shape:</b> an ASCII comment header terminated by a literal <c>0x1A</c> byte, followed
/// by a sequence of tracks (read until end-of-file — there is no separate geometry header field;
/// <see cref="DskImage.Tracks"/>/<see cref="DskImage.Sides"/> are derived from the highest
/// cylinder/head seen across all track descriptors). Each track is: a 5-byte descriptor (mode,
/// cylinder, head-byte, sector count, sector-size code), a sector-numbering map (one byte per
/// sector — the physical position each logical sector occupies, i.e. real interleave), then per
/// -sector data records in that SAME physical order (not logical sector order).
///
/// <b>Flagged limitation (not covered by the milestone text):</b> a track's head byte can also
/// carry an optional cylinder-map and/or head-map (bits <c>0x80</c>/<c>0x40</c>) for representing
/// physically-remapped tracks — <see cref="Read"/> parses past them correctly (so a file that
/// happens to use them still loads) but does not preserve or re-emit them; <see cref="Write"/>
/// never sets those bits. This project has no field to carry that remapping and no known P2000
/// image is expected to need it, so re-saving such a file would not be byte-identical — the same
/// shape of limitation already accepted for sector data's deleted-DAM/bad-CRC flags below.
/// </summary>
internal static class ImdFormat
{
    private const byte HeaderTerminator = 0x1A;

    /// <summary>128 &lt;&lt; size-code 1 = 256 — the project's one fixed sector size
    /// (<see cref="DskImage.BytesPerSector"/>); every IMD track this project writes uses it, and
    /// <see cref="Read"/> rejects any track that doesn't.</summary>
    private const byte SectorSizeCode = 1;

    /// <summary>Mode 5 = 250 kbps MFM — inferred from this project's already-documented 5¼"
    /// double-density, 300 RPM, MFM FDC model (reference doc §12/§13 FDC write-up), NOT an
    /// independently confirmed real-hardware data rate figure; a cosmetic/non-functional choice
    /// (project CLAUDE.md §17 findings log) since nothing in this emulator's timing model
    /// currently reads the mode byte back.</summary>
    private const byte Mode250KbpsMfm = 5;

    /// <summary>True if <paramref name="bytes"/> begins with IMD's own literal text signature
    /// (<c>"IMD "</c>) — content-based detection, per the milestone's explicit requirement, so a
    /// renamed file still loads correctly and a non-IMD file is never misread.</summary>
    public static bool IsImdFile(byte[] bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == (byte)'I' && bytes[1] == (byte)'M' && bytes[2] == (byte)'D' && bytes[3] == (byte)' ';

    /// <summary>Parses an IMD file into a flat side-major/cylinder-minor sector-dump buffer
    /// (matching <see cref="DskImage"/>'s own internal layout) plus the derived geometry and each
    /// track's sector-order map (for <see cref="DskImage.SectorOrderMaps"/>, so a later unmodified
    /// resave round-trips the real interleave rather than silently flattening it — reference doc
    /// §3a "round-tripping the per-sector order map faithfully").</summary>
    public static (byte[] Data, int Tracks, int Sides, Dictionary<(int Cylinder, int Head), byte[]> OrderMaps) Read(byte[] bytes)
    {
        var pos = 0;
        while (pos < bytes.Length && bytes[pos] != HeaderTerminator) pos++;
        if (pos >= bytes.Length)
            throw new InvalidDataException("IMD file has no header terminator (0x1A) — not a valid IMD file.");
        pos++; // past the terminator, into the first track descriptor

        var trackCylinders = new List<int>();
        var trackHeads = new List<int>();
        var trackOrderMaps = new List<byte[]>();
        var trackSectorData = new List<byte[][]>();

        while (pos < bytes.Length)
        {
            // 5-byte track descriptor: mode, cylinder, head-byte, sector count, size code.
            // `mode` isn't branched on — this project has exactly one FDC timing model regardless
            // of what a source file's tracks claim.
            pos++; // mode
            var cylinder = bytes[pos++];
            var headByte = bytes[pos++];
            var head = headByte & 0x3F;
            var hasCylinderMap = (headByte & 0x80) != 0;
            var hasHeadMap = (headByte & 0x40) != 0;
            var sectorCount = bytes[pos++];
            var sizeCode = bytes[pos++];
            var sectorSize = 128 << sizeCode;
            if (sectorSize != DskImage.BytesPerSector)
            {
                throw new InvalidDataException(
                    $"IMD track (cylinder {cylinder}, head {head}) uses {sectorSize}-byte sectors — " +
                    $"this project only supports {DskImage.BytesPerSector}-byte P2000 disk sectors.");
            }

            var orderMap = new byte[sectorCount];
            Array.Copy(bytes, pos, orderMap, 0, sectorCount);
            pos += sectorCount;

            // Optional cylinder/head remapping maps — parsed past (so file parsing stays
            // correct) but not preserved; see this class's own doc comment above.
            if (hasCylinderMap) pos += sectorCount;
            if (hasHeadMap) pos += sectorCount;

            var sectorData = new byte[sectorCount][];
            for (var i = 0; i < sectorCount; i++)
            {
                var type = bytes[pos++];
                var sectorBytes = new byte[sectorSize];
                switch (type)
                {
                    case 0:
                        // Unavailable — no data recorded at all. Zero-fill, matching this
                        // project's existing "unformatted disk area reads as 0x00" convention
                        // (DskImage.ExtendTo's own doc comment, project CLAUDE.md milestone 20d).
                        break;
                    case 1: case 3: case 5: case 7:
                        // Normal / deleted-DAM / bad-CRC variants — all store explicit data bytes.
                        // The deleted-DAM and bad-CRC distinctions are lost (DskImage has no field
                        // for either) — a flagged, deliberate limitation, same shape as the
                        // cylinder/head map one above.
                        Array.Copy(bytes, pos, sectorBytes, 0, sectorSize);
                        pos += sectorSize;
                        break;
                    case 2: case 4: case 6: case 8:
                        // Compressed ("all sectors this value") variants — one fill byte stands
                        // in for the whole sector; real IMD files use this for unformatted/blank
                        // regions (milestone 21's own explicit call-out not to assume
                        // explicit-only storage).
                        var fill = bytes[pos++];
                        Array.Fill(sectorBytes, fill);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown IMD sector data type {type}.");
                }
                sectorData[i] = sectorBytes;
            }

            trackCylinders.Add(cylinder);
            trackHeads.Add(head);
            trackOrderMaps.Add(orderMap);
            trackSectorData.Add(sectorData);
        }

        var tracks = trackCylinders.Count == 0 ? 0 : trackCylinders.Max() + 1;
        var sides = trackHeads.Count == 0 ? 0 : trackHeads.Max() + 1;

        var data = new byte[tracks * sides * DskImage.BytesPerTrack];
        var orderMaps = new Dictionary<(int Cylinder, int Head), byte[]>();

        for (var t = 0; t < trackCylinders.Count; t++)
        {
            var cylinder = trackCylinders[t];
            var head = trackHeads[t];
            var orderMap = trackOrderMaps[t];
            orderMaps[(cylinder, head)] = orderMap;

            for (var i = 0; i < orderMap.Length; i++)
            {
                var logicalSector = orderMap[i]; // 1-based, µPD765 convention
                var offset = head * tracks * DskImage.BytesPerTrack + cylinder * DskImage.BytesPerTrack +
                             (logicalSector - 1) * DskImage.BytesPerSector;
                trackSectorData[t][i].CopyTo(data, offset);
            }
        }

        return (data, tracks, sides, orderMaps);
    }

    /// <summary>Serializes a flat side-major/cylinder-minor sector-dump buffer into IMD form.
    /// <paramref name="orderMaps"/> entries are reused verbatim per (cylinder, head) when present
    /// and sized correctly; otherwise a plain sequential map is emitted (milestone 21: "nothing
    /// in this project currently generates or tracks real interleave... the map still needs to
    /// exist and round-trip correctly"). Every sector is written as data type 1 (normal,
    /// uncompressed) — no compression or DAM/bad-CRC modeling, since <see cref="DskImage"/> has
    /// no fields for those (flagged limitation, same as <see cref="Read"/>'s side of it).</summary>
    public static byte[] Write(byte[] data, int tracks, int sides,
        IReadOnlyDictionary<(int Cylinder, int Head), byte[]>? orderMaps)
    {
        using var stream = new MemoryStream();
        var header = System.Text.Encoding.ASCII.GetBytes("IMD 1.18: MMulator P2000T emulator export\r\n");
        stream.Write(header, 0, header.Length);
        stream.WriteByte(HeaderTerminator);

        for (var cylinder = 0; cylinder < tracks; cylinder++)
        {
            for (var head = 0; head < sides; head++)
            {
                const int sectorCount = DskImage.SectorsPerTrack;
                byte[] orderMap;
                if (orderMaps != null && orderMaps.TryGetValue((cylinder, head), out var preserved) &&
                    preserved.Length == sectorCount)
                {
                    orderMap = preserved;
                }
                else
                {
                    orderMap = new byte[sectorCount];
                    for (var i = 0; i < sectorCount; i++) orderMap[i] = (byte)(i + 1);
                }

                stream.WriteByte(Mode250KbpsMfm);
                stream.WriteByte((byte)cylinder);
                stream.WriteByte((byte)head); // no cylinder/head map flags ever set — see class doc comment
                stream.WriteByte((byte)sectorCount);
                stream.WriteByte(SectorSizeCode);
                stream.Write(orderMap, 0, orderMap.Length);

                foreach (var logicalSector in orderMap)
                {
                    var offset = head * tracks * DskImage.BytesPerTrack + cylinder * DskImage.BytesPerTrack +
                                 (logicalSector - 1) * DskImage.BytesPerSector;
                    stream.WriteByte(1); // normal, uncompressed
                    stream.Write(data, offset, DskImage.BytesPerSector);
                }
            }
        }

        return stream.ToArray();
    }
}

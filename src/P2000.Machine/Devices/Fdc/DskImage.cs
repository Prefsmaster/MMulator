namespace P2000.Machine.Devices.Fdc;

/// <summary>
/// Host-side <c>.dsk</c> disk-image API (project CLAUDE.md §13 milestone 19; format facts
/// from <c>docs/JWSDOS-format.md</c>) — mirrors <see cref="Cassette.MiniTape"/>'s role for the
/// cassette: a plain data model the chip (<see cref="Upd765"/>) reads/writes sectors from,
/// completely separate from the port-facing chip logic. Always host-speed, independent of
/// <see cref="TimingPolicy"/> — mount/eject/create-blank/write-protect/browse never simulate
/// real disk-drive delays; only <see cref="Upd765"/>'s command execution does.
///
/// <b>Raw layout (derived, not directly stated by the format doc — flagged as such):</b> a
/// side-major, cylinder-minor linear sector dump: side 0's cylinders 0..N-1 (each 16 sectors ×
/// 256 B = 4096 B) come first, then side 1's, if present. This is the only layout consistent
/// with <c>docs/JWSDOS-format.md</c> §2's confirmed byte ranges: "track 1" (raw
/// <c>0x0000</c>-<c>0x0FFF</c>) and "track 2" (raw <c>0x1000</c>-<c>0x1FFF</c>) are
/// <c>getdos</c>'s own names for cylinders 0 and 1 of side 0, and the active side-1 directory
/// at raw <c>0x1800</c>-<c>0x1FFF</c> (cylinder 1, sectors 9-16) has every entry's side byte
/// equal to 0 — only possible if consecutive cylinders of the SAME side are contiguous, ruling
/// out a per-cylinder side-interleaved layout.
/// </summary>
public sealed class DskImage
{
    public const int SectorsPerTrack = 16;
    public const int BytesPerSector = 256;
    private const int BytesPerTrack = SectorsPerTrack * BytesPerSector;

    /// <summary>Raw offset of the geometry/system label's SS/DS indicator byte
    /// (<c>docs/JWSDOS-format.md</c> §3, <c>$FEF</c>): ASCII <c>'D'</c> (double-sided) or
    /// <c>'S'</c> (single-sided).</summary>
    private const int SideIndicatorOffset = 0x0FEF;

    /// <summary>Raw offset of the track-count byte (<c>docs/JWSDOS-format.md</c> §3,
    /// <c>$FFF</c>): binary track count <b>+1</b> (e.g. <c>0x29</c> = 41 → 40 tracks).</summary>
    private const int TrackCountOffset = 0x0FFF;

    /// <summary>Active side-1 directory region (<c>docs/JWSDOS-format.md</c> §2, confirmed via
    /// <c>dir_side1_prep</c>): raw <c>0x1800</c>-<c>0x1FFF</c>, NOT <c>0x1000</c>-<c>0x17FF</c>
    /// (a stale/unrelated cluster — see the format doc's §2/§7 item 3 caution).</summary>
    private const int DirectoryOffset = 0x1800;
    private const int DirectorySize = 0x0800; // 2048 B = 8 sectors = 64 × 32-byte entries
    private const int DirectoryEntrySize = 32;

    private byte[] _data;

    public int Tracks { get; private set; }
    public int Sides { get; private set; }
    public bool WriteProtected { get; set; }

    /// <summary>Mounts a raw <c>.dsk</c> image from disk, auto-detecting geometry from the
    /// on-disk label (an emulator-side UX improvement beyond real JWSDOS, which does NOT
    /// auto-detect — <c>docs/JWSDOS-format.md</c> §3).</summary>
    public DskImage(string path) : this(File.ReadAllBytes(path)) { }

    /// <summary>Mounts directly from bytes (test fixtures, in-memory images).</summary>
    public DskImage(byte[] image)
    {
        if (image.Length < TrackCountOffset + 1)
            throw new ArgumentException(
                $"Disk image is only {image.Length} bytes — too short to contain the geometry label at 0x{TrackCountOffset:X}.",
                nameof(image));

        _data = image;
        Sides = image[SideIndicatorOffset] == (byte)'D' ? 2 : 1;
        Tracks = image[TrackCountOffset] - 1;
    }

    /// <summary>Creates a blank (all-zero), unformatted image of the given geometry — no
    /// on-disk label, since a freshly blanked disk hasn't been through JWSDOS's format menu
    /// yet (mirrors <see cref="Cassette.MdcrDevice.InsertBlankTape"/>'s blank-tape shape).</summary>
    public static DskImage CreateBlank(int tracks, int sides)
    {
        var image = new DskImage
        {
            _data = new byte[tracks * sides * BytesPerTrack],
            Tracks = tracks,
            Sides = sides,
        };
        return image;
    }

    // Private parameterless-ish constructor for CreateBlank (fields assigned via object initializer).
    private DskImage()
    {
        _data = Array.Empty<byte>();
    }

    private int SectorOffset(int cylinder, int head, int sector) =>
        head * Tracks * BytesPerTrack + cylinder * BytesPerTrack + (sector - 1) * BytesPerSector;

    /// <summary>Reads one 256-byte sector. <paramref name="sector"/> is 1-based (µPD765
    /// convention).</summary>
    public ReadOnlySpan<byte> ReadSector(int cylinder, int head, int sector) =>
        _data.AsSpan(SectorOffset(cylinder, head, sector), BytesPerSector);

    /// <summary>Writes one 256-byte sector. No-op (silently discarded) when
    /// <see cref="WriteProtected"/> — mirrors <see cref="Cassette.MiniTape"/>'s write-protect
    /// behaviour for the cassette.</summary>
    public void WriteSector(int cylinder, int head, int sector, ReadOnlySpan<byte> data)
    {
        if (WriteProtected) return;
        data[..BytesPerSector].CopyTo(_data.AsSpan(SectorOffset(cylinder, head, sector)));
    }

    /// <summary>Browses side 1's confirmed active directory only (raw <c>0x1800</c>-<c>0x1FFF</c>
    /// — <c>docs/JWSDOS-format.md</c> §2/§4). Side 2's directory location in a raw image is not
    /// yet confirmed (format doc §7 item 2) — deliberately NOT modeled here, per the milestone's
    /// own "don't guess an offset" instruction. Empty (zero-padded) slots are omitted.</summary>
    public IReadOnlyList<DiskDirectoryEntry> ReadDirectory()
    {
        var entries = new List<DiskDirectoryEntry>();
        var region = _data.AsSpan(DirectoryOffset, DirectorySize);
        var count = DirectorySize / DirectoryEntrySize;

        for (var i = 0; i < count; i++)
        {
            var entry = region.Slice(i * DirectoryEntrySize, DirectoryEntrySize);

            // An empty slot is zero-padded (never-written) or space-padded (erased filename).
            var filenameBytes = entry[..16];
            var isEmpty = true;
            foreach (var b in filenameBytes)
            {
                if (b != 0x00 && b != 0x20) { isEmpty = false; break; }
            }
            if (isEmpty) continue;

            var filename = System.Text.Encoding.ASCII.GetString(entry[..16]).TrimEnd();
            var extension = System.Text.Encoding.ASCII.GetString(entry.Slice(16, 3)).TrimEnd();
            var fileType = entry[19];
            var fileLength = (ushort)(entry[20] | (entry[21] << 8));
            var transferAddress = (ushort)(entry[22] | (entry[23] << 8));
            var head = entry[24];
            var startSector = (ushort)(entry[25] | (entry[26] << 8));
            var endSector = (ushort)(entry[27] | (entry[28] << 8));

            entries.Add(new DiskDirectoryEntry(filename, extension, fileType, fileLength,
                transferAddress, head, startSector, endSector));
        }

        return entries;
    }
}

/// <summary>One parsed 32-byte JWSDOS directory entry (<c>docs/JWSDOS-format.md</c> §4,
/// field layout sourced from the <c>jwsdos5.0.asm</c> <c>DE_*</c> symbols). Offsets 29-31
/// (transient FDC-transfer scratch, not persisted per-file metadata — format doc §4) are
/// deliberately not exposed.</summary>
public readonly record struct DiskDirectoryEntry(
    string Filename,
    string Extension,
    byte FileType,
    ushort FileLength,
    ushort TransferAddress,
    byte Head,
    ushort StartSector,
    ushort EndSector)
{
    public string FullName => Extension.Length > 0 ? $"{Filename}.{Extension}" : Filename;
}

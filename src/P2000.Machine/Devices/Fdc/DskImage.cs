namespace P2000.Machine.Devices.Fdc;

/// <summary>
/// Host-side <c>.dsk</c> disk-image API (project CLAUDE.md §13 milestone 19; format facts
/// from <c>docs/P2000T-disk-formats.md</c>) — mirrors <see cref="Cassette.MiniTape"/>'s role for the
/// cassette: a plain data model the chip (<see cref="Upd765"/>) reads/writes sectors from,
/// completely separate from the port-facing chip logic. Always host-speed, independent of
/// <see cref="TimingPolicy"/> — mount/eject/create-blank/write-protect/browse never simulate
/// real disk-drive delays; only <see cref="Upd765"/>'s command execution does.
///
/// <b>Raw layout (derived, not directly stated by the format doc — flagged as such):</b> a
/// side-major, cylinder-minor linear sector dump: side 0's cylinders 0..N-1 (each 16 sectors ×
/// 256 B = 4096 B) come first, then side 1's, if present. This is the only layout consistent
/// with <c>docs/P2000T-disk-formats.md</c> §2's confirmed byte ranges: "track 1" (raw
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
    internal const int BytesPerTrack = SectorsPerTrack * BytesPerSector;

    /// <summary>Raw offset of the geometry/system label's SS/DS indicator byte
    /// (<c>docs/P2000T-disk-formats.md</c> §3, <c>$FEF</c>): ASCII <c>'D'</c> (double-sided) or
    /// <c>'S'</c> (single-sided).</summary>
    private const int SideIndicatorOffset = 0x0FEF;

    /// <summary>Raw offset of the track-count byte (<c>docs/P2000T-disk-formats.md</c> §3,
    /// <c>$FFF</c>): binary track count <b>+1</b> (e.g. <c>0x29</c> = 41 → 40 tracks).</summary>
    private const int TrackCountOffset = 0x0FFF;

    /// <summary>Active side-1 directory region (<c>docs/P2000T-disk-formats.md</c> §2, confirmed via
    /// <c>dir_side1_prep</c>): raw <c>0x1800</c>-<c>0x1FFF</c>, NOT <c>0x1000</c>-<c>0x17FF</c>
    /// (a stale/unrelated cluster — see the format doc's §2/§7 item 3 caution).</summary>
    private const int DirectoryOffset = 0x1800;
    private const int DirectorySize = 0x0800; // 2048 B = 8 sectors = 64 × 32-byte entries
    private const int DirectoryEntrySize = 32;

    /// <summary>PDOS's own FCB directory region — track 1 (raw <c>0x0000</c>-<c>0x0FFF</c>,
    /// <c>getdos</c>'s own name for cylinder 0), 128 fixed 32-byte slots
    /// (<c>docs/P2000T-disk-formats.md</c> §6a: "the first track of working disks... contains the
    /// index"). Completely separate from JWSDOS's side-1 directory above — a different DOS, a
    /// different on-disk region.</summary>
    private const int PdosFcbOffset = 0x0000;
    private const int PdosFcbSize = 32;
    private const int PdosFcbCount = 0x1000 / PdosFcbSize; // 4096 B / 32 B/FCB = 128, a clean full-track fit

    /// <summary>PDOS's allocation unit — 4 sectors = 1024 bytes (<c>docs/P2000T-disk-formats.md</c>
    /// §6a) — used to convert an allocation-map record number into a track number for display
    /// (milestone 22a/UI ms.15a).</summary>
    private const int PdosSectorsPerRecord = 4;

    private byte[] _data;

    /// <summary>Per-track sector-order (interleave) maps carried over verbatim from a mounted
    /// IMD file's own numbering map (project CLAUDE.md milestone 21) — keyed by (cylinder, head),
    /// each entry the 1-based logical sector number occupying each physical position in that
    /// track. <c>null</c> for a `.dsk`-mounted or freshly-created image (nothing to preserve;
    /// <see cref="ImdFormat.Write"/> falls back to a plain sequential map per track). Optional
    /// IMD cylinder/head remapping maps are parsed-and-discarded on read, not stored here — a
    /// flagged limitation (project CLAUDE.md §17 findings log), since the milestone only calls
    /// out preserving the sector-order/interleave map itself.</summary>
    internal IReadOnlyDictionary<(int Cylinder, int Head), byte[]>? SectorOrderMaps { get; set; }

    public int Tracks { get; private set; }
    public int Sides { get; private set; }
    public bool WriteProtected { get; set; }

    /// <summary>Which host container format this image was mounted from or last saved as
    /// (project CLAUDE.md milestone 21) — defaults to <see cref="DiskImageFormat.Dsk"/> for
    /// every existing construction path (legacy constructors, <see cref="CreateBlank"/>,
    /// <see cref="FromEmbeddedState"/>); only <see cref="Mount"/>'s IMD-sniffing branch and a
    /// format-changing host Save As (`P2000.UI` milestone 14f) ever set it to
    /// <see cref="DiskImageFormat.Imd"/>. Purely informational — nothing in this class branches
    /// on it; it exists so callers can decide default Save behavior without re-sniffing the file.</summary>
    public DiskImageFormat Format { get; set; } = DiskImageFormat.Dsk;

    /// <summary>True once a WRITE DATA command has mutated this image since it was
    /// mounted/created/last saved (project CLAUDE.md §13 milestone 20a) — the machine-layer
    /// signal the UI's unsaved-changes eject/replace warning hangs off. Mounting/creating never
    /// sets it; only <see cref="MarkClean"/> (called after a successful host Save/Save-as)
    /// clears it back.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Clears <see cref="IsDirty"/> — call after a successful host-side Save/Save-as.
    /// Does not touch the image's content.</summary>
    public void MarkClean() => IsDirty = false;

    /// <summary>Sets <see cref="IsDirty"/> directly — <c>.state</c> restore only (project
    /// CLAUDE.md milestones 20/20a), the inverse of <see cref="MarkClean"/>: a restored image
    /// must reproduce whatever dirty state was captured, not always come back clean.</summary>
    internal void RestoreDirtyFlag(bool dirty) => IsDirty = dirty;

    /// <summary>The host file this image was last mounted from or saved to; <c>null</c> for an
    /// unbacked image (fresh off "New (blank) disk," or mounted directly from bytes with no
    /// known path) — project CLAUDE.md milestone 20c, the field <c>Machine.CaptureCurrentConfig()</c>
    /// reads to reflect what's ACTUALLY mounted rather than a stale construction-time value.
    /// Settable so a host Save-as (a new file) or a
    /// live UI mount (which reads bytes itself but knows the real path) can update it after
    /// construction; the <see cref="DskImage(string)"/> constructor sets it automatically.</summary>
    public string? MountedPath { get; set; }

    /// <summary>Mounts a raw <c>.dsk</c> image from disk, auto-detecting geometry from the
    /// on-disk label UNCONDITIONALLY — kept for callers that already know they have a genuine,
    /// well-formed JWSDOS image (test fixtures, real captured disks) and don't need the
    /// mismatch-aware dance <see cref="Mount"/> does. Real user-facing mount paths (a `.cfg`'s
    /// <c>ImagePath</c> at machine construction, a live UI mount) go through <see cref="Mount"/>
    /// instead (project CLAUDE.md milestone 20d) — it validates the label against the file's
    /// actual length before trusting it, since a non-JWSDOS image (PDOS, e.g.) has nothing
    /// meaningful at these offsets. Throws if the file is too short to even contain the label.</summary>
    public DskImage(string path) : this(File.ReadAllBytes(path)) => MountedPath = path;

    /// <summary>Mounts directly from bytes, unconditionally trusting the label — see the
    /// <see cref="DskImage(string)"/> doc comment for when to use this vs. <see cref="Mount"/>.</summary>
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

    /// <summary>The six canonical Capacity×Sides combinations JWSDOS itself supports
    /// (<c>docs/P2000T-disk-formats.md</c> §3) — the fixed set <see cref="Mount"/>'s candidate-matching
    /// checks against when neither the label nor the configured geometry validates.</summary>
    private static readonly (int Tracks, int Sides)[] CanonicalGeometries =
    {
        (35, 1), (35, 2), (40, 1), (40, 2), (80, 1), (80, 2),
    };

    /// <summary>
    /// Mounts a raw <c>.dsk</c> image, validating the on-disk JWSDOS label against the file's
    /// ACTUAL length rather than trusting it unconditionally, and falling back to the drive's
    /// configured geometry when the label is absent or doesn't validate (project CLAUDE.md
    /// milestone 20d; reference doc §5d "RESOLVED — the label-based auto-detect... was silently
    /// over-trusted" — triggered by a real PDOS boot floppy, which has nothing at the label
    /// offsets at all, and a genuinely short mount that produced zero feedback).
    ///
    /// <b>Never throws, never fails to mount</b> — every path returns a usable <see cref="DskImage"/>.
    /// The returned <see cref="DiskGeometryMismatch"/> says whether the chosen geometry's implied
    /// byte length actually matches the file (<see cref="DiskGeometryMismatchKind.None"/>), or —
    /// if not — whether the file's exact length matches some OTHER canonical geometry
    /// (<see cref="DiskGeometryMismatchKind.Candidate"/>, 1 or 2 matches: 40-track/DS and
    /// 80-track/SS collide at 327,680 bytes) or none at all
    /// (<see cref="DiskGeometryMismatchKind.NoCandidate"/>, a genuinely short/odd-sized file).
    /// The caller (`P2000.UI`, ms.14e) decides whether/how to surface that; this method only
    /// detects.
    /// </summary>
    public static (DskImage Image, DiskGeometryMismatch Mismatch) Mount(
        byte[] bytes, int configuredTracks, int configuredSides)
    {
        // 0. IMD is fully self-describing (geometry + real sector order live in the file itself,
        // project CLAUDE.md milestone 21; reference doc §3a "IMD, once written, is fully
        // self-describing... needs NONE of ms.20d's guessing machinery") — sniffed by content
        // (its own text header), never by extension, and detected BEFORE any label/config
        // fallback logic runs so an IMD mount can never hit the mismatch dialog (UI ms.14f test e).
        if (ImdFormat.IsImdFile(bytes))
        {
            var (data, tracks, sides, orderMaps) = ImdFormat.Read(bytes);
            var imdImage = new DskImage
            {
                _data = data,
                Tracks = tracks,
                Sides = sides,
                Format = DiskImageFormat.Imd,
                SectorOrderMaps = orderMaps,
            };
            return (imdImage, DiskGeometryMismatch.None(bytes.Length));
        }

        var (winningTracks, winningSides, mismatch) = DetectMismatchCore(bytes, configuredTracks, configuredSides);
        var image = new DskImage { _data = bytes, Tracks = winningTracks, Sides = winningSides };
        return (image, mismatch);
    }

    /// <summary>
    /// Previews whether a `.dsk`-shaped file's bytes agree with <paramref name="configuredTracks"/>/
    /// <paramref name="configuredSides"/>, WITHOUT constructing a <see cref="DskImage"/> — the exact
    /// label-read/validate → config-fallback → canonical-candidate-match logic <see cref="Mount"/>
    /// uses internally, extracted so a caller can preview a mismatch before any live drive/machine
    /// necessarily exists to mount into (project CLAUDE.md milestone 20e; `P2000.UI` milestone 14g's
    /// Config-window offline case). Pure refactor of <see cref="Mount"/>'s own detection — does NOT
    /// sniff IMD (step 0 above): IMD is fully self-describing and never produces a mismatch, so
    /// there is nothing for a preview to detect there either; a caller previewing an IMD file gets
    /// <see cref="DiskGeometryMismatch.None"/> for the wrong reason (candidate-matching against
    /// `.dsk` geometries) unless it checks <c>ImdFormat.IsImdFile</c> itself first.
    /// </summary>
    public static DiskGeometryMismatch DetectMismatch(byte[] bytes, int configuredTracks, int configuredSides) =>
        DetectMismatchCore(bytes, configuredTracks, configuredSides).Mismatch;

    /// <summary>True if <paramref name="bytes"/> is an IMD (ImageDisk) file by CONTENT (its own
    /// text header) — the same sniff <see cref="Mount"/> uses internally before running any label/
    /// config/candidate logic, exposed publicly (project CLAUDE.md milestone 20e) so a caller using
    /// <see cref="DetectMismatch"/> on a not-yet-mounted file (which does NOT sniff IMD itself, see
    /// its own doc comment) can skip the preview correctly for an IMD-shaped file — IMD is fully
    /// self-describing and never mismatches.</summary>
    public static bool IsImdFile(byte[] bytes) => ImdFormat.IsImdFile(bytes);

    private static (int Tracks, int Sides, DiskGeometryMismatch Mismatch) DetectMismatchCore(
        byte[] bytes, int configuredTracks, int configuredSides)
    {
        var actualLength = bytes.Length;

        // 1. Trust the label ONLY if it's self-consistent — its implied length must equal the
        // file's actual length exactly. This single check is what makes it safe to read blind
        // on a non-JWSDOS file: random sector bytes forming a combination that ALSO happens to
        // byte-length-match is vanishingly unlikely.
        if (TryReadLabel(bytes, out var labelTracks, out var labelSides) &&
            (long)labelTracks * labelSides * BytesPerTrack == actualLength)
        {
            return (labelTracks, labelSides, DiskGeometryMismatch.None(actualLength));
        }

        // 2. Otherwise, the drive's configured geometry is the real fallback — promoted from
        // "blank-media seed only" to this, since most real images aren't JWSDOS-labeled.
        var configuredLength = configuredTracks * configuredSides * BytesPerTrack;
        if (configuredLength == actualLength)
            return (configuredTracks, configuredSides, DiskGeometryMismatch.None(actualLength));

        // 3./4. The configured geometry doesn't match either — check the other canonical
        // combinations for an exact length match (0, 1, or 2 candidates).
        var candidates = new List<(int Tracks, int Sides)>();
        foreach (var (tracks, sides) in CanonicalGeometries)
        {
            if (tracks == configuredTracks && sides == configuredSides) continue; // already ruled out
            if ((long)tracks * sides * BytesPerTrack == actualLength)
                candidates.Add((tracks, sides));
        }

        var kind = candidates.Count > 0 ? DiskGeometryMismatchKind.Candidate : DiskGeometryMismatchKind.NoCandidate;
        // Mounted regardless — using the configured geometry — per the "nothing here blocks a
        // mount" rule; the mismatch result is informational, not gating.
        return (configuredTracks, configuredSides,
            new DiskGeometryMismatch(kind, actualLength, configuredLength, candidates));
    }

    /// <summary>Reads the JWSDOS geometry label's raw bytes without validating them against
    /// anything — <see cref="Mount"/>'s own length-consistency check is what makes this safe to
    /// call blind on a non-JWSDOS file. False (label absent) only when the file is too short to
    /// even contain the label bytes.</summary>
    private static bool TryReadLabel(byte[] data, out int tracks, out int sides)
    {
        if (data.Length < TrackCountOffset + 1)
        {
            tracks = 0;
            sides = 0;
            return false;
        }
        sides = data[SideIndicatorOffset] == (byte)'D' ? 2 : 1;
        tracks = data[TrackCountOffset] - 1;
        return true;
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

    /// <summary>Reconstructs a disk image from previously-embedded <c>.state</c> bytes plus
    /// explicitly-known geometry (project CLAUDE.md milestones 20/20a's self-contained
    /// <c>.state</c>) — bypasses the on-disk label auto-detect the <c>string</c>/<c>byte[]</c>
    /// constructors use, since a blank/unformatted image (no label yet) would otherwise
    /// misdetect a negative track count. The live instance being saved already knows its own
    /// <see cref="Tracks"/>/<see cref="Sides"/>; restore should trust that, not re-derive it.</summary>
    internal static DskImage FromEmbeddedState(byte[] data, int tracks, int sides) => new()
    {
        _data = data,
        Tracks = tracks,
        Sides = sides,
    };

    private int SectorOffset(int cylinder, int head, int sector) =>
        head * Tracks * BytesPerTrack + cylinder * BytesPerTrack + (sector - 1) * BytesPerSector;

    /// <summary>Reads one 256-byte sector. <paramref name="sector"/> is 1-based (µPD765
    /// convention). A sector address beyond the image's actual byte length (an unpadded short
    /// mount, continued anyway — project CLAUDE.md milestone 20d) reads as <c>0x00</c> fill,
    /// never an exception — mirrors the cartridge's confirmed "open-bus reads <c>0xFF</c> past a
    /// short image" shape (reference doc §5c), using disk's own fill byte instead.</summary>
    public ReadOnlySpan<byte> ReadSector(int cylinder, int head, int sector)
    {
        var offset = SectorOffset(cylinder, head, sector);
        if (offset + BytesPerSector <= _data.Length)
            return _data.AsSpan(offset, BytesPerSector);

        var buffer = new byte[BytesPerSector]; // defaults to 0x00
        if (offset < _data.Length)
            _data.AsSpan(offset, _data.Length - offset).CopyTo(buffer); // partially in range
        return buffer;
    }

    /// <summary>Writes one 256-byte sector. No-op (silently discarded) when
    /// <see cref="WriteProtected"/> — mirrors <see cref="Cassette.MiniTape"/>'s write-protect
    /// behaviour for the cassette — or when the target sector falls beyond the image's actual
    /// byte length (an unpadded short mount): there's nowhere to write the bytes without
    /// implicitly growing the image, which only <see cref="ExtendTo"/> does explicitly (project
    /// CLAUDE.md milestone 20d).</summary>
    public void WriteSector(int cylinder, int head, int sector, ReadOnlySpan<byte> data)
    {
        if (WriteProtected) return;
        var offset = SectorOffset(cylinder, head, sector);
        if (offset + BytesPerSector > _data.Length) return;
        data[..BytesPerSector].CopyTo(_data.AsSpan(offset));
        IsDirty = true;
    }

    /// <summary>Extends the in-memory image to <paramref name="targetLength"/> bytes, filling
    /// new space with <c>0x00</c> — the same fill byte real FORMAT A TRACK writes into
    /// unformatted sectors (<c>jwsformat.asm</c> disassembly, reference doc §5d), reused here
    /// rather than inventing a second "blank" convention (project CLAUDE.md milestone 20d).
    /// Purely in-memory, per the existing buffered-write model — nothing touches the host file
    /// until an explicit Save/Save-as. No-op if the image is already at least that long.</summary>
    public void ExtendTo(int targetLength)
    {
        if (targetLength <= _data.Length) return;
        var extended = new byte[targetLength]; // defaults to 0x00
        _data.CopyTo(extended, 0);
        _data = extended;
        IsDirty = true;
    }

    /// <summary>Returns a copy of the raw sector-dump bytes for a host Save/Save-as (project
    /// CLAUDE.md §13 milestone 20's host <c>.dsk</c> API) — a plain byte-for-byte write, no
    /// bitstream-style encode step the way <c>.cas</c> needs. A copy, not the live backing
    /// array, so the caller can't bypass <see cref="WriteSector"/>'s write-protect check.</summary>
    public byte[] GetBytes() => (byte[])_data.Clone();

    /// <summary>Serializes this image's current content into IMD (ImageDisk) form (project
    /// CLAUDE.md milestone 21) — the emulator's new native/preferred container. Reuses
    /// <see cref="SectorOrderMaps"/> verbatim per track when present (an image mounted from a
    /// real IMD file, unmodified since); otherwise (a `.dsk`-mounted or freshly-created image,
    /// which has no genuine interleave data to preserve) <see cref="ImdFormat.Write"/> emits a
    /// plain sequential order map — still a fully valid, standard IMD file, just not one
    /// recording real physical interleave. Write-protect is deliberately not part of this
    /// serialization (config/`.state` concern, unaffected by host container format).</summary>
    public byte[] GetImdBytes() => ImdFormat.Write(_data, Tracks, Sides, SectorOrderMaps);

    /// <summary>Browses side 1's confirmed active directory only (raw <c>0x1800</c>-<c>0x1FFF</c>
    /// — <c>docs/P2000T-disk-formats.md</c> §2/§4). Side 2's directory location in a raw image is not
    /// yet confirmed (format doc §7 item 2) — deliberately NOT modeled here, per the milestone's
    /// own "don't guess an offset" instruction. Empty (zero-padded) slots are omitted.
    ///
    /// <b>An unpadded short mount's directory region reads as all-zero (project CLAUDE.md
    /// milestone 20d)</b> — same out-of-range convention <see cref="ReadSector"/> uses, applied
    /// here too: this used to assume <c>_data</c> was always at least <c>0x2000</c> bytes long
    /// and threw <see cref="ArgumentOutOfRangeException"/> otherwise, which a genuinely short
    /// mount (now always allowed to mount, never rejected) would hit on every directory browse.</summary>
    public IReadOnlyList<DiskDirectoryEntry> ReadDirectory()
    {
        var entries = new List<DiskDirectoryEntry>();
        foreach (var (_, entry) in EnumerateDirectorySlots())
        {
            var filename = System.Text.Encoding.ASCII.GetString(entry[..16]).TrimEnd();
            var extension = System.Text.Encoding.ASCII.GetString(entry[16..19]).TrimEnd();
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

    /// <summary>Walks side 1's directory region (same offsets/empty-slot rule as
    /// <see cref="ReadDirectory"/>) and yields only the non-empty 32-byte slots, each still
    /// backed by the same underlying buffer copy — shared by <see cref="ReadDirectory"/> and
    /// <see cref="DetectDirectoryFormat"/> so the two never drift on what counts as "empty."</summary>
    private IEnumerable<(int Index, byte[] Entry)> EnumerateDirectorySlots()
    {
        var regionBuffer = new byte[DirectorySize]; // defaults to 0x00 — an empty directory
        if (DirectoryOffset < _data.Length)
        {
            var available = Math.Min(DirectorySize, _data.Length - DirectoryOffset);
            _data.AsSpan(DirectoryOffset, available).CopyTo(regionBuffer);
        }
        var count = DirectorySize / DirectoryEntrySize;

        for (var i = 0; i < count; i++)
        {
            var entry = new byte[DirectoryEntrySize];
            Array.Copy(regionBuffer, i * DirectoryEntrySize, entry, 0, DirectoryEntrySize);

            // An empty slot is zero-padded (never-written) or space-padded (erased filename).
            var isEmpty = true;
            for (var b = 0; b < 16; b++)
            {
                if (entry[b] != 0x00 && entry[b] != 0x20) { isEmpty = false; break; }
            }
            if (isEmpty) continue;

            yield return (i, entry);
        }
    }

    /// <summary>Auto-detects which directory format this image holds (project CLAUDE.md §13
    /// milestones 22/22a; reference doc §3a "RESOLVED — the Disk Drives window's directory browse
    /// table gets format auto-detection..."). Tries JWSDOS first (side-1 directory, raw
    /// <c>0x1800</c>); then PDOS's own FCB directory (track 1, raw <c>0x0000</c>,
    /// <c>docs/P2000T-disk-formats.md</c> §6a).
    ///
    /// <b>JWSDOS check:</b> reuses the same "non-empty slot" rule as <see cref="ReadDirectory"/>,
    /// then additionally requires every non-empty slot's filename+extension+filetype bytes
    /// (offsets 0-19) to be plausible printable ASCII/space — matching this codebase's existing
    /// self-consistency-checking spirit (e.g. <see cref="Mount"/>'s label-length validation): a
    /// non-JWSDOS image (PDOS, garbage) has arbitrary binary data at this offset, which "bytes
    /// are present" alone would wrongly accept. <b>CHANGED (machine milestone 23):</b> an
    /// all-empty directory (every slot zero-padded) no longer counts as a valid JWSDOS
    /// directory — it is EQUALLY consistent with a blank PDOS working disk (PDOS's own track-1
    /// directory also reads as all-zero before anything's written), so defaulting to
    /// <see cref="DiskDirectoryFormat.Jwsdos"/> here was an arbitrary pick between two
    /// equally-plausible blank states, not a real detection. An all-empty region now requires at
    /// least one non-empty slot to be considered a plausible JWSDOS directory at all, so it falls
    /// through to the PDOS check below (which also fails on an all-zero first FCB slot — zero
    /// bytes aren't plausible printable-ASCII/space) and ultimately reaches
    /// <see cref="DiskDirectoryFormat.Unknown"/>. See <see cref="IsDirectoryRegionBlank"/> for how
    /// a caller distinguishes this genuinely-blank case from real unrecognized garbage.
    ///
    /// <b>PDOS check (milestone 22a) — the one genuine ambiguity in either format:</b> a PDOS
    /// system disk's track 1 offset 0 is the SAME byte value (<c>0xF3</c>) a working disk's own
    /// first FCB slot could legitimately carry as its own flag (<c>docs/P2000T-disk-formats.md</c>
    /// §7 item 8) — so byte 0 alone can never decide it. Instead, validate the REST of the first
    /// FCB slot (name/extension/sector-count/allocation-map, <see cref="IsPlausiblePdosFcb"/>)
    /// regardless of what byte 0 says. If it looks like a plausible FCB, this is
    /// <see cref="DiskDirectoryFormat.PdosWorking"/> even if byte 0 happens to be <c>0xF3</c> (that
    /// disk's own flag value, not a system-disk marker). Only when validation fails AND byte 0 is
    /// genuinely <c>0xF3</c> is this reported as <see cref="DiskDirectoryFormat.PdosSystem"/> (no
    /// directory at all, real boot code occupies this region instead); validation failing with any
    /// other byte 0 value falls through to <see cref="DiskDirectoryFormat.Unknown"/> — neither
    /// format matched.</summary>
    public DiskDirectoryFormat DetectDirectoryFormat()
    {
        if (IsPlausibleJwsdosDirectory()) return DiskDirectoryFormat.Jwsdos;

        var firstFcbSlot = ReadPdosFcbSlot(0);
        if (IsPlausiblePdosFcb(firstFcbSlot)) return DiskDirectoryFormat.PdosWorking;

        return firstFcbSlot[0] == 0xF3 ? DiskDirectoryFormat.PdosSystem : DiskDirectoryFormat.Unknown;
    }

    private bool IsPlausibleJwsdosDirectory()
    {
        var sawNonEmptySlot = false;
        foreach (var (_, entry) in EnumerateDirectorySlots())
        {
            sawNonEmptySlot = true;
            // Filename (0-15) + extension (16-18) + filetype (19) — the fields real JWSDOS
            // filenames/extensions occupy — must all be printable ASCII or space.
            for (var i = 0; i < 20; i++)
            {
                var b = entry[i];
                if (b < 0x20 || b > 0x7E) return false;
            }
        }
        // An all-empty directory is no longer treated as a plausible JWSDOS directory (machine
        // milestone 23) — see this method's own doc comment for why.
        return sawNonEmptySlot;
    }

    /// <summary>True when NEITHER a JWSDOS directory slot (raw <c>0x1800</c>-<c>0x1FFF</c>) NOR a
    /// PDOS FCB slot (track 1, raw <c>0x0000</c>) has any non-empty entry — i.e. this image is
    /// genuinely blank at both formats' directory regions, not just unrecognized (machine
    /// milestone 23). Meaningful only when <see cref="DetectDirectoryFormat"/> has already
    /// returned <see cref="DiskDirectoryFormat.Unknown"/> — a caller (the UI fallback view) uses
    /// this to show a distinct "clean disk" message instead of the generic "unknown disk
    /// contents/structure" wording for genuine garbage.</summary>
    public bool IsDirectoryRegionBlank() =>
        !EnumerateDirectorySlots().Any() && !EnumeratePdosFcbSlots().Any();

    /// <summary>Reads one raw 32-byte PDOS FCB slot (1-based positions in
    /// <c>docs/P2000T-disk-formats.md</c> §6a map to 0-based <paramref name="index"/>*32 + (position-1)
    /// here) — same "reads as 0x00 past an unpadded short image" convention as
    /// <see cref="ReadSector"/>, so this never throws regardless of the mounted image's real length.</summary>
    private byte[] ReadPdosFcbSlot(int index)
    {
        var slot = new byte[PdosFcbSize];
        var offset = PdosFcbOffset + index * PdosFcbSize;
        if (offset < _data.Length)
        {
            var available = Math.Min(PdosFcbSize, _data.Length - offset);
            _data.AsSpan(offset, available).CopyTo(slot);
        }
        return slot;
    }

    /// <summary>Walks PDOS's 128 fixed FCB slots and yields only the non-empty ones — an unused
    /// slot is presumed all-zero, <c>docs/P2000T-disk-formats.md</c> §6a.</summary>
    private IEnumerable<(int Index, byte[] Slot)> EnumeratePdosFcbSlots()
    {
        for (var i = 0; i < PdosFcbCount; i++)
        {
            var slot = ReadPdosFcbSlot(i);
            if (Array.TrueForAll(slot, b => b == 0x00)) continue;
            yield return (i, slot);
        }
    }

    /// <summary>Validates that a 32-byte PDOS FCB slot's non-flag fields look like a plausible
    /// file entry — printable ASCII/space name (positions 2-9) and extension (10-12), and a
    /// sector count (position 16) consistent with its own allocation map's real record span
    /// (positions 17-32, never referencing records <c>00</c>-<c>03</c>, permanently reserved for
    /// track 1's own index — <c>docs/P2000T-disk-formats.md</c> §6a). Deliberately does NOT look
    /// at position 1 (the continuation-index/flag byte) — that field is exactly what milestone
    /// 22a's disambiguation needs to stay independent of (a working disk's first FCB can
    /// legitimately carry <c>0xF3</c> there too, §7 item 8).</summary>
    private static bool IsPlausiblePdosFcb(byte[] slot)
    {
        // Positions are 1-based in the source doc; slot[] is 0-based, so position P is slot[P-1].
        for (var pos = 2; pos <= 12; pos++) // name (2-9) + extension (10-12)
        {
            var b = slot[pos - 1];
            if (b < 0x20 || b > 0x7E) return false;
        }

        if (!TryCountPdosAllocationMapRecords(slot.AsSpan(16, 16), out var recordCount)) return false;
        if (recordCount == 0) return false;

        var sectorCount = slot[15]; // position 16
        if (sectorCount == 0) return false;
        // A file's real length in sectors must be accounted for by its own allocation map's
        // record span — ceil(sectorCount / 4) == recordCount, confirmed exactly against every
        // known real/worked example (docs/P2000T-disk-formats.md §6a: VOLORG 44/11 exact fit,
        // VOLINFO 14/4 with 2 sectors' slack, the source docx's own 27/7 worked example).
        var impliedRecordCount = (sectorCount + PdosSectorsPerRecord - 1) / PdosSectorsPerRecord;
        return impliedRecordCount == recordCount;
    }

    /// <summary>Counts the real (non-padding) record numbers in a 16-byte PDOS allocation map,
    /// enforcing the two structural rules <c>docs/P2000T-disk-formats.md</c> §6a confirms against
    /// real data: <c>0x00</c> only ever appears as trailing padding (never followed by a real
    /// record), and no real record number is ever <c>00</c>-<c>03</c> (permanently reserved for
    /// track 1's own index/FCB area). Returns false — an implausible map — if either rule is
    /// violated.</summary>
    private static bool TryCountPdosAllocationMapRecords(ReadOnlySpan<byte> map, out int recordCount)
    {
        recordCount = 0;
        var seenPadding = false;
        foreach (var b in map)
        {
            if (seenPadding)
            {
                if (b != 0x00) return false; // a real value after padding already started
                continue;
            }
            if (b == 0x00) { seenPadding = true; continue; }
            if (b <= 0x03) return false; // records 00-03 are permanently reserved, never file data
            recordCount++;
        }
        return true;
    }

    /// <summary>Parses PDOS's FCB directory (track 1, raw <c>0x0000</c>) into logical file
    /// entries (project CLAUDE.md §13 milestone 22a; <c>docs/P2000T-disk-formats.md</c> §6a).
    /// Position 1 of each slot is a continuation-sequence index, not a per-file flag in general
    /// (<c>0x00</c> = a file's primary/only FCB; <c>0x01</c>/<c>0x02</c>/… = additional FCBs for
    /// the same filename+extension when one FCB's 16-record allocation map isn't enough) — every
    /// slot sharing a (name, extension) pair is folded into ONE logical entry here, ordered by
    /// that continuation index, so a caller never sees the same file split across multiple rows.
    /// Only meaningful once <see cref="DetectDirectoryFormat"/> has already returned
    /// <see cref="DiskDirectoryFormat.PdosWorking"/> for this image — this method itself does not
    /// re-validate plausibility, it trusts every non-empty slot the same way <see cref="ReadDirectory"/>
    /// trusts every non-empty JWSDOS slot once already inside that directory's own region.</summary>
    public IReadOnlyList<PdosDirectoryEntry> ReadPdosDirectory()
    {
        var order = new List<(string Name, string Extension)>();
        var membersByKey = new Dictionary<(string Name, string Extension), List<(int ContinuationIndex, byte SectorCount, byte[] AllocationMap)>>();

        foreach (var (_, slot) in EnumeratePdosFcbSlots())
        {
            var continuationIndex = slot[0];
            var name = System.Text.Encoding.ASCII.GetString(slot.AsSpan(1, 8)).TrimEnd();
            var extension = System.Text.Encoding.ASCII.GetString(slot.AsSpan(9, 3)).TrimEnd();
            var sectorCount = slot[15];
            var allocationMap = slot[16..32];

            var key = (name, extension);
            if (!membersByKey.TryGetValue(key, out var members))
            {
                members = new List<(int, byte, byte[])>();
                membersByKey[key] = members;
                order.Add(key);
            }
            members.Add((continuationIndex, sectorCount, allocationMap));
        }

        var entries = new List<PdosDirectoryEntry>(order.Count);
        foreach (var key in order)
        {
            var members = membersByKey[key];
            members.Sort((a, b) => a.ContinuationIndex.CompareTo(b.ContinuationIndex));

            var records = new List<int>();
            var totalSectorCount = 0;
            foreach (var member in members)
            {
                totalSectorCount += member.SectorCount;
                foreach (var b in member.AllocationMap)
                {
                    if (b == 0x00) break; // trailing padding within this FCB's own map
                    records.Add(b);
                }
            }

            var startTrack = records.Count > 0 ? RecordToTrack(records[0]) : 0;
            var endTrack = records.Count > 0 ? RecordToTrack(records[^1]) : 0;
            entries.Add(new PdosDirectoryEntry(
                key.Name, key.Extension, totalSectorCount * BytesPerSector,
                startTrack, endTrack, records.Count * PdosSectorsPerRecord));
        }

        return entries;
    }

    /// <summary>Converts a PDOS allocation-map record number into a 1-based track number, matching
    /// this doc's own confirmed mapping (<c>docs/P2000T-disk-formats.md</c> §6a: "track N's four
    /// records are numbered (N-1)×4 through (N-1)×4+3" — e.g. records 8-11 are track 3, confirmed
    /// directly against real <c>volorg.dsk</c> content). UI milestone 15a's own "record ÷ 4"
    /// phrasing is shorthand for this same formula, not a distinct 0-based scheme — see this
    /// project's CLAUDE.md §17 findings log for the reconciliation.</summary>
    private static int RecordToTrack(int record) => record / PdosSectorsPerRecord + 1;
}

/// <summary>One parsed 32-byte JWSDOS directory entry (<c>docs/P2000T-disk-formats.md</c> §4,
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

/// <summary>One logical PDOS file entry (<c>docs/P2000T-disk-formats.md</c> §6a), as returned by
/// <see cref="DskImage.ReadPdosDirectory"/> — already folded from however many physical 32-byte
/// FCB slots the file's allocation map needed (a file over 16 KB spans more than one FCB, chained
/// via position 1's continuation index; callers never see the same file split across multiple
/// entries). <see cref="StartTrack"/>/<see cref="EndTrack"/> are pre-derived from the combined
/// allocation map's record numbers (`record ÷ 4` + 1, confirmed against real <c>volorg.dsk</c>
/// content) so the UI (milestone 15a) never needs to re-derive the formula itself.
/// <b>No Side field</b> — PDOS has no double-sided concept at all (§6a's own hard geometry
/// ceiling rules out anything wider than single-sided 35/40-track).</summary>
public readonly record struct PdosDirectoryEntry(
    string Name,
    string Extension,
    int FileLength,
    int StartTrack,
    int EndTrack,
    int TotalSectors)
{
    public string FullName => Extension.Length > 0 ? $"{Name}.{Extension}" : Name;
}

/// <summary>Which directory format a <see cref="DskImage"/> holds (project CLAUDE.md §13
/// milestones 22/22a; reference doc §3a "RESOLVED — the Disk Drives window's directory browse
/// table gets format auto-detection..."), as returned by
/// <see cref="DskImage.DetectDirectoryFormat"/>.</summary>
public enum DiskDirectoryFormat
{
    /// <summary>A JWSDOS directory (docs/P2000T-disk-formats.md §4) — real logic, milestone 22.</summary>
    Jwsdos,

    /// <summary>A PDOS working disk's FCB directory (track 1, docs/P2000T-disk-formats.md §6a) —
    /// real logic, milestone 22a. Read it via <see cref="DskImage.ReadPdosDirectory"/>.</summary>
    PdosWorking,

    /// <summary>A PDOS system disk (no file directory — track 1 offset 0 is the official <c>0xF3</c>
    /// boot signature, and the rest of that first FCB slot did NOT validate as a plausible working-
    /// disk entry) — real logic, milestone 22a.</summary>
    PdosSystem,

    /// <summary>Neither JWSDOS nor a validated PDOS directory/system marker matched — the
    /// catch-all case (garbage, an unrecognized format, or a genuinely blank/unformatted image).</summary>
    Unknown,
}

/// <summary>Which host container format a <see cref="DskImage"/> was mounted from or last saved
/// as (project CLAUDE.md milestone 21). Purely descriptive — the emulation-facing sector I/O
/// (<see cref="DskImage.ReadSector"/>/<see cref="DskImage.WriteSector"/>) is identical either
/// way; only the host Save/Save-As path (`P2000.UI` milestone 14f) branches on it.</summary>
public enum DiskImageFormat
{
    /// <summary>A raw sector dump — the legacy import/export format (project CLAUDE.md milestone
    /// 20d's label/config-fallback/mismatch-dialog machinery applies only to this format).</summary>
    Dsk,

    /// <summary>IMD (ImageDisk) — the emulator's native/preferred container: self-describing
    /// geometry and real per-sector physical order, natively read/writeable by MAME, Greaseweazle,
    /// and FluxEngine (reference doc §3a).</summary>
    Imd,
}

/// <summary>Which shape of geometry mismatch <see cref="DskImage.Mount"/> found, if any (project
/// CLAUDE.md milestone 20d).</summary>
public enum DiskGeometryMismatchKind
{
    /// <summary>The chosen geometry's implied byte length matches the file exactly — the common
    /// case, nothing to report.</summary>
    None,

    /// <summary>The file's exact length matches one or two OTHER canonical Capacity×Sides
    /// combinations (35/40/80-track × SS/DS) — 40-track/DS and 80-track/SS collide at 327,680
    /// bytes, so this can carry two candidates.</summary>
    Candidate,

    /// <summary>The file's length matches NO canonical combination at all — genuinely short or
    /// odd-sized (e.g. a partial/incomplete mount).</summary>
    NoCandidate,
}

/// <summary>Result of <see cref="DskImage.Mount"/>'s geometry decision (project CLAUDE.md
/// milestone 20d; reference doc §5d "RESOLVED — the label-based auto-detect... was silently
/// over-trusted"). Never gates the mount — <see cref="DskImage.Mount"/> always returns a usable
/// image regardless of <see cref="Kind"/>; this is purely informational for a caller (`P2000.UI`,
/// ms.14e) that wants to offer the user a choice.</summary>
public readonly record struct DiskGeometryMismatch(
    DiskGeometryMismatchKind Kind,
    int ActualLength,
    int ExpectedLength,
    IReadOnlyList<(int Tracks, int Sides)> Candidates)
{
    public static DiskGeometryMismatch None(int length) =>
        new(DiskGeometryMismatchKind.None, length, length, Array.Empty<(int, int)>());

    /// <summary>True only for a <see cref="DiskGeometryMismatchKind.NoCandidate"/> mismatch where
    /// the file is SHORTER than the geometry in use — the only case "extend to full size" makes
    /// sense for (nothing to pad when the file is already at or beyond the expected length; a
    /// longer file just has unused trailing bytes, reference doc §5d point 5).</summary>
    public bool CanPad => Kind == DiskGeometryMismatchKind.NoCandidate && ActualLength < ExpectedLength;
}

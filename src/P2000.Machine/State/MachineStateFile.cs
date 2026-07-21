using System.Text;

namespace P2000.Machine.State;

/// <summary>
/// Reads and writes <c>.state</c> binary files (project CLAUDE.md §11). Format:
/// <list type="bullet">
///   <item><b>Header:</b> 4-byte magic <c>"P2ST"</c>, 4-byte version int32, 4-byte
///     config-JSON length, then that many UTF-8 bytes of <see cref="MachineConfigFile"/>
///     JSON (the full <see cref="MachineConfig"/> — so a state file is self-contained).</item>
///   <item><b>Device state:</b> the machine's runtime state in a fixed field order
///     (see <see cref="Machine.SaveState"/>). Any format change requires a version bump.</item>
/// </list>
/// Restore is rebuild-from-config then load-device-state (reset-to-apply + overwrite RAM
/// and device internals): <see cref="Machine"/> is constructed fresh from the embedded
/// config, then <see cref="Machine.LoadState"/> overwrites its mutable runtime state.
/// </summary>
public static class MachineStateFile
{
    /// <summary>Current <c>.state</c> format version. Increment when the device-state
    /// layout changes; the reader rejects files whose version is outside the accepted range.
    /// <list type="bullet">
    ///   <item>v1: original (milestones 11–11). Missing SoundDevice block; Interrupts wrote 1 bool.</item>
    ///   <item>v2: SoundDevice block added between Mdcr and Interrupts (milestone 16);
    ///     Interrupts writes 2 bools (_intPending + _nmiPending, milestone 12).</item>
    ///   <item>v3: Interrupts writes a 3rd bool (Lock, milestone 17); an optional Ctc block
    ///     (4 channels + vector base) is appended after Interrupts when the machine's
    ///     internal board is FloppyRam.</item>
    ///   <item>v4: the optional board block (FloppyRam only) grew a second device — an FDC
    ///     (<see cref="Devices.Fdc.Upd765"/>) block appended after the Ctc block (milestone 19).
    ///     The mounted <c>.dsk</c> image's bytes are NOT part of this block (reloaded from
    ///     <see cref="MachineConfig.FloppyDiskImagePath"/> at machine reconstruction, same
    ///     precedent as SLOT1 cartridges) — only the chip's transient register/phase state.</item>
    /// </list></summary>
    public const int CurrentVersion = 4;

    /// <summary>Oldest <c>.state</c> version accepted by this build. Older files are rejected
    /// because the device-stream layout changed incompatibly without a version bump at the
    /// time (v1→v2: SoundDevice added, NMI bool added to Interrupts; v2→v3: Lock bool added to
    /// Interrupts, optional Ctc block appended; v3→v4: FDC block appended after Ctc).</summary>
    private const int MinVersion = 4;

    private static readonly byte[] Magic = "P2ST"u8.ToArray();

    // ---- Save -------------------------------------------------------------------------------

    /// <summary>Saves the machine's runtime state to <paramref name="stream"/>.
    /// <b>Must be called at an instruction boundary</b> — see
    /// <see cref="Machine.SaveState"/>.</summary>
    public static void Save(Machine machine, Stream stream)
    {
        var configJson = MachineConfigFile.Serialize(machine.Config);
        var configBytes = Encoding.UTF8.GetBytes(configJson);

        stream.Write(Magic);
        WriteInt32(stream, CurrentVersion);
        WriteInt32(stream, configBytes.Length);
        stream.Write(configBytes);

        using var writer = new StreamStateWriter(stream, leaveOpen: true);
        machine.SaveState(writer);
    }

    /// <summary>Saves the machine's runtime state to <paramref name="path"/>.</summary>
    public static void Save(Machine machine, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(machine, fs);
    }

    // ---- Load -------------------------------------------------------------------------------

    /// <summary>Loads a machine from a <c>.state</c> stream: reads the header, rebuilds
    /// the machine from the embedded <see cref="MachineConfig"/>, then restores its
    /// runtime state. Throws <see cref="InvalidDataException"/> on bad magic, unsupported
    /// version, or truncated payload.</summary>
    public static Machine Load(Stream stream)
    {
        // Magic
        var magic = new byte[4];
        if (stream.Read(magic) != 4 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a P2000T state file (bad magic).");

        // Version
        var version = ReadInt32(stream);
        if (version < MinVersion || version > CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported .state version {version}. This build supports versions {MinVersion}–{CurrentVersion}.");

        // Config JSON
        var configLen = ReadInt32(stream);
        var configBytes = new byte[configLen];
        if (stream.Read(configBytes) != configLen)
            throw new InvalidDataException("Truncated .state file (config JSON incomplete).");
        var configJson = Encoding.UTF8.GetString(configBytes);
        var config = MachineConfigFile.Deserialize(configJson);

        // Rebuild machine from config (reset-to-apply), then overwrite device state.
        var machine = new Machine(config);
        using var reader = new StreamStateReader(stream, leaveOpen: true);
        machine.LoadState(reader);
        return machine;
    }

    /// <summary>Loads a machine from a <c>.state</c> file at <paramref name="path"/>.</summary>
    public static Machine Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static void WriteInt32(Stream s, int value)
    {
        var buf = new byte[4];
        buf[0] = (byte)value;
        buf[1] = (byte)(value >> 8);
        buf[2] = (byte)(value >> 16);
        buf[3] = (byte)(value >> 24);
        s.Write(buf);
    }

    private static int ReadInt32(Stream s)
    {
        var buf = new byte[4];
        if (s.Read(buf) != 4)
            throw new InvalidDataException("Truncated .state file (header incomplete).");
        return buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
    }
}

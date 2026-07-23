using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2000.Machine.State;

/// <summary>
/// Serializes and deserializes <see cref="MachineConfig"/> as a versioned JSON
/// <c>.cfg</c> file (project CLAUDE.md §11). The format is human-editable, small,
/// and shareable. Loading a config file produces a <see cref="MachineConfig"/> that
/// can be passed to <see cref="Machine"/> (reset-to-apply, locked decision §2.3).
/// </summary>
public static class MachineConfigFile
{
    /// <summary>Current <c>.cfg</c> format version. Increment when fields are added or
    /// removed; the reader rejects files whose version exceeds this value.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Enum VALUES are serialized as their declared name (T38, P2000T, etc.) — do NOT apply
        // camelCase to enum values, only to property names (PropertyNamingPolicy above).
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes <paramref name="config"/> to an indented JSON string.</summary>
    public static string Serialize(MachineConfig config)
    {
        var dto = ToDto(config);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Deserializes a <see cref="MachineConfig"/> from a JSON string produced
    /// by <see cref="Serialize"/>. Throws <see cref="InvalidDataException"/> when the
    /// version field is missing, zero, or newer than <see cref="CurrentVersion"/>.</summary>
    public static MachineConfig Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Config JSON deserialized to null.");
        ValidateVersion(dto.Version);
        return FromDto(dto);
    }

    /// <summary>Reads a <c>.cfg</c> file from <paramref name="path"/> and deserializes it.</summary>
    public static MachineConfig LoadFromFile(string path) =>
        Deserialize(File.ReadAllText(path));

    /// <summary>Serializes <paramref name="config"/> and writes it to <paramref name="path"/>.</summary>
    public static void SaveToFile(MachineConfig config, string path) =>
        File.WriteAllText(path, Serialize(config));

    private static void ValidateVersion(int version)
    {
        if (version < 1 || version > CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported .cfg version {version}. This build supports versions 1–{CurrentVersion}.");
    }

    private static ConfigDto ToDto(MachineConfig c) => new()
    {
        Version = CurrentVersion,
        Model = c.Model,
        Board = c.Board,
        RamVariant = c.RamVariant,
        BankCount = c.BankCount,
        MonitorRomPath = c.MonitorRomPath,
        Slot1CartridgePath = c.Slot1CartridgePath,
        FloppyDrives = c.FloppyDrives.ToList(),
        RamSeed = c.RamSeed,
    };

    private static MachineConfig FromDto(ConfigDto d) => new()
    {
        Model = d.Model,
        Board = d.Board,
        RamVariant = d.RamVariant,
        BankCount = d.BankCount,
        MonitorRomPath = d.MonitorRomPath,
        Slot1CartridgePath = d.Slot1CartridgePath,
        FloppyDrives = d.FloppyDrives ?? new List<FloppyDriveConfig>(),
        RamSeed = d.RamSeed,
    };

    private sealed class ConfigDto
    {
        public int Version { get; set; }
        public MachineModel Model { get; set; }
        public InternalBoard Board { get; set; }
        public RamVariant RamVariant { get; set; }
        public int? BankCount { get; set; }
        public string? MonitorRomPath { get; set; }
        public string? Slot1CartridgePath { get; set; }
        public List<FloppyDriveConfig>? FloppyDrives { get; set; }
        public ulong? RamSeed { get; set; }
    }
}

using P2000.Machine.State;

namespace P2000.Machine.Tests.State;

/// <summary>
/// Round-trip and version-gate tests for <see cref="MachineConfigFile"/> (.cfg JSON format,
/// project CLAUDE.md §11).
/// </summary>
public class MachineConfigFileTests
{
    // ---- Round-trip -------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_BareDefault_PreservesAllFields()
    {
        var original = new MachineConfig();
        var json = MachineConfigFile.Serialize(original);
        var restored = MachineConfigFile.Deserialize(json);

        Assert.Equal(original.Model, restored.Model);
        Assert.Equal(original.Board, restored.Board);
        Assert.Equal(original.RamVariant, restored.RamVariant);
        Assert.Equal(original.BankCount, restored.BankCount);
        Assert.Equal(original.MonitorRomPath, restored.MonitorRomPath);
        Assert.Equal(original.Slot1CartridgePath, restored.Slot1CartridgePath);
    }

    [Fact]
    public void RoundTrip_T102WithCustomBankCount_PreservesAllFields()
    {
        var original = new MachineConfig
        {
            Model = MachineModel.P2000T,
            Board = InternalBoard.RamOnly,
            RamVariant = RamVariant.T102,
            BankCount = 12,
            MonitorRomPath = null,
            Slot1CartridgePath = null,
        };
        var restored = MachineConfigFile.Deserialize(MachineConfigFile.Serialize(original));

        Assert.Equal(original.RamVariant, restored.RamVariant);
        Assert.Equal(original.BankCount, restored.BankCount);
        Assert.Equal(original.Board, restored.Board);
    }

    [Fact]
    public void RoundTrip_FloppyDrives_PreservesAllFields()
    {
        var original = new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 1, ImagePath = "spel1.dsk" },
                new FloppyDriveConfig
                {
                    DriveIndex = 2, Enabled = false, Capacity = 80, Sides = DiskSides.Double,
                },
            },
        };
        var restored = MachineConfigFile.Deserialize(MachineConfigFile.Serialize(original));

        Assert.Equal(2, restored.FloppyDrives.Count);
        Assert.Equal(1, restored.FloppyDrives[0].DriveIndex);
        Assert.True(restored.FloppyDrives[0].Enabled);
        Assert.Equal("spel1.dsk", restored.FloppyDrives[0].ImagePath);
        Assert.Equal(2, restored.FloppyDrives[1].DriveIndex);
        Assert.False(restored.FloppyDrives[1].Enabled);
        Assert.Equal(80, restored.FloppyDrives[1].Capacity);
        Assert.Equal(DiskSides.Double, restored.FloppyDrives[1].Sides);
        Assert.Null(restored.FloppyDrives[1].ImagePath);
    }

    [Fact]
    public void RoundTrip_NoFloppyDrives_DefaultsToEmpty()
    {
        var restored = MachineConfigFile.Deserialize(MachineConfigFile.Serialize(new MachineConfig()));
        Assert.Empty(restored.FloppyDrives);
    }

    [Fact]
    public void RoundTrip_ExplicitRamSeed_IsPreserved()
    {
        // Pre-existing gap (flagged during milestone 20/20a, now fixed): RamSeed was never
        // wired into ConfigDto at all, so a .cfg/.state saved with an explicit RamSeed silently
        // lost it on load — MachineConfig.RamSeed's own doc comment describes it as exactly the
        // kind of override a saved config should be able to pin.
        var original = new MachineConfig { RamSeed = 0xCAFEF00DDEADBEEF };
        var restored = MachineConfigFile.Deserialize(MachineConfigFile.Serialize(original));

        Assert.Equal(original.RamSeed, restored.RamSeed);
    }

    [Fact]
    public void RoundTrip_NoRamSeed_DefaultsToNull()
    {
        var restored = MachineConfigFile.Deserialize(MachineConfigFile.Serialize(new MachineConfig()));
        Assert.Null(restored.RamSeed);
    }

    [Fact]
    public void Serialize_ProducesReadableJson_WithVersionField()
    {
        var json = MachineConfigFile.Serialize(new MachineConfig());

        Assert.Contains("\"version\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{MachineConfigFile.CurrentVersion}", json);
    }

    [Fact]
    public void Serialize_EncodesEnums_AsStrings()
    {
        var json = MachineConfigFile.Serialize(new MachineConfig { RamVariant = RamVariant.T54 });

        Assert.Contains("T54", json);
        Assert.Contains("P2000T", json);
    }

    // ---- Version gating ---------------------------------------------------------------------

    [Fact]
    public void Deserialize_VersionZero_Throws()
    {
        var json = """{ "version": 0, "model": "P2000T", "board": "None", "ramVariant": "T38" }""";
        Assert.Throws<InvalidDataException>(() => MachineConfigFile.Deserialize(json));
    }

    [Fact]
    public void Deserialize_FutureVersion_Throws()
    {
        var futureVersion = MachineConfigFile.CurrentVersion + 1;
        var json = $$"""{ "version": {{futureVersion}}, "model": "P2000T", "board": "None", "ramVariant": "T38" }""";
        Assert.Throws<InvalidDataException>(() => MachineConfigFile.Deserialize(json));
    }

    [Fact]
    public void Deserialize_CurrentVersion_Succeeds()
    {
        var json = $$"""
            {
              "version": {{MachineConfigFile.CurrentVersion}},
              "model": "P2000T",
              "board": "None",
              "ramVariant": "T38",
              "bankCount": null,
              "monitorRomPath": null,
              "slot1CartridgePath": null
            }
            """;
        var config = MachineConfigFile.Deserialize(json);
        Assert.Equal(MachineModel.P2000T, config.Model);
    }
}

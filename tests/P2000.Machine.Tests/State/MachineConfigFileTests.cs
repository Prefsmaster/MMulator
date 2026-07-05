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

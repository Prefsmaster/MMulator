using System.Text.Json.Serialization;

namespace Z80.Tests.SingleStepTests;

/// <summary>One test case from a SingleStepTests/z80 v1 JSON file.</summary>
public sealed class TestCase
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("initial")]
    public TestState Initial { get; set; } = null!;

    [JsonPropertyName("final")]
    public TestState Final { get; set; } = null!;

    /// <summary>Each entry is [address|null, data|null, "rwmi" flags string],
    /// one entry per T-state.</summary>
    [JsonPropertyName("cycles")]
    public List<List<object?>> Cycles { get; set; } = new();

    /// <summary>Each entry is [port address, value, "r"|"w"]. Absent for
    /// instructions that don't touch the IO bus.</summary>
    [JsonPropertyName("ports")]
    public List<List<object?>>? Ports { get; set; }
}

/// <summary>Initial or final CPU + RAM state for one test case. Field names match
/// the JSON exactly (see SingleStepTests/z80 README.MD).</summary>
public sealed class TestState
{
    [JsonPropertyName("pc")] public ushort Pc { get; set; }
    [JsonPropertyName("sp")] public ushort Sp { get; set; }
    [JsonPropertyName("a")] public byte A { get; set; }
    [JsonPropertyName("b")] public byte B { get; set; }
    [JsonPropertyName("c")] public byte C { get; set; }
    [JsonPropertyName("d")] public byte D { get; set; }
    [JsonPropertyName("e")] public byte E { get; set; }
    [JsonPropertyName("f")] public byte F { get; set; }
    [JsonPropertyName("h")] public byte H { get; set; }
    [JsonPropertyName("l")] public byte L { get; set; }
    [JsonPropertyName("i")] public byte I { get; set; }
    [JsonPropertyName("r")] public byte R { get; set; }
    [JsonPropertyName("ei")] public int Ei { get; set; }
    [JsonPropertyName("wz")] public ushort Wz { get; set; }
    [JsonPropertyName("ix")] public ushort Ix { get; set; }
    [JsonPropertyName("iy")] public ushort Iy { get; set; }
    [JsonPropertyName("af_")] public ushort AfShadow { get; set; }
    [JsonPropertyName("bc_")] public ushort BcShadow { get; set; }
    [JsonPropertyName("de_")] public ushort DeShadow { get; set; }
    [JsonPropertyName("hl_")] public ushort HlShadow { get; set; }
    [JsonPropertyName("im")] public byte Im { get; set; }
    [JsonPropertyName("p")] public int P { get; set; }
    [JsonPropertyName("q")] public byte Q { get; set; }
    [JsonPropertyName("iff1")] public int Iff1 { get; set; }
    [JsonPropertyName("iff2")] public int Iff2 { get; set; }

    /// <summary>Each entry is [address, value].</summary>
    [JsonPropertyName("ram")] public List<List<int>> Ram { get; set; } = new();
}

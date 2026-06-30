using System.Text.Json;

namespace Z80.Tests.SingleStepTests;

public static class SingleStepTestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static List<TestCase> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<TestCase>>(stream, Options)
            ?? throw new InvalidDataException($"'{path}' did not contain a JSON test array.");
    }

    /// <summary>Resolves a vendored SingleStepTests data file by its lowercase
    /// opcode-page name (e.g. "00", "cb 00", "dd e6"), relative to
    /// tests/Z80.Tests/SingleStepTests/data/.</summary>
    public static string DataPath(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "SingleStepTests", "data");
        return Path.Combine(dir, $"{name}.json");
    }
}

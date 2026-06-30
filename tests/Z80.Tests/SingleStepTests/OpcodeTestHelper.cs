namespace Z80.Tests.SingleStepTests;

/// <summary>Loads and runs every case in a SingleStepTests opcode file (fetched
/// on demand, see <see cref="SingleStepTestLoader"/>), aggregating failures into
/// one assertion so a single [Fact] can cover an opcode (or opcode group)
/// without 1000 separate xUnit test results.</summary>
public static class OpcodeTestHelper
{
    public static void RunFile(string name, int maxFailuresShown = 20)
    {
        var tests = SingleStepTestLoader.Load(name);

        var failures = new List<string>();
        foreach (var test in tests)
        {
            try
            {
                SingleStepTestRunner.Run(test);
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
                if (failures.Count >= maxFailuresShown)
                    break;
            }
        }

        if (failures.Count > 0)
            Assert.Fail($"[{name}] {failures.Count} of {tests.Count} cases failed (showing up to {maxFailuresShown}):\n" + string.Join("\n", failures));
    }
}

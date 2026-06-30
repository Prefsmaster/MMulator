using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class NopTests
{
    [Fact]
    public void Nop_MatchesSingleStepTests()
    {
        var path = SingleStepTestLoader.DataPath("00");
        var tests = SingleStepTestLoader.Load(path);

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
                if (failures.Count >= 20)
                    break;
            }
        }

        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} of {tests.Count} cases failed (showing up to 20):\n" + string.Join("\n", failures));
    }
}

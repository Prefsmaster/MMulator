using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class Quadrant11Tests
{
    private static readonly HashSet<int> Prefixes = new() { 0xCB, 0xDD, 0xED, 0xFD };

    public static IEnumerable<object[]> Opcodes()
    {
        for (var op = 0xC0; op <= 0xFF; op++)
            if (!Prefixes.Contains(op))
                yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void Quadrant11_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"{opcode:x2}");
}

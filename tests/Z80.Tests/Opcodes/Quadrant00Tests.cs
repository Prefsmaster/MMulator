using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class Quadrant00Tests
{
    public static IEnumerable<object[]> Opcodes()
    {
        for (var op = 0x01; op <= 0x3F; op++)
            yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void Quadrant00_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"{opcode:x2}");
}

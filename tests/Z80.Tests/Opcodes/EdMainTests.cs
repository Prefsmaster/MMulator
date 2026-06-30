using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class EdMainTests
{
    public static IEnumerable<object[]> Opcodes()
    {
        for (var op = 0x40; op <= 0x7F; op++)
            yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void EdMain_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"ed {opcode:x2}");
}

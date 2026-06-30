using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class CbTests
{
    public static IEnumerable<object[]> Opcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void Cb_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"cb {opcode:x2}");
}

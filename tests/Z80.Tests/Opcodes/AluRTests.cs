using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class AluRTests
{
    public static IEnumerable<object[]> Opcodes()
    {
        for (var op = 0x80; op <= 0xBF; op++)
            yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void AluR_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"{opcode:x2}");
}

using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class EdBlockTests
{
    public static IEnumerable<object[]> Opcodes()
    {
        foreach (var op in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA8, 0xA9, 0xAA, 0xAB, 0xB0, 0xB1, 0xB2, 0xB3, 0xB8, 0xB9, 0xBA, 0xBB })
            yield return new object[] { op };
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void EdBlock_MatchesSingleStepTests(int opcode) => OpcodeTestHelper.RunFile($"ed {opcode:x2}");
}

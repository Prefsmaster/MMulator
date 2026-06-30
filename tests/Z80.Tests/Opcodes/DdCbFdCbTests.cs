using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class DdCbFdCbTests
{
    public static IEnumerable<object[]> DdCbOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            yield return new object[] { $"dd cb __ {op:x2}" };
    }

    public static IEnumerable<object[]> FdCbOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            yield return new object[] { $"fd cb __ {op:x2}" };
    }

    [Theory]
    [MemberData(nameof(DdCbOpcodes))]
    public void DdCb_MatchesSingleStepTests(string name) => OpcodeTestHelper.RunFile(name);

    [Theory]
    [MemberData(nameof(FdCbOpcodes))]
    public void FdCb_MatchesSingleStepTests(string name) => OpcodeTestHelper.RunFile(name);
}

using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class DdFdTests
{
    public static IEnumerable<object[]> DdOpcodes()
    {
        // All DD plain opcodes except 0xCB (→DDCB, separate), 0xDD/0xED/0xFD (prefix chain)
        for (var op = 0x00; op <= 0xFF; op++)
            if (op != 0xCB && op != 0xDD && op != 0xED && op != 0xFD)
                yield return new object[] { $"dd {op:x2}" };
    }

    public static IEnumerable<object[]> FdOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            if (op != 0xCB && op != 0xDD && op != 0xED && op != 0xFD)
                yield return new object[] { $"fd {op:x2}" };
    }

    [Theory]
    [MemberData(nameof(DdOpcodes))]
    public void Dd_MatchesSingleStepTests(string name) => OpcodeTestHelper.RunFile(name);

    [Theory]
    [MemberData(nameof(FdOpcodes))]
    public void Fd_MatchesSingleStepTests(string name) => OpcodeTestHelper.RunFile(name);
}

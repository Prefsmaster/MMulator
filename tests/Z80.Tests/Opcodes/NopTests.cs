using Z80.Tests.SingleStepTests;

namespace Z80.Tests.Opcodes;

public class NopTests
{
    [Fact]
    public void Nop_MatchesSingleStepTests() => OpcodeTestHelper.RunFile("00");
}

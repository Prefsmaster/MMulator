using Z80.Tests.SingleStepTests;
using Cpu = Z80.Core.Z80;
using PinBits = Z80.Core.Pins;

namespace Z80.Disassembler.Tests;

/// <summary>
/// The anti-drift guarantee (disassembler CLAUDE.md §10): for every opcode in the
/// full set (base + all six prefix pages), the disassembler's declared
/// <see cref="DisasmLine.Length"/> must equal the number of bytes the CORE actually
/// fetches sequentially from the opcode's start address while executing it. This is
/// a behavioural check — it runs the real <c>Z80.Core.Z80</c>, not a second copy of
/// the decode tables — so the disassembler cannot silently diverge from what the
/// core will actually execute. Encoding length never depends on register/flag
/// values, so one representative SingleStepTests case per opcode is sufficient;
/// this also cross-checks against the SAME vendored SingleStepTests data the core's
/// own test suite (tests/Z80.Tests) already relies on as ground truth.
/// </summary>
public class ConformanceTests
{
    public static IEnumerable<object[]> BaseOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            if (op is not (0xCB or 0xDD or 0xED or 0xFD))
                yield return new object[] { $"{op:x2}" };
    }

    public static IEnumerable<object[]> CbOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            yield return new object[] { $"cb {op:x2}" };
    }

    public static IEnumerable<object[]> EdOpcodes()
    {
        for (var op = 0x40; op <= 0x7F; op++)
            yield return new object[] { $"ed {op:x2}" };
        foreach (var op in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA8, 0xA9, 0xAA, 0xAB, 0xB0, 0xB1, 0xB2, 0xB3, 0xB8, 0xB9, 0xBA, 0xBB })
            yield return new object[] { $"ed {op:x2}" };
    }

    public static IEnumerable<object[]> DdOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            if (op is not (0xCB or 0xDD or 0xED or 0xFD))
                yield return new object[] { $"dd {op:x2}" };
    }

    public static IEnumerable<object[]> FdOpcodes()
    {
        for (var op = 0x00; op <= 0xFF; op++)
            if (op is not (0xCB or 0xDD or 0xED or 0xFD))
                yield return new object[] { $"fd {op:x2}" };
    }

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

    [Theory] [MemberData(nameof(BaseOpcodes))]
    public void Base_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(CbOpcodes))]
    public void Cb_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(EdOpcodes))]
    public void Ed_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(DdOpcodes))]
    public void Dd_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(FdOpcodes))]
    public void Fd_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(DdCbOpcodes))]
    public void DdCb_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    [Theory] [MemberData(nameof(FdCbOpcodes))]
    public void FdCb_LengthMatchesCore(string fileName) => AssertLengthMatchesCore(fileName);

    private static void AssertLengthMatchesCore(string fileName)
    {
        var test = SingleStepTestLoader.Load(fileName)[0];

        var memory = new byte[65536];
        foreach (var entry in test.Initial.Ram)
            memory[entry[0]] = (byte)entry[1];

        var line = new Z80.Disassembler.Disassembler().Decode(test.Initial.Pc, addr => memory[addr]);
        var consumed = CountCoreConsumedBytes(test.Initial.Pc, (byte[])memory.Clone());

        Assert.True(
            line.Length == consumed,
            $"[{fileName}] disassembler Length={line.Length} (\"{line.Text}\") but core consumed {consumed} byte(s) sequentially from PC.");
    }

    /// <summary>Steps the real core from <paramref name="startPc"/> and counts how
    /// many memory reads land at the expected PC-sequential address (PC, PC+1,
    /// PC+2, ...) — i.e. the opcode/prefix/immediate/displacement bytes that are
    /// actually part of this instruction's encoding, as opposed to stack/(HL)/
    /// port reads that happen to occur while executing it (disassembler
    /// CLAUDE.md §10). Runs past instruction completion is safe: repeat-block
    /// instructions (LDIR etc.) never re-touch PC-sequential addresses once their
    /// fixed 1-2 byte encoding has been fetched, and undefined-ED/HALT opcodes
    /// still fetch their fixed encoding before the guard expires.</summary>
    private static int CountCoreConsumedBytes(ushort startPc, byte[] memory)
    {
        var cpu = new Cpu();
        cpu.Reg.PC = startPc;

        var expectedAddr = startPc;
        var consumed = 0;
        ulong pins = 0;

        for (var guard = 0; guard < 100; guard++)
        {
            pins = cpu.Step(pins);
            var addr = PinBits.GetAddress(pins);
            var mreq = (pins & PinBits.MREQ) != 0;
            var rd = (pins & PinBits.RD) != 0;
            var wr = (pins & PinBits.WR) != 0;
            var iorq = (pins & PinBits.IORQ) != 0;

            if (mreq && rd)
            {
                if (addr == expectedAddr)
                {
                    consumed++;
                    expectedAddr = (ushort)(expectedAddr + 1);
                }
                pins = PinBits.SetData(pins, memory[addr]);
            }
            else if (mreq && wr)
            {
                memory[addr] = PinBits.GetData(pins);
            }
            else if (iorq && rd)
            {
                pins = PinBits.SetData(pins, 0xFF);
            }

            if (cpu.AtInstructionBoundary && consumed > 0)
                break;
        }

        return consumed;
    }
}

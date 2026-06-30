using System.Text.Json;
using Z80.Core;
using Xunit;
using Xunit.Sdk;
using Cpu = Z80.Core.Z80;

namespace Z80.Tests.SingleStepTests;

/// <summary>
/// Drives a <see cref="Z80"/> through one SingleStepTests case, asserting
/// (a) the final register state, (b) the final RAM contents, and (c) the
/// per-T-state bus activity (address, data, RD/WR/MREQ/IORQ) recorded in the
/// case's "cycles" array. See CLAUDE.md §7a.
/// </summary>
public static class SingleStepTestRunner
{
    public static void Run(TestCase test)
    {
        var memory = new byte[65536];
        foreach (var entry in test.Initial.Ram)
            memory[entry[0]] = (byte)entry[1];

        var cpu = new Cpu();
        ApplyState(cpu, test.Initial);

        ulong pins = 0;
        var portReads = new Queue<byte>();
        if (test.Ports is not null)
        {
            foreach (var port in test.Ports)
                if (AsString(port[2]) == "r")
                    portReads.Enqueue((byte)AsInt(port[1])!);
        }

        for (var t = 0; t < test.Cycles.Count; t++)
        {
            pins = cpu.Step(pins);
            AssertCycle(test, t, test.Cycles[t], pins);
            pins = ServiceBus(pins, memory, portReads);
        }

        AssertFinalState(test, cpu);
        AssertFinalRam(test, memory);
    }

    private static void ApplyState(Cpu cpu, TestState s)
    {
        ref var r = ref cpu.Reg;
        r.PC = s.Pc;
        r.SP = s.Sp;
        r.A = s.A; r.F = s.F;
        r.B = s.B; r.C = s.C;
        r.D = s.D; r.E = s.E;
        r.H = s.H; r.L = s.L;
        r.I = s.I; r.R = s.R;
        r.WZ = s.Wz;
        r.IX = s.Ix; r.IY = s.Iy;
        r.AF_ = s.AfShadow; r.BC_ = s.BcShadow; r.DE_ = s.DeShadow; r.HL_ = s.HlShadow;
        r.IM = s.Im;
        r.Q = s.Q;
        r.IFF1 = s.Iff1 != 0;
        r.IFF2 = s.Iff2 != 0;
        r.EiPending = s.Ei != 0;
        r.LastWasLdAIR = s.P != 0;
    }

    private static ulong ServiceBus(ulong pins, byte[] memory, Queue<byte> portReads)
    {
        var addr = Pins.GetAddress(pins);
        var mreq = (pins & Pins.MREQ) != 0;
        var iorq = (pins & Pins.IORQ) != 0;
        var rd = (pins & Pins.RD) != 0;
        var wr = (pins & Pins.WR) != 0;

        if (mreq && rd)
            pins = Pins.SetData(pins, memory[addr]);
        else if (mreq && wr)
            memory[addr] = Pins.GetData(pins);
        else if (iorq && rd)
            pins = Pins.SetData(pins, portReads.Count > 0 ? portReads.Dequeue() : (byte)0xFF);
        // IORQ+WR: nothing to capture for the harness today; revisit when OUT is implemented.

        return pins;
    }

    private static void AssertCycle(TestCase test, int t, List<object?> expected, ulong pins)
    {
        var expectedAddr = AsInt(expected[0]);
        var expectedData = AsInt(expected[1]);
        var expectedFlags = AsString(expected[2]);

        if (expectedAddr is not null)
        {
            var actualAddr = Pins.GetAddress(pins);
            if (actualAddr != expectedAddr)
                throw FailureFor(test, t, $"address: expected {expectedAddr}, got {actualAddr}");
        }

        if (expectedData is not null)
        {
            var actualData = Pins.GetData(pins);
            if (actualData != expectedData)
                throw FailureFor(test, t, $"data: expected {expectedData}, got {actualData}");
        }

        var expectedRd = expectedFlags[0] == 'r';
        var expectedWr = expectedFlags[1] == 'w';
        var expectedMreq = expectedFlags[2] == 'm';
        var expectedIorq = expectedFlags[3] == 'i';

        var actualRd = (pins & Pins.RD) != 0;
        var actualWr = (pins & Pins.WR) != 0;
        var actualMreq = (pins & Pins.MREQ) != 0;
        var actualIorq = (pins & Pins.IORQ) != 0;

        if (actualRd != expectedRd || actualWr != expectedWr || actualMreq != expectedMreq || actualIorq != expectedIorq)
        {
            var actualFlags = $"{(actualRd ? 'r' : '-')}{(actualWr ? 'w' : '-')}{(actualMreq ? 'm' : '-')}{(actualIorq ? 'i' : '-')}";
            throw FailureFor(test, t, $"flags: expected \"{expectedFlags}\", got \"{actualFlags}\"");
        }
    }

    private static void AssertFinalState(TestCase test, Cpu cpu)
    {
        var r = cpu.Reg;
        var f = test.Final;
        var mismatches = new List<string>();

        void Check(string name, object actual, object expected)
        {
            if (!Equals(actual, expected))
                mismatches.Add($"{name}: expected {expected}, got {actual}");
        }

        Check(nameof(r.PC), r.PC, f.Pc);
        Check(nameof(r.SP), r.SP, f.Sp);
        Check(nameof(r.A), r.A, f.A);
        Check(nameof(r.F), r.F, f.F);
        Check(nameof(r.B), r.B, f.B);
        Check(nameof(r.C), r.C, f.C);
        Check(nameof(r.D), r.D, f.D);
        Check(nameof(r.E), r.E, f.E);
        Check(nameof(r.H), r.H, f.H);
        Check(nameof(r.L), r.L, f.L);
        Check(nameof(r.I), r.I, f.I);
        Check(nameof(r.R), r.R, f.R);
        Check(nameof(r.WZ), r.WZ, f.Wz);
        Check(nameof(r.IX), r.IX, f.Ix);
        Check(nameof(r.IY), r.IY, f.Iy);
        Check(nameof(r.AF_), r.AF_, f.AfShadow);
        Check(nameof(r.BC_), r.BC_, f.BcShadow);
        Check(nameof(r.DE_), r.DE_, f.DeShadow);
        Check(nameof(r.HL_), r.HL_, f.HlShadow);
        Check(nameof(r.IM), r.IM, f.Im);
        Check(nameof(r.Q), r.Q, f.Q);
        Check(nameof(r.IFF1), r.IFF1, f.Iff1 != 0);
        Check(nameof(r.IFF2), r.IFF2, f.Iff2 != 0);
        Check(nameof(r.EiPending), r.EiPending, f.Ei != 0);
        Check(nameof(r.LastWasLdAIR), r.LastWasLdAIR, f.P != 0);

        if (mismatches.Count > 0)
            throw FailureFor(test, null, "final state mismatch: " + string.Join("; ", mismatches));
    }

    private static void AssertFinalRam(TestCase test, byte[] memory)
    {
        foreach (var entry in test.Final.Ram)
        {
            var addr = entry[0];
            var expected = (byte)entry[1];
            var actual = memory[addr];
            if (actual != expected)
                throw FailureFor(test, null, $"RAM[{addr}]: expected {expected}, got {actual}");
        }
    }

    private static XunitException FailureFor(TestCase test, int? t, string message) =>
        new($"[{test.Name}]{(t is null ? "" : $" T-state {t}")}: {message}");

    private static int? AsInt(object? value) =>
        value is JsonElement { ValueKind: JsonValueKind.Null } or null
            ? null
            : ((JsonElement)value).GetInt32();

    private static string AsString(object? value) => ((JsonElement)value!).GetString()!;
}

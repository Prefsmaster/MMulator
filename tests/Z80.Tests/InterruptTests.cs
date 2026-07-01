using Z80.Core;
using Xunit;
using Cpu = Z80.Core.Z80;

namespace Z80.Tests;

/// <summary>
/// Targeted unit tests for NMI, INT (IM0/IM1/IM2), EI one-instruction delay,
/// DI guard, and HALT wake-up. These tests are the primary validation gate for
/// milestone 7 since the SingleStepTests suite is opcode-centric and does not
/// exercise asynchronous interrupt delivery.
/// </summary>
public class InterruptTests
{
    // ---- Test harness helpers ------------------------------------------------

    /// <summary>Runs the CPU for exactly <paramref name="steps"/> T-states,
    /// servicing memory reads/writes from <paramref name="mem"/>. Optional I/O
    /// data is returned from <paramref name="ioByte"/> when IORQ+RD or IORQ+M1
    /// are asserted. Returns the step count actually executed (== steps).</summary>
    private static void Run(Cpu cpu, byte[] mem, ulong startPins, int steps,
        byte ioByte = 0)
    {
        var pins = startPins;
        for (var i = 0; i < steps; i++)
        {
            pins = cpu.Step(pins);

            // Service memory
            if ((pins & Pins.MREQ) != 0)
            {
                var addr = Pins.GetAddress(pins);
                if ((pins & Pins.RD) != 0)
                    pins = Pins.SetData(pins, mem[addr]);
                else if ((pins & Pins.WR) != 0)
                    mem[addr] = Pins.GetData(pins);
            }

            // Service I/O reads (IORQ+RD = normal I/O; IORQ+M1 = INT-ack)
            if ((pins & Pins.IORQ) != 0 && (pins & (Pins.RD | Pins.M1)) != 0)
                pins = Pins.SetData(pins, ioByte);
        }
    }

    /// <summary>Steps the CPU until it finishes the first instruction at the
    /// initial PC, then continues for <paramref name="extraSteps"/> more T-states,
    /// servicing the bus throughout. This is a convenience for tests that want to
    /// assert state after N additional T-states following an instruction boundary.</summary>
    private static ulong RunWithPins(Cpu cpu, byte[] mem, ulong pins, int steps,
        byte ioByte = 0)
    {
        for (var i = 0; i < steps; i++)
        {
            pins = cpu.Step(pins);

            if ((pins & Pins.MREQ) != 0)
            {
                var addr = Pins.GetAddress(pins);
                if ((pins & Pins.RD) != 0)
                    pins = Pins.SetData(pins, mem[addr]);
                else if ((pins & Pins.WR) != 0)
                    mem[addr] = Pins.GetData(pins);
            }

            if ((pins & Pins.IORQ) != 0 && (pins & (Pins.RD | Pins.M1)) != 0)
                pins = Pins.SetData(pins, ioByte);
        }
        return pins;
    }

    private static byte[] MakeMemory(params (ushort addr, byte[] bytes)[] blocks)
    {
        var mem = new byte[65536];
        foreach (var (addr, bytes) in blocks)
            bytes.CopyTo(mem, addr);
        return mem;
    }

    // ---- NMI tests ----------------------------------------------------------

    [Fact]
    public void Nmi_PushesPC_JumpsTo0066_SavesIFF1()
    {
        // NOP at 0x0100, NMI asserted at the instruction boundary.
        var mem = MakeMemory((0x0100, new byte[] { 0x00 })); // NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IFF2 = false;

        // Run 4 T-states to complete the NOP M1 (reaches the instruction boundary).
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);

        // Now assert NMI for one tick and then run the full 11-T NMI sequence.
        pins |= Pins.NMI;
        pins = RunWithPins(cpu, mem, pins, 11);

        Assert.Equal(0x0066, cpu.Reg.PC);
        Assert.False(cpu.Reg.IFF1);
        Assert.True(cpu.Reg.IFF2);  // IFF2 ← old IFF1 (was true)
        Assert.Equal(0x01FE, cpu.Reg.SP); // pushed 2 bytes
        Assert.Equal(0x01, mem[0x01FF]); // PCH = 0x01
        Assert.Equal(0x01, mem[0x01FE]); // PCL = 0x01 (return to 0x0101, next after NOP)
    }

    [Fact]
    public void Nmi_TakesExactly11TStates()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x00 })); // NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;

        // Complete the NOP (4T).
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);

        // Assert NMI and step 10 T-states — CPU should NOT have reached 0x0066 yet.
        pins |= Pins.NMI;
        pins = RunWithPins(cpu, mem, pins, 10);
        Assert.NotEqual(0x0066, cpu.Reg.PC);

        // One more step completes the NMI sequence.
        pins = RunWithPins(cpu, mem, pins, 1);
        Assert.Equal(0x0066, cpu.Reg.PC);
    }

    [Fact]
    public void Nmi_EdgeTriggered_FiresOnce()
    {
        // Keep NMI asserted continuously; should fire on the RISING EDGE only.
        var mem = MakeMemory(
            (0x0100, new byte[] { 0x00, 0x00 }), // two NOPs
            (0x0066, new byte[] { 0x00 }));        // NMI vector: NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0300;

        // NOP (4T) then NMI fires.
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);
        pins |= Pins.NMI;
        // NMI sequence: 11T → PC = 0x0066
        pins = RunWithPins(cpu, mem, pins, 11);
        Assert.Equal(0x0066, cpu.Reg.PC);

        // Run NOP at 0x0066 (4T) — NMI still held high but no rising edge → no second NMI.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0067, cpu.Reg.PC); // returned past 0x0066 NOP, not re-entered
    }

    [Fact]
    public void Nmi_FromHalt_WakesUp_PushesCorrectReturnAddress()
    {
        // HALT at 0x0100. NMI fires while halted. Push address should be 0x0101
        // (the address AFTER the HALT instruction, which is where PC sits during halt).
        var mem = MakeMemory((0x0100, new byte[] { 0x76 })); // HALT
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = false; // irrelevant for NMI

        ulong pins = 0;
        // Complete HALT M1 (4T): PC advances to 0x0101, HALT mode entered.
        pins = RunWithPins(cpu, mem, pins, 4);
        // Let it run a couple of halted M1 cycles (4T each).
        pins = RunWithPins(cpu, mem, pins, 8);

        // Assert NMI → 11T to complete the NMI sequence.
        pins |= Pins.NMI;
        pins = RunWithPins(cpu, mem, pins, 11);

        Assert.Equal(0x0066, cpu.Reg.PC);
        Assert.Equal(0x0101, (ushort)((mem[0x01FF] << 8) | mem[0x01FE])); // pushed 0x0101
    }

    // ---- INT IM1 tests ------------------------------------------------------

    [Fact]
    public void Int_Im1_JumpsTo0038_PushesPC()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x00 })); // NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IFF2 = true;
        cpu.Reg.IM = 1;

        // Complete NOP (4T).
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);

        // Assert INT and run the INT response (13T = 6T ack + 1T internal + 3T + 3T).
        pins |= Pins.INT;
        pins = RunWithPins(cpu, mem, pins, 13);

        Assert.Equal(0x0038, cpu.Reg.PC);
        Assert.False(cpu.Reg.IFF1);
        Assert.False(cpu.Reg.IFF2);
        Assert.Equal(0x01FE, cpu.Reg.SP);
        Assert.Equal(0x01, mem[0x01FF]); // PCH
        Assert.Equal(0x01, mem[0x01FE]); // PCL (return to 0x0101)
    }

    [Fact]
    public void Int_Im1_NotTaken_WhenIFF1_False()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x00, 0x00 })); // two NOPs
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.IFF1 = false; // interrupts disabled
        cpu.Reg.IM = 1;

        ulong pins = Pins.INT; // INT asserted from the start
        pins = RunWithPins(cpu, mem, pins, 4); // NOP at 0x0100
        Assert.Equal(0x0101, cpu.Reg.PC); // not diverted

        pins = RunWithPins(cpu, mem, pins, 4); // NOP at 0x0101
        Assert.Equal(0x0102, cpu.Reg.PC); // still not diverted
    }

    [Fact]
    public void Int_Im1_TakesExactly13TStates()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x00 })); // NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 1;

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4); // complete NOP
        pins |= Pins.INT;

        // 12T: should not have finished yet
        pins = RunWithPins(cpu, mem, pins, 12);
        Assert.NotEqual(0x0038, cpu.Reg.PC);

        // 1 more T → done
        pins = RunWithPins(cpu, mem, pins, 1);
        Assert.Equal(0x0038, cpu.Reg.PC);
    }

    [Fact]
    public void Int_Im1_FromHalt_WakesUp()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x76 })); // HALT
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 1;

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);   // HALT M1
        pins = RunWithPins(cpu, mem, pins, 8);   // two halted M1 cycles
        pins |= Pins.INT;
        pins = RunWithPins(cpu, mem, pins, 13);  // INT response

        Assert.Equal(0x0038, cpu.Reg.PC);
        Assert.Equal(0x0101, (ushort)((mem[0x01FF] << 8) | mem[0x01FE]));
    }

    // ---- INT IM2 tests ------------------------------------------------------

    [Fact]
    public void Int_Im2_ReadsVectorTable_JumpsToISR()
    {
        // ISR address stored at vector table entry 0x5000: lo=0xAB, hi=0xCD → ISR at 0xCDAB.
        var mem = MakeMemory(
            (0x0100, new byte[] { 0x00 }),  // NOP
            (0x5000, new byte[] { 0xAB, 0xCD })); // vector table entry
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0300;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 2;
        cpu.Reg.I = 0x50;

        // ioByte = 0x00 → vector addr = (0x50 << 8) | (0x00 & 0xFE) = 0x5000
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4); // NOP
        pins |= Pins.INT;
        pins = RunWithPins(cpu, mem, pins, 19, ioByte: 0x00); // 6+1+3+3+3+3 = 19T

        Assert.Equal(0xCDAB, cpu.Reg.PC);
        Assert.False(cpu.Reg.IFF1);
        Assert.Equal(0x02FE, cpu.Reg.SP);
        Assert.Equal(0x01, mem[0x02FF]); // PCH
        Assert.Equal(0x01, mem[0x02FE]); // PCL
    }

    [Fact]
    public void Int_Im2_LowBitOfAckByteForced0()
    {
        // ack byte = 0x01 (odd) → forced to 0x00 → vector at (I<<8)|0x00 = 0x5000
        var mem = MakeMemory(
            (0x0100, new byte[] { 0x00 }),
            (0x5000, new byte[] { 0x34, 0x12 })); // ISR at 0x1234
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0300;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 2;
        cpu.Reg.I = 0x50;

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);
        pins |= Pins.INT;
        pins = RunWithPins(cpu, mem, pins, 19, ioByte: 0x01); // odd ack byte → use 0x00

        Assert.Equal(0x1234, cpu.Reg.PC);
    }

    // ---- INT IM0 tests ------------------------------------------------------

    [Fact]
    public void Int_Im0_RstFF_JumpsTo0038_ViaRst38()
    {
        // IM0: peripheral drives RST 0x38 (opcode 0xFF) onto the data bus.
        // INT-ack: 6T. Then RST execute: 1T internal + 3T MW + 3T MW = 7T. Total = 13T.
        var mem = MakeMemory((0x0100, new byte[] { 0x00 })); // NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 0;

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4); // NOP
        pins |= Pins.INT;
        pins = RunWithPins(cpu, mem, pins, 13, ioByte: 0xFF); // RST 0x38

        Assert.Equal(0x0038, cpu.Reg.PC);
        Assert.Equal(0x01FE, cpu.Reg.SP);
        Assert.Equal(0x01, mem[0x01FF]); // PCH of 0x0101
        Assert.Equal(0x01, mem[0x01FE]); // PCL
    }

    // ---- EI one-instruction delay -------------------------------------------

    [Fact]
    public void Ei_DelaysInt_ByOneInstruction()
    {
        // EI (0xFB) at 0x0100, NOP (0x00) at 0x0101.
        // INT asserted before EI executes. INT must NOT be taken after EI;
        // it must be taken only AFTER the NOP following EI completes.
        var mem = MakeMemory((0x0100, new byte[] { 0xFB, 0x00 })); // EI, NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = false; // EI will enable it
        cpu.Reg.IM = 1;

        ulong pins = Pins.INT; // INT asserted throughout

        // EI (4T): IFF1 set but EiPending = true.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0101, cpu.Reg.PC); // EI ran, not interrupted

        // NOP (4T): EiPending cleared; INT should be taken at the NEXT boundary.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0102, cpu.Reg.PC); // NOP ran, not interrupted

        // Now INT is taken (13T for IM1).
        pins = RunWithPins(cpu, mem, pins, 13);
        Assert.Equal(0x0038, cpu.Reg.PC);
    }

    // ---- DI guard -----------------------------------------------------------

    [Fact]
    public void Di_BlocksInt()
    {
        // INT must NOT be asserted while IFF1=true at the moment DI starts; the real
        // Z80 samples INT at the instruction boundary — before DI's M1 begins — so
        // asserting INT while IFF1=true would take the interrupt instead of running DI.
        // The correct test: INT is not asserted until AFTER DI has run (IFF1→false).
        var mem = MakeMemory((0x0100, new byte[] { 0xF3, 0x00, 0x00 })); // DI, NOP, NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 1;

        ulong pins = 0; // INT not yet asserted

        // DI (4T): IFF1 → false; INT is not asserted here so DI runs normally.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0101, cpu.Reg.PC);
        Assert.False(cpu.Reg.IFF1);

        // Now assert INT — but IFF1 is false, so it must be blocked.
        pins |= Pins.INT;

        // NOP (4T): INT asserted but IFF1=false → not taken.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0102, cpu.Reg.PC);

        // NOP (4T): same — INT still asserted, still blocked.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0103, cpu.Reg.PC);
    }

    // ---- HALT behaviour -----------------------------------------------------

    [Fact]
    public void Halt_PinAssertsAndStaysHigh()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x76 })); // HALT
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;

        ulong pins = 0;
        // 4T for HALT M1 — HALT pin should be asserted by DispatchHalt at T4.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.NotEqual(0UL, pins & Pins.HALT);

        // Further halted M1 cycles should keep HALT asserted.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.NotEqual(0UL, pins & Pins.HALT);
    }

    [Fact]
    public void Halt_PcDoesNotAdvanceDuringLoop()
    {
        var mem = MakeMemory((0x0100, new byte[] { 0x76 })); // HALT
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);   // HALT M1 → PC = 0x0101
        Assert.Equal(0x0101, cpu.Reg.PC);        // M1 incremented it once

        pins = RunWithPins(cpu, mem, pins, 16);  // four more halted M1 cycles
        Assert.Equal(0x0101, cpu.Reg.PC);        // PC must NOT advance further
    }

    // ---- Prefix-byte interrupt inhibition -----------------------------------

    [Fact]
    public void Int_NotTaken_BetweenDdPrefix_AndOpcode()
    {
        // DD 00 = DD-prefixed NOP (treated as plain NOP in our DD dispatch).
        // INT is asserted and IFF1=true, but the interrupt must NOT fire between
        // the DD byte and the 00 byte — only at a full instruction boundary.
        var mem = MakeMemory(
            (0x0100, new byte[] { 0xDD, 0x00, 0x00 })); // DD NOP, then plain NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IM = 1;

        // Complete the DD byte M1 (4T): _prefix=DD, no interrupt yet.
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0101, cpu.Reg.PC); // DD consumed PC+1

        // Now assert INT — we are between DD and its opcode byte.
        // The CPU must NOT take the interrupt here; it must finish the DD-NOP first.
        pins |= Pins.INT;

        // DD-NOP M1 (4T) + no execute cycle (NOP): total 4T for the second byte.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0102, cpu.Reg.PC); // opcode byte consumed

        // NOW the interrupt fires at the full instruction boundary (13T for IM1).
        pins = RunWithPins(cpu, mem, pins, 13);
        Assert.Equal(0x0038, cpu.Reg.PC);
    }

    [Fact]
    public void Nmi_NotTaken_BetweenDdPrefix_AndOpcode()
    {
        // Same scenario but with NMI — NMI is also deferred past the prefixed instruction.
        var mem = MakeMemory(
            (0x0100, new byte[] { 0xDD, 0x00, 0x00 })); // DD NOP, then NOP
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;

        // Complete DD byte M1 (4T).
        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);

        // Assert NMI between DD and its opcode.
        pins |= Pins.NMI;

        // DD-NOP M1 (4T): NMI must be deferred — instruction not yet complete.
        pins = RunWithPins(cpu, mem, pins, 4);
        Assert.Equal(0x0102, cpu.Reg.PC); // opcode byte ran, not interrupted

        // NMI fires at the next full boundary (11T).
        pins = RunWithPins(cpu, mem, pins, 11);
        Assert.Equal(0x0066, cpu.Reg.PC);
    }

    // ---- RETN / RETI restore IFF1 from IFF2 ---------------------------------

    [Fact]
    public void Retn_RestoresIFF1FromIFF2()
    {
        // RETN = ED 45. Place it at 0x0066 (NMI vector) so after NMI, RETN runs.
        var mem = MakeMemory(
            (0x0100, new byte[] { 0x00 }), // NOP (the instruction that gets interrupted)
            (0x0066, new byte[] { 0xED, 0x45 })); // RETN at NMI vector
        var cpu = new Cpu();
        cpu.Reg.PC = 0x0100;
        cpu.Reg.SP = 0x0200;
        cpu.Reg.IFF1 = true;
        cpu.Reg.IFF2 = false; // initial IFF2; NMI will overwrite it with old IFF1 (true)

        ulong pins = 0;
        pins = RunWithPins(cpu, mem, pins, 4);   // NOP
        pins |= Pins.NMI;
        pins = RunWithPins(cpu, mem, pins, 11);  // NMI (sets IFF2=true because IFF1 was true, IFF1=false)

        // Now at 0x0066, run RETN (ED prefix M1 = 4T, then 45 M1 = 4T, then 2×MR = 6T → 14T)
        pins &= ~Pins.NMI; // release NMI
        pins = RunWithPins(cpu, mem, pins, 14);

        // RETN should have popped 0x0101 from stack and restored IFF1 = IFF2 = true
        Assert.Equal(0x0101, cpu.Reg.PC);
        Assert.True(cpu.Reg.IFF1); // restored from IFF2 which was set to old IFF1 (true) by NMI
    }
}

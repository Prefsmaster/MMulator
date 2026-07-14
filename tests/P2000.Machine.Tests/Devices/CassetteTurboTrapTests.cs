using P2000.Machine.Devices.Cassette;
using Z80.Core;
using Machine = P2000.Machine.Machine;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// Tests for the turbo ROM trap (project CLAUDE.md §13.18): traps <c>cas_Read</c> (0x0552)
/// and <c>cas_Write</c> (0x057A) when <see cref="MdcrDevice.Policy"/> is
/// <see cref="TimingPolicy.Turbo"/> and performs the whole multi-block transfer directly
/// against the mounted <c>.cas</c> tape, bypassing the ROM's bit-engine.
///
/// Each test drives the REAL embedded monitor ROM's cassette routines directly — a tiny
/// bootstrap written into RAM mimics <c>do_cas_jump</c>'s own entry protocol (push a return
/// marker, jump to the routine) so the routine's own final <c>RET</c> lands on a HALT we can
/// detect, exactly like <c>do_cas_jump</c> jumping into <c>cas_Read</c>/<c>cas_Write</c> with
/// <c>cas_command_return</c> already on the stack.
/// </summary>
public class CassetteTurboTrapTests
{
    private const ushort CasReadEntry = 0x0552;
    private const ushort CasWriteEntry = 0x057A;

    // RAM variables the "cassette" jump-table dispatcher sets before jumping in (Cassette.asm
    // knowncascommand) — see CassetteTurboTrap's class doc for the full contract.
    private const ushort Transfer = 0x6030;
    private const ushort FileLength = 0x6032;
    private const ushort RecordLength = 0x6034;
    private const ushort Des1 = 0x6068;
    private const ushort DesLength = 0x606A;

    // Bootstrap: mimics do_cas_jump's "push return address; jp routine" entry protocol.
    private const ushort Bootstrap = 0x9000;
    private const ushort ReturnMarker = 0x9010;
    private const ushort Stack = 0x9100;

    // ---- (a) Turbo load byte-identical to an authentic load ----------------------------

    [Fact]
    public void TurboRead_ByteIdentical_ToAuthenticRead()
    {
        var cas = BuildCasRecord(headerSeed: 0x10, dataSeed: 0x40);

        var authentic = NewMachineWithTape(cas, TimingPolicy.Authentic);
        SetupTransferVars(authentic, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(authentic, CasReadEntry);
        RunUntilHalted(authentic, 20_000_000);

        var turbo = NewMachineWithTape(cas, TimingPolicy.Turbo);
        SetupTransferVars(turbo, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(turbo, CasReadEntry);
        RunUntilHalted(turbo, 1_000);

        Assert.Equal(0, authentic.Cpu.Reg.A); // sanity: authentic leg actually succeeded
        AssertMemoryEqual(authentic, turbo, 0x8000, 1024);
        AssertMemoryEqual(authentic, turbo, 0x8800, 32);
        Assert.Equal(authentic.Cpu.Reg.A, turbo.Cpu.Reg.A);
    }

    // ---- (b) Turbo save round-trips (authentic decode matches) -------------------------

    [Fact]
    public void TurboWrite_RoundTrips_ViaAuthenticDecode()
    {
        var blank = BuildCasRecord(headerSeed: 0, dataSeed: 0);
        var machine = NewMachineWithTape(blank, TimingPolicy.Turbo, writeProtect: false);

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0x50 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 1_000);

        Assert.Equal(0, machine.Cpu.Reg.A); // success

        // SaveTape() is the authentic phase-decode path (MiniTape.Save) — the same decoder
        // milestone 9a's CSAVE round-trip tests rely on.
        var savedCas = machine.Mdcr.SaveTape();
        Assert.NotNull(savedCas);
        Assert.Equal(header, savedCas![0x30..0x50]);
        Assert.Equal(data, savedCas[0x100..0x500]);
    }

    // ---- (c) Result registers/flags match the ROM's documented postconditions ----------

    [Fact]
    public void TurboRead_Success_SetsAZeroAndZFlag()
    {
        var cas = BuildCasRecord(headerSeed: 1, dataSeed: 2);
        var machine = NewMachineWithTape(cas, TimingPolicy.Turbo);
        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasReadEntry);
        RunUntilHalted(machine, 1_000);

        Assert.Equal(0, machine.Cpu.Reg.A);
        Assert.NotEqual(0, machine.Cpu.Reg.F & Alu.ZF);
    }

    [Fact]
    public void TurboRead_NoCassette_SetsErrorA_AndClearsZFlag()
    {
        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Turbo; // no tape inserted
        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasReadEntry);
        RunUntilHalted(machine, 1_000);

        Assert.Equal((byte)'A', machine.Cpu.Reg.A);
        Assert.Equal(0, machine.Cpu.Reg.F & Alu.ZF);
    }

    [Fact]
    public void TurboWrite_WriteProtected_SetsErrorG_AndClearsZFlag()
    {
        var cas = BuildCasRecord(headerSeed: 0, dataSeed: 0);
        var machine = NewMachineWithTape(cas, TimingPolicy.Turbo, writeProtect: true);
        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 1_000);

        Assert.Equal((byte)'G', machine.Cpu.Reg.A);
        Assert.Equal(0, machine.Cpu.Reg.F & Alu.ZF);
    }

    // ---- (d) Authentic mode never fires the trap ---------------------------------------

    [Fact]
    public void AuthenticMode_NeverShortCircuits_UnlikeTurbo()
    {
        var cas = BuildCasRecord(headerSeed: 0, dataSeed: 0);

        var authentic = NewMachineWithTape(cas, TimingPolicy.Authentic);
        SetupTransferVars(authentic, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(authentic, CasReadEntry);
        for (var i = 0; i < 100; i++) authentic.Tick();
        Assert.NotEqual((ushort)(ReturnMarker + 1), authentic.Cpu.Reg.PC); // still deep inside the real routine

        var turbo = NewMachineWithTape(cas, TimingPolicy.Turbo);
        SetupTransferVars(turbo, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(turbo, CasReadEntry);
        for (var i = 0; i < 100; i++) turbo.Tick();
        Assert.Equal((ushort)(ReturnMarker + 1), turbo.Cpu.Reg.PC); // trap resolved instantly
    }

    // ---- Helpers -------------------------------------------------------------------

    private static byte[] BuildCasRecord(byte headerSeed, byte dataSeed)
    {
        var record = new byte[1280];
        for (var i = 0; i < 32; i++) record[0x30 + i] = (byte)(headerSeed + i);
        for (var i = 0; i < 1024; i++) record[0x100 + i] = (byte)(dataSeed + i);
        return record;
    }

    private static Machine NewMachineWithTape(byte[] cas, TimingPolicy policy, bool writeProtect = true)
    {
        var machine = new Machine();
        machine.Mdcr.Policy = policy;
        machine.Mdcr.InsertTape(cas);
        // Write-protect now defaults from the file itself (unset → writable); set it live
        // via the same host-side control the UI's toggle uses (machine CLAUDE.md §17).
        machine.Mdcr.SetWriteProtected(writeProtect);
        return machine;
    }

    private static void SetupTransferVars(
        Machine machine, ushort transfer, ushort fileLength, ushort recordLength, ushort des1)
    {
        WriteWord(machine, Transfer, transfer);
        WriteWord(machine, FileLength, fileLength);
        WriteWord(machine, RecordLength, recordLength);
        WriteWord(machine, Des1, des1);
        WriteWord(machine, DesLength, 0x0020); // fixed by the real dispatcher (knowncascommand)
    }

    private static void WriteWord(Machine machine, ushort address, ushort value)
    {
        machine.Memory.Write(address, (byte)(value & 0xFF));
        machine.Memory.Write((ushort)(address + 1), (byte)(value >> 8));
    }

    private static void LoadBytes(Machine machine, ushort address, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            machine.Memory.Write((ushort)(address + i), bytes[i]);
    }

    /// <summary>Writes the bootstrap (LD SP,Stack / LD HL,ReturnMarker / PUSH HL / JP
    /// entryPoint) into RAM, puts a HALT at <see cref="ReturnMarker"/>, and runs the machine
    /// until PC reaches <paramref name="entryPoint"/> — i.e. right before the CPU would fetch
    /// the first opcode there, the exact point the turbo trap (and an exec breakpoint) checks.</summary>
    private static void StartCassetteEntry(Machine machine, ushort entryPoint)
    {
        LoadBytes(machine, Bootstrap, new byte[]
        {
            0x31, (byte)(Stack & 0xFF), (byte)(Stack >> 8),               // LD SP,Stack
            0x21, (byte)(ReturnMarker & 0xFF), (byte)(ReturnMarker >> 8), // LD HL,ReturnMarker
            0xE5,                                                        // PUSH HL
            0xC3, (byte)(entryPoint & 0xFF), (byte)(entryPoint >> 8),     // JP entryPoint
        });
        machine.Memory.Write(ReturnMarker, 0x76); // HALT

        machine.Cpu.Reg.PC = Bootstrap;

        for (var i = 0; i < 100; i++)
        {
            if (machine.Cpu.Reg.PC == entryPoint) return;
            machine.Tick();
        }
        Assert.Fail($"bootstrap did not reach 0x{entryPoint:X4} within 100 ticks " +
                    $"(PC=0x{machine.Cpu.Reg.PC:X4})");
    }

    /// <summary>Ticks until PC reaches <see cref="ReturnMarker"/>+1 (HALT's M1 already
    /// advanced PC past it — the CPU stays there indefinitely while halted).</summary>
    private static void RunUntilHalted(Machine machine, int tickLimit)
    {
        var haltedAt = (ushort)(ReturnMarker + 1);
        for (var i = 0; i < tickLimit; i++)
        {
            if (machine.Cpu.Reg.PC == haltedAt) return;
            machine.Tick();
        }
        Assert.Fail($"cassette entry did not halt within {tickLimit:N0} ticks " +
                    $"(PC=0x{machine.Cpu.Reg.PC:X4})");
    }

    private static void AssertMemoryEqual(Machine a, Machine b, ushort address, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var addr = (ushort)(address + i);
            Assert.True(a.Memory.Read(addr) == b.Memory.Read(addr),
                $"mismatch at 0x{addr:X4}: authentic=0x{a.Memory.Read(addr):X2} turbo=0x{b.Memory.Read(addr):X2}");
        }
    }
}

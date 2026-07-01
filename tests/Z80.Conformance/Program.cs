using System.Text;
using Z80Cpu = Z80.Core.Z80;
using PinBits = Z80.Core.Pins;

// ---------------------------------------------------------------------------
// Minimal CP/M harness for ZEXDOC / ZEXALL
//
// Usage:
//   dotnet run --project tests/Z80.Conformance -- zexdoc
//   dotnet run --project tests/Z80.Conformance -- zexall
//
// ROM files (zexdoc.com, zexall.com) must be placed in:
//   tests/Z80.Conformance/roms/
//
// CP/M model:
//   0x0100  .com entry point (program loaded here)
//   0x0005  BDOS trap (intercepted before fetch; simulates RET after servicing)
//   0x0000  Warm-boot trap (intercepted before fetch; terminates the run)
//
// BDOS functions supported:
//   C=2  Write char in E to stdout
//   C=9  Write $-terminated string at DE to stdout
// ---------------------------------------------------------------------------

if (args.Length == 0 || (args[0] != "zexdoc" && args[0] != "zexall"))
{
    Console.Error.WriteLine("Usage: Z80.Conformance <zexdoc|zexall>");
    return 1;
}

var romName = args[0];
var romsDir = Path.Combine(AppContext.BaseDirectory, "roms");
var comPath = Path.Combine(romsDir, romName + ".com");

if (!File.Exists(comPath))
{
    Console.Error.WriteLine($"ROM not found: {comPath}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Download the CP/M exerciser binaries and place them in:");
    Console.Error.WriteLine($"  {romsDir}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Sources:");
    Console.Error.WriteLine("  https://mdfs.net/Software/Z80/Exerciser/");
    Console.Error.WriteLine("  https://github.com/anotherlin/z80emu/tree/master/testfiles");
    return 1;
}

// --- Memory ------------------------------------------------------------------

var mem = new byte[65536];
var rom = File.ReadAllBytes(comPath);
if (rom.Length > 65536 - 0x0100)
{
    Console.Error.WriteLine($"ROM too large: {rom.Length} bytes");
    return 1;
}
Array.Copy(rom, 0, mem, 0x0100, rom.Length);

// Put a JP 0x0000 at warm-boot address so it loops visibly if ever executed;
// BDOS address gets a RET so nothing breaks if we ever miss the interception.
mem[0x0000] = 0xC3; mem[0x0001] = 0x00; mem[0x0002] = 0x00; // JP 0x0000
mem[0x0005] = 0xC9;                                           // RET

// --- CPU ---------------------------------------------------------------------

var cpu = new Z80Cpu();
cpu.Reg.PC = 0x0100;
cpu.Reg.SP = 0xF000; // ZEXALL sets its own SP; this is just a safe initial value

ulong pins = 0;
bool running = true;
var output = new StringBuilder(4096);

// --- Host loop ---------------------------------------------------------------

while (running)
{
    // Instruction boundary: check PC before we start a new M1 fetch.
    if (cpu.AtInstructionBoundary)
    {
        switch (cpu.Reg.PC)
        {
            case 0x0000: // Warm boot — program signals completion
                running = false;
                continue;

            case 0x0005: // BDOS call — intercept and simulate a RET
                ServiceBdos(cpu, mem, output);
                // Flush output on every newline so the user sees each test result live.
                if (output.Length > 0 &&
                    (output[output.Length - 1] == '\n' || output.Length > 512))
                    FlushOutput(output);
                continue; // Don't Step(); PC is already the return address
        }
    }

    pins = cpu.Step(pins);

    // Service the memory bus (MREQ).
    if ((pins & PinBits.MREQ) != 0)
    {
        var addr = PinBits.GetAddress(pins);
        if ((pins & PinBits.RD) != 0)
            pins = PinBits.SetData(pins, mem[addr]);
        else if ((pins & PinBits.WR) != 0)
            mem[addr] = PinBits.GetData(pins);
    }
    // Service I/O bus (IORQ): return 0xFF for reads, ignore writes.
    // Guard M1 to avoid triggering on the int-ack M-cycle (M1+IORQ).
    else if ((pins & PinBits.IORQ) != 0 && (pins & PinBits.RD) != 0
                                         && (pins & PinBits.M1) == 0)
        pins = PinBits.SetData(pins, 0xFF);
}

FlushOutput(output);
Console.WriteLine(); // Ensure a clean final newline
return 0;

// --- Helpers -----------------------------------------------------------------

static void FlushOutput(StringBuilder sb)
{
    if (sb.Length > 0)
    {
        Console.Write(sb);
        sb.Clear();
    }
}

static void ServiceBdos(Z80Cpu cpu, byte[] mem, StringBuilder output)
{
    // Simulate the RET that a real BDOS would execute: pop the return address
    // that the CALL 0x0005 pushed onto the stack and redirect PC there.
    var sp = cpu.Reg.SP;
    var retAddr = (ushort)(mem[sp] | (mem[(ushort)(sp + 1)] << 8));
    cpu.Reg.SP = (ushort)(sp + 2);
    cpu.Reg.PC = retAddr;

    switch (cpu.Reg.C)
    {
        case 2: // Write character in E
            AppendChar(output, cpu.Reg.E);
            break;

        case 9: // Write $-terminated string starting at DE
            var addr = cpu.Reg.DE;
            while (true)
            {
                var b = mem[addr++];
                if (b == (byte)'$') break;
                AppendChar(output, b);
            }
            break;
    }
}

static void AppendChar(StringBuilder sb, byte b)
{
    // Translate CP/M line endings (\r\n → \n for clean console output.
    if (b == '\r') return;
    sb.Append((char)b);
}

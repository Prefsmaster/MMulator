using P2000.Machine.Memory;
using Z80.Core;

namespace P2000.Machine.Devices.Cassette;

/// <summary>
/// Turbo ROM-trap for the monitor ROM's cassette load/save entry points (project CLAUDE.md
/// §13.18; trap addresses and calling convention confirmed from a commented monitor-ROM
/// disassembly — <c>MonitorRom.sym</c> / <c>Cassette.asm</c> / <c>Startup.asm</c>).
///
/// Traps <c>cas_Read</c> (0x0552) and <c>cas_Write</c> (0x057A) — NOT the lower
/// <c>cas_block_read</c>/<c>cas_block_write</c> — because these two own the WHOLE multi-block
/// transfer loop themselves (Cassette.asm's own <c>block_counter</c> loop) and are entered
/// with a clean RAM contract already established by the "cassette" jump-table dispatcher
/// (Cassette.asm <c>knowncascommand</c>) before <c>do_cas_jump</c> jumps here:
/// <list type="bullet">
/// <item><c>transfer</c> (0x6030): data source/destination address for block 0; block N's
/// data is always 1024 bytes further (<c>get_block_parameters</c>' <c>next_block</c> chaining),
/// regardless of how many bytes in that block are "real" vs padding.</item>
/// <item><c>file_length</c> (0x6032): total byte length — determines the block count,
/// ceil(file_length/1024) with a minimum of 1 (<c>get_length_blocks</c>).</item>
/// <item><c>record_length</c> (0x6034): total REAL (non-padding) byte length — determines
/// each block's <c>valid_length</c> (<c>get_block_parameters</c>); the LAST block is the only
/// partial one.</item>
/// <item><c>des1</c> (0x6068): header source/destination address — every block's 32-byte
/// header is read/written here EVERY iteration (<c>load_block</c>/<c>save_block</c>),
/// overwriting the previous block's (the on-tape header is always 32 bytes; <c>des_length</c>
/// at 0x606A is architecturally fixed at 0x20 and is not separately read here).</item>
/// </list>
///
/// Both routines are entered via <c>jp (hl)</c> with the return address
/// (<c>cas_command_return</c>) already pushed onto the real stack by <c>do_cas_jump</c> —
/// functionally identical to a <c>CALL</c>. The trap performs the whole transfer in C#,
/// writes <c>cassette_error</c> (0x6017), and simulates the routine's own final <c>RET</c>
/// (pop PC off the real stack, matching A/Z to what that RET's own operands would produce).
/// It does NOT need to replicate <c>cas_command_return</c>'s cleanup (motor off,
/// <c>enablekey</c>, register restore) — that code runs completely normally afterwards,
/// since the trap only ever intercepts execution AT the cas_Read/cas_Write entry point
/// itself, never past it.
///
/// Only active under <see cref="TimingPolicy.Turbo"/> (<see cref="Machine.Mdcr"/>'s policy) —
/// Authentic mode never checks these addresses (project CLAUDE.md §13.18 test (d)).
/// </summary>
internal static class CassetteTurboTrap
{
    public const ushort CasReadEntry = 0x0552;
    public const ushort CasWriteEntry = 0x057A;

    // RAM variables (MonitorRom.sym; cross-checked against Cassette.asm's own reads/writes).
    private const ushort Transfer = 0x6030;
    private const ushort FileLength = 0x6032;
    private const ushort RecordLength = 0x6034;
    private const ushort Des1 = 0x6068;
    private const ushort CassetteError = 0x6017;

    private const int BlockSize = 1024;
    private const int HeaderSize = 32;

    // cassette_error codes (Cassette.asm's own comments document these letters).
    private const byte NoCassette = (byte)'A';
    private const byte WriteProtectedError = (byte)'G';
    private const byte EndOfTapeDuringWrite = (byte)'E';
    private const byte EndOfTapeDuringRead = (byte)'L';

    /// <summary>Called at an instruction boundary, before <c>Cpu.Step()</c> executes the
    /// opcode at the current PC (same point exec breakpoints check). Returns true if it
    /// handled a trapped entry point and simulated the return — the caller must skip
    /// stepping the CPU this tick when true.</summary>
    public static bool TryHandle(Machine machine)
    {
        var pc = machine.Cpu.Reg.PC;
        if (pc == CasReadEntry) { Read(machine); return true; }
        if (pc == CasWriteEntry) { Write(machine); return true; }
        return false;
    }

    private static void Read(Machine machine)
    {
        if (!machine.Mdcr.HasTape) { Return(machine, NoCassette); return; }

        var mem = machine.Memory;
        var transfer = ReadWord(mem, Transfer);
        var fileLength = ReadWord(mem, FileLength);
        int remaining = ReadWord(mem, RecordLength);
        var des1 = ReadWord(mem, Des1);

        var blockCount = CountBlocks(fileLength);

        for (var i = 0; i < blockCount; i++)
        {
            var validLength = NextValidLength(ref remaining);

            if (!machine.Mdcr.TryReadBlockAtHead(out var header, out var data))
            {
                Return(machine, EndOfTapeDuringRead);
                return;
            }

            WriteBytes(mem, des1, header, HeaderSize);
            var dest = (ushort)(transfer + i * BlockSize);
            WriteBytes(mem, dest, data, validLength);
        }

        Return(machine, 0);
    }

    private static void Write(Machine machine)
    {
        if (!machine.Mdcr.HasTape) { Return(machine, NoCassette); return; }
        if (machine.Mdcr.IsWriteProtected) { Return(machine, WriteProtectedError); return; }

        var mem = machine.Memory;
        var transfer = ReadWord(mem, Transfer);
        var fileLength = ReadWord(mem, FileLength);
        int remaining = ReadWord(mem, RecordLength);
        var des1 = ReadWord(mem, Des1);

        var blockCount = CountBlocks(fileLength);

        for (var i = 0; i < blockCount; i++)
        {
            var validLength = NextValidLength(ref remaining);

            var header = ReadBytes(mem, des1, HeaderSize);
            var data = new byte[BlockSize]; // zero-padded — matches fetch_padding_byte's 0x00 fill
            var src = (ushort)(transfer + i * BlockSize);
            for (var b = 0; b < validLength; b++)
                data[b] = mem.Read((ushort)(src + b));

            if (!machine.Mdcr.WriteBlockAtHead(header, data))
            {
                Return(machine, EndOfTapeDuringWrite);
                return;
            }
        }

        Return(machine, 0);
    }

    /// <summary>ceil(fileLength/1024) with a minimum of 1 block — replicates
    /// Cassette.asm's <c>get_length_blocks</c> loop (repeated 16-bit subtract, stops on the
    /// first borrow) exactly, including its behaviour for fileLength==0.</summary>
    private static int CountBlocks(int fileLength)
    {
        var hl = (fileLength - 1) & 0xFFFF;
        var count = 0;
        int result;
        do
        {
            count++;
            result = hl - BlockSize;
            hl = result & 0xFFFF;
        } while (result >= 0);
        return count;
    }

    /// <summary>Computes this block's real (non-padding) byte count and updates
    /// <paramref name="remaining"/> — replicates <c>get_block_parameters</c>'s min(remaining,
    /// 1024) split exactly (only the last block can be partial).</summary>
    private static int NextValidLength(ref int remaining)
    {
        if (remaining >= BlockSize)
        {
            remaining -= BlockSize;
            return BlockSize;
        }
        var valid = remaining;
        remaining = 0;
        return valid;
    }

    /// <summary>Pops the return address <c>do_cas_jump</c> pushed (<c>cas_command_return</c>)
    /// off the real stack and jumps there — the trap's equivalent of the routine's own final
    /// <c>RET</c>. Sets A/Z exactly as that RET's own operands would (cassette_error==0 →
    /// A=0, Z set; nonzero → A=cassette_error, Z clear) even though <c>cas_command_return</c>
    /// immediately re-derives both from <c>cassette_error</c> anyway — matching the ROM's
    /// documented postcondition instead of relying on that.</summary>
    private static void Return(Machine machine, byte cassetteError)
    {
        machine.Memory.Write(CassetteError, cassetteError);

        var sp = machine.Cpu.Reg.SP;
        var lo = machine.Memory.Read(sp);
        var hi = machine.Memory.Read((ushort)(sp + 1));
        machine.Cpu.Reg.SP = (ushort)(sp + 2);
        machine.Cpu.Reg.PC = (ushort)(lo | (hi << 8));

        machine.Cpu.Reg.A = cassetteError;
        machine.Cpu.Reg.F = cassetteError == 0
            ? (byte)(machine.Cpu.Reg.F | Alu.ZF)
            : (byte)(machine.Cpu.Reg.F & ~Alu.ZF);
    }

    private static ushort ReadWord(PageTable mem, ushort address)
        => (ushort)(mem.Read(address) | (mem.Read((ushort)(address + 1)) << 8));

    private static void WriteBytes(PageTable mem, ushort dest, byte[] source, int count)
    {
        for (var i = 0; i < count; i++)
            mem.Write((ushort)(dest + i), source[i]);
    }

    private static byte[] ReadBytes(PageTable mem, ushort address, int count)
    {
        var buf = new byte[count];
        for (var i = 0; i < count; i++)
            buf[i] = mem.Read((ushort)(address + i));
        return buf;
    }
}

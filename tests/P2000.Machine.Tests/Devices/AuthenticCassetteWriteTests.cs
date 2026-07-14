using P2000.Machine.Devices.Cassette;
using Machine = P2000.Machine.Machine;

namespace P2000.Machine.Tests.Devices;

/// <summary>
/// Drives the REAL <c>cas_Write</c>/<c>cas_Read</c> ROM entry points under
/// <see cref="TimingPolicy.Authentic"/> — the actual bit-engine BASIC/CSAVE uses — end to end.
/// Unlike <see cref="CassetteTurboTrapTests"/> (which only exercises Turbo for writes and
/// Authentic only for reads), nothing in this suite previously drove a real authentic CSAVE
/// through the ROM. Added 2026-07-14 chasing a live-app report: "CSAVE consistently fails
/// with 'Cassette fout N'". Root cause found and fixed here (machine CLAUDE.md §17):
/// <see cref="MiniTape"/>'s blank-tape fill was deterministic pseudo-noise, which defeated
/// `cas_Write`'s own forward scan for "nothing recorded here yet" — noise never presents that
/// signal, so the scan ran to the physical end of the tape before giving up with an EOT
/// error. Blank tape is now silence (matches what BASIC's own "Tape init" command produces).
/// </summary>
public class AuthenticCassetteWriteTests
{
    private const ushort CasReadEntry = 0x0552;
    private const ushort CasWriteEntry = 0x057A;

    private const ushort Transfer = 0x6030;
    private const ushort FileLength = 0x6032;
    private const ushort RecordLength = 0x6034;
    private const ushort Des1 = 0x6068;
    private const ushort DesLength = 0x606A;

    private const ushort Bootstrap = 0x9000;
    private const ushort ReturnMarker = 0x9010;
    private const ushort Stack = 0x9100;

    [Fact]
    public void AuthenticWrite_ToBlankUnprotectedTape_Succeeds()
    {
        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertBlankTape(); // unprotected by construction

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0x50 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);

        Assert.Equal(0, machine.Cpu.Reg.A); // 0 = success; nonzero = ROM error char (e.g. 'N'/'E')
    }

    [Fact]
    public void AuthenticWrite_ThenAuthenticRead_RoundTrips()
    {
        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertBlankTape();

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0xA0 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i ^ 0x55);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);
        Assert.Equal(0, machine.Cpu.Reg.A); // write must succeed first

        // cas_Write does its own internal write+verify pass and leaves the head PAST the
        // block it just wrote — a fresh cas_Read from there would search forward into
        // genuinely blank tape and correctly find nothing. Rewind by ejecting and
        // re-inserting the just-saved tape (mirrors a real "eject, reinsert" round trip;
        // there is no other rewind entry point — see project CLAUDE.md §17 "Rewind" note).
        var savedCas = machine.Mdcr.SaveTape();
        Assert.NotNull(savedCas);
        Assert.Equal(1280, savedCas!.Length);
        Assert.Equal(header, savedCas[0x30..0x50]);
        Assert.Equal(data, savedCas[0x100..0x500]);

        // Read back via a FRESH machine/mount (a real "save to file, load it in a new
        // session" round trip) rather than reusing the same machine cas_Write just ran on —
        // reusing the same machine leaves other ROM working RAM in a state that isn't
        // equivalent to a clean mount (see the class doc's "same-machine" finding below).
        var reader = new Machine();
        reader.Mdcr.Policy = TimingPolicy.Authentic;
        reader.Mdcr.InsertTape(savedCas);

        SetupTransferVars(reader, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(reader, CasReadEntry);
        RunUntilHalted(reader, 20_000_000);

        Assert.Equal(0, reader.Cpu.Reg.A); // 0 = success; 'N' = not found
        for (var i = 0; i < 1024; i++)
            Assert.Equal(data[i], reader.Memory.Read((ushort)(0x8000 + i)));
        for (var i = 0; i < 32; i++)
            Assert.Equal(header[i], reader.Memory.Read((ushort)(0x8800 + i)));
    }

    [Fact]
    public void AuthenticWrite_AppendOntoMountedTape_Succeeds()
    {
        // "Adding a file to a tape" from the owner's report: mount a tape that already has one
        // real recorded block, then cas_Write a second, different file.
        var existing = new byte[1280];
        for (var i = 0; i < 32; i++) existing[0x30 + i] = (byte)(0x11 + i);
        for (var i = 0; i < 1024; i++) existing[0x100 + i] = (byte)(0x22 + i);

        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertTape(existing); // unprotected by default (no protect byte set)

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0x50 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);

        Assert.Equal(0, machine.Cpu.Reg.A);
    }

    [Fact]
    public void AuthenticWrite_OverwriteSameHeader_Succeeds()
    {
        // "Overwriting the file with itself" from the owner's report: mount a tape with one
        // real block, then cas_Write using the SAME header bytes (same name).
        var existing = new byte[1280];
        var headerBytes = new byte[32];
        for (var i = 0; i < 32; i++) headerBytes[i] = (byte)(0x11 + i);
        Array.Copy(headerBytes, 0, existing, 0x30, 32);
        for (var i = 0; i < 1024; i++) existing[0x100 + i] = (byte)(0x22 + i);

        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertTape(existing);

        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, headerBytes); // SAME header as the existing block

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);

        Assert.Equal(0, machine.Cpu.Reg.A);
    }

    [Fact]
    public void AuthenticWrite_AppendOntoRealMultiBlockAsset_Succeeds()
    {
        // Real asset (41 blocks), not a synthetic 1-block tape.
        var path = FindRepoFile("assets/Basic Demo cassette (kant A).cas");
        var existing = File.ReadAllBytes(path);

        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertTape(existing);

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0x90 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);

        Assert.Equal(0, machine.Cpu.Reg.A);
    }

    [Fact]
    public void AuthenticCload_ThenCsave_FromWhereverHeadEndsUp_Succeeds()
    {
        // Replicates the owner's actual repro: mount a real tape, CLOAD an existing program
        // (moving the head forward to wherever the ROM leaves it after a read), THEN CSAVE —
        // without resetting position in between.
        var path = FindRepoFile("assets/Basic Demo cassette (kant A).cas");
        var existing = File.ReadAllBytes(path);

        var machine = new Machine();
        machine.Mdcr.Policy = TimingPolicy.Authentic;
        machine.Mdcr.InsertTape(existing);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasReadEntry);
        RunUntilHalted(machine, 20_000_000);
        Assert.Equal(0, machine.Cpu.Reg.A); // CLOAD must succeed first, or this isn't the same repro

        var header = new byte[32];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(0x90 + i);
        var data = new byte[1024];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        LoadBytes(machine, 0x8000, data);
        LoadBytes(machine, 0x8800, header);

        SetupTransferVars(machine, transfer: 0x8000, fileLength: 1024, recordLength: 1024, des1: 0x8800);
        StartCassetteEntry(machine, CasWriteEntry);
        RunUntilHalted(machine, 20_000_000);

        Assert.Equal(0, machine.Cpu.Reg.A);
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}");
    }

    // ---- Helpers (mirrors CassetteTurboTrapTests) ---------------------------------------

    private static void SetupTransferVars(
        Machine machine, ushort transfer, ushort fileLength, ushort recordLength, ushort des1)
    {
        WriteWord(machine, Transfer, transfer);
        WriteWord(machine, FileLength, fileLength);
        WriteWord(machine, RecordLength, recordLength);
        WriteWord(machine, Des1, des1);
        WriteWord(machine, DesLength, 0x0020);
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
    /// entryPoint) into RAM, puts a JR -2 spin (NOT HALT — HALT's internal _halted flag only
    /// clears on a real interrupt, which would break a second StartCassetteEntry call reusing
    /// the same machine, since setting PC directly does not un-halt the core) at
    /// <see cref="ReturnMarker"/>, and runs the machine until PC reaches
    /// <paramref name="entryPoint"/>.</summary>
    private static void StartCassetteEntry(Machine machine, ushort entryPoint)
    {
        LoadBytes(machine, Bootstrap, new byte[]
        {
            0x31, (byte)(Stack & 0xFF), (byte)(Stack >> 8),               // LD SP,Stack
            0x21, (byte)(ReturnMarker & 0xFF), (byte)(ReturnMarker >> 8), // LD HL,ReturnMarker
            0xE5,                                                        // PUSH HL
            0xC3, (byte)(entryPoint & 0xFF), (byte)(entryPoint >> 8),     // JP entryPoint
        });
        machine.Memory.Write(ReturnMarker, 0x18);
        machine.Memory.Write((ushort)(ReturnMarker + 1), 0xFE); // JR -2

        machine.Cpu.Reg.PC = Bootstrap;

        for (var i = 0; i < 100; i++)
        {
            if (machine.Cpu.Reg.PC == entryPoint) return;
            machine.Tick();
        }
        Assert.Fail($"bootstrap did not reach 0x{entryPoint:X4} within 100 ticks " +
                    $"(PC=0x{machine.Cpu.Reg.PC:X4})");
    }

    /// <summary>Ticks until PC reaches <see cref="ReturnMarker"/> (the JR -2 spin).</summary>
    private static void RunUntilHalted(Machine machine, int tickLimit)
    {
        for (var i = 0; i < tickLimit; i++)
        {
            if (machine.Cpu.Reg.PC == ReturnMarker) return;
            machine.Tick();
        }
        Assert.Fail($"cassette entry did not halt within {tickLimit:N0} ticks " +
                    $"(PC=0x{machine.Cpu.Reg.PC:X4})");
    }
}

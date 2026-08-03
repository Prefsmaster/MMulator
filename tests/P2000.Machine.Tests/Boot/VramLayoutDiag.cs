using P2000.Machine.Contention;
using P2000.Machine.Devices.Cassette;
using P2000.Machine.Devices.Fdc;
using P2000.Machine.Memory;
using Xunit.Abstractions;

namespace P2000.Machine.Tests.Boot;

/// <summary>
/// Diagnostic-only: dumps raw VRAM (0x5000-0x57FF, all 1920 bytes) after the SYSTEM B repro's
/// "Disk I/O error"+"Ok" print completes, to find the real byte layout empirically rather than
/// assuming a stride — <c>Video.cs</c>'s own <c>BufferColumns=80</c>/<c>PanX</c> windowing
/// contradicts the 40-stride <c>row*40+col</c> formula every screen-reading test in this project
/// (including this session's own diagnostics) has been using.
/// </summary>
public class VramLayoutDiag
{
    private readonly ITestOutputHelper _output;
    public VramLayoutDiag(ITestOutputHelper output) => _output = output;

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "MMulator.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("repo root not found");
    }

    private static void Ticks(Machine machine, int fields)
    {
        for (var f = 0; f < fields; f++)
            for (var t = 0; t < VideoFetchUnit.TStatesPerField; t++)
                machine.Tick();
    }

    private static (bool Found, string Screen) WaitForScreenContains(Machine machine, string needle, int maxFields)
    {
        for (var f = 0; f < maxFields; f++)
        {
            Ticks(machine, 1);
            var screen = SnapshotScreenText40Stride(machine);
            if (screen.Contains(needle)) return (true, screen);
        }
        return (false, SnapshotScreenText40Stride(machine));
    }

    private static string SnapshotScreenText40Stride(Machine m)
    {
        var sb = new System.Text.StringBuilder();
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 40; col++)
            {
                var b = m.Memory.Read((ushort)(PageTable.VideoRamStart + row * 40 + col));
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? ' ' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string SnapshotScreenText80Stride(Machine m)
    {
        var sb = new System.Text.StringBuilder();
        for (var row = 0; row < 24; row++)
        {
            for (var col = 0; col < 40; col++)
            {
                var b = m.Memory.Read((ushort)(PageTable.VideoRamStart + row * 80 + col));
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0 ? ' ' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, (int Row, int Col, bool Shift)> CharMap = new()
    {
        ['L'] = (8, 1, false), ['O'] = (6, 1, false), ['A'] = (4, 2, false), ['D'] = (1, 4, false),
        [' '] = (2, 1, false), ['"'] = (7, 7, true), ['V'] = (3, 7, false), ['R'] = (4, 7, false),
        ['G'] = (1, 5, false), ['B'] = (3, 5, false), [':'] = (8, 7, false), ['I'] = (8, 6, false),
        ['N'] = (3, 1, false), ['F'] = (1, 7, false), ['T'] = (4, 5, false), ['E'] = (4, 4, false),
        ['S'] = (1, 3, false), ['Y'] = (4, 1, false), ['U'] = (4, 6, false), ['M'] = (3, 6, false),
    };

    private const int EnterRow = 6;
    private const int EnterCol = 4;

    private static void TypeChar(Machine machine, int row, int col, bool shift)
    {
        if (shift) { machine.Keyboard.SetKey(9, 0, true); Ticks(machine, 5); }
        machine.Keyboard.SetKey(row, col, true);
        Ticks(machine, 8);
        machine.Keyboard.SetKey(row, col, false);
        Ticks(machine, 5);
        if (shift) { machine.Keyboard.SetKey(9, 0, false); Ticks(machine, 5); }
        Ticks(machine, 5);
    }

    private static void TypeString(Machine machine, string text)
    {
        foreach (var ch in text) { var (row, col, shift) = CharMap[ch]; TypeChar(machine, row, col, shift); }
    }

    private static void PressEnter(Machine machine) => TypeChar(machine, EnterRow, EnterCol, false);

    private static void WaitForReadyPrompt40Stride(Machine machine, int maxFields = 5000)
    {
        for (var f = 0; f < maxFields; f++)
        {
            Ticks(machine, 1);
            var lastLine = SnapshotScreenText40Stride(machine).Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (lastLine == "Ok") return;
        }
    }

    [Fact]
    public void Diag_RESET_ThenSYSTEMB_RawVramDumpAfterPrintCompletes()
    {
        var repoRoot = FindRepoRoot();
        var cartridgePath = Path.Combine(repoRoot, "assets", "Basic-24.bin");
        var bootFloppyPath = Path.Combine(repoRoot, "assets", "Disks", "diskbasic_1.6uk.dsk");
        var secondDiskPath = Path.Combine(repoRoot, "assets", "Disks", "volorg.dsk");

        var machine = new Machine(new MachineConfig
        {
            Board = InternalBoard.FloppyRam,
            RamVariant = RamVariant.T102,
            Slot1CartridgePath = cartridgePath,
            FloppyDrives = new[]
            {
                new FloppyDriveConfig { DriveIndex = 1, Capacity = 35, Sides = DiskSides.Single, ImagePath = bootFloppyPath },
                new FloppyDriveConfig { DriveIndex = 2, Capacity = 35, Sides = DiskSides.Single, ImagePath = secondDiskPath },
            },
        });
        machine.Fdc!.Policy = TimingPolicy.Turbo;

        for (var i = 0; i < 40_000_000; i++)
        {
            machine.Tick();
            if (machine.Cpu.Reg.PC is >= PageTable.CartridgeStart and <= PageTable.CartridgeEnd) break;
        }
        WaitForScreenContains(machine, "many files", 3000);
        PressEnter(machine);
        WaitForScreenContains(machine, "Runtime support", 3000);
        PressEnter(machine);
        WaitForScreenContains(machine, "Ok", 3000);
        Ticks(machine, 20);

        TypeString(machine, "RESET");
        PressEnter(machine);
        WaitForReadyPrompt40Stride(machine);
        Ticks(machine, 100);

        TypeString(machine, "SYSTEM B");
        PressEnter(machine);

        // Let the whole print sequence (confirmed via the earlier character trace to complete
        // by ~t=5.46M T-states) finish with a large margin, then stop touching the keyboard.
        for (var t = 0; t < 30_000_000; t++) machine.Tick();

        _output.WriteLine("=== Raw VRAM dump (0x5000-0x57FF), 80 bytes per line, hex + ASCII ===");
        for (var rowStart = 0; rowStart < 1920; rowStart += 80)
        {
            var bytes = new byte[80];
            for (var i = 0; i < 80; i++) bytes[i] = machine.Memory.Read((ushort)(PageTable.VideoRamStart + rowStart + i));
            var ascii = new string(Array.ConvertAll(bytes, b => b is >= 0x20 and < 0x7F ? (char)b : (b == 0 ? ' ' : '.')));
            _output.WriteLine($"0x{PageTable.VideoRamStart + rowStart:X4}: {ascii}");
        }

        _output.WriteLine("");
        _output.WriteLine("=== 40-stride read (existing convention) ===");
        _output.WriteLine(SnapshotScreenText40Stride(machine));
        _output.WriteLine("=== 80-stride read (Video.cs's own BufferColumns convention, PanX=0) ===");
        _output.WriteLine(SnapshotScreenText80Stride(machine));

        _output.WriteLine($"Cursor video-memory address &H60B1/&H60B2: 0x{machine.Memory.Read(0x60B2):X2}{machine.Memory.Read(0x60B1):X2}");
        _output.WriteLine($"Logical cursor position &H66C3: 0x{machine.Memory.Read(0x66C3):X2}");
    }
}

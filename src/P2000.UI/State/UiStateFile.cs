using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;

namespace P2000.UI.State;

/// <summary>
/// One window's position/size, plus whether it's currently open — the common shape every
/// satellite window captures (project CLAUDE.md milestone 14b).
/// </summary>
public class WindowLayout
{
    public bool IsOpen { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>One memory watch window's layout plus the range/follow-register it was showing
/// (project CLAUDE.md milestone 14b; mirrors <c>MemoryWatchVm.BaseAddress</c>/<c>Length</c>/
/// <c>Follow</c>).</summary>
public sealed class MemoryWatchLayout : WindowLayout
{
    public ushort BaseAddress { get; set; }
    public int Length { get; set; } = 256;
    public string Follow { get; set; } = "None";
}

/// <summary>The debugger window's own layout plus its nested VRAM window and every open memory
/// watch window (project CLAUDE.md milestone 14b).</summary>
public sealed class DebuggerLayout
{
    public WindowLayout? Window { get; set; }
    public WindowLayout? Vram { get; set; }
    public bool VramShowHex { get; set; }
    public List<MemoryWatchLayout> MemoryWatches { get; set; } = new();
}

/// <summary>
/// Root <c>.uistate</c> payload — pure UI window-layout state (project CLAUDE.md milestone 14b;
/// reference doc §3a "a separate `.uistate` sidecar file, NOT embedded in `.state`"). NOT machine
/// state and NOT part of <c>.cfg</c>: which satellite windows are open, their positions, each
/// memory-watch window's configured range/follow-register, and the VRAM window's glyph/hex
/// toggle. Owned entirely by <c>P2000.UI</c> — <c>P2000.Machine</c> neither knows nor cares about
/// its contents.
/// </summary>
public sealed class UiStateData
{
    public int Version { get; set; } = UiStateFile.CurrentVersion;

    public WindowLayout? MainWindow { get; set; }
    public WindowLayout? CassetteDeck { get; set; }
    public WindowLayout? DiskDrive { get; set; }
    public WindowLayout? Config { get; set; }
    public WindowLayout? Keyboard { get; set; }
    public DebuggerLayout? Debugger { get; set; }
}

/// <summary>
/// Reads/writes the <c>.uistate</c> sidecar (project CLAUDE.md milestone 14b) — a JSON file
/// living alongside a <c>.state</c> file with the same base filename (<c>mygame.state</c> +
/// <c>mygame.uistate</c>), written/read only immediately after the EXISTING Save State / Load
/// State actions (ms.8) succeed. Own version field, own reject-by-returning-null discipline,
/// entirely independent of <c>MachineStateFile</c>'s — a version bump here never touches, and is
/// never triggered by, a machine device-block change.
///
/// <b>Best-effort by design:</b> <see cref="TryLoad"/> returns <c>null</c> on a missing file,
/// version mismatch, or any read/parse failure — the caller's job is to treat that as "default
/// window layout," never as a failed <c>.state</c> load.
/// </summary>
public static class UiStateFile
{
    /// <summary>Current <c>.uistate</c> format version. Bump when the shape of
    /// <see cref="UiStateData"/> (or any nested layout type) changes.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Derives the sidecar path from a <c>.state</c> file's path by swapping the
    /// extension — <c>mygame.state</c> → <c>mygame.uistate</c>.</summary>
    public static string SidecarPathFor(string statePath) =>
        Path.ChangeExtension(statePath, ".uistate");

    public static void Save(UiStateData data, string path)
    {
        data.Version = CurrentVersion;
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    /// <summary>Best-effort load — see the class doc comment. Never throws.</summary>
    public static UiStateData? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<UiStateData>(File.ReadAllText(path), JsonOptions);
            return data is { Version: CurrentVersion } ? data : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Window <-> WindowLayout helpers (shared by DisplayWindow/DebuggerWindow code-behind) --

    /// <summary>Captures a window's current open/position/size — <c>null</c> input (never
    /// constructed) captures as simply "closed."</summary>
    public static WindowLayout Capture(Window? window) => new()
    {
        IsOpen = window is { IsVisible: true },
        X = window?.Position.X ?? 0,
        Y = window?.Position.Y ?? 0,
        Width = window?.Width ?? 0,
        Height = window?.Height ?? 0,
    };

    /// <summary>Applies a captured position/size onto an already-shown window. No-op if either
    /// argument is missing, or if the captured size is degenerate (never actually measured).</summary>
    public static void Apply(Window? window, WindowLayout? layout)
    {
        if (window is null || layout is null) return;
        if (layout.Width > 0 && layout.Height > 0)
        {
            window.Width = layout.Width;
            window.Height = layout.Height;
        }
        window.Position = new PixelPoint(layout.X, layout.Y);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace P2000.UI.State;

/// <summary>
/// App-level startup preferences (project CLAUDE.md milestone 14c; reference doc §3a "RESOLVED —
/// startup configuration") — a FOURTH file type, distinct from <c>.cfg</c>/<c>.state</c>/
/// <c>.uistate</c>. Lives once per user install, not per saved session.
/// </summary>
public sealed class AppPreferences
{
    /// <summary>Path to the <c>.cfg</c> file to load and apply at startup in place of the bare
    /// default; <c>null</c> means "nothing remembered yet" (a fresh install).</summary>
    public string? StartupCfgPath { get; set; }

    /// <summary>When <c>true</c>, auto-remember (writing <see cref="AppPreferencesFile.LastSessionCfgPath"/>
    /// on quit) stops overwriting <see cref="StartupCfgPath"/> — the user has pinned a specific,
    /// separately-saved config as their permanent startup default instead.</summary>
    public bool StartupCfgIsPinned { get; set; }
}

/// <summary>
/// Reads/writes <see cref="AppPreferences"/> as JSON in the platform-appropriate per-user
/// app-data folder (NOT the user's documents/save folder) — project CLAUDE.md milestone 14c.
/// <b>Fail-soft by design:</b> <see cref="Load"/> never throws — a missing file, a corrupt file,
/// or any other read/parse problem all return a fresh (unpinned, no startup path) instance, so a
/// fresh install boots bare exactly as today.
/// </summary>
public static class AppPreferencesFile
{
    /// <summary>Test-only seam: overrides the preferences directory so tests never read/write
    /// the developer's REAL per-user app-data folder (project CLAUDE.md milestone 14c — every
    /// <see cref="P2000.UI.Runner.EmulationRunner"/> construction calls through
    /// <see cref="Load"/>, so leaving this real by default in a test run could load an actual
    /// pinned startup config from the machine running the tests). <c>null</c> (production
    /// default) uses the real platform-appropriate folder.</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string DirectoryPath => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MMulator");

    /// <summary>The app preferences JSON file itself.</summary>
    public static string PreferencesFilePath => Path.Combine(DirectoryPath, "AppPreferences.json");

    /// <summary>Fixed path for the auto-remembered "last session" <c>.cfg</c> — an ordinary
    /// <c>.cfg</c> file (the existing serializer, no new format), just always written/read at
    /// this one location.</summary>
    public static string LastSessionCfgPath => Path.Combine(DirectoryPath, "last-session.cfg");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Best-effort load — see the class doc comment. Never throws.</summary>
    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesFilePath)) return new AppPreferences();
            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesFilePath), JsonOptions)
                   ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    /// <summary>Writes <paramref name="prefs"/>, creating the app-data directory if needed.</summary>
    public static void Save(AppPreferences prefs)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PreferencesFilePath, JsonSerializer.Serialize(prefs, JsonOptions));
    }
}

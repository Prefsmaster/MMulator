using System.Runtime.CompilerServices;

// Grants the test project visibility into internal test-seam members — specifically
// AppPreferencesFile.DirectoryOverride (project CLAUDE.md milestone 14c), so tests can fully
// isolate themselves from the real per-user app-data folder instead of reading/writing the
// developer's actual AppPreferences.json/last-session.cfg on every test run.
[assembly: InternalsVisibleTo("P2000.UI.Tests")]

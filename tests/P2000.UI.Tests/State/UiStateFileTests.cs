using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using P2000.UI.State;

namespace P2000.UI.Tests.State;

/// <summary>
/// Tests for the <c>.uistate</c> sidecar (project CLAUDE.md milestone 14b) — pure UI
/// window-layout state, independent of <c>MachineStateFile</c>'s own versioning/reject
/// discipline. Covers round-trip serialization and the best-effort missing/version-mismatch
/// contract; <see cref="UiStateFile.Capture"/>/<see cref="UiStateFile.Apply"/> need a real
/// (headless) <see cref="Window"/>, exercised separately below via <c>[AvaloniaFact]</c>.
/// </summary>
public class UiStateFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"uistate-test-{Guid.NewGuid():N}.uistate");

    // ---- Round-trip --------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_FullLayout_PreservesAllFields()
    {
        var path = TempPath();
        try
        {
            var data = new UiStateData
            {
                MainWindow = new WindowLayout { IsOpen = true, X = 10, Y = 20, Width = 800, Height = 600 },
                CassetteDeck = new WindowLayout { IsOpen = true, X = 1, Y = 2, Width = 300, Height = 200 },
                DiskDrive = new WindowLayout { IsOpen = false },
                Debugger = new DebuggerLayout
                {
                    Window = new WindowLayout { IsOpen = true, X = 5, Y = 6, Width = 500, Height = 400 },
                    Vram = new WindowLayout { IsOpen = true, X = 7, Y = 8, Width = 200, Height = 150 },
                    VramShowHex = true,
                    MemoryWatches =
                    {
                        new MemoryWatchLayout
                        {
                            IsOpen = true, X = 11, Y = 12, Width = 250, Height = 300,
                            BaseAddress = 0x6000, Length = 512, Follow = "HL",
                        },
                    },
                },
            };

            UiStateFile.Save(data, path);
            var restored = UiStateFile.TryLoad(path);

            Assert.NotNull(restored);
            Assert.Equal(UiStateFile.CurrentVersion, restored!.Version);
            Assert.True(restored.MainWindow!.IsOpen);
            Assert.Equal(10, restored.MainWindow.X);
            Assert.Equal(20, restored.MainWindow.Y);
            Assert.Equal(800, restored.MainWindow.Width);
            Assert.Equal(600, restored.MainWindow.Height);
            Assert.False(restored.DiskDrive!.IsOpen);
            Assert.NotNull(restored.Debugger);
            Assert.True(restored.Debugger!.VramShowHex);
            Assert.Single(restored.Debugger.MemoryWatches);
            var watch = restored.Debugger.MemoryWatches[0];
            Assert.Equal(0x6000, watch.BaseAddress);
            Assert.Equal(512, watch.Length);
            Assert.Equal("HL", watch.Follow);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_NoDebugger_RestoresNullDebugger()
    {
        var path = TempPath();
        try
        {
            UiStateFile.Save(new UiStateData(), path);
            var restored = UiStateFile.TryLoad(path);

            Assert.NotNull(restored);
            Assert.Null(restored!.Debugger);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Best-effort missing/version-mismatch contract ---------------------------------------

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        var path = TempPath(); // never written
        Assert.Null(UiStateFile.TryLoad(path));
    }

    [Fact]
    public void TryLoad_FutureVersion_ReturnsNull_NotThrow()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, $$"""{ "version": {{UiStateFile.CurrentVersion + 1}} }""");
            Assert.Null(UiStateFile.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsNull_NotThrow()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json ");
            Assert.Null(UiStateFile.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Sidecar path derivation ---------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\saves\mygame.state", @"C:\saves\mygame.uistate")]
    [InlineData("plain.state", "plain.uistate")]
    public void SidecarPathFor_SwapsExtension(string statePath, string expected)
    {
        Assert.Equal(expected, UiStateFile.SidecarPathFor(statePath));
    }

    // ---- Window capture/apply (needs a real headless Window) ----------------------------------

    [AvaloniaFact]
    public void Capture_ClosedWindow_ReportsNotOpen()
    {
        var window = new Window();
        var layout = UiStateFile.Capture(window);
        Assert.False(layout.IsOpen); // never shown -> IsVisible is false
    }

    [AvaloniaFact]
    public void Capture_NullWindow_ReportsNotOpen_WithZeroedGeometry()
    {
        var layout = UiStateFile.Capture(null);
        Assert.False(layout.IsOpen);
        Assert.Equal(0, layout.X);
        Assert.Equal(0, layout.Y);
        Assert.Equal(0, layout.Width);
        Assert.Equal(0, layout.Height);
    }

    // Position round-trips through a real platform window handle (Show()), which this test
    // project's headless setup deliberately doesn't render (UseHeadlessDrawing = false, same
    // reason no other test in this suite exercises a shown Window — see MemoryWatchVmTests'
    // own doc comment). Width/Height are plain styled properties and need no platform impl, so
    // those are verified directly; Position's actual restore is exercised only by hand/manual
    // testing, same category as the rest of this project's Views layer.
    [AvaloniaFact]
    public void Apply_SetsSize()
    {
        var window = new Window();
        var layout = new WindowLayout { IsOpen = true, X = 42, Y = 84, Width = 640, Height = 480 };

        UiStateFile.Apply(window, layout);

        Assert.Equal(640, window.Width);
        Assert.Equal(480, window.Height);
    }

    [AvaloniaFact]
    public void Apply_NullLayout_IsNoOp()
    {
        var window = new Window { Width = 100, Height = 100 };
        UiStateFile.Apply(window, null);
        Assert.Equal(100, window.Width);
        Assert.Equal(100, window.Height);
    }
}

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(P2000.UI.Tests.TestApp))]

namespace P2000.UI.Tests;

/// <summary>Minimal Avalonia headless application used by <c>[AvaloniaFact]</c> tests.</summary>
public class TestApp : Avalonia.Application
{
    public override void Initialize() { }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            // UseHeadlessDrawing: true (was false) — needed to actually construct/Show() a real
            // Window subclass with rendering controls (e.g. DisplayWindow's DisplayControl, which
            // eagerly allocates a WriteableBitmap in its constructor): with drawing disabled, no
            // IPlatformRenderInterface is registered at all and any such control's constructor
            // throws. Enables Views/DisplayWindowTests.cs's real end-to-end crash regression test
            // (project CLAUDE.md, 2026-07-27 startup-crash fix) without needing a real GPU/OS
            // window — still fully headless, just backed by Avalonia.Headless's own lightweight
            // render interface instead of none at all.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

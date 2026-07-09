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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

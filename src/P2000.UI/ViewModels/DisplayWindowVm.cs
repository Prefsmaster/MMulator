using CommunityToolkit.Mvvm.ComponentModel;
using P2000.UI.Runner;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the main display window. Owns the emulation runner for
/// the lifetime of the application.</summary>
public sealed class DisplayWindowVm : ObservableObject, IDisposable
{
    public EmulationRunner Runner { get; } = new();

    public DisplayWindowVm() => Runner.Start();

    public void Dispose() => Runner.Dispose();
}

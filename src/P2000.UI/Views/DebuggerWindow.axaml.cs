using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DebuggerWindow : Window
{
    private DebuggerWindowVm? _vm;
    private VramWindow?       _vramWindow;

    public DebuggerWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested    -= OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested   -= OnOpenMemoryWatch;
        }

        _vm = DataContext as DebuggerWindowVm;

        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested    += OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested   += OnOpenMemoryWatch;
        }
    }

    private void OnOpenVramWindow()
    {
        if (_vramWindow is { IsVisible: true })
        {
            _vramWindow.Activate();
            return;
        }

        _vramWindow = new VramWindow { DataContext = _vm!.Vram };
        _vramWindow.Closed += (_, _) => _vramWindow = null;
        _vramWindow.Show(this);
    }

    private void OnOpenMemoryWatch(MemoryWatchVm watchVm)
    {
        var win = new MemoryWatchWindow { DataContext = watchVm };
        win.Closed += (_, _) =>
        {
            if (_vm is not null)
                _vm.RemoveMemoryWatchCommand.Execute(watchVm);
        };
        win.Show(this);
    }

    private void OnDisasmTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        // Walk up the visual tree from the tapped element to find a DisassemblyLineVm.
        if (e.Source is Avalonia.Controls.Control c)
        {
            Avalonia.Controls.Control? el = c;
            while (el is not null)
            {
                if (el.DataContext is DisassemblyLineVm lineVm)
                {
                    _vm.ToggleExecBreakpoint(lineVm.RawAddress);
                    return;
                }
                el = el.Parent as Avalonia.Controls.Control;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested  -= OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested -= OnOpenMemoryWatch;
        }
    }
}

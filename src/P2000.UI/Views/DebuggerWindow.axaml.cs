using Avalonia.Controls;
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

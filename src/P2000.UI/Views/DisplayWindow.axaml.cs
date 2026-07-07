using Avalonia.Controls;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DisplayWindow : Window
{
    private DisplayWindowVm? _vm;

    public DisplayWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.Runner.FrameReady -= Display.Present;

        _vm = DataContext as DisplayWindowVm;

        if (_vm is not null)
            _vm.Runner.FrameReady += Display.Present;

        base.OnDataContextChanged(e);
    }
}

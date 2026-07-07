using Avalonia.Controls;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class ConfigWindow : Window
{
    public ConfigWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Refresh axes from the live machine config each time the window is shown.
        (DataContext as ConfigWindowVm)?.LoadFromCurrentConfig();
    }
}

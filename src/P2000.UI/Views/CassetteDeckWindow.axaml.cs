using Avalonia.Controls;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class CassetteDeckWindow : Window
{
    private CassetteDeckVm? _vm;

    public CassetteDeckWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.ShowMessageRequested -= ShowErrorDialog;

        _vm = DataContext as CassetteDeckVm;

        if (_vm is not null)
            _vm.ShowMessageRequested += ShowErrorDialog;

        base.OnDataContextChanged(e);
    }

    // ── Error dialog (save failures) — same small dialog as DisplayWindow/MemoryWatchWindow ──
    private async void ShowErrorDialog(string message)
    {
        var dialog = new Window
        {
            Title = "MMulator",
            Width = 440, Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button
        {
            Content = "OK",
            MinWidth = 80,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ok,
            }
        };
        await dialog.ShowDialog(this);
    }
}

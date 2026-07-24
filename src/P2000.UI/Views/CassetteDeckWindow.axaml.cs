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
        {
            _vm.ShowMessageRequested -= ShowErrorDialog;
            _vm.ConfirmDiscardRequested -= ShowConfirmDiscardDialog;
        }

        _vm = DataContext as CassetteDeckVm;

        if (_vm is not null)
        {
            _vm.ShowMessageRequested += ShowErrorDialog;
            _vm.ConfirmDiscardRequested += ShowConfirmDiscardDialog;
        }

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

    // ── Discard/Cancel dialog (unsaved-changes warning, §14 milestone 14a) ──────────────────
    private async Task<bool> ShowConfirmDiscardDialog(string message)
    {
        var dialog = new Window
        {
            Title = "MMulator",
            Width = 440, Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = false;
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        var discard = new Button { Content = "Discard", MinWidth = 80 };
        cancel.Click += (_, _) => { result = false; dialog.Close(); };
        discard.Click += (_, _) => { result = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children = { cancel, discard },
                },
            }
        };
        await dialog.ShowDialog(this);
        return result;
    }
}

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class MemoryWatchWindow : Window
{
    private MemoryWatchVm? _vm;

    public MemoryWatchWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.ShowMessageRequested -= ShowErrorDialog;

        _vm = DataContext as MemoryWatchVm;

        if (_vm is not null)
            _vm.ShowMessageRequested += ShowErrorDialog;

        base.OnDataContextChanged(e);
    }

    private void OnGoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var addrBox = this.FindControl<TextBox>("AddressBox");
        var lenBox = this.FindControl<TextBox>("LengthBox");
        if (addrBox is null || lenBox is null || DataContext is not MemoryWatchVm vm) return;

        var addrText = addrBox.Text?.Trim().TrimStart('0', 'x', 'X', '$') ?? "";
        if (!ushort.TryParse(addrText, NumberStyles.HexNumber, null, out var addr)) return;

        var lenText = lenBox.Text?.Trim().TrimStart('0', 'x', 'X', '$') ?? "";
        var length = vm.Length;
        if (lenText.Length > 0 && !int.TryParse(lenText, NumberStyles.HexNumber, null, out length))
            return;

        vm.SetRange(addr, length);
    }

    // ── Save range (prompts for start/length, defaulting to this window's range) ─────────
    private async void OnSaveRangeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MemoryWatchVm vm) return;

        var range = await PromptRangeAsync(vm.BaseAddress, vm.Length);
        if (range is null) return;

        await vm.SaveRangeToFileAsync(range.Value.Start, range.Value.Length);
    }

    /// <summary>Small modal asking for a hex start address + hex length, pre-filled with the
    /// caller's defaults and freely editable. Returns null on Cancel.</summary>
    private async Task<(ushort Start, int Length)?> PromptRangeAsync(ushort defaultStart, int defaultLength)
    {
        var dialog = new Window
        {
            Title = "Save Memory Range",
            Width = 320, Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var startBox = new TextBox { Text = $"{defaultStart:X4}", FontFamily = "Consolas,Courier New,monospace" };
        var lengthBox = new TextBox { Text = $"{defaultLength:X4}", FontFamily = "Consolas,Courier New,monospace" };
        var save = new Button { Content = "Save", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };

        (ushort Start, int Length)? result = null;

        save.Click += (_, _) =>
        {
            var startText = startBox.Text?.Trim().TrimStart('0', 'x', 'X', '$') ?? "";
            var lengthText = lengthBox.Text?.Trim().TrimStart('0', 'x', 'X', '$') ?? "";
            if (ushort.TryParse(startText, NumberStyles.HexNumber, null, out var start) &&
                int.TryParse(lengthText, NumberStyles.HexNumber, null, out var length) &&
                length > 0)
            {
                result = (start, length);
                dialog.Close();
            }
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Start address (hex):" },
                startBox,
                new TextBlock { Text = "Length (hex bytes):" },
                lengthBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(0, 10, 0, 0),
                    Children = { save, cancel },
                },
            },
        };

        await dialog.ShowDialog(this);
        return result;
    }

    // ── Error dialog (save/load failures) — same small dialog as DisplayWindow ────────────
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

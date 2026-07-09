using Avalonia.Controls;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class MemoryWatchWindow : Window
{
    public MemoryWatchWindow()
    {
        InitializeComponent();
    }

    private void OnGoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var tb = this.FindControl<TextBox>("AddressBox");
        if (tb is null || DataContext is not MemoryWatchVm vm) return;

        var text = tb.Text?.Trim().TrimStart('0', 'x', 'X', '$') ?? "";
        if (ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                            null, out ushort addr))
        {
            vm.BaseAddress = addr;
        }
    }
}

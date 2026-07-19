using Avalonia.Controls;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class KeyboardWindow : Window
{
    private KeyboardWindowVm? _vm;

    public KeyboardWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.KeyActivated -= OnKeyActivated;

        _vm = DataContext as KeyboardWindowVm;

        if (_vm is not null)
            _vm.KeyActivated += OnKeyActivated;

        base.OnDataContextChanged(e);
    }

    // This window has no text input of its own — every click should hand OS focus straight back
    // to whichever window had it before (typically the emulator display), so the user can keep
    // typing without re-clicking it (owner-reported 2026-07-20).
    private void OnKeyActivated() => Owner?.Activate();
}

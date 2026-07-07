using Avalonia.Controls;
using Avalonia.Input;
using P2000.UI.Input;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DisplayWindow : Window
{
    private DisplayWindowVm? _vm;
    // Track which Avalonia Keys are currently down to suppress OS key-repeat events.
    // The P2000T's 50 Hz ISR handles auto-repeat at the hardware level.
    private readonly HashSet<Key> _keysDown = new();

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_keysDown.Add(e.Key)) return;  // already down = OS repeat, ignore
        var entry = KeyMap.Map(e.Key);
        if (entry.HasValue)
            _vm?.Runner.EnqueueKey(entry.Value.Row, entry.Value.Col, true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
        var entry = KeyMap.Map(e.Key);
        if (entry.HasValue)
            _vm?.Runner.EnqueueKey(entry.Value.Row, entry.Value.Col, false);
    }
}

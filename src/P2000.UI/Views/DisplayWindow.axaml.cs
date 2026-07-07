using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2000.UI.Input;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DisplayWindow : Window
{
    private DisplayWindowVm? _vm;
    private CassetteDeckWindow? _deckWindow;
    // Track which Avalonia Keys are currently down to suppress OS key-repeat events.
    // The P2000T's 50 Hz ISR handles auto-repeat at the hardware level.
    private readonly HashSet<Key> _keysDown = new();

    public DisplayWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        // Tunnel (pre-child) so P2000T keys (including Enter/Return) are sent to the
        // emulator before any focused toolbar button can consume them and trigger its action.
        // Non-matrix keys (F5, F11, …) pass through unhandled and reach the KeyBindings normally.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Runner.FrameReady -= Display.Present;
            _vm.OpenDeckWindowRequested -= ShowDeckWindow;
        }

        _vm = DataContext as DisplayWindowVm;

        if (_vm is not null)
        {
            _vm.Runner.FrameReady += Display.Present;
            _vm.OpenDeckWindowRequested += ShowDeckWindow;
        }

        base.OnDataContextChanged(e);
    }

    // ── Cassette deck satellite window ────────────────────────────────────────

    private void ShowDeckWindow()
    {
        if (_deckWindow is { IsVisible: true })
        {
            _deckWindow.Activate();
            return;
        }
        _deckWindow = new CassetteDeckWindow { DataContext = _vm!.CassetteVm };
        _deckWindow.Show(this);
    }

    // ── Drag-and-drop (.cas mount) ────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasCasFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null) return;
        var items = e.Data.GetFiles();
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is not IStorageFile file) continue;
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ext is not (".cas" or ".p2000t")) continue;

            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var name = Path.GetFileNameWithoutExtension(file.Name);
            _vm.CassetteVm.MountBytes(ms.ToArray(), name);
            break; // mount only the first cassette
        }
    }

    private static bool HasCasFile(IDataObject data)
    {
        if (!data.Contains(DataFormats.Files)) return false;
        var items = data.GetFiles();
        if (items is null) return false;
        return items.Any(f =>
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            return ext is ".cas" or ".p2000t";
        });
    }

    // ── Keyboard passthrough to P2000T matrix ────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_keysDown.Add(e.Key)) return;  // OS repeat — P2000T handles auto-repeat at 50 Hz
        var entry = KeyMap.Map(e.Key);
        if (!entry.HasValue) return;
        _vm?.Runner.EnqueueKey(entry.Value.Row, entry.Value.Col, true);
        e.Handled = true; // prevent focused toolbar button from consuming e.g. Enter
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        _keysDown.Remove(e.Key);
        var entry = KeyMap.Map(e.Key);
        if (!entry.HasValue) return;
        _vm?.Runner.EnqueueKey(entry.Value.Row, entry.Value.Col, false);
        e.Handled = true;
    }
}

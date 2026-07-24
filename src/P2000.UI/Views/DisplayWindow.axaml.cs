using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DisplayWindow : Window
{
    private DisplayWindowVm? _vm;
    private CassetteDeckWindow? _deckWindow;
    private DiskDriveWindow? _diskWindow;
    private ConfigWindow? _configWindow;
    private DebuggerWindow? _debuggerWindow;
    private KeyboardWindow? _keyboardWindow;
    private Action<uint[], bool, bool[]>? _frameReadyHandler;

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
        if (_vm is not null && _frameReadyHandler is not null)
        {
            _vm.Runner.FrameReady           -= _frameReadyHandler;
            _vm.OpenDeckWindowRequested     -= ShowDeckWindow;
            _vm.OpenDiskDriveWindowRequested -= ShowDiskDriveWindow;
            _vm.OpenConfigWindowRequested   -= ShowConfigWindow;
            _vm.OpenDebuggerWindowRequested -= ShowDebuggerWindow;
            _vm.OpenKeyboardWindowRequested -= ShowKeyboardWindow;
            _vm.ShowMessageRequested        -= ShowErrorDialog;
        }

        _vm = DataContext as DisplayWindowVm;

        if (_vm is not null)
        {
            _frameReadyHandler = (pixels, fieldWasOdd, corruption) =>
            {
                Display.Mode             = _vm.DisplayMode;
                Display.Crop             = _vm.Crop;
                Display.IntegerScale     = _vm.IntegerScale;
                Display.PalAspect        = _vm.PalAspect;
                Display.ShowScanlines    = _vm.ShowScanlines;
                Display.ShowDebugOverlay = _vm.ShowDebugOverlay;
                Display.Present(pixels, fieldWasOdd, corruption);
            };
            _vm.Runner.FrameReady += _frameReadyHandler;
            _vm.OpenDeckWindowRequested     += ShowDeckWindow;
            _vm.OpenDiskDriveWindowRequested += ShowDiskDriveWindow;
            _vm.OpenConfigWindowRequested   += ShowConfigWindow;
            _vm.OpenDebuggerWindowRequested += ShowDebuggerWindow;
            _vm.OpenKeyboardWindowRequested += ShowKeyboardWindow;
            _vm.ShowMessageRequested        += ShowErrorDialog;
        }

        base.OnDataContextChanged(e);
    }

    // ── Error dialog (version mismatch / save-load failure) ──────────────────

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
        dialog.Content = new Avalonia.Controls.StackPanel
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

    // ── Satellite windows ─────────────────────────────────────────────────────

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

    private void ShowDiskDriveWindow()
    {
        if (_diskWindow is { IsVisible: true })
        {
            _diskWindow.Activate();
            return;
        }
        _diskWindow = new DiskDriveWindow { DataContext = _vm!.DiskVm };
        _diskWindow.Show(this);
    }

    private void ShowConfigWindow()
    {
        if (_configWindow is { IsVisible: true })
        {
            _configWindow.Activate();
            return;
        }
        _configWindow = new ConfigWindow
        {
            DataContext = new ConfigWindowVm(_vm!.Runner)
        };
        _configWindow.Show(this);
    }

    private void ShowDebuggerWindow()
    {
        if (_debuggerWindow is { IsVisible: true })
        {
            _debuggerWindow.Activate();
            return;
        }
        _debuggerWindow = new DebuggerWindow { DataContext = _vm!.DebuggerVm };
        _debuggerWindow.Show(this);
    }

    private void ShowKeyboardWindow()
    {
        if (_keyboardWindow is { IsVisible: true })
        {
            _keyboardWindow.Activate();
            return;
        }
        _keyboardWindow = new KeyboardWindow { DataContext = _vm!.KeyboardVm };
        _keyboardWindow.Show(this);
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
            await _vm.CassetteVm.TryMountBytesAsync(ms.ToArray(), name, file);
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
    // Routed through HostKeyTranslator (project CLAUDE.md §14.3a) so P2000-Authentic vs
    // Standard-Host mode (set from the soft-keyboard window) applies here too. The translator
    // itself suppresses OS auto-repeat — the P2000T's 50 Hz ISR handles repeat at the hardware level.

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Only claim the event for recognized P2000 keys — F5/F11/F6/F8/F12 etc. must still
        // reach the window's own KeyBindings unhandled. e.PhysicalKey is passed through so the
        // translator can recover a real numpad press even when Windows (Shift + NumLock on)
        // reports it as a navigation key instead (owner-reported 2026-07-19, see HostKeyTranslator).
        if (_vm is not null && _vm.KeyTranslator.KeyDown(e.Key, e.PhysicalKey))
            e.Handled = true; // prevent a focused toolbar button from consuming e.g. Enter
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_vm is not null && _vm.KeyTranslator.KeyUp(e.Key, e.PhysicalKey))
            e.Handled = true;
    }
}

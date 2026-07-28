using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
using P2000.UI.State;
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
            _vm.StateSaved                  -= OnStateSaved;
            _vm.StateLoaded                 -= OnStateLoaded;
            _vm.DiskVm.GeometryMismatchDetected -= ShowGeometryMismatchDialog;
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
            _vm.StateSaved                  += OnStateSaved;
            _vm.StateLoaded                 += OnStateLoaded;

            // Proactive geometry-mismatch surfacing (project CLAUDE.md milestone 14g) —
            // subscribed here, on the ALWAYS-present main window, so a mismatch from the
            // startup-config auto-load (or a later ConfigWindowVm.Apply) shows a dialog even if
            // the Disk Drives satellite window is never opened this session. The actual RAISE is
            // deferred to OnOpened (below), NOT done here — see that override's doc comment for
            // why doing it here crashes the app.
            _vm.DiskVm.GeometryMismatchDetected += ShowGeometryMismatchDialog;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>FIX (project CLAUDE.md §17/§18 findings, post-14g): <see cref="RaiseAnyPendingMismatches"/>
    /// must NOT be called from <see cref="OnDataContextChanged"/> — <c>App.axaml.cs</c> sets
    /// <c>DataContext</c> via `new DisplayWindow { DataContext = vm }`, which fires
    /// <see cref="OnDataContextChanged"/> SYNCHRONOUSLY, before `desktop.MainWindow = win` and
    /// therefore before this window is ever shown. <see cref="ShowGeometryMismatchDialog"/>'s
    /// `dialog.ShowDialog(this)` requires a VISIBLE owner — calling it that early throws
    /// `InvalidOperationException` ("Cannot show window with non-visible owner"), unhandled inside
    /// an `async void` method, which crashes the whole process with no user-facing error. Because
    /// "Continue mounting as-is" deliberately never clears the underlying mismatch (it stays "on
    /// record for the session," `DiskDriveVm.ContinueWithCurrentMount`'s own doc comment), this
    /// reproduced on EVERY subsequent launch once a mismatched drive's config got auto-saved as
    /// the startup config — a permanent crash loop. Raising here instead, once this window is
    /// actually visible, fixes it while preserving the "subscribe then raise" ordering.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _vm?.DiskVm.RaiseAnyPendingMismatches();
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

    // ── Geometry-mismatch dialog (project CLAUDE.md milestone 14g proactive surfacing) ──────
    // Same shape as DiskDriveWindow's own dialog (ms.14e) — duplicated here (matching this
    // project's existing per-window dialog convention, e.g. ShowErrorDialog above) so a mismatch
    // from an Apply or the startup auto-load shows up even when the Disk Drives window itself was
    // never opened. Non-blocking: the image (or config) is already applied by the time this shows.

    private async void ShowGeometryMismatchDialog(DiskDriveVm drive, DiskGeometryMismatch mismatch)
    {
        var dialog = new Window
        {
            Title = "MMulator — Disk Geometry Mismatch",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var configuredName = GeometryName(drive.Capacity, drive.Sides == DiskSides.Double ? 2 : 1);
        string message;
        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        if (mismatch.Kind == DiskGeometryMismatchKind.Candidate)
        {
            var names = string.Join(" or ", mismatch.Candidates.Select(c => GeometryName(c.Tracks, c.Sides)));
            message = $"Drive {drive.DriveIndex}: this file's size matches {names}, but the " +
                      $"drive is configured for {configuredName}.";

            foreach (var (tracks, sides) in mismatch.Candidates)
            {
                var button = new Button { Content = $"Use {GeometryName(tracks, sides)} + remount", MinWidth = 100 };
                var diskSides = sides == 2 ? DiskSides.Double : DiskSides.Single;
                button.Click += (_, _) =>
                {
                    drive.ReconfigureAndRemount(tracks, diskSides);
                    dialog.Close();
                };
                buttons.Children.Add(button);
            }
        }
        else
        {
            var percent = mismatch.ExpectedLength > 0 ? mismatch.ActualLength * 100 / mismatch.ExpectedLength : 0;
            message = $"Drive {drive.DriveIndex}: {mismatch.ActualLength:N0} bytes mounted; the " +
                      $"drive expects {mismatch.ExpectedLength:N0} bytes for {configuredName} — " +
                      $"about {percent}% of the expected data is present.";

            if (mismatch.CanPad)
            {
                var pad = new Button { Content = "Extend to full size", MinWidth = 120 };
                ToolTip.SetTip(pad, "Fills the missing space with blank sectors — it does NOT recover any missing data.");
                pad.Click += (_, _) =>
                {
                    drive.ExtendMountedDiskToFullSize(mismatch.ExpectedLength);
                    dialog.Close();
                };
                buttons.Children.Add(pad);
            }
        }

        var continueBtn = new Button { Content = "Continue mounting as-is", MinWidth = 100 };
        continueBtn.Click += (_, _) => { drive.ContinueWithCurrentMount(); dialog.Close(); };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        cancelBtn.Click += (_, _) => { drive.CancelMount(); dialog.Close(); };
        buttons.Children.Add(continueBtn);
        buttons.Children.Add(cancelBtn);

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons,
            }
        };

        await dialog.ShowDialog(this);
    }

    private static string GeometryName(int tracks, int sides) =>
        $"{tracks}-track/{(sides == 2 ? "double-sided" : "single-sided")}";

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
            DataContext = new ConfigWindowVm(_vm!.Runner, _vm!.DiskVm)
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

    // ── .uistate sidecar (project CLAUDE.md milestone 14b) ────────────────────
    // Pure UI window-layout state, written/read only alongside the EXISTING Save State / Load
    // State actions (ms.8) — never inside .state itself, and never a reason to fail a .state
    // load (reference doc §3a "a separate .uistate sidecar file, NOT embedded in .state").

    private void OnStateSaved(string statePath)
    {
        var data = new UiStateData
        {
            MainWindow = UiStateFile.Capture(this),
            CassetteDeck = UiStateFile.Capture(_deckWindow),
            DiskDrive = UiStateFile.Capture(_diskWindow),
            Config = UiStateFile.Capture(_configWindow),
            Keyboard = UiStateFile.Capture(_keyboardWindow),
            Debugger = _debuggerWindow?.CaptureLayout(),
        };
        try
        {
            UiStateFile.Save(data, UiStateFile.SidecarPathFor(statePath));
        }
        catch
        {
            // Best-effort: a sidecar write failure must never surface as a .state save failure.
        }
    }

    private void OnStateLoaded(string statePath)
    {
        var data = UiStateFile.TryLoad(UiStateFile.SidecarPathFor(statePath));
        if (data is null) return; // missing/version-mismatched -> default layout, silent no-op

        UiStateFile.Apply(this, data.MainWindow);

        if (data.CassetteDeck is { IsOpen: true })
        {
            ShowDeckWindow();
            UiStateFile.Apply(_deckWindow, data.CassetteDeck);
        }
        if (data.DiskDrive is { IsOpen: true })
        {
            ShowDiskDriveWindow();
            UiStateFile.Apply(_diskWindow, data.DiskDrive);
        }
        if (data.Config is { IsOpen: true })
        {
            ShowConfigWindow();
            UiStateFile.Apply(_configWindow, data.Config);
        }
        if (data.Keyboard is { IsOpen: true })
        {
            ShowKeyboardWindow();
            UiStateFile.Apply(_keyboardWindow, data.Keyboard);
        }
        if (data.Debugger is { } debuggerLayout &&
            (debuggerLayout.Window is { IsOpen: true } || debuggerLayout.MemoryWatches.Count > 0))
        {
            ShowDebuggerWindow();
            _debuggerWindow!.ApplyLayout(debuggerLayout);
        }
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
        // Yield to the menu bar while it's navigating (project CLAUDE.md milestone 14i): several
        // P2000 matrix keys — arrows, M/C/V/W/D/K/F, Enter — double as menu mnemonics/navigation
        // keys. This tunnel handler runs before Avalonia's own AccessKeyHandler/Menu bubble
        // handlers ever see the event, so marking it Handled here would swallow it from them.
        // MainMenu.IsOpen tracks exactly the state those handlers themselves gate on.
        if (MainMenu.IsOpen) return;

        // Only claim the event for recognized P2000 keys — F5/F11/F6/F8/F12 etc. must still
        // reach the window's own KeyBindings unhandled. e.PhysicalKey is passed through so the
        // translator can recover a real numpad press even when Windows (Shift + NumLock on)
        // reports it as a navigation key instead (owner-reported 2026-07-19, see HostKeyTranslator).
        if (_vm is not null && _vm.KeyTranslator.KeyDown(e.Key, e.PhysicalKey))
            e.Handled = true; // prevent a focused toolbar button from consuming e.g. Enter
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        // Deliberately asymmetric with OnPreviewKeyDown (project CLAUDE.md §17/§18 findings,
        // post-14i): KeyUp must ALWAYS reach the translator, even while MainMenu.IsOpen. A key
        // can be pressed while the menu is closed (a real matrix press + HostKeyTranslator
        // bookkeeping — e.g. a Standard-Host forced-Shift entry in _activePress/_activeForce) and
        // then released while the menu happens to be open (e.g. the user taps Alt while still
        // holding it). Gating KeyUp here too would silently drop that release — the matrix
        // crosspoint never gets un-pressed and the translator's forced-shift counters never
        // decrement, permanently leaking state that corrupts a LATER, unrelated keypress landing
        // on the same crosspoint. Recognized-but-never-pressed KeyUps (e.g. Enter releasing while
        // selecting a menu item) are already harmless no-ops here — HostKeyTranslator.KeyUp only
        // emits a release for a key it actually has recorded as pressed.
        if (_vm is not null && _vm.KeyTranslator.KeyUp(e.Key, e.PhysicalKey))
            e.Handled = true;
    }
}

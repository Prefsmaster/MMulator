using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using P2000.UI.State;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DebuggerWindow : Window
{
    private DebuggerWindowVm? _vm;
    private VramWindow?       _vramWindow;

    /// <summary>Open memory watch windows, keyed by their VM — needed to capture each one's
    /// position/size for the <c>.uistate</c> sidecar (project CLAUDE.md milestone 14b).
    /// <see cref="DebuggerWindowVm.MemoryWatches"/> tracks the VMs; this tracks their windows.</summary>
    private readonly Dictionary<MemoryWatchVm, MemoryWatchWindow> _memoryWatchWindows = new();

    public DebuggerWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested    -= OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested   -= OnOpenMemoryWatch;
        }

        _vm = DataContext as DebuggerWindowVm;

        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested    += OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested   += OnOpenMemoryWatch;
        }
    }

    private void OnOpenVramWindow()
    {
        if (_vramWindow is { IsVisible: true })
        {
            _vramWindow.Activate();
            return;
        }

        _vramWindow = new VramWindow { DataContext = _vm!.Vram };
        _vramWindow.Closed += (_, _) => _vramWindow = null;
        _vramWindow.Show(this);
    }

    private void OnOpenMemoryWatch(MemoryWatchVm watchVm)
    {
        var win = new MemoryWatchWindow { DataContext = watchVm };
        _memoryWatchWindows[watchVm] = win;
        win.Closed += (_, _) =>
        {
            _memoryWatchWindows.Remove(watchVm);
            if (_vm is not null)
                _vm.RemoveMemoryWatchCommand.Execute(watchVm);
        };
        win.Show(this);
    }

    // ── .uistate sidecar (project CLAUDE.md milestone 14b) ────────────────────

    /// <summary>Captures this window's own layout plus its nested VRAM window and every open
    /// memory watch window. Called by <c>DisplayWindow</c> only when this window is open.</summary>
    public DebuggerLayout CaptureLayout() => new()
    {
        Window = UiStateFile.Capture(this),
        Vram = UiStateFile.Capture(_vramWindow),
        VramShowHex = _vm?.Vram.ShowHex ?? false,
        MemoryWatches = _memoryWatchWindows.Select(kvp =>
        {
            var (watchVm, win) = (kvp.Key, kvp.Value);
            var layout = UiStateFile.Capture(win);
            return new MemoryWatchLayout
            {
                IsOpen = layout.IsOpen,
                X = layout.X, Y = layout.Y, Width = layout.Width, Height = layout.Height,
                BaseAddress = watchVm.BaseAddress,
                Length = watchVm.Length,
                Follow = watchVm.Follow,
            };
        }).ToList(),
    };

    /// <summary>Restores this window's own layout, re-opens the VRAM window if it was open, and
    /// re-creates each saved memory watch with its captured range/follow-register. Called by
    /// <c>DisplayWindow</c> after re-showing this window from a <c>.uistate</c> sidecar.</summary>
    public void ApplyLayout(DebuggerLayout layout)
    {
        UiStateFile.Apply(this, layout.Window);

        if (_vm is null) return;
        _vm.Vram.ShowHex = layout.VramShowHex;

        if (layout.Vram is { IsOpen: true })
        {
            OnOpenVramWindow();
            UiStateFile.Apply(_vramWindow, layout.Vram);
        }

        foreach (var watchLayout in layout.MemoryWatches)
        {
            var watchVm = _vm.RestoreMemoryWatch(watchLayout.BaseAddress, watchLayout.Length, watchLayout.Follow);
            if (_memoryWatchWindows.TryGetValue(watchVm, out var win))
                UiStateFile.Apply(win, watchLayout);
        }
    }

    private void OnDisasmTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        // Walk up the visual tree from the tapped element to find a DisassemblyLineVm.
        if (e.Source is Avalonia.Controls.Control c)
        {
            Avalonia.Controls.Control? el = c;
            while (el is not null)
            {
                if (el.DataContext is DisassemblyLineVm lineVm)
                {
                    _vm.ToggleExecBreakpoint(lineVm.RawAddress);
                    return;
                }
                el = el.Parent as Avalonia.Controls.Control;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.OpenVramWindowRequested  -= OnOpenVramWindow;
            _vm.OpenMemoryWatchRequested -= OnOpenMemoryWatch;
        }
    }
}

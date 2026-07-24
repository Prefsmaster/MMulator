using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class DiskDriveWindow : Window
{
    private DiskDriveWindowVm? _vm;

    public DiskDriveWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ShowMessageRequested -= ShowErrorDialog;
            _vm.ConfirmDiscardRequested -= ShowConfirmDiscardDialog;
        }

        _vm = DataContext as DiskDriveWindowVm;

        if (_vm is not null)
        {
            _vm.ShowMessageRequested += ShowErrorDialog;
            _vm.ConfirmDiscardRequested += ShowConfirmDiscardDialog;
        }

        base.OnDataContextChanged(e);
    }

    // ── Error dialog (mount/save failures) — same small dialog as CassetteDeckWindow ──
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

    // ── Drag-and-drop (.dsk/.img mount, project CLAUDE.md §14 "DRIVE TABS" decision,
    // 2026-07-23): a drop lands on whichever drive's tab is currently selected — resolves the
    // N-drive drop-target ambiguity milestone 14 originally left unbuilt, exactly like dropping
    // a file onto a specific document tab in an editor. ──────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _vm?.SelectedDrive is not null && HasDiskFile(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var drive = _vm?.SelectedDrive;
        if (drive is null) return;
        var items = e.Data.GetFiles();
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is not IStorageFile file) continue;
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ext is not (".dsk" or ".img")) continue;

            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var name = Path.GetFileNameWithoutExtension(file.Name);
            await drive.TryMountBytesAsync(ms.ToArray(), name, file);
            break; // mount only the first disk image
        }
    }

    private static bool HasDiskFile(IDataObject data)
    {
        if (!data.Contains(DataFormats.Files)) return false;
        var items = data.GetFiles();
        if (items is null) return false;
        return items.Any(f =>
        {
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            return ext is ".dsk" or ".img";
        });
    }
}

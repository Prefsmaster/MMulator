using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
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
            _vm.GeometryMismatchDetected -= ShowGeometryMismatchDialog;
        }

        _vm = DataContext as DiskDriveWindowVm;

        if (_vm is not null)
        {
            _vm.ShowMessageRequested += ShowErrorDialog;
            _vm.ConfirmDiscardRequested += ShowConfirmDiscardDialog;
            _vm.GeometryMismatchDetected += ShowGeometryMismatchDialog;
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

    // ── Geometry-mismatch dialog (project CLAUDE.md milestone 14e; machine ms.20d) ──────────
    // Non-blocking: the image is ALREADY mounted by the time this shows (mounting never fails).
    // Two shapes over the same dialog window — which buttons appear depends on the mismatch kind.

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
        var buttons = new StackPanel
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

        dialog.Content = new StackPanel
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

using Avalonia.Controls;
using P2000.Machine;
using P2000.Machine.Devices.Fdc;
using P2000.UI.ViewModels;

namespace P2000.UI.Views;

public partial class ConfigWindow : Window
{
    private ConfigWindowVm? _vm;

    public ConfigWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Refresh axes from the live machine config each time the window is shown.
        (DataContext as ConfigWindowVm)?.LoadFromCurrentConfig();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.OfflineMismatchDetected -= ShowOfflineMismatchDialog;
        _vm = DataContext as ConfigWindowVm;
        if (_vm is not null) _vm.OfflineMismatchDetected += ShowOfflineMismatchDialog;
        base.OnDataContextChanged(e);
    }

    // ── Offline geometry-mismatch preview dialog (project CLAUDE.md milestone 14g) ───────────
    // The Config-window analogue of DiskDriveWindow's live mismatch dialog (ms.14e) — same shape,
    // but NO pad option (this window never touches file bytes, per the owner's explicit decision)
    // and the candidate action updates the row's Capacity/Sides instead of remounting a live disk.

    private async void ShowOfflineMismatchDialog(FloppyDriveRowVm row, DiskGeometryMismatch mismatch)
    {
        var dialog = new Window
        {
            Title = "MMulator — Disk Geometry Mismatch",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var configuredName = GeometryName(row.Capacity, row.Sides == DiskSides.Double ? 2 : 1);
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
            message = $"Drive {row.DriveIndex}: this file's size matches {names}, but the row is " +
                      $"configured for {configuredName}.";

            foreach (var (tracks, sides) in mismatch.Candidates)
            {
                var button = new Button { Content = $"Update row to {GeometryName(tracks, sides)}", MinWidth = 100 };
                var diskSides = sides == 2 ? DiskSides.Double : DiskSides.Single;
                button.Click += (_, _) => { row.UpdateGeometryTo(tracks, diskSides); dialog.Close(); };
                buttons.Children.Add(button);
            }
        }
        else
        {
            var percent = mismatch.ExpectedLength > 0 ? mismatch.ActualLength * 100 / mismatch.ExpectedLength : 0;
            message = $"Drive {row.DriveIndex}: {mismatch.ActualLength:N0} bytes in this file; the row " +
                      $"is configured for {configuredName} ({mismatch.ExpectedLength:N0} bytes) — " +
                      $"about {percent}% of the expected data is present.";
        }

        var keepBtn = new Button { Content = "Keep current settings anyway", MinWidth = 100 };
        keepBtn.Click += (_, _) => dialog.Close();
        var chooseBtn = new Button { Content = "Choose a different file", MinWidth = 100 };
        chooseBtn.Click += (_, _) => { dialog.Close(); row.BrowseImageCommand.Execute(null); };
        buttons.Children.Add(keepBtn);
        buttons.Children.Add(chooseBtn);

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
}

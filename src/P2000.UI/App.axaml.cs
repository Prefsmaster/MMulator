using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using P2000.UI.ViewModels;
using P2000.UI.Views;

namespace P2000.UI;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new DisplayWindowVm();
            var win = new DisplayWindow { DataContext = vm };
            desktop.MainWindow = win;
            desktop.Exit += (_, _) => vm.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

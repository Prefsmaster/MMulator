using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace P2000.UI;

/// <summary>Maps ViewModels to their Views by convention:
/// P2000.UI.ViewModels.FooVm → P2000.UI.Views.Foo.</summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;

        var name = data.GetType().FullName!
            .Replace(".ViewModels.", ".Views.")
            .Replace("Vm", "");

        var type = Type.GetType(name);
        return type is null
            ? new TextBlock { Text = $"View not found: {name}" }
            : (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ObservableObject;
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace P2000.UI.ViewModels;

/// <summary>One line in the disassembly view.</summary>
public sealed partial class DisassemblyLineVm : ObservableObject
{
    [ObservableProperty] private string _address   = string.Empty;
    [ObservableProperty] private string _bytes     = string.Empty;
    [ObservableProperty] private string _text      = string.Empty;
    [ObservableProperty] private bool   _isPC      = false;   // true → yellow highlight
    [ObservableProperty] private bool   _hasBreakpoint = false; // true → red dot in gutter

    /// <summary>Raw address — used to toggle breakpoints on click.</summary>
    public ushort RawAddress { get; set; }
}

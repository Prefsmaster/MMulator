using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.UI.Input;

namespace P2000.UI.ViewModels;

/// <summary>One clickable key on the soft-keyboard window (project CLAUDE.md §14.3a). Wraps a
/// <see cref="SoftKeyDef"/> with the display state (label depends on the owner's current
/// <see cref="KeyMappingMode"/>; <see cref="IsActive"/> highlights an engaged sticky/lock key).</summary>
public sealed partial class SoftKeyVm : ObservableObject
{
    public SoftKeyDef Def { get; }
    private readonly KeyboardWindowVm _owner;

    public SoftKeyVm(SoftKeyDef def, KeyboardWindowVm owner)
    {
        Def = def;
        _owner = owner;
    }

    public string Label => _owner.Mode == KeyMappingMode.StandardHost ? Def.HostBase ?? Def.Base : Def.Base;
    public string? ShiftedLabel => _owner.Mode == KeyMappingMode.StandardHost ? Def.HostShifted ?? Def.Shifted : Def.Shifted;

    // Icons never vary by mode (none of the icon-bearing keys have a Standard-Host override),
    // so these don't need RefreshLabels() to run on a mode change.
    public bool ShowBaseIcon => Def.BaseIcon is not null;
    public bool ShowShiftedIcon => Def.ShiftedIcon is not null;
    public bool ShowBaseText => !ShowBaseIcon;
    public bool ShowShiftedText => !ShowShiftedIcon && !string.IsNullOrEmpty(ShiftedLabel);
    public string? BaseIconUri => Def.BaseIcon is { } b ? $"avares://P2000.UI/Assets/Icons/{b}.png" : null;
    public string? ShiftedIconUri => Def.ShiftedIcon is { } s ? $"avares://P2000.UI/Assets/Icons/{s}.png" : null;

    private const double KeyUnitPixels = 40;
    public double PixelWidth
        => (_owner.Mode == KeyMappingMode.StandardHost ? Def.StandardHostWidth ?? Def.Width : Def.Width) * KeyUnitPixels;

    // Standard-Host mode hides ISO-only keys so the soft-keyboard's shape matches a standard
    // ANSI host keyboard (owner-reported 2026-07-19) — P2000-Authentic always shows every key,
    // since that mode reflects the P2000T's own (ISO-style) physical layout.
    public bool IsVisible => !(Def.IsIsoOnly && _owner.Mode == KeyMappingMode.StandardHost);

    [ObservableProperty] private bool _isActive;

    /// <summary>Raised by the owner after <see cref="KeyboardWindowVm.Mode"/> changes so bound
    /// labels/shape refresh (they are computed, not observable properties themselves).</summary>
    public void RefreshForModeChange()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ShiftedLabel));
        OnPropertyChanged(nameof(ShowShiftedText));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(IsVisible));
    }

    [RelayCommand]
    private Task Activate() => _owner.ActivateAsync(this);
}

/// <summary>ViewModel for the soft-keyboard satellite window (project CLAUDE.md §5 item 3 /
/// §14.3a). Every key press goes through the SAME <see cref="HostKeyTranslator"/> instance the
/// physical-keyboard handler uses, so both obey the same P2000-Authentic/Standard-Host mode —
/// clicking here is indistinguishable, downstream, from a real key press for the corresponding
/// <see cref="System.Windows.Input.ICommand"/>-less host key.</summary>
public sealed partial class KeyboardWindowVm : ObservableObject
{
    private readonly HostKeyTranslator _translator;

    public IReadOnlyList<IReadOnlyList<SoftKeyVm>> Rows { get; }
    public IReadOnlyList<IReadOnlyList<SoftKeyVm>> Numpad { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStandardHost))]
    private KeyMappingMode _mode;

    public bool IsStandardHost => Mode == KeyMappingMode.StandardHost;

    // Sticky Shift/CODE: click to latch, click again OR press any regular key to release
    // (project CLAUDE.md §14.3a). At most one of each is latched at a time.
    private SoftKeyVm? _stickyShift;
    private SoftKeyVm? _stickyCode;
    private bool _lockEngaged;

    /// <summary>Raised after every key activation completes (click-to-press, sticky latch/
    /// unlatch, or lock toggle) so the view can hand OS focus back to whichever window had it
    /// before the click — this window has no text input of its own, so keeping focus here just
    /// blocks typing into the emulator until the user re-clicks it (owner-reported 2026-07-20).</summary>
    public event Action? KeyActivated;

    public KeyboardWindowVm(HostKeyTranslator translator)
    {
        _translator = translator;
        _mode = translator.Mode;
        Rows = SoftKeyLayout.Rows
            .Select(row => (IReadOnlyList<SoftKeyVm>)row.Select(d => new SoftKeyVm(d, this)).ToList())
            .ToList();
        Numpad = SoftKeyLayout.Numpad
            .Select(row => (IReadOnlyList<SoftKeyVm>)row.Select(d => new SoftKeyVm(d, this)).ToList())
            .ToList();
    }

    partial void OnModeChanged(KeyMappingMode value)
    {
        _translator.Mode = value;
        foreach (var row in Rows) foreach (var k in row) k.RefreshForModeChange();
        foreach (var row in Numpad) foreach (var k in row) k.RefreshForModeChange();
    }

    [RelayCommand]
    private void ToggleMode()
        => Mode = Mode == KeyMappingMode.P2000Authentic ? KeyMappingMode.StandardHost : KeyMappingMode.P2000Authentic;

    public async Task ActivateAsync(SoftKeyVm key)
    {
        if (key.Def.IsLock)
        {
            _lockEngaged = !_lockEngaged;
            key.IsActive = _lockEngaged;
            if (key.Def.HostKey is { } lockKey)
            {
                if (_lockEngaged) _translator.KeyDown(lockKey); else _translator.KeyUp(lockKey);
            }
            KeyActivated?.Invoke();
            return;
        }

        if (key.Def.IsSticky)
        {
            bool isShift = key.Def.HostKey is Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift;
            ActivateSticky(key, isShift);
            KeyActivated?.Invoke();
            return;
        }

        // Regular key: a momentary press — enqueue press, hold briefly (long enough to be seen
        // at the next 50 Hz frame boundary), then release. Matches "click a key → enqueue the
        // matrix event, applied at frame boundary like any host key" (§5 item 3).
        if (key.Def.HostKey is { } regularKey)
        {
            _translator.KeyDown(regularKey);
            await Task.Delay(80);
            _translator.KeyUp(regularKey);
        }
        else
        {
            _translator.PressRaw(key.Def.Row, key.Def.Col, true);
            await Task.Delay(80);
            _translator.PressRaw(key.Def.Row, key.Def.Col, false);
        }

        ReleaseStickyAfterRegularKey();
        KeyActivated?.Invoke();
    }

    private void ActivateSticky(SoftKeyVm key, bool isShift)
    {
        var current = isShift ? _stickyShift : _stickyCode;

        if (ReferenceEquals(current, key))
        {
            if (key.Def.HostKey is { } hk) _translator.KeyUp(hk);
            key.IsActive = false;
            if (isShift) _stickyShift = null; else _stickyCode = null;
            return;
        }

        if (current is { } prev)
        {
            if (prev.Def.HostKey is { } prevKey) _translator.KeyUp(prevKey);
            prev.IsActive = false;
        }
        if (key.Def.HostKey is { } newKey) _translator.KeyDown(newKey);
        key.IsActive = true;
        if (isShift) _stickyShift = key; else _stickyCode = key;
    }

    private void ReleaseStickyAfterRegularKey()
    {
        if (_stickyShift is { } shift)
        {
            if (shift.Def.HostKey is { } hk) _translator.KeyUp(hk);
            shift.IsActive = false;
            _stickyShift = null;
        }
        if (_stickyCode is { } code)
        {
            if (code.Def.HostKey is { } hk) _translator.KeyUp(hk);
            code.IsActive = false;
            _stickyCode = null;
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using P2000.Machine.Debug;
using P2000.Machine.State;
using P2000.UI.Rendering;
using P2000.UI.Runner;
using System.Runtime.InteropServices;
using MachineDebug = P2000.Machine.Debug;
using Video = P2000.Machine.Devices.Video;

namespace P2000.UI.ViewModels;

/// <summary>ViewModel for the main display window. Owns the <see cref="EmulationRunner"/>
/// for the application lifetime. All machine mutations go through the command queue;
/// observable properties are updated at the FrameReady cadence (50 Hz) and on break events.</summary>
public sealed partial class DisplayWindowVm : ObservableObject, IDisposable
{
    public EmulationRunner Runner { get; } = new();

    /// <summary>Cassette deck ViewModel — shared between the main window (menu) and the
    /// satellite deck window.</summary>
    public CassetteDeckVm CassetteVm { get; }

    /// <summary>Raised when the user requests the cassette deck satellite window.</summary>
    public event Action? OpenDeckWindowRequested;

    /// <summary>Raised when the user requests the config window.</summary>
    public event Action? OpenConfigWindowRequested;

    /// <summary>Raised with a user-facing error message (e.g. version-mismatch on load).
    /// The view code-behind shows a modal dialog.</summary>
    public event Action<string>? ShowMessageRequested;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty] private string _stateText = "Running";
    [ObservableProperty] private string _runPauseHeader = "Pause";   // text shown on toolbar button
    [ObservableProperty] private string _speedText = "–";
    [ObservableProperty] private bool _cassetteActive;
    [ObservableProperty] private string _modelText = "T";
    [ObservableProperty] private bool _isTurbo;

    // ── Video prefs (project CLAUDE.md §8) ───────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsModeInterlaced), nameof(IsModeProgressive),
        nameof(IsModeEvenOnly),   nameof(IsModeOddOnly))]
    private DisplayMode _displayMode = DisplayMode.Interlaced;

    // Computed bools for menu IsChecked bindings (ConverterParameter+x:Static is unreliable in AXAML).
    public bool IsModeInterlaced  => DisplayMode == DisplayMode.Interlaced;
    public bool IsModeProgressive => DisplayMode == DisplayMode.Progressive;
    public bool IsModeEvenOnly    => DisplayMode == DisplayMode.EvenOnly;
    public bool IsModeOddOnly     => DisplayMode == DisplayMode.OddOnly;

    [ObservableProperty] private bool _integerScale;
    [ObservableProperty] private bool _palAspect = true;
    [ObservableProperty] private bool _showScanlines;
    [ObservableProperty] private bool _showDebugOverlay;

    // ── Audio prefs (project CLAUDE.md §9) ───────────────────────────────────
    [ObservableProperty] private bool  _audioMute;
    [ObservableProperty] private float _audioVolume = 0.5f;

    private int _framesSinceStatusUpdate;

    public DisplayWindowVm()
    {
        CassetteVm = new CassetteDeckVm(Runner);
        Runner.FrameReady += OnFrameReady;
        Runner.BreakHit += _ => Dispatcher.UIThread.Post(UpdatePauseState);
        Runner.Start();
    }

    // ── Frame callback ────────────────────────────────────────────────────────

    private void OnFrameReady(uint[] _, bool __, bool[] ___)
    {
        // Update cassette LED every frame (cheap flag read).
        CassetteActive = Runner.Machine.CpOut.Forward || Runner.Machine.CpOut.Reverse;

        // Update speed + model text at ~5 Hz to reduce PropertyChanged churn.
        if (++_framesSinceStatusUpdate >= 10)
        {
            _framesSinceStatusUpdate = 0;
            SpeedText = IsTurbo ? "Turbo" : $"{Runner.SpeedPercent}%";
            var cfg = Runner.Machine.Config;
            var model = cfg.Model.ToString().Replace("P2000", "");
            var ram = cfg.RamVariant switch
            {
                P2000.Machine.RamVariant.T38  => "38",
                P2000.Machine.RamVariant.T54  => "54",
                P2000.Machine.RamVariant.T102 => "102",
                _                             => "?"
            };
            ModelText = $"{model}/{ram}";
        }
    }

    private void UpdatePauseState()
    {
        bool paused = Runner.Machine.IsPaused;
        StateText = paused ? "Paused" : "Running";
        RunPauseHeader = paused ? "Run" : "Pause";
        StepCommand.NotifyCanExecuteChanged();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleRunPause()
    {
        if (Runner.Machine.IsPaused)
        {
            Runner.Machine.Enqueue(new RunCommand());
            StateText = "Running";
            RunPauseHeader = "Pause";
            StepCommand.NotifyCanExecuteChanged();
        }
        else
        {
            Runner.Machine.Enqueue(new PauseCommand());
            StateText = "Paused";
            RunPauseHeader = "Run";
            StepCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void WarmReset()
    {
        Runner.Machine.Enqueue(new WarmResetCommand());
        StateText = "Running";
        RunPauseHeader = "Pause";
        StepCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ColdReset()
    {
        Runner.Machine.Enqueue(new ColdResetCommand());
        StateText = "Running";
        RunPauseHeader = "Pause";
        StepCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private void Step() => Runner.Machine.Enqueue(new SingleStepCommand());

    private bool CanStep() => Runner.Machine.IsPaused;

    [RelayCommand]
    private void OpenCassetteDeck() => OpenDeckWindowRequested?.Invoke();

    [RelayCommand]
    private void OpenConfig() => OpenConfigWindowRequested?.Invoke();

    [RelayCommand]
    private void ToggleTurbo()
    {
        IsTurbo = !IsTurbo;
        Runner.Turbo = IsTurbo;
        SpeedText = IsTurbo ? "Turbo" : $"{Runner.SpeedPercent}%";
    }

    // ── Video prefs commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void SetDisplayMode(DisplayMode mode) => DisplayMode = mode;

    [RelayCommand]
    private void ToggleIntegerScale() => IntegerScale = !IntegerScale;

    [RelayCommand]
    private void TogglePalAspect() => PalAspect = !PalAspect;

    [RelayCommand]
    private void ToggleScanlines() => ShowScanlines = !ShowScanlines;

    [RelayCommand]
    private void ToggleDebugOverlay() => ShowDebugOverlay = !ShowDebugOverlay;

    partial void OnAudioMuteChanged(bool value)   => Runner.Audio.Mute   = value;
    partial void OnAudioVolumeChanged(float value) => Runner.Audio.Volume = value;

    [RelayCommand]
    private void ToggleAudioMute() => AudioMute = !AudioMute;

    // ── Save / Load state ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveStateAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Machine State",
            SuggestedFileName = "machine.state",
            FileTypeChoices = [new FilePickerFileType("P2000T State") { Patterns = ["*.state"] }],
            DefaultExtension = "state",
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            Runner.SaveStateToStream(stream);
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Save failed:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadStateAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Machine State",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("P2000T State") { Patterns = ["*.state"] }],
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var machine = await Task.Run(() => MachineStateFile.Load(ms));
            Runner.ReconfigureWithMachine(machine);
        }
        catch (InvalidDataException ex)
        {
            ShowMessageRequested?.Invoke($"Cannot load state file — version mismatch or corrupt file:\n{ex.Message}");
        }
        catch (Exception ex)
        {
            ShowMessageRequested?.Invoke($"Load failed:\n{ex.Message}");
        }
    }

    private static TopLevel? GetTopLevel()
    {
        var mainWindow = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return mainWindow as TopLevel ?? TopLevel.GetTopLevel(mainWindow);
    }

    [RelayCommand]
    private unsafe void Screenshot()
    {
        var pixels = Runner.GetCurrentFrame();
        var bitmap = new WriteableBitmap(
            new Avalonia.PixelSize(Video.Width, Video.Height),
            new Avalonia.Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);

        using (var fb = bitmap.Lock())
        {
            fixed (uint* src = pixels)
            {
                int srcStride = Video.Width * sizeof(uint);
                if (fb.RowBytes == srcStride)
                    Buffer.MemoryCopy(src, fb.Address.ToPointer(), (long)fb.RowBytes * fb.Size.Height, (long)srcStride * Video.Height);
                else
                    for (int row = 0; row < Video.Height; row++)
                        Buffer.MemoryCopy((byte*)src + row * srcStride, (byte*)fb.Address + row * fb.RowBytes, srcStride, srcStride);
            }
        }

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var path = Path.Combine(folder, $"P2000T-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");

        using var stream = File.Create(path);
        bitmap.Save(stream);
    }

    public void Dispose()
    {
        Runner.FrameReady -= OnFrameReady;
        CassetteVm.Detach();
        Runner.Dispose();
    }
}

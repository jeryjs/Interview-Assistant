using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Naveen_Sir.Models;
using Naveen_Sir.Services;

namespace Naveen_Sir;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _transcriptLines = [];
    private readonly ObservableCollection<ChipItem> _chipFeed = [];

    private readonly AppState _state;
    private readonly AssistantEngine _assistantEngine;

    private CancellationTokenSource? _threadCts;
    private ThreadWindow? _threadWindow;
    private bool _suppressToggleHandlers;

    public MainWindow()
    {
        _state = AppStateStore.Load();
        _assistantEngine = new AssistantEngine(_state);
        InitializeComponent();

        TranscriptList.ItemsSource = _transcriptLines;
        ChipItemsControl.ItemsSource = _chipFeed;

        ApplyTheme(ThemeService.IsDarkTheme());
        ApplyStateToUi();
        BindEngine();
        _assistantEngine.Start();

        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SetStatus("Ready");
    }

    private void BindEngine()
    {
        _assistantEngine.TranscriptGenerated += line => Dispatcher.Invoke(() => OnTranscriptGenerated(line));
        _assistantEngine.ChipsGenerated += chips => Dispatcher.Invoke(() => OnChipsGenerated(chips));
        _assistantEngine.StatusChanged += message => Dispatcher.Invoke(() => SetStatus(message));
    }

    private void OnTranscriptGenerated(string line)
    {
        _transcriptLines.Add(line);
        while (_transcriptLines.Count > 400)
        {
            _transcriptLines.RemoveAt(0);
        }
        TranscriptList.ScrollIntoView(_transcriptLines[^1]);
    }

    private void OnChipsGenerated(IReadOnlyList<ChipItem> chips)
    {
        var existing = _chipFeed.Select(static c => c.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var chip in chips.Where(chip => !existing.Contains(chip.Text)))
        {
            _chipFeed.Add(chip);
        }

        while (_chipFeed.Count > _state.MaxChipCount)
        {
            _chipFeed.RemoveAt(0);
        }

        _state.ChipFeed = _chipFeed.ToList();
        _ = AppStateStore.SaveAsync(_state);
        SetStatus($"Updated chips at {DateTime.Now:HH:mm:ss}");
    }

    private void ApplyStateToUi()
    {
        _suppressToggleHandlers = true;
        _state.Normalize();
        MicToggle.IsChecked = _state.MicEnabled;
        SystemToggle.IsChecked = _state.SystemAudioEnabled;
        ScreenToggle.IsChecked = _state.ScreenShareEnabled;
        TopmostToggle.IsChecked = _state.PinOnTop;
        Topmost = _state.PinOnTop;
        TranscriptExpander.IsExpanded = _state.TranscriptExpanded;

        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
        _chipFeed.Clear();
        foreach (var chip in _state.ChipFeed)
        {
            _chipFeed.Add(chip);
        }

        var activeLoadout = _state.ResolveActiveLoadout();
        ProviderStatusText.Text = $"Provider: {activeLoadout.Name} · {activeLoadout.ModelId} · {activeLoadout.Endpoint}";
        _suppressToggleHandlers = false;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private async void OnCaptureToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandlers)
        {
            return;
        }

        _state.MicEnabled = MicToggle.IsChecked == true;
        _state.SystemAudioEnabled = SystemToggle.IsChecked == true;
        _state.ScreenShareEnabled = ScreenToggle.IsChecked == true;
        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
        await AppStateStore.SaveAsync(_state);
        SetStatus("Capture state updated");
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new LoadoutDialog(_state.ProviderLoadouts, _state.ActiveLoadoutId)
        {
            Owner = this,
        };

        var result = dialog.ShowDialog();
        if (result != true)
        {
            return;
        }

        _state.ProviderLoadouts = dialog.ResultLoadouts.ToList();
        _state.ActiveLoadoutId = dialog.ResultActiveLoadoutId;
        _state.Normalize();
        await AppStateStore.SaveAsync(_state);
        ApplyStateToUi();
        SetStatus("Provider settings saved");
    }

    private async void OnClearChips(object sender, RoutedEventArgs e)
    {
        _chipFeed.Clear();
        _state.ChipFeed.Clear();
        _state.ThreadCache.Clear();
        await AppStateStore.SaveAsync(_state);
        _threadCts?.Cancel();
        _threadWindow?.Close();
        _threadWindow = null;
        SetStatus("Cleared chip feed and thread cache");
    }

    private async void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChipItem chip)
        {
            return;
        }

        SetStatus($"Opening thread: {chip.Text}");

        _threadCts?.Cancel();
        _threadCts = new CancellationTokenSource();
        var cancellationToken = _threadCts.Token;

        if (_threadWindow is null || !_threadWindow.IsLoaded)
        {
            _threadWindow = new ThreadWindow
            {
                Owner = this,
            };

            _threadWindow.Closed += (_, _) =>
            {
                _threadCts?.Cancel();
                _threadWindow = null;
            };
        }

        _threadWindow.Show();
        _threadWindow.Activate();
        await _threadWindow.SetTopicAsync(chip.Text);

        if (_state.ThreadCache.TryGetValue(chip.Id, out var cachedThread))
        {
            await _threadWindow.LoadMarkdownAsync(cachedThread);
            SetStatus("Loaded cached thread");
            return;
        }

        try
        {
            await foreach (var chunk in _assistantEngine.StreamTopicOverviewAsync(chip.Text, _state.ResolveActiveLoadout(), cancellationToken))
            {
                if (_threadWindow is null)
                {
                    return;
                }

                await _threadWindow.AppendMarkdownAsync(chunk);
            }

            if (_threadWindow is not null)
            {
                _state.ThreadCache[chip.Id] = _threadWindow.GetCurrentMarkdown();
            }

            await AppStateStore.SaveAsync(_state);
            SetStatus("Thread generated and cached");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Thread stream canceled");
        }
    }

    private async void OnPinTopToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandlers)
        {
            return;
        }

        _state.PinOnTop = TopmostToggle.IsChecked == true;
        Topmost = _state.PinOnTop;
        await AppStateStore.SaveAsync(_state);
        SetStatus(_state.PinOnTop ? "Pinned on top" : "Pin released");
    }

    private async void OnTranscriptExpanded(object sender, RoutedEventArgs e)
    {
        _state.TranscriptExpanded = true;
        await AppStateStore.SaveAsync(_state);
    }

    private async void OnTranscriptCollapsed(object sender, RoutedEventArgs e)
    {
        _state.TranscriptExpanded = false;
        await AppStateStore.SaveAsync(_state);
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Dispatcher.Invoke(() => ApplyTheme(ThemeService.IsDarkTheme()));
        }
    }

    private void ApplyTheme(bool isDark)
    {
        var windowBackground = isDark ? "#0C1118" : "#EDF3FB";
        var panelBackground = isDark ? "#121B29" : "#FFFFFF";
        var panelBorder = isDark ? "#293C57" : "#CAD9ED";
        var controlBackground = isDark ? "#1D2B3F" : "#E5EEF9";
        var controlBorder = isDark ? "#406089" : "#ABC4E4";
        var primaryText = isDark ? "#EEF4FF" : "#15243A";
        var secondaryText = isDark ? "#A9C0E1" : "#4A668D";
        var chipSurface = isDark ? "#1F3553" : "#DCEAFF";
        var chipBorder = isDark ? "#4C77AE" : "#97B8E3";
        var transcriptSurface = isDark ? "#111B2A" : "#F4F9FF";

        Resources["WindowBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(windowBackground));
        Resources["PanelBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(panelBackground));
        Resources["PanelBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(panelBorder));
        Resources["ControlBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(controlBackground));
        Resources["ControlBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(controlBorder));
        Resources["PrimaryTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(primaryText));
        Resources["SecondaryTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(secondaryText));
        Resources["ChipSurfaceBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chipSurface));
        Resources["ChipBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chipBorder));
        Resources["TranscriptSurfaceBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(transcriptSurface));
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _threadCts?.Cancel();
        _threadWindow?.Close();
        _state.ChipFeed = _chipFeed.ToList();
        AppStateStore.SaveAsync(_state).GetAwaiter().GetResult();
        _assistantEngine.Dispose();
        base.OnClosing(e);
    }
}
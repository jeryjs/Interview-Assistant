using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private readonly ObservableCollection<UiLogEntry> _logs = [];
    private readonly ObservableCollection<string> _quickModelOptions = [];
    private readonly ICollectionView _chipView;

    private readonly AppState _state;
    private readonly AssistantEngine _assistantEngine;

    private CancellationTokenSource? _threadCts;
    private ThreadWindow? _threadWindow;
    private bool _suppressToggleHandlers;
    private bool _suppressModelEvents;
    private bool _suppressContextWindowEvents;
    private string _chipSearchText = string.Empty;

    public MainWindow()
    {
        _state = AppStateStore.Load();
        _assistantEngine = new AssistantEngine(_state);
        _chipView = CollectionViewSource.GetDefaultView(_chipFeed);
        _chipView.Filter = FilterChip;
        InitializeComponent();

        TranscriptList.ItemsSource = _transcriptLines;
        ChipItemsControl.ItemsSource = _chipView;
        LogList.ItemsSource = _logs;
        ChipModelCombo.ItemsSource = _quickModelOptions;
        TopicModelCombo.ItemsSource = _quickModelOptions;

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
        _assistantEngine.ScreenSourceUnavailable += message => Dispatcher.Invoke(() => OnScreenSourceUnavailable(message));
    }

    private async void OnScreenSourceUnavailable(string message)
    {
        if (_state.ScreenShareEnabled)
        {
            _state.ScreenShareEnabled = false;
            _suppressToggleHandlers = true;
            ScreenToggle.IsChecked = false;
            _suppressToggleHandlers = false;
            _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
            await AppStateStore.SaveAsync(_state);
        }

        SetStatus(message);
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
            EnsureChipPalette(chip);
            _chipFeed.Add(chip);
        }

        while (_chipFeed.Count > _state.MaxChipCount)
        {
            _chipFeed.RemoveAt(0);
        }

        _state.ChipFeed = _chipFeed.ToList();
        _ = AppStateStore.SaveAsync(_state);
        _chipView.Refresh();
        SetStatus($"Updated chips at {DateTime.Now:HH:mm:ss}");
    }

    private void ApplyStateToUi()
    {
        _suppressToggleHandlers = true;
        _state.Normalize();
        if (!_state.MicEnabled && !_state.SystemAudioEnabled && !_state.ScreenShareEnabled)
        {
            _state.IsPaused = true;
        }

        PauseToggle.IsChecked = _state.IsPaused;
        UpdatePauseUi(_state.IsPaused);
        MicToggle.IsChecked = _state.MicEnabled;
        SystemToggle.IsChecked = _state.SystemAudioEnabled;
        ScreenToggle.IsChecked = _state.ScreenShareEnabled;
        TopmostToggle.IsChecked = _state.PinOnTop;
        Topmost = _state.PinOnTop;
        TranscriptExpander.IsExpanded = _state.TranscriptExpanded;
        _suppressContextWindowEvents = true;
        ContextWindowSlider.Value = _state.ContextWindowSeconds;
        UpdateContextWindowLabel();
        _suppressContextWindowEvents = false;

        ApplyScreenSourceToEngine();
        RefreshQuickModelUi();
        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
        _chipFeed.Clear();
        foreach (var chip in _state.ChipFeed)
        {
            EnsureChipPalette(chip);
            _chipFeed.Add(chip);
        }
        _chipView.Refresh();

        var activeLoadout = _state.ResolveActiveLoadout();
        ProviderStatusText.Text = $"Provider: {activeLoadout.Name} · {activeLoadout.ModelId} · {activeLoadout.Endpoint}";
        _suppressToggleHandlers = false;
    }

    private void SetStatus(string message)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();

        const int maxLength = 160;
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength] + "…";
        }

        StatusText.Text = normalized;
        StatusText.ToolTip = message;
        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (_logs.Count > 0 && string.Equals(_logs[0].RawMessage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _logs.Insert(0, new UiLogEntry(normalized));
        const int maxLogCount = 220;
        while (_logs.Count > maxLogCount)
        {
            _logs.RemoveAt(_logs.Count - 1);
        }
    }

    private static void EnsureChipPalette(ChipItem chip)
    {
        if (!string.IsNullOrWhiteSpace(chip.BorderColor)
            && !string.IsNullOrWhiteSpace(chip.GradientStartColor)
            && !string.IsNullOrWhiteSpace(chip.GradientEndColor))
        {
            return;
        }

        var palette = ChipColorService.ForText(chip.Text);
        chip.BorderColor = palette.BorderHex;
        chip.GradientStartColor = palette.GradientStartHex;
        chip.GradientEndColor = palette.GradientEndHex;
    }

    private void OnLogItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not UiLogEntry entry)
        {
            return;
        }

        entry.IsExpanded = !entry.IsExpanded;
        LogList.SelectedItem = null;
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

        if (_state.ScreenShareEnabled && !HasValidConfiguredScreenSource())
        {
            _state.ScreenShareEnabled = false;
            _suppressToggleHandlers = true;
            ScreenToggle.IsChecked = false;
            _suppressToggleHandlers = false;
            SetStatus("Selected screen source is unavailable. Use the cog to choose another source.");
        }

        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);

        var allCaptureDisabled = !_state.MicEnabled && !_state.SystemAudioEnabled && !_state.ScreenShareEnabled;
        if (allCaptureDisabled)
        {
            await SetPausedStateAsync(true, "All capture inputs disabled — paused");
            return;
        }

        if (_state.IsPaused)
        {
            await SetPausedStateAsync(false, "Capture input re-enabled — resumed");
            return;
        }

        await AppStateStore.SaveAsync(_state);
        SetStatus("Capture state updated");
    }

    private async void OnPauseToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandlers)
        {
            return;
        }

        await SetPausedStateAsync(PauseToggle.IsChecked == true, PauseToggle.IsChecked == true ? "Paused all activity" : "Resumed all activity");
    }

    private async Task SetPausedStateAsync(bool paused, string statusMessage)
    {
        if (_state.IsPaused == paused)
        {
            await AppStateStore.SaveAsync(_state);
            SetStatus(statusMessage);
            return;
        }

        _state.IsPaused = paused;
        _suppressToggleHandlers = true;
        PauseToggle.IsChecked = paused;
        _suppressToggleHandlers = false;

        UpdatePauseUi(paused);
        _assistantEngine.SetPaused(paused);

        if (paused)
        {
            _threadCts?.Cancel();
            if (_threadWindow is not null)
            {
                _threadWindow.Close();
                _threadWindow = null;
            }
        }

        await AppStateStore.SaveAsync(_state);
        SetStatus(statusMessage);
    }

    private void UpdatePauseUi(bool isPaused)
    {
        PauseToggle.Content = isPaused ? "▶" : "⏸";
        PauseToggle.ToolTip = isPaused ? "Resume all" : "Pause all";
    }

    private void ApplyScreenSourceToEngine()
    {
        var mode = ResolveScreenSourceMode();
        _assistantEngine.SetScreenSource(mode, _state.ScreenSourceWindowHandle, _state.ScreenSourceWindowTitle);
        UpdateScreenSourceTooltip(mode);
    }

    private ScreenSourceMode ResolveScreenSourceMode()
    {
        return string.Equals(_state.ScreenSourceMode, "SpecificWindow", StringComparison.Ordinal)
            ? ScreenSourceMode.SpecificWindow
            : ScreenSourceMode.EntireScreen;
    }

    private bool HasValidConfiguredScreenSource()
    {
        var mode = ResolveScreenSourceMode();
        if (mode == ScreenSourceMode.EntireScreen)
        {
            return true;
        }

        return WindowCatalogService.IsWindowValid(_state.ScreenSourceWindowHandle);
    }

    private void UpdateScreenSourceTooltip(ScreenSourceMode mode)
    {
        if (mode == ScreenSourceMode.EntireScreen)
        {
            ScreenSourceButton.ToolTip = "Screen source: Entire screen";
            return;
        }

        var label = string.IsNullOrWhiteSpace(_state.ScreenSourceWindowTitle)
            ? "Specific window"
            : _state.ScreenSourceWindowTitle;
        ScreenSourceButton.ToolTip = $"Screen source: {label}";
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

    private void RefreshQuickModelUi()
    {
        _suppressModelEvents = true;

        var activeLoadout = _state.ResolveActiveLoadout();
        if (string.IsNullOrWhiteSpace(activeLoadout.ChipModelId))
        {
            activeLoadout.ChipModelId = activeLoadout.ModelId;
        }

        if (string.IsNullOrWhiteSpace(activeLoadout.TopicModelId))
        {
            activeLoadout.TopicModelId = activeLoadout.ModelId;
        }

        var options = _state.ProviderLoadouts
            .SelectMany(loadout => new[]
            {
                loadout.ModelId,
                loadout.ChipModelId,
                loadout.TopicModelId,
            })
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (options.Count == 0)
        {
            options.Add("gpt-4o-mini");
            options.Add("gpt-4.1-mini");
            options.Add("gpt-4.1");
        }

        _quickModelOptions.Clear();
        foreach (var option in options)
        {
            _quickModelOptions.Add(option);
        }

        ChipModelCombo.Text = activeLoadout.ResolveChipModelId();
        TopicModelCombo.Text = activeLoadout.ResolveTopicModelId();
        ChipModelCombo.ToolTip = $"Chip model: {activeLoadout.ResolveChipModelId()}";
        TopicModelCombo.ToolTip = $"Topic model: {activeLoadout.ResolveTopicModelId()}";

        _suppressModelEvents = false;
    }

    private async void OnQuickModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ApplyQuickModelSelectionAsync(sender as ComboBox);
    }

    private void OnContextWindowValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressContextWindowEvents)
        {
            return;
        }

        _state.ContextWindowSeconds = (int)Math.Round(ContextWindowSlider.Value);
        UpdateContextWindowLabel();
        _ = AppStateStore.SaveAsync(_state);
    }

    private void OnChipSearchChanged(object sender, TextChangedEventArgs e)
    {
        _chipSearchText = ChipSearchBox.Text?.Trim() ?? string.Empty;
        _chipView.Refresh();
    }

    private bool FilterChip(object item)
    {
        if (item is not ChipItem chip)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_chipSearchText))
        {
            return true;
        }

        return chip.Text.Contains(_chipSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateContextWindowLabel()
    {
        ContextSecondsLabel.Text = $"Ctx {_state.ContextWindowSeconds}s";
    }

    private async void OnQuickModelCommit(object sender, RoutedEventArgs e)
    {
        await ApplyQuickModelSelectionAsync(sender as ComboBox);
    }

    private async Task ApplyQuickModelSelectionAsync(ComboBox? combo)
    {
        if (_suppressModelEvents || combo is null)
        {
            return;
        }

        var selectedModel = combo.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return;
        }

        var activeLoadout = _state.ResolveActiveLoadout();
        var isChipModel = ReferenceEquals(combo, ChipModelCombo);
        if (isChipModel)
        {
            activeLoadout.ChipModelId = selectedModel;
        }
        else
        {
            activeLoadout.TopicModelId = selectedModel;
        }

        if (!_quickModelOptions.Any(existing => string.Equals(existing, selectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            _quickModelOptions.Insert(0, selectedModel);
        }

        await AppStateStore.SaveAsync(_state);
        SetStatus(isChipModel
            ? $"Chip model set to {selectedModel}"
            : $"Topic model set to {selectedModel}");
        RefreshQuickModelUi();
    }

    private async void OnOpenScreenSourceSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new ScreenSourceDialog(ResolveScreenSourceMode(), _state.ScreenSourceWindowHandle)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _state.ScreenSourceMode = dialog.ResultMode == ScreenSourceMode.SpecificWindow
            ? "SpecificWindow"
            : "EntireScreen";
        _state.ScreenSourceWindowHandle = dialog.ResultWindowHandle;
        _state.ScreenSourceWindowTitle = dialog.ResultWindowTitle;

        ApplyScreenSourceToEngine();

        if (_state.ScreenShareEnabled && !HasValidConfiguredScreenSource())
        {
            _state.ScreenShareEnabled = false;
            _suppressToggleHandlers = true;
            ScreenToggle.IsChecked = false;
            _suppressToggleHandlers = false;
            _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
            await AppStateStore.SaveAsync(_state);
            SetStatus("Selected window is not currently available. Screen share turned off.");
            return;
        }

        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
        await AppStateStore.SaveAsync(_state);
        SetStatus("Screen source updated");
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
                await _threadWindow.FlushAsync();
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
        catch (Exception ex)
        {
            SetStatus($"Thread generation failed: {ex.Message}");
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
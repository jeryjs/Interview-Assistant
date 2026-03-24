using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        _state = AppStateStore.Load();
        _assistantEngine = new AssistantEngine(_state);
        InitializeComponent();

        TranscriptList.ItemsSource = _transcriptLines;
        ChipList.ItemsSource = _chipFeed;

        ApplyStateToUi();
        BindEngine();
        _assistantEngine.Start();
        SetStatus("Ready");
    }

    private void BindEngine()
    {
        _assistantEngine.TranscriptGenerated += OnTranscriptGenerated;
        _assistantEngine.ChipsGenerated += OnChipsGenerated;
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

    private void OnChipsGenerated(IReadOnlyList<string> chips)
    {
        var existing = _chipFeed.Select(static c => c.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var chipText in chips.Where(chipText => !existing.Contains(chipText)))
        {
            _chipFeed.Add(new ChipItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = chipText,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        while (_chipFeed.Count > 200)
        {
            _chipFeed.RemoveAt(0);
        }

        _state.ChipFeed = _chipFeed.ToList();
        _ = AppStateStore.SaveAsync(_state);
        if (_chipFeed.Count > 0)
        {
            ChipList.ScrollIntoView(_chipFeed[^1]);
        }
        SetStatus($"Updated chips at {DateTime.Now:HH:mm:ss}");
    }

    private void ApplyStateToUi()
    {
        _state.Normalize();
        MicToggle.IsChecked = _state.MicEnabled;
        SystemToggle.IsChecked = _state.SystemAudioEnabled;
        ScreenToggle.IsChecked = _state.ScreenShareEnabled;

        _assistantEngine.SetCaptureState(_state.MicEnabled, _state.SystemAudioEnabled, _state.ScreenShareEnabled);
        _chipFeed.Clear();
        foreach (var chip in _state.ChipFeed)
        {
            _chipFeed.Add(chip);
        }

        var activeLoadout = _state.ResolveActiveLoadout();
        ProviderStatusText.Text = $"Provider: {activeLoadout.Name} · {activeLoadout.ModelId}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private async void OnCaptureToggleChanged(object sender, RoutedEventArgs e)
    {
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
        ThreadPanel.Visibility = Visibility.Collapsed;
        SetStatus("Cleared chip feed and thread cache");
    }

    private async void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ChipItem chip)
        {
            return;
        }

        ThreadPanel.Visibility = Visibility.Visible;
        ThreadTitleText.Text = $"Topic: {chip.Text}";
        SetStatus($"Opening thread: {chip.Text}");

        _threadCts?.Cancel();
        _threadCts = new CancellationTokenSource();
        var cancellationToken = _threadCts.Token;

        if (_state.ThreadCache.TryGetValue(chip.Id, out var cachedThread))
        {
            ThreadContentBox.Text = cachedThread;
            SetStatus("Loaded cached thread");
            return;
        }

        ThreadContentBox.Text = string.Empty;
        try
        {
            await foreach (var chunk in _assistantEngine.StreamTopicOverviewAsync(chip.Text, _state.ResolveActiveLoadout(), cancellationToken))
            {
                ThreadContentBox.AppendText(chunk);
                ThreadContentBox.ScrollToEnd();
            }

            _state.ThreadCache[chip.Id] = ThreadContentBox.Text;
            await AppStateStore.SaveAsync(_state);
            SetStatus("Thread generated and cached");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Thread stream canceled");
        }
    }

    private void OnCloseThread(object sender, RoutedEventArgs e)
    {
        _threadCts?.Cancel();
        ThreadPanel.Visibility = Visibility.Collapsed;
        SetStatus("Thread closed");
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
        _threadCts?.Cancel();
        _state.ChipFeed = _chipFeed.ToList();
        AppStateStore.SaveAsync(_state).GetAwaiter().GetResult();
        _assistantEngine.Dispose();
        base.OnClosing(e);
    }
}
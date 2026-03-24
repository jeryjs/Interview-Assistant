using System.Collections.ObjectModel;
using System.Windows;
using Naveen_Sir.Models;
using Naveen_Sir.Services;

namespace Naveen_Sir;

public partial class ScreenSourceDialog : Window
{
    private readonly ObservableCollection<WindowCatalogService.WindowCandidate> _windows = [];
    private readonly long _currentHandle;

    public ScreenSourceMode ResultMode { get; private set; }
    public long ResultWindowHandle { get; private set; }
    public string ResultWindowTitle { get; private set; } = string.Empty;

    public ScreenSourceDialog(ScreenSourceMode mode, long selectedHandle)
    {
        InitializeComponent();

        _currentHandle = selectedHandle;
        ResultMode = mode;
        WindowList.ItemsSource = _windows;

        EntireScreenRadio.IsChecked = mode == ScreenSourceMode.EntireScreen;
        SpecificWindowRadio.IsChecked = mode == ScreenSourceMode.SpecificWindow;

        ReloadWindows();
        UpdateUiState();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateUiState();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        ReloadWindows();
        UpdateUiState();
    }

    private void ReloadWindows()
    {
        var selectedHandle = (WindowList.SelectedItem as WindowCatalogService.WindowCandidate)?.HandleValue;
        _windows.Clear();

        foreach (var window in WindowCatalogService.GetVisibleWindows())
        {
            _windows.Add(window);
        }

        var preferredHandle = selectedHandle ?? _currentHandle;
        if (preferredHandle != 0)
        {
            var match = _windows.FirstOrDefault(window => window.HandleValue == preferredHandle);
            if (match is not null)
            {
                WindowList.SelectedItem = match;
                return;
            }
        }

        if (_windows.Count > 0)
        {
            WindowList.SelectedIndex = 0;
        }
    }

    private void UpdateUiState()
    {
        var specificWindowMode = SpecificWindowRadio.IsChecked == true;
        WindowList.IsEnabled = specificWindowMode;
        HintText.Text = specificWindowMode
            ? "Select a currently open top-level window."
            : "Captures the full primary screen.";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ResultMode = SpecificWindowRadio.IsChecked == true
            ? ScreenSourceMode.SpecificWindow
            : ScreenSourceMode.EntireScreen;

        if (ResultMode == ScreenSourceMode.EntireScreen)
        {
            ResultWindowHandle = 0;
            ResultWindowTitle = string.Empty;
            DialogResult = true;
            return;
        }

        if (WindowList.SelectedItem is not WindowCatalogService.WindowCandidate selected)
        {
            HintText.Text = "Select a window before saving.";
            return;
        }

        ResultWindowHandle = selected.HandleValue;
        ResultWindowTitle = selected.Title;
        DialogResult = true;
    }
}
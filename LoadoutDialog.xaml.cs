using System.Collections.ObjectModel;
using System.Windows;
using Naveen_Sir.Models;

namespace Naveen_Sir;

public partial class LoadoutDialog : Window
{
    private readonly ObservableCollection<ProviderLoadout> _editableLoadouts;

    public IReadOnlyList<ProviderLoadout> ResultLoadouts { get; private set; } = [];
    public string ResultActiveLoadoutId { get; private set; } = string.Empty;

    public LoadoutDialog(IReadOnlyList<ProviderLoadout> sourceLoadouts, string activeLoadoutId)
    {
        InitializeComponent();

        _editableLoadouts = new ObservableCollection<ProviderLoadout>(sourceLoadouts.Select(loadout => loadout.Clone()));
        if (_editableLoadouts.Count == 0)
        {
            _editableLoadouts.Add(ProviderLoadout.CreateDefault());
        }

        LoadoutsGrid.ItemsSource = _editableLoadouts;
        LoadoutsGrid.SelectedIndex = 0;

        ActiveLoadoutCombo.ItemsSource = _editableLoadouts;
        ActiveLoadoutCombo.SelectedValue = _editableLoadouts.Any(loadout => loadout.Id == activeLoadoutId)
            ? activeLoadoutId
            : _editableLoadouts[0].Id;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var loadout = ProviderLoadout.CreateDefault();
        loadout.Id = Guid.NewGuid().ToString("N");
        loadout.Name = "New Provider";
        _editableLoadouts.Add(loadout);
        LoadoutsGrid.SelectedItem = loadout;
        ActiveLoadoutCombo.SelectedItem = loadout;
    }

    private void OnFork(object sender, RoutedEventArgs e)
    {
        var source = LoadoutsGrid.SelectedItem as ProviderLoadout ?? _editableLoadouts.First();
        var forked = source.Fork();
        _editableLoadouts.Add(forked);
        LoadoutsGrid.SelectedItem = forked;
        ActiveLoadoutCombo.SelectedItem = forked;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (LoadoutsGrid.SelectedItem is not ProviderLoadout selected)
        {
            return;
        }

        if (_editableLoadouts.Count == 1)
        {
            return;
        }

        _editableLoadouts.Remove(selected);
        LoadoutsGrid.SelectedIndex = 0;
        if (ActiveLoadoutCombo.SelectedItem == selected)
        {
            ActiveLoadoutCombo.SelectedIndex = 0;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var loadout in _editableLoadouts)
        {
            loadout.Temperature = Math.Clamp(loadout.Temperature, 0f, 1.5f);
            loadout.MaxTokens = Math.Clamp(loadout.MaxTokens, 128, 4096);
        }

        var active = ActiveLoadoutCombo.SelectedItem as ProviderLoadout ?? _editableLoadouts[0];
        ResultLoadouts = _editableLoadouts.ToList();
        ResultActiveLoadoutId = active.Id;
        DialogResult = true;
    }
}
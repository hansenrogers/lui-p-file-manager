using System.Collections.ObjectModel;
using System.Windows;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App;

public partial class LocationEditorWindow : Window
{
    private readonly ILocationRepository _locationRepository;
    private readonly LocationKind _kind;

    private readonly ObservableCollection<ConfiguredLocation> _locations = new();

    public LocationEditorWindow(ILocationRepository locationRepository, LocationKind kind)
    {
        _locationRepository = locationRepository;
        _kind = kind;

        InitializeComponent();
        HeaderTextBlock.Text = kind == LocationKind.Source ? "Source Locations" : "Destination Locations";
        LocationsDataGrid.ItemsSource = _locations;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var rows = await _locationRepository.GetByKindAsync(_kind, CancellationToken.None).ConfigureAwait(true);
        _locations.Clear();
        foreach (var row in rows)
        {
            _locations.Add(new ConfiguredLocation
            {
                Id = row.Id,
                Name = row.Name,
                Path = row.Path,
                Kind = row.Kind,
                IsEnabled = row.IsEnabled,
                SortOrder = row.SortOrder
            });
        }
    }

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        var nextOrder = _locations.Count == 0 ? 1 : _locations.Max(x => x.SortOrder) + 1;
        _locations.Add(new ConfiguredLocation
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = _kind == LocationKind.Source ? "New Source" : "New Destination",
            Path = string.Empty,
            Kind = _kind,
            IsEnabled = true,
            SortOrder = nextOrder
        });
    }

    private void RemoveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocationsDataGrid.SelectedItem is not ConfiguredLocation selected)
        {
            return;
        }

        _locations.Remove(selected);
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var invalid = _locations.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Path));
        if (invalid is not null)
        {
            MessageBox.Show(this, "Each location must have a name and a path.", "Locations", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = await _locationRepository.GetByKindAsync(_kind, CancellationToken.None).ConfigureAwait(true);
        var currentIds = _locations.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _locations)
        {
            await _locationRepository.UpsertAsync(row, CancellationToken.None).ConfigureAwait(true);
        }

        foreach (var toDelete in existing.Where(x => !currentIds.Contains(x.Id)))
        {
            await _locationRepository.DeleteAsync(toDelete.Id, _kind, CancellationToken.None).ConfigureAwait(true);
        }

        MessageBox.Show(this, "Locations saved.", "Locations", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }
}

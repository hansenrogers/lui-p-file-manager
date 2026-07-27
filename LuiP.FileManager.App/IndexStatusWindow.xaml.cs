using System.Windows;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App;

public partial class IndexStatusWindow : Window
{
    private readonly IIndexService _indexService;

    public IndexStatusWindow(IIndexService indexService)
    {
        _indexService = indexService;
        InitializeComponent();
        _ = LoadStatusesAsync();
    }

    private async Task LoadStatusesAsync()
    {
        try
        {
            var statuses = await _indexService.GetLocationStatusesAsync(CancellationToken.None).ConfigureAwait(true);
            StatusesDataGrid.ItemsSource = statuses;
            StatusTextBlock.Text = $"Loaded {statuses.Count} location status row(s).";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to load index status: {ex.Message}";
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadStatusesAsync().ConfigureAwait(true);
    }

    private async void RebuildButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Rebuilding index...";
        try
        {
            await _indexService.RebuildAllAsync(CancellationToken.None).ConfigureAwait(true);
            StatusTextBlock.Text = "Rebuild completed.";
            await LoadStatusesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Rebuild failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Rebuild Index", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

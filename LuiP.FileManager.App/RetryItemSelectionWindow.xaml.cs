using System.Windows;

namespace LuiP.FileManager.App;

public partial class RetryItemSelectionWindow : Window
{
    public IReadOnlyList<string> SelectedPaths { get; private set; } = Array.Empty<string>();

    public RetryItemSelectionWindow(IReadOnlyList<string> candidatePaths)
    {
        InitializeComponent();
        ItemsListBox.ItemsSource = candidatePaths;

        foreach (var path in candidatePaths)
        {
            ItemsListBox.SelectedItems.Add(path);
        }
    }

    private void SelectAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        ItemsListBox.SelectAll();
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        ItemsListBox.UnselectAll();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RetrySelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = ItemsListBox.SelectedItems
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one item.", "Retry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPaths = selected;
        DialogResult = true;
        Close();
    }
}

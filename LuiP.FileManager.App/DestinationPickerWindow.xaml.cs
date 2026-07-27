using System.Windows;
using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.App;

public partial class DestinationPickerWindow : Window
{
    public ConfiguredLocation? SelectedDestination { get; private set; }

    public DestinationPickerWindow(IReadOnlyList<ConfiguredLocation> destinations)
    {
        InitializeComponent();
        DestinationsListBox.ItemsSource = destinations;
        if (destinations.Count > 0)
        {
            DestinationsListBox.SelectedIndex = 0;
        }
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DestinationsListBox.SelectedItem is not ConfiguredLocation selected)
        {
            MessageBox.Show(this, "Select a destination.", "Shell Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedDestination = selected;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

using System.IO;
using System.Windows;

namespace LuiP.FileManager.App;

public partial class SyncBoardSetupWindow : Window
{
    public string SourceRootPath => SourceRootTextBox.Text?.Trim() ?? string.Empty;

    public string DestinationRootPath => DestinationRootTextBox.Text?.Trim() ?? string.Empty;

    public SyncBoardSetupWindow(string sourceRootPath, string destinationRootPath)
    {
        InitializeComponent();
        SourceRootTextBox.Text = sourceRootPath;
        DestinationRootTextBox.Text = destinationRootPath;
    }

    private void BrowseSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = SourceRootTextBox.Text;
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
        {
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select source root folder",
            InitialDirectory = initialDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            SourceRootTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseDestinationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = DestinationRootTextBox.Text;
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
        {
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Dropbox destination root folder",
            InitialDirectory = initialDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DestinationRootTextBox.Text = dialog.FolderName;
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceRootPath) || string.IsNullOrWhiteSpace(DestinationRootPath))
        {
            MessageBox.Show(this, "Source and Dropbox destination are required.", "Sync Board Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(SourceRootPath))
        {
            MessageBox.Show(this, "Source root folder does not exist.", "Sync Board Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(DestinationRootPath);
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

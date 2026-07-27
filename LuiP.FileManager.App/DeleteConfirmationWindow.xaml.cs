using System.Windows;

namespace LuiP.FileManager.App;

public partial class DeleteConfirmationWindow : Window
{
    public bool IsConfirmed { get; private set; }

    public DeleteConfirmationWindow(string locationPath, int selectedCount, long? totalSizeBytes)
    {
        InitializeComponent();

        LocationRun.Text = locationPath;
        CountTextBlock.Text = $"Number of selected items: {selectedCount}";
        SizeTextBlock.Text = totalSizeBytes.HasValue
            ? $"Total size: {FormatBytes(totalSizeBytes.Value)}"
            : "Total size: not available";
        RecoveryTextBlock.Text = "Can be recovered: Yes";
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var idx = 0;

        while (value >= 1024 && idx < units.Length - 1)
        {
            value /= 1024;
            idx++;
        }

        return $"{value:0.##} {units[idx]}";
    }
}

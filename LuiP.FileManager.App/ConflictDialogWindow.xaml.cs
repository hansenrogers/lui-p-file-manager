using System.IO;
using System.Windows;
using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.App;

public partial class ConflictDialogWindow : Window
{
    public ConflictResolution SelectedResolution { get; private set; } = ConflictResolution.KeepBoth;

    public bool ApplyToAll { get; private set; }

    public ConflictDialogWindow(string sourcePath, string destinationPath)
    {
        InitializeComponent();

        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);

        SourceTextBlock.Text = $"Source: {sourcePath}";
        DestinationTextBlock.Text = $"Destination: {destinationPath}";
        SourceMetaTextBlock.Text = $"Source modified: {sourceInfo.LastWriteTime} | size: {FormatBytes(sourceInfo.Length)}";
        DestinationMetaTextBlock.Text = $"Destination modified: {destinationInfo.LastWriteTime} | size: {FormatBytes(destinationInfo.Length)}";
    }

    private void ReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedResolution = ConflictResolution.Replace;
        ApplyToAll = ApplyToAllCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void KeepBothButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedResolution = ConflictResolution.KeepBoth;
        ApplyToAll = ApplyToAllCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedResolution = ConflictResolution.Cancel;
        ApplyToAll = false;
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

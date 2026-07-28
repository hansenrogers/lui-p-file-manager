using System.Windows;

namespace LuiP.FileManager.App;

public partial class DeleteConfirmationWindow : Window
{
    public bool IsConfirmed { get; private set; }

    public DeleteConfirmationWindow(string locationPath, int selectedCount, long? totalSizeBytes)
    {
        InitializeComponent();

        LocationRun.Text = locationPath;
        CountTextBlock.Text = selectedCount == 1
            ? "1 item selected"
            : $"{selectedCount} items selected";
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

}

using System.Windows;
using LuiP.FileManager.Core.Models;

namespace LuiP.FileManager.App;

public partial class InterruptedOperationsWindow : Window
{
    public InterruptedOperationsWindow(IReadOnlyList<FileOperationRecord> interrupted)
    {
        InitializeComponent();
        InterruptedDataGrid.ItemsSource = interrupted;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

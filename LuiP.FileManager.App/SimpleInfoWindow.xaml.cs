using System.Windows;

namespace LuiP.FileManager.App;

public partial class SimpleInfoWindow : Window
{
    public SimpleInfoWindow(string title, string body)
    {
        InitializeComponent();
        Title = title;
        HeaderTextBlock.Text = title;
        BodyTextBlock.Text = body;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

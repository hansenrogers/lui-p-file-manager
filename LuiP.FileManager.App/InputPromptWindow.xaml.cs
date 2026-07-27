using System.Windows;

namespace LuiP.FileManager.App;

public partial class InputPromptWindow : Window
{
    public string? Result { get; private set; }

    public InputPromptWindow(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptTextBlock.Text = prompt;
        ValueTextBox.Text = initialValue;
        ValueTextBox.SelectAll();
        ValueTextBox.Focus();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ValueTextBox.Text;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

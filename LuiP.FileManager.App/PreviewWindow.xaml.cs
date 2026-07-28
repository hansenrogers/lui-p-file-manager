using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace LuiP.FileManager.App;

public partial class PreviewWindow : Window
{
    private string _primaryPath;
    private string? _secondaryPath;
    private int _renderVersion;

    public PreviewWindow(string targetPath, string? comparePath = null)
    {
        _primaryPath = targetPath;
        _secondaryPath = comparePath;
        InitializeComponent();
        _ = LoadPreviewAsync();
    }

    public void SetPaths(string primaryPath, string? comparePath = null)
    {
        _primaryPath = primaryPath;
        _secondaryPath = comparePath;
        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        var renderVersion = ++_renderVersion;
        var compareMode = !string.IsNullOrWhiteSpace(_secondaryPath);

        HeaderTextBlock.Text = compareMode
            ? $"Compare: {_primaryPath}  |  {_secondaryPath}"
            : _primaryPath;

        PropertiesText.Text = compareMode
            ? $"Primary:\n{BuildProperties(_primaryPath)}\n\nSecondary:\n{BuildProperties(_secondaryPath!)}"
            : BuildProperties(_primaryPath);

        SetCompareLayout(compareMode);

        await RenderPaneAsync(_primaryPath, PrimaryTitleTextBlock, PrimaryImagePreview, PrimaryTextPreview, PrimaryMediaPreview, PrimaryPdfPreview, PrimaryUnavailableText, renderVersion).ConfigureAwait(true);
        if (compareMode)
        {
            await RenderPaneAsync(_secondaryPath!, SecondaryTitleTextBlock, SecondaryImagePreview, SecondaryTextPreview, SecondaryMediaPreview, SecondaryPdfPreview, SecondaryUnavailableText, renderVersion).ConfigureAwait(true);
        }
        else
        {
            HidePane(SecondaryImagePreview, SecondaryTextPreview, SecondaryMediaPreview, SecondaryPdfPreview, SecondaryUnavailableText);
            SecondaryTitleTextBlock.Text = string.Empty;
        }
    }

    private void SetCompareLayout(bool compareMode)
    {
        CompareSplitterColumn.Width = compareMode ? new GridLength(5) : new GridLength(0);
        CompareRightColumn.Width = compareMode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        CompareSplitter.Visibility = compareMode ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPaneBorder.Visibility = compareMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RenderPaneAsync(
        string targetPath,
        System.Windows.Controls.TextBlock title,
        System.Windows.Controls.Image imagePreview,
        System.Windows.Controls.TextBox textPreview,
        System.Windows.Controls.MediaElement mediaPreview,
        WebView2 pdfPreview,
        System.Windows.Controls.TextBlock unavailableText,
        int renderVersion)
    {
        title.Text = targetPath;
        HidePane(imagePreview, textPreview, mediaPreview, pdfPreview, unavailableText);

        if (!File.Exists(targetPath))
        {
            unavailableText.Visibility = Visibility.Visible;
            unavailableText.Text = "This item cannot be previewed because it is not an existing file.";
            return;
        }

        var ext = Path.GetExtension(targetPath).ToLowerInvariant();

        if (IsImageExtension(ext))
        {
            imagePreview.Visibility = Visibility.Visible;
            imagePreview.Source = new BitmapImage(new Uri(targetPath));
            return;
        }

        if (IsTextExtension(ext))
        {
            textPreview.Visibility = Visibility.Visible;
            textPreview.Text = ReadTextSafe(targetPath);
            return;
        }

        if (IsMediaExtension(ext))
        {
            mediaPreview.Visibility = Visibility.Visible;
            mediaPreview.Source = new Uri(targetPath);
            mediaPreview.Play();
            return;
        }

        if (IsPdfExtension(ext))
        {
            try
            {
                await pdfPreview.EnsureCoreWebView2Async();
                if (renderVersion != _renderVersion)
                {
                    return;
                }

                pdfPreview.Visibility = Visibility.Visible;
                pdfPreview.Source = new Uri(targetPath);
                return;
            }
            catch (Exception ex)
            {
                unavailableText.Visibility = Visibility.Visible;
                unavailableText.Text = $"Inline PDF preview requires WebView2 runtime. {ex.Message}";
                return;
            }
        }

        if (IsActiveContentExtension(ext))
        {
            unavailableText.Visibility = Visibility.Visible;
            unavailableText.Text = "Inline preview is disabled for this file type for safety. Use Open Default to view the file in its associated app.";
            return;
        }

        unavailableText.Visibility = Visibility.Visible;
        unavailableText.Text = "Preview is not available for this file type. Use Open Default to view the file in its associated app.";
    }

    private static void HidePane(
        System.Windows.Controls.Image imagePreview,
        System.Windows.Controls.TextBox textPreview,
        System.Windows.Controls.MediaElement mediaPreview,
        WebView2 pdfPreview,
        System.Windows.Controls.TextBlock unavailableText)
    {
        imagePreview.Visibility = Visibility.Collapsed;
        imagePreview.Source = null;
        textPreview.Visibility = Visibility.Collapsed;
        textPreview.Text = string.Empty;
        mediaPreview.Stop();
        mediaPreview.Source = null;
        mediaPreview.Visibility = Visibility.Collapsed;
        pdfPreview.Visibility = Visibility.Collapsed;
        pdfPreview.Source = null;
        unavailableText.Visibility = Visibility.Collapsed;
        unavailableText.Text = string.Empty;
    }

    private static bool IsPdfExtension(string ext)
    {
        return string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageExtension(string ext)
    {
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp";
    }

    private static bool IsTextExtension(string ext)
    {
        return ext is ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".md" or ".cs" or ".xaml";
    }

    private static bool IsMediaExtension(string ext)
    {
        return ext is ".mp3" or ".wav" or ".m4a" or ".mp4" or ".mov" or ".wmv" or ".avi";
    }

    private static bool IsActiveContentExtension(string ext)
    {
        return ext is ".htm" or ".html" or ".mht" or ".mhtml" or ".svg";
    }

    private static string ReadTextSafe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var content = reader.ReadToEnd();
            const int max = 1024 * 1024;
            if (content.Length > max)
            {
                return content[..max] + "\n\n[Truncated for preview]";
            }

            return content;
        }
        catch (Exception ex)
        {
            return $"Preview unavailable: {ex.Message}";
        }
    }

    private static string BuildProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return "Item does not exist.";
        }

        var isFile = File.Exists(path);
        var info = isFile ? new FileInfo(path) as FileSystemInfo : new DirectoryInfo(path);

        var lines = new List<string>
        {
            $"Name: {info.Name}",
            $"Full Path: {info.FullName}",
            $"Type: {(isFile ? "File" : "Folder")}",
            $"Created: {info.CreationTime}",
            $"Modified: {info.LastWriteTime}"
        };

        if (isFile)
        {
            var fileInfo = (FileInfo)info;
            lines.Add($"Size: {fileInfo.Length:N0} bytes");
            lines.Add($"Extension: {fileInfo.Extension}");
        }

        return string.Join("\n", lines);
    }

    private void OpenDefaultButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenPathWithShell(_primaryPath);
    }

    private void ShowInFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_primaryPath) && !Directory.Exists(_primaryPath))
        {
            return;
        }

        if (File.Exists(_primaryPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_primaryPath}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _primaryPath,
            UseShellExecute = true
        });
    }

    private void CompareWithButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select file to compare",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(_primaryPath)
                ? Path.GetDirectoryName(_primaryPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetPaths(_primaryPath, dialog.FileName);
    }

    private void SingleViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetPaths(_primaryPath, comparePath: null);
    }

    public static void OpenPathWithShell(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}

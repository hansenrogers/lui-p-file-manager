using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LuiP.FileManager.App;

public partial class PreviewWindow : Window
{
    private readonly string _targetPath;

    public PreviewWindow(string targetPath)
    {
        _targetPath = targetPath;
        InitializeComponent();
        LoadPreview();
    }

    private void LoadPreview()
    {
        HeaderTextBlock.Text = _targetPath;
        PropertiesText.Text = BuildProperties(_targetPath);

        HideAllPreviewControls();

        if (!File.Exists(_targetPath))
        {
            UnavailableText.Visibility = Visibility.Visible;
            UnavailableText.Text = "This item cannot be previewed because it is not an existing file.";
            return;
        }

        var ext = Path.GetExtension(_targetPath).ToLowerInvariant();

        if (IsImageExtension(ext))
        {
            ImagePreview.Visibility = Visibility.Visible;
            ImagePreview.Source = new BitmapImage(new Uri(_targetPath));
            return;
        }

        if (IsTextExtension(ext))
        {
            TextPreview.Visibility = Visibility.Visible;
            TextPreview.Text = ReadTextSafe(_targetPath);
            return;
        }

        if (IsMediaExtension(ext))
        {
            MediaPreview.Visibility = Visibility.Visible;
            MediaPreview.Source = new Uri(_targetPath);
            MediaPreview.Play();
            return;
        }

        if (IsActiveContentExtension(ext))
        {
            UnavailableText.Visibility = Visibility.Visible;
            UnavailableText.Text = "Inline preview is disabled for this file type for safety. Use Open Default to view the file in its associated app.";
            return;
        }

        UnavailableText.Visibility = Visibility.Visible;
        UnavailableText.Text = "Preview is not available for this file type. Use Open Default to view the file in its associated app.";
    }

    private void HideAllPreviewControls()
    {
        ImagePreview.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        MediaPreview.Visibility = Visibility.Collapsed;
        UnavailableText.Visibility = Visibility.Collapsed;
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
        return ext is ".pdf" or ".htm" or ".html" or ".mht" or ".mhtml" or ".svg";
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
        OpenPathWithShell(_targetPath);
    }

    private void ShowInFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_targetPath) && !Directory.Exists(_targetPath))
        {
            return;
        }

        if (File.Exists(_targetPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_targetPath}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _targetPath,
            UseShellExecute = true
        });
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

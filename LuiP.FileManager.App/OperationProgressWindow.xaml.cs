using System.Windows;
using System.Diagnostics;
using LuiP.FileManager.Core.Services;

namespace LuiP.FileManager.App;

public partial class OperationProgressWindow : Window
{
    private readonly Action _cancelAction;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public OperationProgressWindow(string title, Action cancelAction)
    {
        _cancelAction = cancelAction;
        InitializeComponent();
        TitleTextBlock.Text = title;
        CurrentItemTextBlock.Text = "Waiting to start...";
        CountTextBlock.Text = "0 / 0";
        SpeedEtaTextBlock.Text = "Speed: calculating...   ETA: calculating...";
        SummaryTextBlock.Text = "You can cancel this operation at any time.";
    }

    public void UpdateProgress(FileOperationProgress progress)
    {
        CurrentItemTextBlock.Text = $"Current item: {progress.CurrentItemPath}";
        CountTextBlock.Text = $"Completed: {progress.CompletedItems} of {progress.TotalItems}";

        if (progress.TotalItems > 0)
        {
            var pct = (double)progress.CompletedItems / progress.TotalItems * 100.0;
            ItemsProgressBar.Value = pct;
        }

        var elapsedSeconds = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001d);
        var speed = progress.ProcessedBytes / elapsedSeconds;
        var remaining = Math.Max(0L, progress.TotalBytes - progress.ProcessedBytes);
        var eta = speed > 0.01d ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;

        var amountUnitLabel = progress.TotalBytes > 100 ? "bytes" : "units";
        SummaryTextBlock.Text = $"Processed {FormatAmount(progress.ProcessedBytes, amountUnitLabel)} of {FormatAmount(progress.TotalBytes, amountUnitLabel)}.";
        SpeedEtaTextBlock.Text = $"Speed: {FormatAmount((long)speed, amountUnitLabel)}/s   ETA: {FormatDuration(eta)}";
    }

    public void MarkCompleted(string summary)
    {
        ItemsProgressBar.Value = 100;
        SpeedEtaTextBlock.Text = $"Elapsed: {FormatDuration(_stopwatch.Elapsed)}";
        SummaryTextBlock.Text = summary;
        CancelButton.Content = "Close";
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CancelButton.Content?.ToString() == "Close")
        {
            Close();
            return;
        }

        _cancelAction();
        CancelButton.IsEnabled = false;
        SummaryTextBlock.Text = "Cancel requested. Waiting for operation to stop safely...";
    }

    private static string FormatAmount(long value, string unitLabel)
    {
        if (unitLabel == "bytes")
        {
            if (value >= 1024L * 1024L * 1024L)
            {
                return $"{value / (1024d * 1024d * 1024d):0.0} GB";
            }

            if (value >= 1024L * 1024L)
            {
                return $"{value / (1024d * 1024d):0.0} MB";
            }

            if (value >= 1024L)
            {
                return $"{value / 1024d:0.0} KB";
            }

            return $"{value} B";
        }

        return value.ToString();
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "calculating...";
        }

        if (duration.Value.TotalHours >= 1)
        {
            return duration.Value.ToString("hh\\:mm\\:ss");
        }

        return duration.Value.ToString("mm\\:ss");
    }
}

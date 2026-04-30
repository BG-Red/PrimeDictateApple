using System.Text;
using System.Windows;

namespace PrimeDictate;

internal partial class MainWindow : Window
{
    internal MainWindow(DictationWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowSettings();
        }
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowHistory();
        }
    }

    private void OnCopyErrorsClick(object sender, RoutedEventArgs e)
    {
        if (this.DataContext is not DictationWorkspaceViewModel viewModel)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var entry in viewModel.GlobalEntries)
        {
            if (entry.Level != AppLogLevel.Error)
            {
                continue;
            }

            builder.Append('[')
                .Append(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] ")
                .Append(entry.Level)
                .Append(": ")
                .AppendLine(entry.DisplayMessage);
        }

        var text = builder.Length > 0 ? builder.ToString() : "No error entries in Global Activity.";
        this.TrySetClipboardText(text);
    }

    private void OnCopyAllActivityClick(object sender, RoutedEventArgs e)
    {
        if (this.DataContext is not DictationWorkspaceViewModel viewModel)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var entry in viewModel.GlobalEntries)
        {
            builder.Append('[')
                .Append(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] ")
                .Append(entry.Level)
                .Append(": ")
                .AppendLine(entry.DisplayMessage);
        }

        var text = builder.Length > 0 ? builder.ToString() : "No entries in Global Activity.";
        this.TrySetClipboardText(text);
    }

    private void TrySetClipboardText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Unable to copy to clipboard: {ex.Message}",
                "Copy failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            this.DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}

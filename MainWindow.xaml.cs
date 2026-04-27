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

using System.Windows;

namespace PrimeDictate;

internal partial class MainWindow : Window
{
    internal MainWindow(DictationWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }
}

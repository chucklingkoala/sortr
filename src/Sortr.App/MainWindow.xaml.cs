using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sortr.App.ViewModels;

namespace Sortr.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private IEnumerable<FileMatchViewModel> SelectedRows => ResultsGrid.SelectedItems.OfType<FileMatchViewModel>();

    private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let Space reach a focused ComboBox/CheckBox inside a cell; otherwise toggle the highlighted rows.
        if (e.Key == Key.Space && e.OriginalSource is not ComboBox and not CheckBox)
        {
            _viewModel.ToggleSelection(SelectedRows);
            e.Handled = true;
        }
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source)?.Item is FileMatchViewModel row)
            OpenWithShell(row.FullPath);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is FileMatchViewModel row)
            OpenWithShell(row.FullPath);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is FileMatchViewModel row && File.Exists(row.FullPath))
            Process.Start("explorer.exe", $"/select,\"{row.FullPath}\"");
    }

    private void ToggleRows_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSelection(SelectedRows);

    private static void OpenWithShell(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sortr.App.Services;
using Sortr.App.ViewModels;
using Sortr.App.Views;

namespace Sortr.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private PreviewWindow? _preview;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Title = $"Sortr {AppInfo.Version}";
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private IEnumerable<FileMatchViewModel> SelectedRows => ResultsGrid.SelectedItems.OfType<FileMatchViewModel>();

    private string? CurrentRowPath => (ResultsGrid.SelectedItem as FileMatchViewModel)?.FullPath;

    private bool IsPreviewOpen => _preview?.IsVisible == true;

    private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let keys reach a focused ComboBox/CheckBox/TextBox inside a cell (typing "p" in a target search, Esc to
        // cancel it, Space to tick); otherwise they act on the highlighted rows.
        if (e.OriginalSource is ComboBox or CheckBox or TextBox)
            return;
        bool noModifiers = Keyboard.Modifiers == ModifierKeys.None;

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.ToggleSelection(SelectedRows);
                e.Handled = true;
                break;
            case Key.P when noModifiers:
                TogglePreview();
                e.Handled = true;
                break;
            case Key.Escape when noModifiers && IsPreviewOpen:
                HidePreview();
                e.Handled = true;
                break;
        }
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from the target ComboBoxes inside cells; only the grid's own rows matter.
        if (e.OriginalSource == ResultsGrid && IsPreviewOpen)
            _preview!.ShowFile(CurrentRowPath);
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source)?.Item is FileMatchViewModel row)
            ShellActions.Open(row.FullPath, this);
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => ShowPreview();

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRowPath is { } path)
            ShellActions.Open(path, this);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRowPath is { } path)
            ShellActions.ShowInExplorer(path);
    }

    private void ToggleRows_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSelection(SelectedRows);

    private void TogglePreview()
    {
        if (IsPreviewOpen)
            HidePreview();
        else
            ShowPreview();
    }

    private void ShowPreview()
    {
        if (_preview is null)
        {
            _preview = new PreviewWindow { Owner = this };
            _preview.CloseRequested += HidePreview;
            _preview.NavigateRequested += MoveSelection;
        }
        _preview.ShowFile(CurrentRowPath);
        if (!_preview.IsVisible)
            _preview.Show(); // ShowActivated=False: the grid keeps focus, so ↑/↓ steps through files.
    }

    private void HidePreview()
    {
        if (_preview is null)
            return;
        _preview.Clear();
        _preview.Hide();
        Activate();
        ResultsGrid.Focus();
    }

    /// <summary>Moves the grid's highlight up or down one row (↑/↓ pressed in the preview window).</summary>
    private void MoveSelection(int delta)
    {
        var view = _viewModel.ItemsView;
        var rows = view.Cast<FileMatchViewModel>().ToList();
        if (rows.Count == 0)
            return;
        int index = ResultsGrid.SelectedItem is FileMatchViewModel current ? rows.IndexOf(current) : -1;
        int next = Math.Clamp(index + delta, 0, rows.Count - 1);
        if (next == index)
            return;
        ResultsGrid.SelectedItem = rows[next];
        ResultsGrid.ScrollIntoView(rows[next]);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsBusy) || _preview is null)
            return;
        if (_viewModel.IsBusy)
            _preview.Clear(); // a playing video holds its file open, which would make the move fail
        else if (IsPreviewOpen)
            _preview.ShowFile(CurrentRowPath);
    }

    protected override void OnClosed(EventArgs e)
    {
        _preview?.CloseForGood();
        base.OnClosed(e);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }
}

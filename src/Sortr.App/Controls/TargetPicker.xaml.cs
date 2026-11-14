using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sortr.Core;

namespace Sortr.App.Controls;

/// <summary>
/// A text box that filters <see cref="Targets"/> as you type (substring, ignoring case, separators and
/// accents) and sets <see cref="SelectedTarget"/> when you pick one with Enter or a click.
/// </summary>
public partial class TargetPicker : UserControl
{
    public static readonly DependencyProperty TargetsProperty = DependencyProperty.Register(
        nameof(Targets), typeof(IReadOnlyList<Target>), typeof(TargetPicker), new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedTargetProperty = DependencyProperty.Register(
        nameof(SelectedTarget), typeof(Target), typeof(TargetPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTargetChanged));

    // Set while the control writes SearchBox.Text itself, so that doesn't count as the user typing.
    private bool _settingText;

    public TargetPicker()
    {
        InitializeComponent();
        // Grid rows are recycled while scrolling; drop any half-typed search when the row changes.
        DataContextChanged += (_, _) => Reset();
    }

    public IReadOnlyList<Target>? Targets
    {
        get => (IReadOnlyList<Target>?)GetValue(TargetsProperty);
        set => SetValue(TargetsProperty, value);
    }

    public Target? SelectedTarget
    {
        get => (Target?)GetValue(SelectedTargetProperty);
        set => SetValue(SelectedTargetProperty, value);
    }

    private static void OnSelectedTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TargetPicker)d).ShowCommittedText();

    private void ShowCommittedText()
    {
        _settingText = true;
        SearchBox.Text = SelectedTarget?.Name ?? "";
        SearchBox.CaretIndex = SearchBox.Text.Length;
        _settingText = false;
    }

    private void Reset()
    {
        ResultsPopup.IsOpen = false;
        ShowCommittedText();
    }

    private void Commit(Target? target)
    {
        SelectedTarget = target;
        ResultsPopup.IsOpen = false;
        ShowCommittedText();
    }

    /// <summary>Fills the list with targets matching <paramref name="query"/> and opens it.</summary>
    private void ShowResults(string? query, Target? highlight = null)
    {
        var results = TargetSearch.Filter(Targets ?? [], query);
        ResultsList.ItemsSource = results;
        NoResultsText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int index = highlight is null ? -1 : IndexOf(results, highlight);
        ResultsList.SelectedIndex = index >= 0 ? index : (results.Count > 0 ? 0 : -1);
        ResultsPopup.IsOpen = true;
        if (ResultsList.SelectedItem is not null)
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private static int IndexOf(IReadOnlyList<Target> list, Target target)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], target))
                return i;
        }
        return -1;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingText && SearchBox.IsKeyboardFocused)
            ShowResults(SearchBox.Text);
    }

    private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Take focus on the first click without the click moving the caret, so the select-all below sticks.
        if (!SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Show every target, with the current one highlighted; typing replaces the selected text.
        SearchBox.SelectAll();
        ShowResults(null, SelectedTarget);
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Reset();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!ResultsPopup.IsOpen)
                    ShowResults(SearchBox.Text == SelectedTarget?.Name ? null : SearchBox.Text, SelectedTarget);
                else
                    MoveHighlight(+1);
                e.Handled = true;
                break;

            case Key.Up:
                if (ResultsPopup.IsOpen)
                    MoveHighlight(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
            case Key.PageUp:
                if (ResultsPopup.IsOpen)
                {
                    MoveHighlight(e.Key == Key.PageDown ? +10 : -10);
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (string.IsNullOrWhiteSpace(SearchBox.Text))
                    Commit(null);
                else if (ResultsPopup.IsOpen && ResultsList.SelectedItem is Target target)
                    Commit(target);
                // Swallow Enter so the grid doesn't jump to the next row.
                e.Handled = true;
                break;

            case Key.Escape:
                if (ResultsPopup.IsOpen || SearchBox.Text != (SelectedTarget?.Name ?? ""))
                {
                    Reset();
                    e.Handled = true;
                }
                break;
        }
    }

    private void MoveHighlight(int delta)
    {
        int count = ResultsList.Items.Count;
        if (count == 0)
            return;
        ResultsList.SelectedIndex = Math.Clamp(ResultsList.SelectedIndex + delta, 0, count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null and not ListBox; node = GetParent(node))
        {
            if (node is ListBoxItem { DataContext: Target target })
            {
                Commit(target);
                e.Handled = true;
                return;
            }
        }
    }

    // Text elements such as Run aren't Visuals, so walk them through the logical tree instead.
    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}

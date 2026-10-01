using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sortr.App.Services;
using Sortr.Core;

namespace Sortr.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxErrorsShown = 15;

    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settingsStore;
    private readonly UndoLog _undoLog;

    public ObservableCollection<FileMatchViewModel> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _sourceFolder = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _targetsFolder = "";

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _showUnmatched;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(MoveSelectedCommand), nameof(UndoLastMoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMaximum = 1;

    [ObservableProperty]
    private string _statusText = "Choose a source folder and a targets folder, then click Scan.";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoLastMoveCommand))]
    private bool _canUndo;

    public MainViewModel(IDialogService dialogs, SettingsStore settingsStore, UndoLog undoLog)
    {
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _undoLog = undoLog;

        var settings = settingsStore.Load();
        _sourceFolder = settings.SourceFolder ?? "";
        _targetsFolder = settings.TargetsFolder ?? "";
        _canUndo = undoLog.LatestBatchPath is not null;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
    }

    partial void OnFilterTextChanged(string value) => ItemsView.Refresh();

    partial void OnShowUnmatchedChanged(bool value) => ItemsView.Refresh();

    private bool FilterItem(object obj)
    {
        var item = (FileMatchViewModel)obj;
        if (!ShowUnmatched && item.Kind == MatchKind.Unmatched)
            return false;
        if (string.IsNullOrWhiteSpace(FilterText))
            return true;
        return item.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || item.Candidates.Any(c => c.Target.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var folder = _dialogs.PickFolder("Select the folder to sort", SourceFolder);
        if (folder is not null)
            SourceFolder = folder;
    }

    [RelayCommand]
    private void BrowseTargets()
    {
        var folder = _dialogs.PickFolder("Select the folder containing target folders", TargetsFolder);
        if (folder is not null)
            TargetsFolder = folder;
    }

    private bool CanScan() =>
        !IsBusy && !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(TargetsFolder);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!Directory.Exists(SourceFolder))
        {
            _dialogs.ShowError($"Source folder not found:\n{SourceFolder}", "Sortr");
            return;
        }
        if (!Directory.Exists(TargetsFolder))
        {
            _dialogs.ShowError($"Targets folder not found:\n{TargetsFolder}", "Sortr");
            return;
        }

        _settingsStore.Save(new AppSettings(SourceFolder, TargetsFolder));
        IsBusy = true;
        StatusText = "Scanning...";
        try
        {
            var source = SourceFolder;
            var targetsFolder = TargetsFolder;
            var (targets, rows) = await Task.Run(() =>
            {
                var targets = TargetLoader.Load(targetsFolder);
                var matcher = new NameMatcher(targets);
                var rows = FileScanner.Scan(source)
                    .Select(path => new FileMatchViewModel(path, matcher.Match(Path.GetFileName(path))))
                    .ToList();
                return (targets, rows);
            });

            foreach (var old in Items)
                old.PropertyChanged -= OnItemPropertyChanged;
            Items.Clear();
            foreach (var row in rows)
            {
                row.PropertyChanged += OnItemPropertyChanged;
                Items.Add(row);
            }

            StatusText = $"Scanned {rows.Count} files against {targets.Count} targets.";
            UpdateSummary();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, "Scan failed");
            StatusText = "Scan failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileMatchViewModel.IsSelected))
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        SelectedCount = Items.Count(i => i.IsSelected && i.CanSelect);
        int strong = Items.Count(i => i.Kind == MatchKind.Strong);
        int loose = Items.Count(i => i.Kind == MatchKind.Loose);
        int ambiguous = Items.Count(i => i.Kind == MatchKind.Ambiguous);
        int unmatched = Items.Count(i => i.Kind == MatchKind.Unmatched);
        SummaryText = $"{strong} strong · {loose} loose · {ambiguous} ambiguous · {unmatched} unmatched · {SelectedCount} selected";
    }

    [RelayCommand]
    private void SelectAll() => SetVisibleSelection(true);

    [RelayCommand]
    private void SelectNone() => SetVisibleSelection(false);

    private void SetVisibleSelection(bool selected)
    {
        foreach (FileMatchViewModel item in ItemsView)
        {
            if (item.CanSelect)
                item.IsSelected = selected;
        }
    }

    /// <summary>Flips the checkbox of the given rows (used by the Space key in the grid).</summary>
    public void ToggleSelection(IEnumerable<FileMatchViewModel> rows)
    {
        var list = rows.Where(r => r.CanSelect).ToList();
        if (list.Count == 0)
            return;
        bool newValue = !list.All(r => r.IsSelected);
        foreach (var row in list)
            row.IsSelected = newValue;
    }

    private bool CanMove() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.SelectedCandidate is not null).ToList();
        if (selected.Count == 0)
            return;

        int targetCount = selected.Select(i => i.SelectedCandidate!.Target).Distinct().Count();
        if (!_dialogs.Confirm($"Move {selected.Count} file(s) into {targetCount} target folder(s)?", "Confirm move"))
            return;

        IsBusy = true;
        ProgressValue = 0;
        ProgressMaximum = selected.Count;
        StatusText = "Moving files...";
        try
        {
            var requests = selected.Select(i => new MoveRequest(i.FullPath, i.SelectedCandidate!.Target.FolderPath)).ToList();
            var results = await new FileMover(_undoLog).MoveAsync(requests, new Progress<int>(v => ProgressValue = v));

            var errors = new List<string>();
            for (int i = 0; i < results.Count; i++)
            {
                var row = selected[i];
                if (results[i].Succeeded)
                {
                    row.PropertyChanged -= OnItemPropertyChanged;
                    Items.Remove(row);
                }
                else
                {
                    row.Status = results[i].Error;
                    errors.Add($"{row.FileName}: {results[i].Error}");
                }
            }

            int moved = results.Count(r => r.Succeeded);
            CanUndo = _undoLog.LatestBatchPath is not null;
            StatusText = errors.Count == 0
                ? $"Moved {moved} file(s)."
                : $"Moved {moved} file(s); {errors.Count} failed (see Status column).";
            UpdateSummary();

            if (errors.Count > 0)
                _dialogs.ShowError(FormatErrors($"{errors.Count} file(s) could not be moved:", errors), "Some moves failed");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    private bool CanUndoLastMove() => !IsBusy && CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndoLastMove))]
    private async Task UndoLastMoveAsync()
    {
        var records = _undoLog.ReadLatest();
        if (records.Count == 0)
        {
            CanUndo = _undoLog.LatestBatchPath is not null;
            return;
        }
        if (!_dialogs.Confirm($"Move {records.Count} file(s) from the last batch back to where they came from?", "Undo last move"))
            return;

        IsBusy = true;
        StatusText = "Undoing last move...";
        UndoResult result;
        try
        {
            result = await Task.Run(_undoLog.UndoLatest);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, "Undo failed");
            StatusText = "Undo failed.";
            return;
        }
        finally
        {
            IsBusy = false;
            CanUndo = _undoLog.LatestBatchPath is not null;
        }

        var message = $"Restored {result.Restored} file(s)" + (result.Errors.Count > 0 ? $"; {result.Errors.Count} could not be restored." : ".");
        if (result.Errors.Count > 0)
            _dialogs.ShowError(FormatErrors($"{result.Errors.Count} file(s) could not be restored:", result.Errors), "Undo incomplete");

        // Restored files are back in the source folder, so rescan to show them for review again.
        if (CanScan())
            await ScanAsync();
        StatusText = message;
    }

    private static string FormatErrors(string header, IReadOnlyList<string> errors)
    {
        var lines = errors.Take(MaxErrorsShown).ToList();
        if (errors.Count > MaxErrorsShown)
            lines.Add($"...and {errors.Count - MaxErrorsShown} more.");
        return header + "\n\n" + string.Join("\n", lines);
    }
}

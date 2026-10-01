using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Sortr.Core;

namespace Sortr.App.ViewModels;

public enum MatchKind
{
    Strong,
    Loose,
    Ambiguous,
    Unmatched,
}

public sealed partial class FileMatchViewModel : ObservableObject
{
    public string FullPath { get; }
    public string FileName { get; }
    public IReadOnlyList<MatchCandidate> Candidates { get; }

    public MatchKind Kind { get; }
    public bool CanSelect => Candidates.Count > 0;
    public bool HasChoice => Candidates.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    private MatchCandidate? _selectedCandidate;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _status;

    public FileMatchViewModel(string fullPath, IReadOnlyList<MatchCandidate> candidates)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Candidates = candidates;
        _selectedCandidate = candidates.FirstOrDefault();

        Kind = candidates.Count switch
        {
            0 => MatchKind.Unmatched,
            1 => candidates[0].Confidence == MatchConfidence.Strong ? MatchKind.Strong : MatchKind.Loose,
            _ => MatchKind.Ambiguous,
        };

        // Ambiguous rows start unchecked so the user must consciously pick a target.
        _isSelected = candidates.Count == 1;
    }

    public string? Destination => SelectedCandidate?.Target.FolderPath;

    public string? TargetName => SelectedCandidate?.Target.Name;

    partial void OnSelectedCandidateChanged(MatchCandidate? value)
    {
        OnPropertyChanged(nameof(TargetName));
        // Choosing a target for an ambiguous file is a clear signal the user wants it moved.
        if (value is not null)
            IsSelected = true;
    }
}

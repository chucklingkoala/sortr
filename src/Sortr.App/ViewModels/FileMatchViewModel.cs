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

    /// <summary>Every target from the scan, offered by the search picker on unmatched rows.</summary>
    public IReadOnlyList<Target> AllTargets { get; }

    public MatchKind Kind { get; }
    public bool IsUnmatched => Kind == MatchKind.Unmatched;
    public bool CanSelect => EffectiveTarget is not null;
    public bool HasChoice => Candidates.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination), nameof(TargetName), nameof(EffectiveTarget), nameof(CanSelect))]
    private MatchCandidate? _selectedCandidate;

    /// <summary>A target the user picked by hand for an unmatched file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination), nameof(TargetName), nameof(EffectiveTarget), nameof(CanSelect))]
    private Target? _manualTarget;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string? _status;

    public FileMatchViewModel(string fullPath, IReadOnlyList<MatchCandidate> candidates, IReadOnlyList<Target> allTargets)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Candidates = candidates;
        AllTargets = allTargets;
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

    /// <summary>The target the file will move to: the chosen match, or the hand-picked target.</summary>
    public Target? EffectiveTarget => SelectedCandidate?.Target ?? ManualTarget;

    public string? Destination => EffectiveTarget?.FolderPath;

    public string? TargetName => EffectiveTarget?.Name;

    partial void OnSelectedCandidateChanged(MatchCandidate? value)
    {
        // Choosing a target for an ambiguous file is a clear signal the user wants it moved.
        if (value is not null)
            IsSelected = true;
    }

    partial void OnManualTargetChanged(Target? value)
    {
        // Same rule for unmatched files; clearing the target also unticks the row.
        IsSelected = value is not null;
    }
}

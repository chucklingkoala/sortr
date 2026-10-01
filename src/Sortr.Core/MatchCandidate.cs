namespace Sortr.Core;

public enum MatchConfidence
{
    /// <summary>Name found as whole tokens, e.g. "breaking.bad", "Bad_Breaking".</summary>
    Strong,

    /// <summary>Name found only with separators ignored, e.g. "breakingbad", "webripBreakingBad".</summary>
    Loose,
}

/// <param name="Start">Start offset of the match within the file's compact (separator-free) name.</param>
/// <param name="Length">Length of the match within the file's compact name.</param>
public sealed record MatchCandidate(Target Target, MatchConfidence Confidence, int Start, int Length)
{
    public int End => Start + Length;
}

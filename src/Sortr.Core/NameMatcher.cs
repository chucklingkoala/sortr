namespace Sortr.Core;

public sealed class NameMatcher
{
    /// <summary>Minimum compact-key length before separator-free (loose) matching is allowed.</summary>
    public const int MinLooseKeyLength = 6;

    private readonly IReadOnlyList<Target> _targets;

    public NameMatcher(IReadOnlyList<Target> targets)
    {
        _targets = targets;
    }

    /// <summary>Returns candidates best-first: strong before loose, then longest match.</summary>
    public IReadOnlyList<MatchCandidate> Match(string fileName)
    {
        var tokens = NameNormalizer.Tokenize(Path.GetFileNameWithoutExtension(fileName));
        if (tokens.Count == 0)
            return [];

        var offsets = new int[tokens.Count];
        for (int i = 1; i < tokens.Count; i++)
            offsets[i] = offsets[i - 1] + tokens[i - 1].Length;
        var compact = string.Concat(tokens);

        var candidates = new List<MatchCandidate>();
        foreach (var target in _targets)
        {
            var candidate = MatchStrong(target, tokens, offsets) ?? MatchLoose(target, compact);
            if (candidate is not null)
                candidates.Add(candidate);
        }

        // Drop matches fully contained in a longer match ("Doctor Who" inside "Doctor Who Confidential").
        return candidates
            .Where(c => !candidates.Any(o => o.Length > c.Length && o.Start <= c.Start && o.End >= c.End))
            .OrderBy(c => c.Confidence)
            .ThenByDescending(c => c.Length)
            .ThenBy(c => c.Target.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MatchCandidate? MatchStrong(Target target, IReadOnlyList<string> tokens, int[] offsets)
    {
        var name = target.Tokens;
        int n = name.Count;
        for (int i = 0; i + n <= tokens.Count; i++)
        {
            if (RunEquals(tokens, i, name, reversed: false) || (n > 1 && RunEquals(tokens, i, name, reversed: true)))
            {
                int start = offsets[i];
                int end = offsets[i + n - 1] + tokens[i + n - 1].Length;
                return new MatchCandidate(target, MatchConfidence.Strong, start, end - start);
            }
        }
        return null;
    }

    private static bool RunEquals(IReadOnlyList<string> tokens, int start, IReadOnlyList<string> name, bool reversed)
    {
        int n = name.Count;
        for (int j = 0; j < n; j++)
        {
            var expected = reversed ? name[n - 1 - j] : name[j];
            if (!string.Equals(tokens[start + j], expected, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static MatchCandidate? MatchLoose(Target target, string compact)
    {
        // Single-word names and short keys are too prone to false positives inside other words.
        if (target.Tokens.Count < 2 || target.CompactKey.Length < MinLooseKeyLength)
            return null;

        int index = compact.IndexOf(target.CompactKey, StringComparison.Ordinal);
        if (index < 0)
            index = compact.IndexOf(target.ReversedCompactKey, StringComparison.Ordinal);
        return index < 0 ? null : new MatchCandidate(target, MatchConfidence.Loose, index, target.CompactKey.Length);
    }
}

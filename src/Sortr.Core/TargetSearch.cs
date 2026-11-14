namespace Sortr.Core;

/// <summary>Type-to-search over targets, used when assigning a target by hand.</summary>
public static class TargetSearch
{
    /// <summary>
    /// Targets whose name contains <paramref name="query"/>, ignoring case. Separators and accents are
    /// also ignored, so "breakingbad" and "brea bad" both find "Breaking Bad". Names that start with the
    /// query come first; otherwise the input order (alphabetical from <see cref="TargetLoader"/>) is kept.
    /// </summary>
    public static IReadOnlyList<Target> Filter(IReadOnlyList<Target> targets, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return targets;

        var trimmed = query.Trim();
        var compact = string.Concat(NameNormalizer.Tokenize(trimmed));

        var prefixMatches = new List<Target>();
        var otherMatches = new List<Target>();
        foreach (var target in targets)
        {
            if (target.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                || (compact.Length > 0 && target.CompactKey.StartsWith(compact, StringComparison.Ordinal)))
                prefixMatches.Add(target);
            else if (target.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || (compact.Length > 0 && target.CompactKey.Contains(compact, StringComparison.Ordinal)))
                otherMatches.Add(target);
        }

        prefixMatches.AddRange(otherMatches);
        return prefixMatches;
    }
}

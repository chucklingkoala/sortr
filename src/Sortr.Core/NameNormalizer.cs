using System.Globalization;
using System.Text;

namespace Sortr.Core;

/// <summary>
/// Normalizes names so that "Breaking.Bad", "breaking_bad", "BREAKING BAD" and "Bréaking-Bad"
/// all reduce to the same tokens: ["breaking", "bad"].
/// </summary>
public static class NameNormalizer
{
    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lowercased, diacritic-free alphanumeric tokens, split on any other character.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var clean = RemoveDiacritics(text).ToLowerInvariant();
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in clean)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }
}

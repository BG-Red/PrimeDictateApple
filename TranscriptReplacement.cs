namespace PrimeDictate;

internal static class TranscriptReplacement
{
    /// <summary>
    /// Applies user rules in order of longest find-string first so multi-word phrases win over shorter overlaps.
    /// Matching is case-insensitive; replacement text is literal.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<TranscriptReplacementRule> rules)
    {
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
        {
            return text;
        }

        var ordered = rules
            .Select(r => (Find: r.Find.Trim(), Replace: r.Replace ?? string.Empty))
            .Where(pair => pair.Find.Length > 0)
            .OrderByDescending(pair => pair.Find.Length)
            .ToList();

        if (ordered.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var (find, replace) in ordered)
        {
            result = result.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}

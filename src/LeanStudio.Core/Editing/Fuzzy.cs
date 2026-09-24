namespace LeanStudio.Core.Editing;

/// <summary>
/// Fuzzy matching for pickers (command palette, quick open): every character of the query must appear in order.
/// Matches score higher when they are contiguous, start words, or fall in the last path segment.
/// </summary>
public static class Fuzzy
{
    /// <summary>
    /// A score, higher is better, or null when the query does not match. Matching ignores case and spaces in the
    /// query; an empty query scores 0. Scores are normalized by the candidate's length, so shorter candidates win ties.
    /// </summary>
    public static int? Score(string query, string candidate)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        int lastSep = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        int score = 0, ci = 0, streak = 0;
        foreach (char qc in query)
        {
            if (qc == ' ')
            {
                continue;
            }
            int found = -1;
            for (int i = ci; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(qc))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                return null;
            }
            bool wordStart = found == 0 || !char.IsLetterOrDigit(candidate[found - 1]) || (char.IsUpper(candidate[found]) && char.IsLower(candidate[found - 1]));
            streak = found == ci ? streak + 1 : 0;
            score += 1 + streak * 3 + (wordStart ? 6 : 0) + (found > lastSep ? 2 : 0) + (candidate[found] == qc ? 1 : 0);
            ci = found + 1;
        }
        return score * 100 / (10 + candidate.Length);
    }

    /// <summary>
    /// The items whose text matches <paramref name="query"/>, best score first, then shortest text first.
    /// </summary>
    /// <param name="items">The items to filter.</param>
    /// <param name="query">What the user typed.</param>
    /// <param name="text">The text of an item to match against, such as its path or title.</param>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> items, string query, Func<T, string> text) =>
        items.Select(i => (Item: i, Score: Score(query, text(i))))
             .Where(x => x.Score is not null)
             .OrderByDescending(x => x.Score)
             .ThenBy(x => text(x.Item).Length)
             .Select(x => x.Item);
}

using System.Globalization;
using System.Text;

namespace GuraFile.Storage;

/// <summary>
/// Sanitizes and builds safe SQLite FTS5 query expressions from user input.
/// Guarantees that raw user text cannot cause FTS5 syntax errors, keyword injections,
/// or operator misuse (e.g. *, :, ^, AND, OR, NOT, NEAR).
/// </summary>
public static class FtsQueryBuilder
{
    public static string? Build(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return null;
        }

        var tokens = ExtractTokens(rawInput);
        if (tokens.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            // In FTS5, wrapping in double quotes treats keywords/special chars as literal terms.
            // Escape any existing double quote by doubling it ("").
            var escaped = tokens[i].Replace("\"", "\"\"", StringComparison.Ordinal);
            builder.Append('"').Append(escaped).Append("\"*");
        }

        return builder.ToString();
    }

    private static List<string> ExtractTokens(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in input.EnumerateRunes())
        {
            if (IsTokenRune(rune))
            {
                current.Append(rune.ToString());
            }
            else
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsTokenRune(Rune rune)
    {
        if (Rune.IsLetterOrDigit(rune))
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark;
    }
}

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Larik.Services;

/// <summary>
/// Minimal SQL formatter: uppercases keywords outside string literals and puts major
/// clauses on their own line. Deliberately conservative — it never rewrites identifiers.
/// </summary>
public static partial class SqlFormatter
{
    private static readonly string[] Clauses =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN",
        "UNION ALL", "UNION", "VALUES", "SET", "RETURNING",
    ];

    // Precomputed replacement strings avoid allocating "\n" + keyword + " " per match.
    private static readonly FrozenDictionary<string, string> ClauseReplacements =
        Clauses.ToFrozenDictionary(
            static clause => clause,
            static clause => $"\n{clause} ",
            StringComparer.OrdinalIgnoreCase);

    public static string Format(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var literals = new List<string>();
        var masked = LiteralRegex().Replace(sql, match =>
        {
            literals.Add(match.Value);
            return $"\u0000{literals.Count - 1}\u0000";
        });

        var text = WhitespaceRegex().Replace(masked, " ").Trim();

        text = ClauseRegex().Replace(
            text,
            static match => ClauseReplacements[match.Groups[1].Value]);

        text = CommaRegex().Replace(text, ",\n    ");
        text = MultiNewlineRegex().Replace(text, "\n\n").Trim();

        // Re-indent everything under the first clause without splitting into line arrays.
        var builder = new StringBuilder(text.Length + 64);
        var span = text.AsSpan();
        foreach (var range in span.Split('\n'))
        {
            var trimmed = span[range].Trim();
            if (trimmed.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (!StartsWithClause(trimmed))
            {
                builder.Append("    ");
            }

            builder.Append(trimmed).Append('\n');
        }

        while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
        {
            builder.Length--;
        }

        var formatted = builder.ToString();
        if (literals.Count == 0)
        {
            return formatted;
        }

        return PlaceholderRegex().Replace(
            formatted,
            match => literals[int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture)]);
    }

    private static bool StartsWithClause(ReadOnlySpan<char> line)
    {
        var clauses = Clauses;
        for (var i = 0; i < clauses.Length; i++)
        {
            if (line.StartsWith(clauses[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"'(?:[^']|'')*'|""(?:[^""]|"""")*""")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex("\u0000(\\d+)\u0000")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Longest-first alternation so multi-word clauses ("UNION ALL") match before prefixes ("UNION").
    [GeneratedRegex(
        @"\s*\b(INNER JOIN|LEFT JOIN|RIGHT JOIN|FULL JOIN|CROSS JOIN|GROUP BY|ORDER BY|UNION ALL|RETURNING|SELECT|HAVING|VALUES|WHERE|UNION|FROM|SET)\b\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClauseRegex();

    [GeneratedRegex(@"\s*,\s*")]
    private static partial Regex CommaRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}

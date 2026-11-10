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

        foreach (var keyword in Clauses)
        {
            text = Regex.Replace(text, $@"\s*\b{Regex.Escape(keyword)}\b\s*", $"\n{keyword} ", RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"\s*,\s*", ",\n    ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        // Re-indent everything under the first clause.
        var builder = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            var isClause = Clauses.Any(c => trimmed.StartsWith(c, StringComparison.OrdinalIgnoreCase));
            builder.Append(isClause ? trimmed : $"    {trimmed}").Append('\n');
        }

        var formatted = builder.ToString().TrimEnd();
        return PlaceholderRegex().Replace(
            formatted,
            match => literals[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]);
    }

    [GeneratedRegex(@"'(?:[^']|'')*'|""(?:[^""]|"""")*""")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex("\u0000(\\d+)\u0000")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

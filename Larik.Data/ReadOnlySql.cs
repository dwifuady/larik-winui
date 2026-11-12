using System.Text;

namespace Larik.Data;

/// <summary>
/// The keyword lists one engine needs to prove a statement read-only. Providers own their lists
/// (see <see cref="IReadOnlyStatementPolicy"/>); the scanning rules below are shared because every
/// SQL engine needs the same literal/comment/identifier handling first.
/// </summary>
public sealed class ReadOnlyKeywordRules
{
    /// <summary>Keywords a statement unit may start with, e.g. <c>select</c> or <c>with</c>.</summary>
    public required IReadOnlySet<string> LeadingKeywords { get; init; }

    /// <summary>
    /// Keywords that must not appear anywhere in a unit. Case-insensitive. Fail-closed: a column
    /// that happens to carry one of these names is refused rather than allowed.
    /// </summary>
    public required IReadOnlySet<string> DeniedKeywords { get; init; }

    /// <summary>Token prefixes that must not appear, e.g. <c>xp_</c> for SQL Server system procedures.</summary>
    public IReadOnlyList<string> DeniedPrefixes { get; init; } = [];

    public static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Fail-closed statement gate for read-only exposure. The scan strips string literals, comments and
/// quoted identifiers first (so a semicolon inside <c>'…'</c> cannot split a statement), then judges
/// every remaining <c>;</c>-separated unit on its own.
/// <para>
/// This is a heuristic and deliberately refuses anything it cannot read confidently. It is the
/// second line for engines that cannot enforce read-only themselves; providers that can ask the
/// server (a read-only transaction) still should.
/// </para>
/// </summary>
public static class ReadOnlySql
{
    /// <summary>True only when every statement unit in <paramref name="sql"/> is provably a read.</summary>
    public static bool IsProvenRead(string sql, ReadOnlyKeywordRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (string.IsNullOrWhiteSpace(sql) || !TryStrip(sql, out var stripped))
        {
            return false;
        }

        var units = 0;
        foreach (var unit in stripped.Split(';'))
        {
            var tokens = Tokenize(unit);
            if (tokens.Count == 0)
            {
                continue;
            }

            units++;
            if (!rules.LeadingKeywords.Contains(tokens[0]))
            {
                return false;
            }

            foreach (var token in tokens)
            {
                if (rules.DeniedKeywords.Contains(token))
                {
                    return false;
                }

                foreach (var prefix in rules.DeniedPrefixes)
                {
                    if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
        }

        return units > 0;
    }

    /// <summary>
    /// Removes literals, comments and quoted identifiers, replacing each with a space so tokens on
    /// either side cannot fuse into a new keyword. False means the input is malformed (an
    /// unterminated literal or comment), which is treated as a refusal.
    /// </summary>
    private static bool TryStrip(string sql, out string stripped)
    {
        var builder = new StringBuilder(sql.Length);
        var index = 0;

        while (index < sql.Length)
        {
            var current = sql[index];

            if (current is '\'' or '"' or '[')
            {
                var closing = current == '[' ? ']' : current;
                if (!SkipQuoted(sql, ref index, current, closing))
                {
                    stripped = string.Empty;
                    return false;
                }

                builder.Append(' ');
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                builder.Append(' ');
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    stripped = string.Empty;
                    return false;
                }

                index = end + 2;
                builder.Append(' ');
                continue;
            }

            builder.Append(current);
            index++;
        }

        stripped = builder.ToString();
        return true;
    }

    /// <summary>Skips one quoted run, honouring the doubled-closer escape (<c>''</c>, <c>]]</c>).</summary>
    private static bool SkipQuoted(string sql, ref int index, char opening, char closing)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] == closing)
            {
                // A doubled closer is an escaped character, not the end of the run.
                if (index + 1 < sql.Length && sql[index + 1] == closing)
                {
                    index += 2;
                    continue;
                }

                index++;
                return true;
            }

            index++;
        }

        return false;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>(8);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];
            var isWordStart = char.IsLetter(current) || current is '_' or '@' or '#';
            if (!isWordStart)
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length)
            {
                var next = text[index];
                if (!char.IsLetterOrDigit(next) && next is not ('_' or '@' or '#' or '$'))
                {
                    break;
                }

                index++;
            }

            tokens.Add(text[start..index]);
        }

        return tokens;
    }
}

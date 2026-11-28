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

        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var span = sql.AsSpan();
        var length = span.Length;
        var hasLeadingLookup = rules.LeadingKeywords is HashSet<string> leadingSet
            && leadingSet.TryGetAlternateLookup<ReadOnlySpan<char>>(out var leadingLookup);
        var hasDeniedLookup = rules.DeniedKeywords is HashSet<string> deniedSet
            && deniedSet.TryGetAlternateLookup<ReadOnlySpan<char>>(out var deniedLookup);
        var deniedPrefixes = rules.DeniedPrefixes;

        var units = 0;
        var unitHasToken = false;
        var index = 0;

        while (index < length)
        {
            var current = span[index];

            if (current is '\'' or '"' or '[')
            {
                var closing = current == '[' ? ']' : current;
                if (!SkipQuoted(span, ref index, closing))
                {
                    return false;
                }

                continue;
            }

            if (current == '-' && index + 1 < length && span[index + 1] == '-')
            {
                var newlineOffset = span.Slice(index + 2).IndexOf('\n');
                index = newlineOffset < 0 ? length : index + 2 + newlineOffset;
                continue;
            }

            if (current == '/' && index + 1 < length && span[index + 1] == '*')
            {
                var endOffset = span.Slice(index + 2).IndexOf("*/", StringComparison.Ordinal);
                if (endOffset < 0)
                {
                    return false;
                }

                index = index + 2 + endOffset + 2;
                continue;
            }

            if (current == ';')
            {
                unitHasToken = false;
                index++;
                continue;
            }

            if (IsWordStart(current))
            {
                var start = index++;
                while (index < length && IsWordPart(span[index]))
                {
                    index++;
                }

                var token = span.Slice(start, index - start);
                string? fallbackString = null;

                if (!unitHasToken)
                {
                    unitHasToken = true;
                    units++;
                    var allowedLeading = hasLeadingLookup
                        ? leadingLookup.Contains(token)
                        : rules.LeadingKeywords.Contains(fallbackString ??= token.ToString());
                    if (!allowedLeading)
                    {
                        return false;
                    }
                }

                var isDenied = hasDeniedLookup
                    ? deniedLookup.Contains(token)
                    : rules.DeniedKeywords.Contains(fallbackString ??= token.ToString());
                if (isDenied)
                {
                    return false;
                }

                for (var p = 0; p < deniedPrefixes.Count; p++)
                {
                    if (token.StartsWith(deniedPrefixes[p], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                continue;
            }

            index++;
        }

        return units > 0;
    }

    private static bool IsWordStart(char value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or '@' or '#'
        || (value >= 128 && char.IsLetter(value));

    private static bool IsWordPart(char value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '@' or '#' or '$'
        || (value >= 128 && char.IsLetterOrDigit(value));

    /// <summary>Skips one quoted run, honouring the doubled-closer escape (<c>''</c>, <c>]]</c>).</summary>
    private static bool SkipQuoted(ReadOnlySpan<char> sql, ref int index, char closing)
    {
        index++;
        while (index < sql.Length)
        {
            var offset = sql.Slice(index).IndexOf(closing);
            if (offset < 0)
            {
                return false;
            }

            index += offset;
            // A doubled closer is an escaped character, not the end of the run.
            if (index + 1 < sql.Length && sql[index + 1] == closing)
            {
                index += 2;
                continue;
            }

            index++;
            return true;
        }

        return false;
    }
}

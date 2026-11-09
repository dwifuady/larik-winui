using Larik.Data;

namespace Larik.Data.Postgres;

/// <summary>The transaction command a statement starts with, as far as the tracker cares.</summary>
internal enum PostgresTransactionCommand
{
    None,
    Begin,
    Commit,
    Rollback,
    RollbackToSavepoint,
}

/// <summary>
/// PostgreSQL cannot tell a session "are you inside an explicit transaction?" (its own
/// <c>pg_stat_activity</c> row is <c>active</c> while the probe runs), so the provider tracks the
/// commands it sent. This helper is separately testable: the rules are small but easy to get wrong
/// around <c>WORK</c>, <c>TRANSACTION</c>, <c>AND CHAIN</c> and <c>ROLLBACK TO SAVEPOINT</c>.
/// </summary>
internal static class PostgresTransactionTracker
{
    public static SessionTransactionState Apply(
        SessionTransactionState current,
        string sql,
        bool succeeded)
    {
        var words = ReadLeadingWords(sql, 4);
        var command = Classify(words);

        if (!succeeded)
        {
            // Any failed statement inside an explicit transaction aborts it; only ROLLBACK can end it.
            return current.HasTransaction ? new SessionTransactionState(1, -1) : current;
        }

        return command switch
        {
            PostgresTransactionCommand.Begin => current.HasTransaction ? current : new SessionTransactionState(1, 1),
            PostgresTransactionCommand.Commit or PostgresTransactionCommand.Rollback =>
                HasChain(words) ? new SessionTransactionState(1, 1) : SessionTransactionState.None,
            // PostgreSQL revives an aborted transaction when ROLLBACK TO SAVEPOINT succeeds: the
            // transaction stays open, but it is committable again.
            PostgresTransactionCommand.RollbackToSavepoint =>
                current.HasTransaction ? new SessionTransactionState(current.TransactionCount, 1) : current,
            _ => current,
        };
    }

    internal static PostgresTransactionCommand Classify(string[] words) =>
        words[0] switch
        {
            "BEGIN" => PostgresTransactionCommand.Begin,
            "START" => words[1] == "TRANSACTION" ? PostgresTransactionCommand.Begin : PostgresTransactionCommand.None,
            "COMMIT" or "END" => PostgresTransactionCommand.Commit,
            "ABORT" => PostgresTransactionCommand.Rollback,
            // ROLLBACK [WORK|TRANSACTION] TO [SAVEPOINT] name; the optional word can be second.
            "ROLLBACK" => Contains(words, "TO")
                ? PostgresTransactionCommand.RollbackToSavepoint
                : PostgresTransactionCommand.Rollback,
            // PREPARE TRANSACTION hands the work to the two-phase machinery and ends the session transaction.
            "PREPARE" => words[1] == "TRANSACTION" ? PostgresTransactionCommand.Commit : PostgresTransactionCommand.None,
            _ => PostgresTransactionCommand.None,
        };

    /// <summary>"AND CHAIN" may follow WORK or TRANSACTION, so scan rather than check two positions.</summary>
    internal static bool HasChain(string[] words)
    {
        for (var index = 1; index + 1 < words.Length; index++)
        {
            if (words[index] == "AND" && words[index + 1] == "CHAIN")
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string[] words, string value)
    {
        for (var index = 1; index < words.Length; index++)
        {
            if (words[index] == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the first few words of a statement, skipping leading whitespace and comments.</summary>
    internal static string[] ReadLeadingWords(string text, int count)
    {
        var words = new string[count];
        var index = 0;

        for (var i = 0; i < count; i++)
        {
            SkipTrivia(text, ref index);
            var start = index;
            while (index < text.Length && char.IsLetter(text[index]))
            {
                index++;
            }

            words[i] = index > start ? text[start..index].ToUpperInvariant() : string.Empty;
        }

        return words;
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index] == '-' && text[index + 1] == '-')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                // PostgreSQL block comments nest.
                var depth = 1;
                index += 2;
                while (index < text.Length && depth > 0)
                {
                    if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            break;
        }
    }
}

namespace Larik.Data.MongoDB;

/// <summary>
/// The single place that decides whether a statement is SQL-on-MongoDB (translated to a pipeline)
/// or native mongosh (<c>db.collection.…</c>, executed as written). Both the editor's
/// <see cref="IEditorLanguage.Split"/> spans and the provider's execution-side splitter use this, so
/// the two views of a document can never disagree about what to run.
/// </summary>
internal static class MongoScriptClassifier
{
    /// <summary>A SQL statement on MongoDB, compiled to a pipeline or a write command.</summary>
    public const string Sql = "sql";

    /// <summary>A mongosh expression (<c>db.users.find(...)</c>), passed through to the driver.</summary>
    public const string Shell = "shell";

    /// <summary>
    /// Tags a trimmed statement. Everything starting with <c>db.</c> is passed straight to the
    /// driver; any other statement is the T-SQL surface. <c>USE</c> is deliberately a SQL statement
    /// so database switching always goes through the translator.
    /// </summary>
    public static string Classify(string? statement)
    {
        var text = statement?.TrimStart() ?? string.Empty;
        return text.StartsWith("db.", StringComparison.OrdinalIgnoreCase) ? Shell : Sql;
    }
}
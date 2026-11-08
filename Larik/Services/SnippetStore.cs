using Larik.Models;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>
/// Snippet persistence in the same SQLite database as the workspace session. The table matches the
/// desktop app's <c>snippets</c> shape (<c>src-tauri/src/storage/snippets.rs</c>), so the two stores
/// describe the same thing: built-ins and user-defined snippets, each enabled or disabled and
/// optionally scoped to a dialect.
/// </summary>
public sealed class SnippetStore
{
    public SnippetStore(string? databasePath = null) =>
        DatabasePath = databasePath ?? LarikPaths.DatabasePath;

    public string DatabasePath { get; }

    public IReadOnlyList<SnippetRecord> Load()
    {
        using var connection = Open();
        EnsureSchema(connection);
        SeedBuiltins(connection);
        return Read(connection);
    }

    public void Save(SnippetRecord record)
    {
        using var connection = Open();
        EnsureSchema(connection);
        Execute(
            connection,
            """
            INSERT INTO snippets (id, trigger, name, content, description, is_builtin, enabled, category, drivers)
            VALUES ($id, $trigger, $name, $content, $description, $builtin, $enabled, $category, $drivers)
            ON CONFLICT(id) DO UPDATE SET
                trigger = excluded.trigger,
                name = excluded.name,
                content = excluded.content,
                description = excluded.description,
                enabled = excluded.enabled,
                category = excluded.category,
                drivers = excluded.drivers,
                updated_at = datetime('now');
            """,
            ("$id", record.Id),
            ("$trigger", record.Trigger),
            ("$name", record.Name),
            ("$content", record.Content),
            ("$description", record.Description),
            ("$builtin", record.IsBuiltin ? 1 : 0),
            ("$enabled", record.IsEnabled ? 1 : 0),
            ("$category", record.Category),
            ("$drivers", DriversColumn(record.Dialects)));
    }

    /// <summary>Deletes a user snippet. Built-ins can be disabled but never removed.</summary>
    public bool Delete(string id)
    {
        using var connection = Open();
        EnsureSchema(connection);
        return Execute(
            connection,
            "DELETE FROM snippets WHERE id = $id AND is_builtin = 0;",
            ("$id", id)) > 0;
    }

    // ---------------------------------------------------------------- schema + seeding

    private SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        Execute(
            connection,
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """);

        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection) =>
        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS snippets (
                id TEXT PRIMARY KEY,
                trigger TEXT NOT NULL,
                name TEXT NOT NULL,
                content TEXT NOT NULL,
                description TEXT,
                is_builtin INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                category TEXT,
                drivers TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_snippets_trigger ON snippets(trigger);
            CREATE INDEX IF NOT EXISTS idx_snippets_enabled ON snippets(enabled);
            """);

    /// <summary>
    /// Seeds the catalog with stable ids. <c>INSERT OR IGNORE</c> leaves user edits, disabled
    /// built-ins and rows imported by an earlier version alone, while new built-ins still arrive.
    /// </summary>
    private static void SeedBuiltins(SqliteConnection connection)
    {
        foreach (var snippet in SnippetCatalog.All)
        {
            var record = SnippetCatalog.ToRecord(snippet);
            Execute(
                connection,
                """
                INSERT OR IGNORE INTO snippets
                    (id, trigger, name, content, description, is_builtin, enabled, category, drivers)
                VALUES ($id, $trigger, $name, $content, $description, 1, 1, $category, $drivers);
                """,
                ("$id", record.Id),
                ("$trigger", record.Trigger),
                ("$name", record.Name),
                ("$content", record.Content),
                ("$description", record.Description),
                ("$category", record.Category),
                ("$drivers", DriversColumn(record.Dialects)));
        }
    }

    // ---------------------------------------------------------------- read

    private static IReadOnlyList<SnippetRecord> Read(SqliteConnection connection)
    {
        var snippets = new List<SnippetRecord>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, trigger, name, content, description, is_builtin, enabled, category, drivers
            FROM snippets;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snippets.Add(new SnippetRecord
            {
                Id = reader.GetString(0),
                Trigger = reader.GetString(1),
                Name = reader.GetString(2),
                Content = reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsBuiltin = reader.GetInt64(5) != 0,
                IsEnabled = reader.GetInt64(6) != 0,
                Category = reader.IsDBNull(7) ? null : reader.GetString(7),
                Dialects = ParseDrivers(reader.IsDBNull(8) ? null : reader.GetString(8)),
            });
        }

        return snippets;
    }

    private static string? DriversColumn(string[]? dialects) =>
        dialects is { Length: > 0 } ? string.Join(",", dialects) : null;

    private static string[]? ParseDrivers(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : SnippetDialects.Normalize(value.Split(',', StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- helpers

    private static int Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }
}

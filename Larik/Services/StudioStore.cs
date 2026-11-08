using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Larik.Services;

/// <summary>The workspace session: spaces, their connections and tabs.</summary>
public sealed class StudioState
{
    public List<SpaceState> Spaces { get; set; } = [];

    public string? ActiveSpaceId { get; set; }

    public int QueryCounter { get; set; }

    /// <summary>Archived tabs, newest first. Archived tabs can be restored into their workspace.</summary>
    public List<ArchivedTabState> ArchivedTabs { get; set; } = [];

    public bool AutoArchiveEnabled { get; set; } = true;

    public int AutoArchiveDays { get; set; } = 30;

    /// <summary>Last window geometry, restored on launch; null on the first run or after a reset.</summary>
    public WindowBoundsState? WindowBounds { get; set; }
}

/// <summary>
/// The window rectangle in device-independent pixels, so a DPI change between launches does not
/// stretch or shrink the restored window. <see cref="Maximized"/> is remembered separately from
/// the rectangle: restoring a maximized window must not make its restore-down size the monitor.
/// </summary>
public sealed class WindowBoundsState
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>One space, including the connection profile it owns (passwords are never stored).</summary>
public sealed class SpaceState
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "#C4B5FD";

    public string ProviderId { get; set; } = "mssql";

    public Dictionary<string, string> Profile { get; set; } = [];

    public string? ActiveDatabase { get; set; }

    public string? ActiveTabId { get; set; }

    public List<TabState> Tabs { get; set; } = [];
}

/// <summary>One tab: its title, its database subtitle and the SQL it holds.</summary>
public sealed class TabState
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool Pinned { get; set; }

    /// <summary>Last selection/edit time; auto-archive measures inactivity from it.</summary>
    public DateTimeOffset? LastAccessedAt { get; set; }
}

/// <summary>A tab kept in the archive/history page after it left its workspace.</summary>
public sealed class ArchivedTabState
{
    public string Id { get; set; } = string.Empty;

    public string SpaceId { get; set; } = string.Empty;

    public string SpaceName { get; set; } = string.Empty;

    public string Color { get; set; } = "#C4B5FD";

    public string Title { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool Pinned { get; set; }

    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Reads the pre-SQLite <c>studio.json</c> state once, so upgrades keep their spaces.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StudioState))]
[JsonSerializable(typeof(WindowBoundsState))]
internal sealed partial class StudioJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The workspace session persisted between launches, in the same shape as the desktop app's
/// storage (spaces / tabs / app_state). Lives in <c>%LOCALAPPDATA%\Larik WinUI\larik-winui.db</c>
/// (<c>larik-winui-dev.db</c> for debug builds) so the WinUI port and the Tauri app never open
/// each other's incompatible store.
/// <para>
/// Writes are best-effort: a broken or unreadable database falls back to the seeded spaces
/// instead of failing the launch. Provider-declared password fields are never written.
/// </para>
/// </summary>
public sealed class StudioStore
{
    private const int SchemaVersion = 2;

    public StudioStore()
        : this(LarikPaths.DatabasePath)
    {
    }

    public StudioStore(string databasePath) => DatabasePath = databasePath;

    public string DatabasePath { get; }

    public StudioState? Load()
    {
        try
        {
            using var connection = Open();
            var state = Read(connection);
            if (state.Spaces.Count > 0)
            {
                return state;
            }

            return TryImportLegacyJson(connection);
        }
        catch
        {
            return null;
        }
    }

    public void Save(StudioState state)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Write(connection, transaction, state);
            transaction.Commit();
        }
        catch
        {
            // Persistence must never take the studio down.
        }
    }

    // ---------------------------------------------------------------- connection + schema

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
            null,
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """);

        EnsureSchema(connection);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS spaces (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                color TEXT,
                provider_id TEXT NOT NULL DEFAULT 'mssql',
                active_database TEXT,
                last_active_tab_id TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS space_settings (
                space_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (space_id, key),
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tabs (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                title TEXT NOT NULL,
                database TEXT,
                content TEXT,
                pinned INTEGER NOT NULL DEFAULT 0,
                last_accessed_at TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (space_id) REFERENCES spaces(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS archived_tabs (
                id TEXT PRIMARY KEY,
                space_id TEXT NOT NULL,
                space_name TEXT NOT NULL,
                color TEXT,
                title TEXT NOT NULL,
                database TEXT,
                content TEXT,
                pinned INTEGER NOT NULL DEFAULT 0,
                archived_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS app_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_spaces_sort_order ON spaces(sort_order);
            CREATE INDEX IF NOT EXISTS idx_tabs_space_id ON tabs(space_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_archived_tabs_archived_at ON archived_tabs(archived_at DESC);
            """);

        // Version 2 adds pin state, the inactivity clock and the archive table. Existing
        // version 1 databases keep their rows; the new tab columns are added in place.
        EnsureColumn(connection, transaction, "tabs", "pinned", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "tabs", "last_accessed_at", "TEXT");

        Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();
    }

    /// <summary>Adds a column to an existing table when it predates the current schema.</summary>
    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(
            connection,
            transaction,
            $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    // ---------------------------------------------------------------- read

    private static StudioState Read(SqliteConnection connection)
    {
        var state = new StudioState
        {
            ActiveSpaceId = ReadState(connection, "active_space_id"),
            QueryCounter = int.TryParse(
                ReadState(connection, "query_counter"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var counter)
                ? counter
                : 0,
            AutoArchiveEnabled = !bool.TryParse(ReadState(connection, "auto_archive_enabled"), out var enabled)
                || enabled,
            AutoArchiveDays = int.TryParse(
                ReadState(connection, "auto_archive_days"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var days)
                ? days
                : 30,
            WindowBounds = ReadWindowBounds(connection),
        };

        var spaces = new List<SpaceState>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, name, COALESCE(color, ''), provider_id, active_database, last_active_tab_id
                FROM spaces
                ORDER BY sort_order, name;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                spaces.Add(new SpaceState
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Color = reader.GetString(2),
                    ProviderId = reader.GetString(3),
                    ActiveDatabase = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ActiveTabId = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
        }

        foreach (var space in spaces)
        {
            space.Profile = ReadProfile(connection, space.Id);
            space.Tabs = ReadTabs(connection, space.Id);
        }

        state.Spaces = spaces;
        state.ArchivedTabs = ReadArchivedTabs(connection);
        return state;
    }

    private static Dictionary<string, string> ReadProfile(SqliteConnection connection, string spaceId)
    {
        var profile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM space_settings WHERE space_id = $id;";
        command.Parameters.AddWithValue("$id", spaceId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            profile[reader.GetString(0)] = reader.GetString(1);
        }

        return profile;
    }

    private static List<TabState> ReadTabs(SqliteConnection connection, string spaceId)
    {
        var tabs = new List<TabState>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, COALESCE(database, ''), COALESCE(content, ''),
                   COALESCE(pinned, 0), COALESCE(last_accessed_at, '')
            FROM tabs
            WHERE space_id = $id
            ORDER BY sort_order;
            """;
        command.Parameters.AddWithValue("$id", spaceId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tabs.Add(new TabState
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Database = reader.GetString(2),
                Content = reader.GetString(3),
                Pinned = reader.GetInt64(4) != 0,
                // A tab saved before the inactivity clock existed counts as used now, so the
                // first launch after the upgrade does not sweep the whole workspace.
                LastAccessedAt = ParseTimestamp(reader.GetString(5)),
            });
        }

        return tabs;
    }

    private static List<ArchivedTabState> ReadArchivedTabs(SqliteConnection connection)
    {
        var tabs = new List<ArchivedTabState>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, space_id, space_name, COALESCE(color, ''), title,
                   COALESCE(database, ''), COALESCE(content, ''), COALESCE(pinned, 0),
                   COALESCE(archived_at, '')
            FROM archived_tabs
            ORDER BY archived_at DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tabs.Add(new ArchivedTabState
            {
                Id = reader.GetString(0),
                SpaceId = reader.GetString(1),
                SpaceName = reader.GetString(2),
                Color = reader.GetString(3),
                Title = reader.GetString(4),
                Database = reader.GetString(5),
                Content = reader.GetString(6),
                Pinned = reader.GetInt64(7) != 0,
                ArchivedAt = ParseTimestamp(reader.GetString(8)),
            });
        }

        return tabs;
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    // ---------------------------------------------------------------- write

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, StudioState state)
    {
        var spaceIds = new HashSet<string>(StringComparer.Ordinal);
        var spaceOrder = 0;

        foreach (var space in state.Spaces)
        {
            spaceIds.Add(space.Id);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO spaces (id, name, color, provider_id, active_database, last_active_tab_id, sort_order)
                VALUES ($id, $name, $color, $provider, $database, $tab, $sort)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    color = excluded.color,
                    provider_id = excluded.provider_id,
                    active_database = excluded.active_database,
                    last_active_tab_id = excluded.last_active_tab_id,
                    sort_order = excluded.sort_order,
                    updated_at = datetime('now');
                """,
                ("$id", space.Id),
                ("$name", space.Name),
                ("$color", space.Color),
                ("$provider", space.ProviderId),
                ("$database", space.ActiveDatabase),
                ("$tab", space.ActiveTabId),
                ("$sort", spaceOrder++));

            WriteProfile(connection, transaction, space);
            WriteTabs(connection, transaction, space);
        }

        var storedSpaces = SelectIds(connection, transaction, "SELECT id FROM spaces;");
        foreach (var id in storedSpaces.Where(id => !spaceIds.Contains(id)))
        {
            Execute(connection, transaction, "DELETE FROM spaces WHERE id = $id;", ("$id", id));
        }

        SetState(connection, transaction, "active_space_id", state.ActiveSpaceId ?? string.Empty);
        SetState(connection, transaction, "query_counter", state.QueryCounter.ToString(CultureInfo.InvariantCulture));
        SetState(connection, transaction, "auto_archive_enabled", state.AutoArchiveEnabled ? "true" : "false");
        SetState(connection, transaction, "auto_archive_days", state.AutoArchiveDays.ToString(CultureInfo.InvariantCulture));
        if (state.WindowBounds is { } bounds)
        {
            SetState(
                connection,
                transaction,
                "window_bounds",
                JsonSerializer.Serialize(bounds, StudioJsonContext.Default.WindowBoundsState));
        }

        WriteArchivedTabs(connection, transaction, state.ArchivedTabs);
    }

    private static void WriteProfile(SqliteConnection connection, SqliteTransaction transaction, SpaceState space)
    {
        Execute(connection, transaction, "DELETE FROM space_settings WHERE space_id = $id;", ("$id", space.Id));
        foreach (var pair in space.Profile)
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO space_settings (space_id, key, value) VALUES ($id, $key, $value);",
                ("$id", space.Id),
                ("$key", pair.Key),
                ("$value", pair.Value));
        }
    }

    private static void WriteTabs(SqliteConnection connection, SqliteTransaction transaction, SpaceState space)
    {
        var tabIds = new HashSet<string>(StringComparer.Ordinal);
        var tabOrder = 0;

        foreach (var tab in space.Tabs)
        {
            tabIds.Add(tab.Id);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO tabs (id, space_id, title, database, content, pinned, last_accessed_at, sort_order)
                VALUES ($id, $space, $title, $database, $content, $pinned, $accessed, $sort)
                ON CONFLICT(id) DO UPDATE SET
                    space_id = excluded.space_id,
                    title = excluded.title,
                    database = excluded.database,
                    content = excluded.content,
                    pinned = excluded.pinned,
                    last_accessed_at = excluded.last_accessed_at,
                    sort_order = excluded.sort_order,
                    updated_at = datetime('now');
                """,
                ("$id", tab.Id),
                ("$space", space.Id),
                ("$title", tab.Title),
                ("$database", tab.Database),
                ("$content", tab.Content),
                ("$pinned", tab.Pinned ? 1 : 0),
                ("$accessed", (tab.LastAccessedAt ?? DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture)),
                ("$sort", tabOrder++));
        }

        var storedTabs = SelectIds(connection, transaction, "SELECT id FROM tabs WHERE space_id = $id;", space.Id);
        foreach (var id in storedTabs.Where(id => !tabIds.Contains(id)))
        {
            Execute(connection, transaction, "DELETE FROM tabs WHERE id = $id;", ("$id", id));
        }
    }

    private static void WriteArchivedTabs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<ArchivedTabState> tabs)
    {
        var tabIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tab in tabs)
        {
            tabIds.Add(tab.Id);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO archived_tabs (id, space_id, space_name, color, title, database, content, pinned, archived_at)
                VALUES ($id, $space, $space_name, $color, $title, $database, $content, $pinned, $archived)
                ON CONFLICT(id) DO UPDATE SET
                    space_id = excluded.space_id,
                    space_name = excluded.space_name,
                    color = excluded.color,
                    title = excluded.title,
                    database = excluded.database,
                    content = excluded.content,
                    pinned = excluded.pinned,
                    archived_at = excluded.archived_at;
                """,
                ("$id", tab.Id),
                ("$space", tab.SpaceId),
                ("$space_name", tab.SpaceName),
                ("$color", tab.Color),
                ("$title", tab.Title),
                ("$database", tab.Database),
                ("$content", tab.Content),
                ("$pinned", tab.Pinned ? 1 : 0),
                ("$archived", tab.ArchivedAt.ToString("o", CultureInfo.InvariantCulture)));
        }

        var storedTabs = SelectIds(connection, transaction, "SELECT id FROM archived_tabs;");
        foreach (var id in storedTabs.Where(id => !tabIds.Contains(id)))
        {
            Execute(connection, transaction, "DELETE FROM archived_tabs WHERE id = $id;", ("$id", id));
        }
    }

    private static void SetState(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
        Execute(
            connection,
            transaction,
            """
            INSERT INTO app_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = datetime('now');
            """,
            ("$key", key),
            ("$value", value));

    private static string? ReadState(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static WindowBoundsState? ReadWindowBounds(SqliteConnection connection)
    {
        var raw = ReadState(connection, "window_bounds");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(raw, StudioJsonContext.Default.WindowBoundsState);
        }
        catch (JsonException)
        {
            // A truncated or hand-edited value must not block the launch; the window just centers.
            return null;
        }
    }

    // ---------------------------------------------------------------- legacy import

    private StudioState? TryImportLegacyJson(SqliteConnection connection)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var legacy = Path.Combine(directory, "studio.json");
        if (!File.Exists(legacy))
        {
            return null;
        }

        StudioState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllText(legacy), StudioJsonContext.Default.StudioState);
        }
        catch
        {
            return null;
        }

        if (state is null || state.Spaces.Count == 0)
        {
            return null;
        }

        using (var transaction = connection.BeginTransaction())
        {
            Write(connection, transaction, state);
            transaction.Commit();
        }

        try
        {
            File.Move(legacy, legacy + ".imported", overwrite: true);
        }
        catch
        {
            // The import succeeded; an un-movable source file is not worth failing over.
        }

        return state;
    }

    // ---------------------------------------------------------------- helpers

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> SelectIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string? parameter = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$id", parameter);
        }

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}

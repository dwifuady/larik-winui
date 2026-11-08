namespace Larik.Services;

/// <summary>
/// Where Larik keeps its local state. The WinUI port installs side by side with the Tauri desktop
/// app, so it owns a separate directory and database name: both stores have tables called
/// <c>spaces</c> and <c>tabs</c> but different schemas and <c>user_version</c> values, so one must
/// never open the other's file. Debug builds use a separate file again, like the Tauri version.
/// </summary>
public static class LarikPaths
{
#if DEBUG
    public const string DatabaseFileName = "larik-winui-dev.db";
#else
    public const string DatabaseFileName = "larik-winui.db";
#endif

    /// <summary><c>%LOCALAPPDATA%\Larik WinUI</c>, or the directory named by <c>LARIK_STATE_DIR</c>.</summary>
    public static string StateDirectory { get; } = ResolveStateDirectory();

    public static string DatabasePath { get; } = Path.Combine(StateDirectory, DatabaseFileName);

    /// <summary>User-installed provider assemblies; the MSIX install folder is read-only.</summary>
    public static string PluginsDirectory { get; } = Path.Combine(StateDirectory, "Plugins");

    /// <summary>
    /// The store used before the WinUI build got its own folder (<c>%LOCALAPPDATA%\Larik</c>).
    /// The Tauri app never wrote there (it uses <c>%APPDATA%\larik\larik-sql-studio\data</c>), so
    /// this is safe to carry over once. Null when <c>LARIK_STATE_DIR</c> points somewhere explicit.
    /// </summary>
    private static string? LegacyDatabasePath { get; } = ResolveLegacyDatabasePath();

    /// <summary>
    /// Copies this app's pre-separation store into <see cref="StateDirectory"/> the first time it
    /// runs after the rename. Best effort: a failure leaves a fresh store rather than a failed
    /// launch, and an existing database always wins.
    /// </summary>
    public static void MigrateLegacyState()
    {
        if (LegacyDatabasePath is null || File.Exists(DatabasePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(StateDirectory);

            // The WAL holds the newest committed state; copy it with the database so the first
            // open replays it instead of silently dropping the last session.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var source = LegacyDatabasePath + suffix;
                var destination = DatabasePath + suffix;
                if (File.Exists(source) && !File.Exists(destination))
                {
                    File.Copy(source, destination);
                }
            }

            // Pre-SQLite state is imported by StudioStore on first load.
            var legacyJson = Path.Combine(Path.GetDirectoryName(LegacyDatabasePath)!, "studio.json");
            var destinationJson = Path.Combine(StateDirectory, "studio.json");
            if (File.Exists(legacyJson) && !File.Exists(destinationJson))
            {
                File.Copy(legacyJson, destinationJson);
            }
        }
        catch
        {
            // Migration is a courtesy; a clean store is recoverable, a crash on launch is not.
        }
    }

    private static string ResolveStateDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("LARIK_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Larik WinUI");
    }

    private static string? ResolveLegacyDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LARIK_STATE_DIR")))
        {
            return null;
        }

#if DEBUG
        var fileName = "larik-dev.db";
#else
        var fileName = "larik.db";
#endif

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Larik",
            fileName);
    }
}

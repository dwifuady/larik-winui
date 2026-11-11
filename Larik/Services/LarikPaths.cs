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

    /// <summary>
    /// The package's <c>LocalState</c> folder for a packaged run, otherwise null. Resolved once:
    /// <c>ApplicationData.Current</c> throws when the process has no package identity.
    /// </summary>
    private static readonly string? PackageStateDirectory = ResolvePackageStateDirectory();

    /// <summary>True when this process runs from an MSIX package (Store, or the debug identity).</summary>
    public static bool IsPackaged => PackageStateDirectory is not null;

    /// <summary>
    /// The package's <c>LocalState</c> for a packaged run, otherwise <c>%LOCALAPPDATA%\Larik WinUI</c>,
    /// or the directory named by <c>LARIK_STATE_DIR</c>.
    /// </summary>
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

        // A packaged copy keeps its state inside the package. Writing to %LOCALAPPDATA% would
        // outlive an uninstall and land in the same folder as the unpackaged (GitHub) edition,
        // which is exactly the edition isolation the Store build promises.
        if (PackageStateDirectory is not null)
        {
            return PackageStateDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Larik WinUI");
    }

    private static string? ResolvePackageStateDirectory()
    {
        try
        {
            var local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            return string.IsNullOrEmpty(local) ? null : local;
        }
        catch
        {
            // No package identity, or the API is unavailable: an unpackaged run is not an error.
            return null;
        }
    }

    private static string? ResolveLegacyDatabasePath()
    {
        if (IsPackaged || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LARIK_STATE_DIR")))
        {
            // The legacy store lives outside the package. A packaged copy must not import it:
            // that would cross the edition boundary the Store build is meant to keep.
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

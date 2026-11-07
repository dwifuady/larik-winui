namespace Larik.Services;

/// <summary>
/// Where Larik keeps its local state. Debug builds use a separate database, like the Tauri
/// version, so a development session never touches a real workspace store.
/// </summary>
public static class LarikPaths
{
#if DEBUG
    public const string DatabaseFileName = "larik-dev.db";
#else
    public const string DatabaseFileName = "larik.db";
#endif

    /// <summary><c>%LOCALAPPDATA%\Larik</c>, or the directory named by <c>LARIK_STATE_DIR</c>.</summary>
    public static string StateDirectory { get; } = ResolveStateDirectory();

    public static string DatabasePath { get; } = Path.Combine(StateDirectory, DatabaseFileName);

    private static string ResolveStateDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("LARIK_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Larik");
    }
}

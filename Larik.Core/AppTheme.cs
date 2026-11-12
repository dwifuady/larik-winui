namespace Larik.Services;

/// <summary>The user's theme choice; <see cref="System"/> follows Windows and is the default.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Storage form of <see cref="AppTheme"/>. It lives here rather than in ThemeService because the
/// store reads the saved preference before any window (and any WinUI type) exists, and because the
/// MCP server reads the same store without referencing WinUI.
/// </summary>
public static class AppThemeText
{
    public static AppTheme Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

    public static string ToStorage(AppTheme theme) =>
        theme switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            _ => "system",
        };
}

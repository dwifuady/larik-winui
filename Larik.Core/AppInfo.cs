using System.Reflection;

namespace Larik.Services;

/// <summary>
/// Build identity shown in the UI and compared against GitHub Releases. Read from assembly
/// metadata, so it matches whatever version the release workflow stamped on the build.
/// </summary>
public static class AppInfo
{
    /// <summary>The release version without build metadata, e.g. "0.1.1".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>True when <see cref="Version"/> is known.</summary>
    public static bool HasVersion => Version.Length > 0;

    /// <summary>Display form for the UI, e.g. "v0.1.1"; empty when the version is unknown.</summary>
    public static string VersionText => HasVersion ? $"v{Version}" : string.Empty;

    private static string ReadVersion()
    {
        // The entry assembly carries the release workflow's version stamp. AppInfo lives in
        // Larik.Core, whose own assembly version is meaningless to the user, so prefer the process
        // that started it: Larik.exe for the app, Larik.Mcp.exe for the MCP server.
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Release builds append "+<commit sha>"; the UI and the update check want the number.
            var buildMetadata = informational.IndexOf('+');
            return buildMetadata > 0 ? informational[..buildMetadata] : informational;
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null
            ? string.Empty
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}

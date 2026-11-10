using System.Reflection;
using System.Runtime.InteropServices;
using Larik.Data.Addons;

namespace Larik.Services;

/// <summary>
/// The app-side add-on catalogue: where the index lives, which channel this build trusts, and the
/// shared HTTP client. The data plane lives in <see cref="Larik.Data.Addons"/> so it is testable
/// without WinUI.
/// </summary>
public static class AddonCatalog
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// The open channel for portable, installer and winget builds. A Store build should point at
    /// <c>official.json</c>; the channel switch arrives with the Store packaging work.
    /// </summary>
    public static string IndexLocation =>
        Environment.GetEnvironmentVariable("LARIK_ADDON_INDEX")
        ?? "https://raw.githubusercontent.com/dwifuady/larik-addons/main/index.json";

    public static string AppVersion { get; } = ReadAppVersion();

    public static string CurrentPlatform =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

    public static AddonIndexClient CreateIndexClient() => new(Http);

    public static AddonInstaller CreateInstaller() => new(Http);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Larik/0.1 (+https://github.com/dwifuady/larik-winui)");
        return client;
    }

    private static string ReadAppVersion()
    {
        var informational = typeof(AddonCatalog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? "0.0.0"
            : informational.Split('+')[0];
    }
}

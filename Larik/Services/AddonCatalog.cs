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
    /// The open channel for portable, installer and winget builds; the official channel for a
    /// Store build. The Store build does not offer add-ons, so this is mostly future-proofing.
    /// </summary>
    public static string IndexLocation => AddonPolicy.IndexLocation;

    public static bool AddonsEnabled => AddonPolicy.AddonsEnabled;

    public static string AppVersion { get; } = AppInfo.HasVersion ? AppInfo.Version : "0.0.0";

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
}

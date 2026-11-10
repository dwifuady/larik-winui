namespace Larik.Services;

/// <summary>
/// What this build is allowed to do with database drivers. The Microsoft Store package ships only
/// the built-in providers and never loads downloaded assemblies; the GitHub build keeps the
/// curated add-on index. Everything else in the app still talks to the provider contracts.
/// </summary>
public static class AddonPolicy
{
#if LARIK_STORE
    public static readonly bool AddonsEnabled = false;
#else
    public static readonly bool AddonsEnabled = true;
#endif

    /// <summary>
    /// The open index for GitHub builds; the curated first-party channel for a Store build. Store
    /// builds do not use it yet, but naming the channel keeps the intent explicit.
    /// </summary>
    public static string IndexLocation =>
        Environment.GetEnvironmentVariable("LARIK_ADDON_INDEX")
        ?? (AddonsEnabled
            ? "https://raw.githubusercontent.com/dwifuady/larik-addons/main/index.json"
            : "https://raw.githubusercontent.com/dwifuady/larik-addons/main/official.json");

    /// <summary>Shown in copy where the edition matters, e.g. a missing driver.</summary>
    public static string MissingDriverMessage => AddonsEnabled
        ? "Driver not installed"
        : "Not included in this edition";
}

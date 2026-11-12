using Larik.Data;
using Larik.Data.Addons;
using Larik.Data.Demo;
using Larik.Data.Postgres;
using Larik.Data.SqlServer;

namespace Larik.Services;

/// <summary>
/// The composition root for database providers. Built-ins are always present; installed add-ons are
/// loaded at startup and a newly installed one can be added while the app is running, so a first
/// install does not need a restart. Updates and removals still do: the old assembly is loaded and
/// .NET cannot unload it.
/// </summary>
public static class DriverRuntime
{
    private static readonly List<ProviderSource> Loaded = [];
    private static readonly List<AddonLoadContext> Contexts = [];
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (!AddonPolicy.AddonsEnabled)
        {
            // The Store build is built-ins only: no Plugins scan, no downloaded assemblies.
            DatabaseProviderCatalog.Initialize(
                [
                    new ProviderSource("built-in: Demo", typeof(DemoProvider).Assembly),
                    new ProviderSource("built-in: SQL Server", typeof(SqlServerProvider).Assembly),
                    new ProviderSource("built-in: PostgreSQL", typeof(PostgresProvider).Assembly),
                ]);

            LogCatalogue();
            return;
        }

        var scan = AddonLocator.Scan(LarikPaths.PluginsDirectory);
        Loaded.AddRange(scan.Sources);
        Contexts.AddRange(scan.Contexts);

        DatabaseProviderCatalog.Initialize(
            [
                new ProviderSource("built-in: Demo", typeof(DemoProvider).Assembly),
                new ProviderSource("built-in: SQL Server", typeof(SqlServerProvider).Assembly),
                new ProviderSource("built-in: PostgreSQL", typeof(PostgresProvider).Assembly),
                .. Loaded,
            ],
            scan.Errors,
            Path.Combine(AppContext.BaseDirectory, "Plugins"),
            LarikPaths.PluginsDirectory);

        LogCatalogue();
    }

    /// <summary>
    /// Writes the provider catalogue and add-on load failures to the diagnostics log. A driver that
    /// fails to load usually only shows as a missing workspace option, so the ids and errors are the
    /// first thing a bug report needs.
    /// </summary>
    private static void LogCatalogue()
    {
        var providers = DatabaseProviderCatalog.Providers;
        AppLog.Info(
            "providers",
            $"{providers.Count} loaded: {string.Join(", ", providers.Select(provider => $"{provider.DisplayName} [{provider.Id}]"))}");

        foreach (var error in DatabaseProviderCatalog.LoadErrors)
        {
            AppLog.Warning("providers", $"load error: {error}");
        }
    }

    /// <summary>
    /// Loads the add-on in <c>Plugins\&lt;id&gt;</c> into the running catalogue. Returns the providers
    /// it added (empty when the id was already loaded or the assembly failed).
    /// </summary>
    public static IReadOnlyList<IDatabaseProvider> LoadInstalled(string id)
    {
        if (!AddonPolicy.AddonsEnabled)
        {
            return [];
        }

        var directory = Path.Combine(LarikPaths.PluginsDirectory, id);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var scan = AddonLocator.ScanAddon(directory);
        if (scan.Sources.Count == 0)
        {
            return [];
        }

        Loaded.AddRange(scan.Sources);
        Contexts.AddRange(scan.Contexts);
        return DatabaseProviderCatalog.AddSources(scan.Sources);
    }

    /// <summary>Whether a provider id already has a live implementation.</summary>
    public static bool IsAvailable(string providerId) =>
        DatabaseProviderCatalog.Find(providerId) is not null;
}

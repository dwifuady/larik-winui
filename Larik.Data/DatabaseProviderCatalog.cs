using System.Reflection;

namespace Larik.Data;

/// <summary>
/// Discovers <see cref="IDatabaseProvider"/> implementations from the app's referenced provider
/// assemblies and manifest-backed add-ons. Adding a database means adding a provider assembly, not
/// editing the app.
/// </summary>
public static class DatabaseProviderCatalog
{
    private static readonly List<string> Errors = [];

    public static IReadOnlyList<IDatabaseProvider> Providers { get; private set; } = [];

    public static IReadOnlyList<string> LoadErrors => Errors;

    /// <summary>Provider used for new workspaces; SQL Server unless it failed to load.</summary>
    public static IDatabaseProvider? Default =>
        Find("mssql") ?? Providers.FirstOrDefault();

    public static IDatabaseProvider? Find(string? providerId) =>
        providerId is null
            ? null
            : Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The only entry point. <paramref name="sources"/> are assemblies the caller already loaded —
    /// built-ins and add-ons alike — so the catalogue never has to guess how to load them.
    /// <paramref name="errors"/> carries failures from the add-on scan. Bare DLLs are deliberately
    /// not discovered: loading them would bypass the add-on manifest, hash check and install prompt.
    /// </summary>
    public static void Initialize(
        IEnumerable<ProviderSource> sources,
        IEnumerable<string>? errors = null)
    {
        Errors.Clear();
        if (errors is not null)
        {
            Errors.AddRange(errors);
        }

        List<IDatabaseProvider> providers = [];

        foreach (var source in sources.DistinctBy(source => source.Assembly))
        {
            AddFromAssembly(source, providers);
        }

        Providers = providers
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Compatibility overload for callers that only have assemblies.</summary>
    public static void Initialize(IEnumerable<Assembly> builtInAssemblies) =>
        Initialize(
            builtInAssemblies.Select(assembly => new ProviderSource(assembly.GetName().Name ?? "built-in", assembly)),
            errors: null);

    /// <summary>
    /// Adds providers from add-ons installed while the app is running. Existing ids win, so an
    /// update to a loaded provider still needs a restart; a brand-new provider becomes usable now.
    /// Returns only the providers that were not already in the catalogue.
    /// </summary>
    public static IReadOnlyList<IDatabaseProvider> AddSources(IEnumerable<ProviderSource> sources)
    {
        var existingIds = Providers
            .Select(provider => provider.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<IDatabaseProvider> discovered = [];
        foreach (var source in sources.DistinctBy(source => source.Assembly))
        {
            AddFromAssembly(source, discovered);
        }

        var fresh = discovered
            .Where(provider => !existingIds.Contains(provider.Id))
            .ToList();

        if (fresh.Count > 0)
        {
            Providers = Providers
                .Concat(fresh)
                .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return fresh;
    }

    private static void AddFromAssembly(ProviderSource source, List<IDatabaseProvider> providers)
    {
        Type[] types;
        try
        {
            types = source.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            Errors.Add($"{source.Origin}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Errors.Add($"{source.Origin}: {ex.GetBaseException().Message}");
            return;
        }

        foreach (var type in types)
        {
            if (type is not { IsAbstract: false, IsInterface: false } ||
                !typeof(IDatabaseProvider).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is IDatabaseProvider provider)
                {
                    providers.Add(provider);
                }
            }
            catch (Exception ex)
            {
                Errors.Add($"{source.Origin} ({type.FullName}): {ex.GetBaseException().Message}");
            }
        }
    }
}

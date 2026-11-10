using System.Reflection;

namespace Larik.Data;

/// <summary>
/// Discovers <see cref="IDatabaseProvider"/> implementations from the app's referenced provider
/// assemblies, installed add-ons, and any DLL dropped into a Plugins folder. Adding a database means
/// adding a provider assembly, not editing the app.
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
    /// The preferred entry point. <paramref name="sources"/> are assemblies the caller already
    /// loaded — built-ins and add-ons alike — so the catalogue never has to guess how to load them.
    /// <paramref name="errors"/> carries failures from the add-on scan; legacy flat DLLs in
    /// <paramref name="legacyPluginDirectories"/> are still supported for compatibility.
    /// </summary>
    public static void Initialize(
        IEnumerable<ProviderSource> sources,
        IEnumerable<string>? errors = null,
        params string[] legacyPluginDirectories)
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

        foreach (var directory in legacyPluginDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                try
                {
                    AddFromAssembly(
                        new ProviderSource(Path.GetFileName(file), Assembly.LoadFrom(file)),
                        providers);
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
                {
                    // Native or otherwise unmanaged DLL sitting beside plugins: not our business.
                }
                catch (Exception ex)
                {
                    Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        Providers = providers
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Compatibility overload for callers that only have assemblies.</summary>
    public static void Initialize(IEnumerable<Assembly> builtInAssemblies, params string[] pluginDirectories) =>
        Initialize(
            builtInAssemblies.Select(assembly => new ProviderSource(assembly.GetName().Name ?? "built-in", assembly)),
            errors: null,
            pluginDirectories);

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

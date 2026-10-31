using System.Reflection;

namespace Larik.Data;

/// <summary>
/// Discovers <see cref="IDatabaseProvider"/> implementations from the app's referenced provider
/// assemblies plus any DLL dropped into a Plugins folder. Adding a database means adding a
/// provider assembly, not editing the app.
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

    public static void Initialize(IEnumerable<Assembly> builtInAssemblies, params string[] pluginDirectories)
    {
        Errors.Clear();

        var providers = new List<IDatabaseProvider>();

        foreach (var assembly in builtInAssemblies.Distinct())
        {
            AddFromAssembly(assembly, providers);
        }

        foreach (var directory in pluginDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                try
                {
                    AddFromAssembly(Assembly.LoadFrom(file), providers);
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

    private static void AddFromAssembly(Assembly assembly, List<IDatabaseProvider> providers)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            Errors.Add($"{assembly.GetName().Name}: {ex.Message}");
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
                Errors.Add($"{type.FullName}: {ex.GetBaseException().Message}");
            }
        }
    }
}

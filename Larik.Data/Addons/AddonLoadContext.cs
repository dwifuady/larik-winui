using System.Reflection;
using System.Runtime.Loader;

namespace Larik.Data.Addons;

/// <summary>
/// One load context per add-on, so two providers can ship different versions of Npgsql or
/// MongoDB.Driver without fighting over the app's copy. It is not collectible: applying an update
/// means a new versioned folder and a restart, not unloading code the app may still reference.
/// </summary>
public sealed class AddonLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _directory;
    private readonly Assembly _contract = typeof(IDatabaseProvider).Assembly;

    public AddonLoadContext(string addonDirectory, string entryAssembly)
        : base($"larik-addon:{Path.GetFileName(addonDirectory)}", isCollectible: false)
    {
        _directory = addonDirectory;

        // A hand-built add-on may have no .deps.json. The resolver needs one; without it, fall
        // back to loading sibling DLLs by simple name.
        var entryPath = Path.Combine(addonDirectory, entryAssembly);
        if (File.Exists(Path.ChangeExtension(entryPath, ".deps.json")))
        {
            _resolver = new AssemblyDependencyResolver(entryPath);
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The contract must be the host's instance. If the add-on got its own copy, its
        // IDatabaseProvider would be a different type and discovery would silently find nothing.
        if (string.Equals(assemblyName.Name, _contract.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return _contract;
        }

        // Share the platform the host already loaded (System.*, Microsoft.*, WinRT). Otherwise
        // resolve from the add-on folder, which is what keeps driver versions isolated.
        var shared = Default.Assemblies.FirstOrDefault(
            assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (shared is not null)
        {
            return shared;
        }

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is { } name)
        {
            var candidate = Path.Combine(_directory, name + ".dll");
            path = File.Exists(candidate) ? candidate : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            var candidate = Path.Combine(_directory, unmanagedDllName);
            if (!File.Exists(candidate))
            {
                candidate = Path.Combine(_directory, unmanagedDllName + ".dll");
            }

            path = File.Exists(candidate) ? candidate : null;
        }

        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

using System.Text.Json;

namespace Larik.Data.Addons;

/// <summary>
/// Discovers add-ons installed as <c>&lt;root&gt;\&lt;id&gt;\current.json</c> pointing at a versioned
/// folder. A failure in one add-on is recorded and skipped; it never stops the core or the other
/// add-ons from loading.
/// </summary>
public static class AddonLocator
{
    private sealed record CurrentVersion(string Version);

    /// <summary>Scans an add-on root. Missing roots are normal, not an error.</summary>
    public static AddonScan Scan(string root)
    {
        List<ProviderSource> sources = [];
        List<string> errors = [];
        List<AddonLoadContext> contexts = [];

        if (!Directory.Exists(root))
        {
            return new AddonScan(sources, errors, contexts);
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Can't read {root}: {ex.Message}");
            return new AddonScan(sources, errors, contexts);
        }

        foreach (var addonDirectory in directories)
        {
            var scan = ScanAddon(addonDirectory);
            sources.AddRange(scan.Sources);
            errors.AddRange(scan.Errors);
            contexts.AddRange(scan.Contexts);
        }

        return new AddonScan(sources, errors, contexts);
    }

    /// <summary>
    /// Scans one installed add-on. Used both at startup and when a driver is installed while the
    /// app is running.
    /// </summary>
    public static AddonScan ScanAddon(string addonDirectory)
    {
        List<ProviderSource> sources = [];
        List<string> errors = [];
        List<AddonLoadContext> contexts = [];

        var folderName = Path.GetFileName(addonDirectory);
        if (File.Exists(Path.Combine(addonDirectory, "disabled")))
        {
            return new AddonScan(sources, errors, contexts);
        }

        var version = ReadCurrentVersion(addonDirectory);
        if (version is null)
        {
            // Not an add-on folder. Flat DLLs in the root are the legacy layout, which the
            // catalogue still scans separately.
            return new AddonScan(sources, errors, contexts);
        }

        var versionDirectory = Path.Combine(addonDirectory, version);
        if (!Directory.Exists(versionDirectory))
        {
            errors.Add($"{folderName} {version}: the active version folder is missing.");
            return new AddonScan(sources, errors, contexts);
        }

        if (!AddonManifest.TryLoad(
                Path.Combine(versionDirectory, "larik-addon.json"),
                out var manifest,
                out var manifestError))
        {
            errors.Add($"{folderName} {version}: {manifestError ?? "the manifest is invalid."}");
            return new AddonScan(sources, errors, contexts);
        }

        var entryPath = Path.Combine(versionDirectory, manifest!.EntryAssembly);
        if (!File.Exists(entryPath))
        {
            errors.Add($"{folderName} {version}: {manifest.EntryAssembly} is missing.");
            return new AddonScan(sources, errors, contexts);
        }

        try
        {
            var context = new AddonLoadContext(versionDirectory, manifest.EntryAssembly);
            var assembly = context.LoadFromAssemblyPath(entryPath);

            // Held for the process lifetime: non-collectible contexts must not be dropped, and
            // the catalogue needs the assembly to stay discoverable.
            contexts.Add(context);
            sources.Add(new ProviderSource($"{manifest.Id} {manifest.Version}", assembly));
        }
        catch (Exception ex)
        {
            errors.Add($"{folderName} {version}: {ex.GetBaseException().Message}");
        }

        return new AddonScan(sources, errors, contexts);
    }

    private static string? ReadCurrentVersion(string addonDirectory)
    {
        var path = Path.Combine(addonDirectory, "current.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<CurrentVersion>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Installed add-on assemblies plus anything that failed to load. The contexts are the live load
/// contexts and must be kept for the process lifetime.
/// </summary>
public sealed record AddonScan(
    IReadOnlyList<ProviderSource> Sources,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AddonLoadContext> Contexts);

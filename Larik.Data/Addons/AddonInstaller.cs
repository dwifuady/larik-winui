using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Larik.Data.Addons;

/// <summary>An add-on visible on disk, whether or not it loaded into the catalogue.</summary>
public sealed record InstalledAddon(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> ProviderIds,
    bool Enabled,
    string Directory,
    AddonManifest Manifest);

/// <summary>
/// Downloads, verifies and installs add-on zips under <c>&lt;root&gt;\&lt;id&gt;\&lt;version&gt;</c>.
/// It never loads the assembly: the host applies an install on the next start.
/// </summary>
public sealed class AddonInstaller
{
    private const long MaxDownloadBytes = 100L * 1024 * 1024;
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private const int MaxEntries = 10_000;

    private readonly HttpClient _http;

    public AddonInstaller(HttpClient http) => _http = http;

    public async Task<InstalledAddon> InstallAsync(
        AddonIndexEntry entry,
        string pluginsRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"larik-addon-{Guid.NewGuid():N}.zip");

        try
        {
            using var response = await _http.GetAsync(
                entry.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            byte[] actualHash;

            // Close the file before the zip is opened for reading; Windows will not share it while
            // the write stream is alive.
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(temp);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > MaxDownloadBytes)
                    {
                        throw new InvalidDataException("The add-on download is larger than 100 MB.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(total is > 0 ? Math.Min(1d, (double)received / total.Value) : 0d);
                }

                actualHash = hash.GetHashAndReset();
            }

            var actual = Convert.ToHexString(actualHash).ToLowerInvariant();
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The add-on download failed verification (expected {entry.Sha256}, got {actual}).");
            }

            return await InstallZipAsync(temp, pluginsRoot, entry.Id, entry.Version, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Installs a zip the user picked. The manifest decides the id and version.</summary>
    public Task<InstalledAddon> InstallZipAsync(
        string zipPath,
        string pluginsRoot,
        CancellationToken cancellationToken = default) =>
        InstallZipAsync(zipPath, pluginsRoot, expectedId: null, expectedVersion: null, cancellationToken);

    private static async Task<InstalledAddon> InstallZipAsync(
        string zipPath,
        string pluginsRoot,
        string? expectedId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        if (archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("The add-on zip contains too many entries.");
        }

        var manifestEntry = archive.Entries.FirstOrDefault(
            entry => string.Equals(entry.FullName, "larik-addon.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The add-on zip has no larik-addon.json at its root.");

        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestJson = await reader.ReadToEndAsync(cancellationToken);
        }

        if (!AddonManifest.TryParse(manifestJson, out var manifest, out var manifestError))
        {
            throw new InvalidDataException($"The add-on manifest is invalid: {manifestError}");
        }

        if (expectedId is not null &&
            !string.Equals(manifest!.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The zip contains '{manifest.Id}', not '{expectedId}'.");
        }

        if (expectedVersion is not null &&
            !string.Equals(manifest!.Version, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The zip contains version {manifest.Version}, not {expectedVersion}.");
        }

        var root = Path.GetFullPath(pluginsRoot);
        Directory.CreateDirectory(root);

        var addonRoot = Path.GetFullPath(Path.Combine(root, manifest!.Id));
        if (!IsInside(root, addonRoot))
        {
            throw new InvalidDataException("The add-on id points outside the plugins folder.");
        }

        var staging = Path.Combine(addonRoot, $".staging-{manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var stagingRoot = Path.GetFullPath(staging);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName));
                if (!IsInside(stagingRoot, destination))
                {
                    throw new InvalidDataException($"The add-on zip contains an unsafe path: {entry.FullName}");
                }

                if (entry.Length > MaxEntryBytes)
                {
                    throw new InvalidDataException($"The add-on zip entry '{entry.FullName}' is too large.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            var entryAssembly = Path.Combine(stagingRoot, manifest.EntryAssembly);
            if (!File.Exists(entryAssembly))
            {
                throw new InvalidDataException(
                    $"The add-on zip does not contain {manifest.EntryAssembly}.");
            }

            var versionDirectory = Path.Combine(addonRoot, manifest.Version);
            if (Directory.Exists(versionDirectory))
            {
                // The same version is already on disk. Replacing a loaded assembly is not possible
                // without a restart, so treat this as already installed rather than half-overwrite.
                throw new InvalidOperationException(
                    $"Add-on {manifest.Id} {manifest.Version} is already installed. Restart Larik before reinstalling it.");
            }

            Directory.Move(staging, versionDirectory);

            var current = JsonSerializer.Serialize(new { version = manifest.Version });
            await File.WriteAllTextAsync(
                Path.Combine(addonRoot, "current.json"),
                current,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            return new InstalledAddon(
                manifest.Id,
                manifest.Publisher ?? manifest.Id,
                manifest.Version,
                manifest.ProviderIds,
                Enabled: true,
                versionDirectory,
                manifest);
        }
        catch
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
            }

            try
            {
                // Leave an existing add-on's folder alone, but do not leave a new empty one behind.
                if (Directory.Exists(addonRoot) && !Directory.EnumerateFileSystemEntries(addonRoot).Any())
                {
                    Directory.Delete(addonRoot);
                }
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public IReadOnlyList<InstalledAddon> ListInstalled(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        List<InstalledAddon> installed = [];

        foreach (var addonDirectory in Directory.EnumerateDirectories(pluginsRoot))
        {
            var currentPath = Path.Combine(addonDirectory, "current.json");
            if (!File.Exists(currentPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(currentPath);
                var current = JsonSerializer.Deserialize<CurrentVersion>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (current?.Version is not { Length: > 0 } version)
                {
                    continue;
                }

                var versionDirectory = Path.Combine(addonDirectory, version);
                if (!AddonManifest.TryLoad(
                        Path.Combine(versionDirectory, "larik-addon.json"),
                        out var manifest,
                        out _))
                {
                    continue;
                }

                installed.Add(new InstalledAddon(
                    manifest!.Id,
                    manifest.Publisher ?? manifest.Id,
                    manifest.Version,
                    manifest.ProviderIds,
                    Enabled: !File.Exists(Path.Combine(addonDirectory, "disabled")),
                    versionDirectory,
                    manifest));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken install is skipped here; the catalogue already reports the load error.
            }
        }

        return installed
            .OrderBy(addon => addon.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Remove(string pluginsRoot, string id)
    {
        var directory = AddonDirectory(pluginsRoot, id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public void SetEnabled(string pluginsRoot, string id, bool enabled)
    {
        var directory = AddonDirectory(pluginsRoot, id);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Add-on {id} is not installed.");
        }

        var marker = Path.Combine(directory, "disabled");
        if (enabled)
        {
            File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker, string.Empty);
        }
    }

    private static string AddonDirectory(string pluginsRoot, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
        {
            throw new ArgumentException("The add-on id is invalid.", nameof(id));
        }

        var root = Path.GetFullPath(pluginsRoot);
        var directory = Path.GetFullPath(Path.Combine(root, id));
        if (!IsInside(root, directory))
        {
            throw new ArgumentException("The add-on id points outside the plugins folder.", nameof(id));
        }

        return directory;
    }

    private static bool IsInside(string root, string candidate) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);

    private sealed record CurrentVersion(string Version);
}

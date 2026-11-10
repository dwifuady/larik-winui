using System.Text.Json;
using System.Text.Json.Serialization;

namespace Larik.Data.Addons;

/// <summary>
/// The <c>larik-addon.json</c> shipped inside an add-on. The index entry is authoritative once an
/// add-on comes from the catalogue; this file exists so an offline zip install can show metadata and
/// so CI can cross-check the two.
/// </summary>
public sealed record AddonManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    /// <summary>Range over <see cref="LarikDataContract.Version"/>, e.g. <c>&gt;=1.0 &lt;2.0</c>.</summary>
    [JsonPropertyName("contract")]
    public required string Contract { get; init; }

    [JsonPropertyName("providerIds")]
    public required IReadOnlyList<string> ProviderIds { get; init; }

    /// <summary>The DLL that contains the provider types, relative to the add-on folder.</summary>
    [JsonPropertyName("entryAssembly")]
    public required string EntryAssembly { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = [];

    [JsonPropertyName("license")]
    public string? License { get; init; }

    /// <summary>
    /// Reads and validates a manifest. Returns false with a human-readable error for anything the
    /// loader would otherwise fail on later, so the add-on page can name the problem.
    /// </summary>
    public static bool TryLoad(string path, out AddonManifest? manifest, out string? error)
    {
        try
        {
            using var stream = File.OpenRead(path);
            manifest = JsonSerializer.Deserialize<AddonManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            manifest = null;
            error = ex.Message;
            return false;
        }

        if (manifest is null)
        {
            error = "The manifest is empty.";
            return false;
        }

        error = manifest.Validate();
        return error is null;
    }

    /// <summary>Null when the manifest is usable, otherwise the reason it is not.</summary>
    public string? Validate()
    {
        if (ManifestVersion != 1)
        {
            return $"manifestVersion {ManifestVersion} is not supported.";
        }

        if (string.IsNullOrWhiteSpace(Id) || Id.Contains('/') || Id.Contains('\\'))
        {
            return "id is missing or contains a path separator.";
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            return "version is missing.";
        }

        if (!LarikDataContract.IsCompatible(Contract))
        {
            return $"contract range '{Contract}' is invalid or does not admit {LarikDataContract.Version}.";
        }

        if (ProviderIds is not { Count: > 0 } || ProviderIds.Any(string.IsNullOrWhiteSpace))
        {
            return "providerIds is empty.";
        }

        if (string.IsNullOrWhiteSpace(EntryAssembly) ||
            !EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            EntryAssembly.Contains('/') ||
            EntryAssembly.Contains('\\'))
        {
            return "entryAssembly must be a dll file name inside the add-on folder.";
        }

        return null;
    }
}

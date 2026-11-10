using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Larik.Data.Addons;

/// <summary>The curated catalogue the app reads from GitHub.</summary>
public sealed record AddonIndex
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "all";

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; init; }

    [JsonPropertyName("addons")]
    public IReadOnlyList<AddonIndexEntry> Addons { get; init; } = [];
}

/// <summary>One approved add-on. The index is authoritative; the zip's manifest is checked against it.</summary>
public sealed record AddonIndexEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("contract")]
    public required string Contract { get; init; }

    [JsonPropertyName("minAppVersion")]
    public required string MinAppVersion { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string> Platforms { get; init; } = [];

    [JsonPropertyName("providerIds")]
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    /// <summary>Whether this entry can run on the given app version and platform.</summary>
    public bool Matches(string appVersion, string platform) =>
        LarikDataContract.IsCompatible(Contract) &&
        Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase) &&
        AddonVersions.IsAtLeast(appVersion, MinAppVersion);
}

/// <summary>Pads versions to four components so <c>1.0</c> and <c>1.0.0</c> compare equal.</summary>
public static class AddonVersions
{
    public static bool IsAtLeast(string? actual, string? required) => Compare(actual, required) >= 0;

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    public static int Compare(string? left, string? right)
    {
        if (!TryParse(left, out var leftVersion) || !TryParse(right, out var rightVersion))
        {
            return 0;
        }

        return leftVersion.CompareTo(rightVersion);
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out var number) || number < 0)
            {
                return false;
            }

            numbers[index] = number;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}

/// <summary>Fetches the index from an https URL, or a local path for tests and offline mirrors.</summary>
public sealed class AddonIndexClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;

    public AddonIndexClient(HttpClient http) => _http = http;

    public async Task<AddonIndex> FetchAsync(string location, CancellationToken cancellationToken = default)
    {
        string json;

        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            var path = uri is { Scheme: "file" } ? uri.LocalPath : location;
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }

        var index = JsonSerializer.Deserialize<AddonIndex>(json, JsonOptions)
            ?? throw new InvalidDataException("The add-on index is empty.");

        if (index.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Add-on index schemaVersion {index.SchemaVersion} is not supported.");
        }

        return index;
    }
}

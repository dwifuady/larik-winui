using Larik.Data.Addons;

namespace Larik.Models;

/// <summary>One row in the Add-ons page: an installed add-on or an index entry.</summary>
public sealed class AddonRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string VersionText { get; init; }

    public required string Detail { get; init; }

    public required string Status { get; init; }

    public required string ActionText { get; init; }

    public string? ToggleText { get; init; }

    public bool IsInstalled { get; init; }

    public AddonIndexEntry? Entry { get; init; }

    public InstalledAddon? Installed { get; init; }
}

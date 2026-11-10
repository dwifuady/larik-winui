namespace Larik.Data;

/// <summary>
/// The semantic version of the provider contract. Add-ons declare a range against this value in
/// their <c>larik-addon.json</c>; the host compares it before loading. It is deliberately separate
/// from <see cref="System.Reflection.AssemblyVersion"/>, which stays pinned so existing plugins keep
/// binding to the host's copy of this assembly.
/// </summary>
public static class LarikDataContract
{
    /// <summary>
    /// Bump the minor version for additive members with defaults; bump the major version only for a
    /// breaking change, which also means accepting that old add-ons stop loading.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Whether <paramref name="range"/> admits the running contract. The only supported form is
    /// <c>&gt;=x.y &lt;z.w</c> (four numeric components are accepted, missing ones count as zero).
    /// Returns false for null, empty, or anything more elaborate.
    /// </summary>
    public static bool IsCompatible(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        var parts = range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !TryParseBound(parts[0], ">=", out var lower) ||
            !TryParseBound(parts[1], "<", out var upper) ||
            !TryParseVersion(Version, out var current))
        {
            return false;
        }

        return current >= lower && current < upper;
    }

    private static bool TryParseBound(string text, string prefix, out Version version)
    {
        version = default!;
        return text.StartsWith(prefix, StringComparison.Ordinal) &&
               TryParseVersion(text[prefix.Length..], out version);
    }

    /// <summary>
    /// Pads to four components so <c>1.0</c> and <c>1.0.0</c> compare equal. <see cref="Version"/>
    /// treats an omitted component as -1, which would make <c>1.0 &lt; 1.0.0</c> true.
    /// </summary>
    private static bool TryParseVersion(string text, out Version version)
    {
        version = default!;
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

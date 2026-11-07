using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Larik.Services;

/// <summary>Space palette and colour helpers (DESIGN.md "Space Colors").</summary>
public static class StudioColors
{
    public static string[] Palette { get; } =
    [
        "#C4B5FD", "#93C5FD", "#67E8F9", "#86EFAC", "#FDE047",
        "#FDBA74", "#F9A8D4", "#FCA5A5", "#A5F3FC", "#E9D5FF",
    ];

    /// <summary>Names for <see cref="Palette"/>, aligned by index (DESIGN.md "Space Colors").</summary>
    public static string[] PaletteNames { get; } =
    [
        "Pastel Purple", "Pastel Blue", "Pastel Cyan", "Pastel Green", "Pastel Yellow",
        "Pastel Orange", "Pastel Pink", "Pastel Red", "Light Cyan", "Light Purple",
    ];

    public static SolidColorBrush Success { get; } = Brush("#22C55E");
    public static SolidColorBrush Warning { get; } = Brush("#F59E0B");
    public static SolidColorBrush Danger { get; } = Brush("#EF4444");
    public static SolidColorBrush Muted { get; } = Brush("#8A8A8F");

    public static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush(Parse(hex));
    }

    /// <summary>Arc-style active tab tint: the space colour at ~26% over the sidebar.</summary>
    public static SolidColorBrush Tint(string hex) => new(WithAlpha(Parse(hex), 0x42));

    public static Color Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6)
        {
            return Color.FromArgb(
                0xFF,
                Convert.ToByte(value[..2], 16),
                Convert.ToByte(value[2..4], 16),
                Convert.ToByte(value[4..6], 16));
        }

        return Color.FromArgb(
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value[2..4], 16),
            Convert.ToByte(value[4..6], 16),
            Convert.ToByte(value[6..8], 16));
    }

    public static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}

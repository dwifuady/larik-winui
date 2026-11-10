using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Larik.Services;

/// <summary>The user's theme choice; <see cref="System"/> follows Windows and is the default.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Owns the app's appearance preference and applies it to the live window. The saved preference is
/// set through <see cref="Initialize"/> before the first window exists (Application.RequestedTheme
/// can only be assigned during startup); later changes go through <see cref="Apply"/>, which moves
/// the root element and the native title bar together.
/// </summary>
public static class ThemeService
{
    private static readonly AccessibilitySettings Accessibility = new();

    /// <summary>Raised after a runtime theme change so view-model brushes can be re-evaluated.</summary>
    public static event EventHandler? Changed;

    public static AppTheme Preference { get; private set; } = AppTheme.System;

    /// <summary>The theme the UI is actually rendering with; used when resolving token brushes in code.</summary>
    public static ElementTheme CurrentTheme =>
        Preference switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => App.Window?.Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default,
        };

    public static ElementTheme RequestedElementTheme =>
        Preference switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    public static AppTheme Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

    public static string ToStorage(AppTheme theme) =>
        theme switch
        {
            AppTheme.Light => "light",
            AppTheme.Dark => "dark",
            _ => "system",
        };

    /// <summary>
    /// Called once from <see cref="App"/> before the first window is created, because the WinUI
    /// Application theme cannot be changed afterwards without restarting. A saved System choice
    /// leaves the platform default in place so Windows controls keep following the OS.
    /// </summary>
    public static void Initialize(AppTheme theme)
    {
        Preference = theme;

        if (theme == AppTheme.System || Application.Current is not { } app)
        {
            return;
        }

        try
        {
            app.RequestedTheme = theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        catch (Exception ex)
        {
            // A late assignment is an API restriction, not a reason to fail the launch.
            System.Diagnostics.Debug.WriteLine($"Could not set the startup theme: {ex.Message}");
        }
    }

    /// <summary>Applies the saved preference to a freshly created window before it is activated.</summary>
    public static void Attach(Window window, AppTheme? preference = null)
    {
        if (preference is { } saved)
        {
            Preference = saved;
        }

        ApplyToWindow(window);
    }

    /// <summary>Switches the live window immediately and remembers the choice for the next launch.</summary>
    public static void Apply(AppTheme theme)
    {
        Preference = theme;

        if (App.Window is { } window)
        {
            ApplyToWindow(window);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyToWindow(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = RequestedElementTheme;
        }

        try
        {
            window.AppWindow.TitleBar.PreferredTheme = Preference switch
            {
                AppTheme.Light => TitleBarTheme.Light,
                AppTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };
        }
        catch (Exception ex)
        {
            // The title bar is cosmetic and unavailable on some host configurations.
            System.Diagnostics.Debug.WriteLine($"Could not set the title bar theme: {ex.Message}");
        }

        // The XAML backdrop captures the window theme when it connects. Recreating it after a
        // forced Light/Dark switch keeps Mica in step with the content instead of the OS.
        if (window.SystemBackdrop is MicaBackdrop && MicaController.IsSupported())
        {
            try
            {
                window.SystemBackdrop = new MicaBackdrop();
            }
            catch (Exception ex)
            {
                // A backdrop that refuses to reconnect is cosmetic; the content theme still works.
                System.Diagnostics.Debug.WriteLine($"Could not refresh Mica: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves a brush token against the element's actual theme. App-level resource lookup only
    /// follows the startup theme, so code that caches brushes (rows, headers) must ask explicitly
    /// when the user switches Light/Dark at runtime.
    /// </summary>
    public static Brush GetBrush(string key, ElementTheme theme, Brush fallback)
    {
        var effective = theme;
        if (effective == ElementTheme.Default)
        {
            effective = CurrentTheme;
        }

        if (effective == ElementTheme.Default)
        {
            effective = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }

        var themeKey = effective == ElementTheme.Dark ? "Dark" : "Light";

        if (IsHighContrast() && TryFindThemeValue(Application.Current.Resources, "HighContrast", key, out var highContrast)
            && highContrast is Brush highContrastBrush)
        {
            return highContrastBrush;
        }

        if (TryFindThemeValue(Application.Current.Resources, themeKey, key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return Application.Current.Resources.TryGetValue(key, out var generic) && generic is Brush genericBrush
            ? genericBrush
            : fallback;
    }

    public static Brush GetBrush(string key, Brush fallback) => GetBrush(key, CurrentTheme, fallback);

    private static bool TryFindThemeValue(ResourceDictionary dictionary, string themeKey, string key, out object? value)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themeDictionary)
            && themeDictionary is ResourceDictionary themed
            && themed.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (TryFindThemeValue(merged, themeKey, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsHighContrast()
    {
        try
        {
            return Accessibility.HighContrast;
        }
        catch
        {
            return false;
        }
    }
}

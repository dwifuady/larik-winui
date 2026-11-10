using Larik.Data;
using Larik.Data.Addons;
using Larik.Data.Demo;
using Larik.Data.MongoDB;
using Larik.Data.Postgres;
using Larik.Data.SqlServer;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Larik;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Keeps the add-on load contexts and their assemblies alive for the process lifetime. They are
    /// non-collectible, so an update is a new versioned folder plus a restart.
    /// </summary>
    private static AddonScan? Addons { get; set; }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Runs before the workspace store is constructed: the WinUI build keeps its own database
        // beside the Tauri app's, and carries over only its own pre-separation file.
        LarikPaths.MigrateLegacyState();

        // Application.RequestedTheme is startup-only. Read the saved appearance now so the first
        // frame, the caption buttons and Mica all open in the user's chosen theme.
        ThemeService.Initialize(new StudioStore().LoadThemePreference());

        // Installed add-ons load next, each in its own context. Built-ins are passed explicitly;
        // flat DLLs in either Plugins folder stay supported for the pre-add-on layout.
        Addons = AddonLocator.Scan(LarikPaths.PluginsDirectory);

        DatabaseProviderCatalog.Initialize(
            [
                new ProviderSource("built-in: Demo", typeof(DemoProvider).Assembly),
                new ProviderSource("built-in: SQL Server", typeof(SqlServerProvider).Assembly),
                new ProviderSource("built-in: PostgreSQL", typeof(PostgresProvider).Assembly),
                new ProviderSource("built-in: MongoDB", typeof(MongoProvider).Assembly),
                .. Addons.Sources,
            ],
            Addons.Errors,
            Path.Combine(AppContext.BaseDirectory, "Plugins"),
            LarikPaths.PluginsDirectory);
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}

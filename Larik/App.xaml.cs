using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about the WinUI project structure,
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
    /// The tab windows, at most one per tab. Owned here rather than by the page so the process
    /// lifetime (main window closing, app exit) is the one place that can close them all.
    /// </summary>
    public static PopoutManager Popouts { get; } = new();

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
    /// Set by <see cref="Program"/> on the first instance; later activations are redirected to it.
    /// </summary>
    internal static AppInstance? PrimaryInstance { get; set; }

    /// <summary>
    /// Brings the studio forward for an activation the user asked for (a toast click, or a second
    /// launch of the shortcut). Windows refuses to hand focus to a background process, so this
    /// restores and activates the window instead of only calling <see cref="Window.Activate"/>.
    /// </summary>
    public static void BringToFront()
    {
        var handle = WindowHandle;
        ShowWindow(handle, SwRestore);
        SetForegroundWindow(handle);
        Window.Activate();
    }

    private const int SwRestore = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Diagnostics start first: everything below (state migration, theme, providers) is a
        // plausible crash site, and a log opened later would miss it.
        AppLog.Initialize();
        HookDiagnostics();

        // Runs before the workspace store is constructed: the WinUI build keeps its own database
        // beside the Tauri app's, and carries over only its own pre-separation file.
        LarikPaths.MigrateLegacyState();

        // Application.RequestedTheme is startup-only. Read the saved appearance now so the first
        // frame, the caption buttons and Mica all open in the user's chosen theme.
        ThemeService.Initialize(new StudioStore().LoadThemePreference());

        // Builds the provider catalogue: built-ins plus installed add-ons. A newly installed
        // add-on can be appended later; this is only the startup snapshot.
        DriverRuntime.Initialize();
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

        // The notice is useful only once the shell is on screen; checking before Activate would
        // race the first layout and could show a bar over a window that is not there yet.
        ((MainWindow)Window).StartUpdateCheck();

        // A redirected activation (a toast click, or the user opening the shortcut again) means "show
        // me the studio" — and, when the click was Run it / Deny, "answer that agent".
        PrimaryInstance?.Activated += (_, activation) =>
            DispatcherQueue.TryEnqueue(() => McpApprovalService.HandleActivation(activation));

        // Agents can ask this window for a write approval while it runs; the channel is silent
        // when the app is closed, and Larik.Mcp then refuses instead of guessing.
        McpApprovalService.Start();
    }

    /// <summary>
    /// Records exceptions the app would otherwise take to the grave. The handlers only log and
    /// flush: swallowing a UI-thread exception here would leave the shell alive in a state WinUI
    /// cannot recover, and the log is there to explain the crash, not to pretend it did not happen.
    /// </summary>
    private void HookDiagnostics()
    {
        UnhandledException += (_, args) =>
        {
            AppLog.Error("crash", "unhandled exception on the UI thread", args.Exception);
            AppLog.Flush();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error(
                "crash",
                $"unhandled exception (terminating: {args.IsTerminating})",
                args.ExceptionObject as Exception);
            AppLog.Flush();
        };

        // Not terminating by default, but a swallowed task failure is still a bug worth a stack.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("crash", "unobserved task exception", args.Exception);
            AppLog.Flush();
        };

        // Normal close path, including Application.Exit() from the updater. The window also calls
        // this on close, so the marker is removed even when ProcessExit does not run.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Shutdown("process exit");
    }
}

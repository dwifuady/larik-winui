using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Larik;

/// <summary>
/// The application window. Hosts the studio shell page.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));
        var mainPage = RootFrame.Content as MainPage;

        // The last edits may still be inside the debounce window; flush them on close. The frame
        // may be showing Settings when the window closes, so keep the shell page itself.
        Closed += (_, _) => mainPage?.ViewModel.SaveNow();

        ResizeForDesktop(1360, 860);
    }

    /// <summary>AppWindow.Resize takes physical pixels, so scale the DIP target by the monitor DPI.</summary>
    private void ResizeForDesktop(double widthDip, double heightDip)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }
}

using System.Runtime.InteropServices;
using Larik.Services;
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

    /// <summary>
    /// The last geometry while the window was in its restored state. A maximized window reports the
    /// monitor's size, so keeping this separately is what makes restore-down return to the size the
    /// user actually chose.
    /// </summary>
    private WindowBoundsState? _normalBounds;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));
        var mainPage = RootFrame.Content as MainPage;

        RestoreBounds(mainPage?.ViewModel.WindowBounds);
        TrackBounds(mainPage);

        // The last edits may still be inside the debounce window; flush them on close. The frame
        // may be showing Settings when the window closes, so keep the shell page itself.
        Closed += (_, _) =>
        {
            mainPage?.ViewModel.SetWindowBounds(CaptureBounds());
            mainPage?.ViewModel.SaveNow();
        };
    }

    /// <summary>Applies the saved rectangle, or the comfortable desktop default on a first run.</summary>
    private void RestoreBounds(WindowBoundsState? bounds)
    {
        try
        {
            RestoreBoundsCore(bounds);
        }
        catch (Exception ex)
        {
            // Geometry is cosmetic; a bad saved rectangle or a projection hiccup must never stop
            // the app from opening. The window then keeps the size the constructor already set.
            System.Diagnostics.Debug.WriteLine($"Could not restore the window rectangle: {ex}");
        }
    }

    private void RestoreBoundsCore(WindowBoundsState? bounds)
    {
        if (bounds is not { Width: > 0, Height: > 0 })
        {
            ResizeForDesktop(1360, 860);
            _normalBounds = MeasureBounds();
            return;
        }

        var scale = Scale();
        var width = (int)Math.Round(bounds.Width * scale);
        var height = (int)Math.Round(bounds.Height * scale);
        var x = (int)Math.Round(bounds.X * scale);
        var y = (int)Math.Round(bounds.Y * scale);

        // The monitor the window was on may be gone; never restore it fully off-screen.
        if (IsOnScreen(x, y, width, height))
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }

        _normalBounds = MeasureBounds();

        if (bounds.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>
    /// Persists the rectangle while the user drags the window, debounced by the view model's save
    /// timer. Doing it on change (not only on close) keeps the geometry if the process is killed.
    /// </summary>
    private void TrackBounds(MainPage? mainPage)
    {
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidPositionChange && !args.DidSizeChange)
            {
                return;
            }

            // Maximized/minimized geometry is not a geometry worth restoring to.
            if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                return;
            }

            _normalBounds = MeasureBounds();
            mainPage?.ViewModel.SetWindowBounds(CaptureBounds());
        };
    }

    private WindowBoundsState CaptureBounds()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var normal = _normalBounds ?? MeasureBounds();
        return new WindowBoundsState
        {
            X = normal.X,
            Y = normal.Y,
            Width = normal.Width,
            Height = normal.Height,
            Maximized = maximized,
        };
    }

    /// <summary>Current window rectangle in DIPs; AppWindow sizes and positions are physical pixels.</summary>
    private WindowBoundsState MeasureBounds()
    {
        var scale = Scale();
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new WindowBoundsState
        {
            X = (int)Math.Round(position.X / scale),
            Y = (int)Math.Round(position.Y / scale),
            Width = (int)Math.Round(size.Width / scale),
            Height = (int)Math.Round(size.Height / scale),
        };
    }

    private double Scale()
    {
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        return scale > 0 ? scale : 1;
    }

    /// <summary>True when the rectangle overlaps a monitor (Win32 keeps this DPI-agnostic).</summary>
    private static bool IsOnScreen(int x, int y, int width, int height)
    {
        // DisplayArea.FindAll() throws a projection cast error on this Windows App SDK build, so
        // ask user32 directly. MONITOR_DEFAULTTONULL returns null when nothing intersects.
        var rect = new NativeRect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        return MonitorFromRect(ref rect, 0) != IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    /// <summary>AppWindow.Resize takes physical pixels, so scale the DIP target by the monitor DPI.</summary>
    private void ResizeForDesktop(double widthDip, double heightDip)
    {
        var scale = Scale();
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }
}

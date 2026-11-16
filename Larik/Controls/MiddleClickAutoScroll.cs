using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Larik.Controls;

/// <summary>
/// Windows-style middle-button autoscroll: one middle click starts the mode and parks a pan puck at
/// the press point, moving the pointer then scrolls the surface in either direction without holding
/// a button, and any click, a key press, Esc or a second middle click ends it. WinUI's
/// <see cref="ScrollViewer"/> has no such gesture, so the surface owns both the mode and its puck.
/// </summary>
/// <remarks>
/// The mode tracks the pointer with a transparent catcher laid over the window root rather than a
/// pointer capture: capture ends with the button release, and the whole point here is to keep
/// panning after it. The catcher also gives the window the classic four-way cursor and swallows the
/// click that ends the mode, so entering autoscroll never also activates whatever was clicked.
/// One instance per scroll surface; it lives as long as the surface because the surface (or the
/// window root) holds the event handlers.
/// </remarks>
internal sealed class MiddleClickAutoScroll
{
    // Slack around the anchor: a pixel of pointer drift must not start a scroll.
    private const double DeadZone = 12;

    // Fastest pan, in pixels per second, once the pointer reaches the far edge.
    private const double MaxSpeed = 2000;

    private readonly FrameworkElement _surface;
    private readonly Canvas _layer;
    private readonly FrameworkElement _indicator;
    private readonly Func<ScrollViewer?> _resolveScroller;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = new();

    private ScrollViewer? _scroller;
    private PointerCatcher? _catcher;
    private Panel? _catcherHost;
    private Point _anchor;
    private Point _pointer;
    private bool _active;

    /// <param name="surface">Element that receives the starting middle press.</param>
    /// <param name="layer">Overlay canvas that hosts the puck, sized to the scroll viewport.</param>
    /// <param name="indicator">The puck, positioned by <see cref="Canvas.LeftProperty"/>.</param>
    /// <param name="resolveScroller">
    /// Resolves the scroller to pan. It is a factory because a control's template scroller may not
    /// exist when the surface is constructed.
    /// </param>
    public MiddleClickAutoScroll(
        FrameworkElement surface,
        Canvas layer,
        FrameworkElement indicator,
        Func<ScrollViewer?> resolveScroller)
    {
        _surface = surface;
        _layer = layer;
        _indicator = indicator;
        _resolveScroller = resolveScroller;

        _timer = surface.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Advance();

        // RichEditBox and ListViewItem both mark pointer presses handled for their own
        // bookkeeping, so the gesture has to listen for handled events too.
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PointerPressed), handledEventsToo: true);
        surface.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(KeyDown), handledEventsToo: true);
    }

    /// <summary>Raised when the mode begins, before the first scroll tick.</summary>
    public event EventHandler? Started;

    /// <summary>Raised when the mode ends for any reason (click, key, Esc, second middle click).</summary>
    public event EventHandler? Ended;

    public bool IsActive => _active;

    private void PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(_surface).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (_active)
        {
            // A second middle press is the Windows way out of autoscroll.
            End();
            args.Handled = true;
            return;
        }

        // Text fields keep their own middle-click behaviour (the tab rail's inline rename box).
        if (StartedInTextInput(args.OriginalSource))
        {
            return;
        }

        var scroller = _resolveScroller();
        if (scroller is null)
        {
            return;
        }

        _scroller = scroller;
        ShowPuck(args);

        if (_catcher is null && TryInstallCatcher() is { } host)
        {
            // The anchor lives in window-root space, the same space the catcher reports moves in.
            _pointer = _anchor = args.GetCurrentPoint(host).Position;
        }
        else
        {
            // No root panel to cover: track the surface, which at least pans inside its own bounds.
            _pointer = _anchor = args.GetCurrentPoint(scroller).Position;
            _surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Tracked), handledEventsToo: true);
        }

        _clock.Restart();
        _timer.Start();
        _active = true;
        Started?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Typing during autoscroll means the user is back at the keyboard; modifiers alone do not.
        if (!_active || args.Key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        End();
    }

    /// <summary>Moves arriving through the window catcher (or the surface when there is none).</summary>
    private void Tracked(object sender, PointerRoutedEventArgs args)
    {
        if (!_active)
        {
            return;
        }

        _pointer = _catcher is not null && _catcherHost is { } host
            ? args.GetCurrentPoint(host).Position
            : args.GetCurrentPoint(_scroller!).Position;
    }

    /// <summary>Any click ends the mode and is swallowed, exactly like Windows autoscroll.</summary>
    private void Catcher_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        End();
        args.Handled = true;
    }

    /// <summary>
    /// The wheel leaves the mode and still scrolls the surface the puck was panning, so a stray
    /// notch is not swallowed by the switch back to normal input.
    /// </summary>
    private void Catcher_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var scroller = _scroller;
        var properties = args.GetCurrentPoint(_catcher).Properties;
        End();

        if (scroller is null || properties.MouseWheelDelta == 0)
        {
            return;
        }

        // One notch is three text lines in Windows; 16 px is the grid's row height.
        var step = properties.MouseWheelDelta / 120.0 * 48;
        if (properties.IsHorizontalMouseWheel)
        {
            scroller.ChangeView(scroller.HorizontalOffset + step, null, null, disableAnimation: true);
        }
        else
        {
            scroller.ChangeView(null, scroller.VerticalOffset - step, null, disableAnimation: true);
        }

        args.Handled = true;
    }

    /// <summary>Places the puck at the press point, clamped so it never hangs off the viewport.</summary>
    private void ShowPuck(PointerRoutedEventArgs args)
    {
        _indicator.Visibility = Visibility.Visible;

        var point = args.GetCurrentPoint(_layer).Position;
        var width = _indicator.ActualWidth > 0 ? _indicator.ActualWidth : _indicator.Width;
        var height = _indicator.ActualHeight > 0 ? _indicator.ActualHeight : _indicator.Height;

        Canvas.SetLeft(
            _indicator,
            Math.Clamp(point.X - (width / 2), 0, Math.Max(0, _layer.ActualWidth - width)));
        Canvas.SetTop(
            _indicator,
            Math.Clamp(point.Y - (height / 2), 0, Math.Max(0, _layer.ActualHeight - height)));
    }

    /// <summary>
    /// Covers the window content with a transparent, hit-test-visible layer so pointer moves keep
    /// arriving outside the scroll surface. Returns the panel used for anchor coordinates.
    /// </summary>
    private Panel? TryInstallCatcher()
    {
        if (_surface.XamlRoot?.Content is not Panel host)
        {
            return null;
        }

        var catcher = new PointerCatcher
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsTabStop = false,
            UseSystemFocusVisuals = false,
        };

        // The window roots are Grids; span every cell so the catcher really covers the window.
        if (host is Grid grid)
        {
            Grid.SetRowSpan(catcher, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(catcher, Math.Max(1, grid.ColumnDefinitions.Count));
        }

        catcher.PointerMoved += Tracked;
        catcher.PointerPressed += Catcher_PointerPressed;
        catcher.PointerWheelChanged += Catcher_PointerWheelChanged;
        catcher.ShowPanCursor();

        // The window root sees keys from whichever control holds focus, so Esc and typing also leave
        // the mode when autoscroll began from a surface that never took focus.
        host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(KeyDown), handledEventsToo: true);

        host.Children.Add(catcher);
        _catcher = catcher;
        _catcherHost = host;
        return host;
    }

    private void Advance()
    {
        if (!_active || _scroller is not { } scroller)
        {
            return;
        }

        // Clamp the step so a stalled UI thread does not teleport the view when it resumes.
        var seconds = Math.Min(_clock.Elapsed.TotalSeconds, 0.05);
        _clock.Restart();

        var horizontal = Speed(_pointer.X - _anchor.X, scroller.ViewportWidth);
        var vertical = Speed(_pointer.Y - _anchor.Y, scroller.ViewportHeight);
        if (horizontal == 0 && vertical == 0)
        {
            return;
        }

        scroller.ChangeView(
            scroller.HorizontalOffset + (horizontal * seconds),
            scroller.VerticalOffset + (vertical * seconds),
            null,
            disableAnimation: true);
    }

    private void End()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _timer.Stop();
        _clock.Stop();
        _indicator.Visibility = Visibility.Collapsed;
        _scroller = null;

        if (_catcher is { } catcher)
        {
            catcher.PointerMoved -= Tracked;
            catcher.PointerPressed -= Catcher_PointerPressed;
            catcher.PointerWheelChanged -= Catcher_PointerWheelChanged;
            catcher.ClearPanCursor();
            _catcherHost?.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(KeyDown));
            _catcherHost?.Children.Remove(catcher);
            _catcher = null;
            _catcherHost = null;
        }
        else
        {
            _surface.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Tracked));
        }

        Ended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pan speed for one axis. The squared curve keeps small slips slow and gives the edge of the
    /// surface a predictable top speed, so the same gesture feels the same in a short editor and a
    /// tall result grid.
    /// </summary>
    private static double Speed(double distance, double viewport)
    {
        var magnitude = Math.Abs(distance);
        if (magnitude <= DeadZone)
        {
            return 0;
        }

        var range = Math.Max(viewport * 0.45, 140);
        var ratio = Math.Clamp((magnitude - DeadZone) / range, 0, 1);
        return Math.Sign(distance) * MaxSpeed * ratio * ratio;
    }

    private bool StartedInTextInput(object? source)
    {
        for (var element = source as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, _surface))
            {
                return false;
            }

            if (element is TextBox or RichEditBox)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The overlay that owns pointer moves during the mode; a Grid for the pan cursor.</summary>
    private sealed class PointerCatcher : Grid
    {
        private InputCursor? _cursor;

        public void ShowPanCursor()
        {
            _cursor ??= InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            ProtectedCursor = _cursor;
        }

        public void ClearPanCursor()
        {
            ProtectedCursor = null;
            _cursor?.Dispose();
            _cursor = null;
        }
    }
}

using Larik.ViewModels;

namespace Larik.Services;

/// <summary>
/// Owns the tab windows: at most one per query tab. The window is a view of the same
/// <see cref="TabViewModel"/> the shell holds, so popping a tab out moves no data, and closing the
/// window docks the tab back with its session, results and SQL intact.
/// </summary>
public sealed class PopoutManager
{
    private readonly Dictionary<string, PopoutWindow> _windows = new(StringComparer.Ordinal);

    // Tabs whose window is being closed because the tab itself is going away (close, archive,
    // workspace delete, app exit). Their close must not re-activate a tab that no longer exists.
    private readonly HashSet<string> _closingWithoutDock = new(StringComparer.Ordinal);

    private StudioViewModel? _viewModel;
    private bool _shuttingDown;

    /// <summary>Binds the manager to the shell's view model; the page calls this once on load.</summary>
    public void Attach(StudioViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.TabClosing -= ViewModel_TabClosing;
        }

        _viewModel = viewModel;
        _viewModel.TabClosing += ViewModel_TabClosing;
    }

    public bool IsOpen(string tabId) => _windows.ContainsKey(tabId);

    /// <summary>Opens the tab in its own window, or brings the window it already has to the front.</summary>
    public void Show(TabViewModel tab)
    {
        if (_viewModel is null || _shuttingDown)
        {
            return;
        }

        if (_windows.TryGetValue(tab.Id, out var open))
        {
            BringToFront(open);
            return;
        }

        var window = new PopoutWindow(_viewModel, tab);
        _windows[tab.Id] = window;
        _viewModel.NotifyPoppedOut(tab);

        // Closed fires for both paths (the window's own button and Close's programmatic close).
        window.Closed += (_, _) => OnWindowClosed(tab);
        window.Activate();
    }

    /// <summary>Brings a tab's window forward; false when it is not open.</summary>
    public bool Focus(string tabId)
    {
        if (!_windows.TryGetValue(tabId, out var window))
        {
            return false;
        }

        BringToFront(window);
        return true;
    }

    /// <summary>
    /// Closes a tab's window. <paramref name="dock"/> false is for a tab that is leaving the
    /// workspace anyway, so the shell must not reselect it.
    /// </summary>
    public bool Close(string tabId, bool dock)
    {
        if (!_windows.Remove(tabId, out var window))
        {
            return false;
        }

        if (!dock)
        {
            _closingWithoutDock.Add(tabId);
        }

        window.Close();
        return true;
    }

    /// <summary>Closes every pop-out as part of app shutdown, without docking anything back.</summary>
    public void CloseAll()
    {
        _shuttingDown = true;

        foreach (var window in _windows.Values.ToArray())
        {
            _closingWithoutDock.Add(window.Tab.Id);
            window.Close();
        }

        _windows.Clear();
    }

    private static void BringToFront(PopoutWindow window)
    {
        try
        {
            // Show restores a minimized window and brings it forward; Activate alone does not.
            window.AppWindow.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warning("popout", "could not bring the pop-out window forward", ex);
        }
    }

    private void OnWindowClosed(TabViewModel tab)
    {
        _windows.Remove(tab.Id);

        // Remove returns true when this close was requested by Close(dock:false) or shutdown.
        var suppressed = _closingWithoutDock.Remove(tab.Id);
        if (!_shuttingDown && !suppressed)
        {
            _viewModel?.NotifyDocked(tab);
        }
    }

    private void ViewModel_TabClosing(object? sender, TabViewModel tab) => Close(tab.Id, dock: false);
}

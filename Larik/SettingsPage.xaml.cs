using System.Collections.ObjectModel;
using System.Diagnostics;
using Larik.Data.Addons;
using Larik.Models;
using Larik.Services;
using Larik.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Larik;

/// <summary>
/// Application settings: appearance, the snippet manager, database drivers and About. Snippet edits go
/// through <see cref="SnippetLibrary"/> so the next keystroke sees them; driver installs go through
/// <see cref="Larik.Data.Addons.AddonInstaller"/> and take effect after a restart.
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>Opens the page on a given section; set by callers that deep-link from the shell.</summary>
    public static string? InitialSection { get; set; }

    private readonly SnippetLibrary _snippets = SnippetLibrary.Current;
    private readonly ObservableCollection<SnippetRecord> _visible = [];
    private readonly HashSet<string> _editorDialects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToggleButton> _dialectButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly AddonIndexClient _addonIndex = AddonCatalog.CreateIndexClient();
    private readonly AddonInstaller _addonInstaller = AddonCatalog.CreateInstaller();

    private ToggleButton? _allDialectsButton;
    private string? _editingId;
    private bool _creating;
    private bool _loadingEditor;
    private bool _suppressSelection;
    private bool _suppressCategory;
    private bool _syncingTheme;
    private bool _loadingUpdates;
    private bool _addonsBusy;
    private IReadOnlyList<AddonRow> _installedRows = [];
    private IReadOnlyList<AddonRow> _availableRows = [];
    private StudioViewModel? _studio;
    private string _search = string.Empty;
    private string? _category;

    public SettingsPage()
    {
        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Required;
        SnippetList.ItemsSource = _visible;
        Loaded += SettingsPage_Loaded;

        // The page is cached and re-shown after Back; a timer belongs to the loaded instance, so
        // stop it when the page leaves the tree and start a fresh one on the way back in.
        Unloaded += (_, _) => StopAgentActivityTimer();

        // Driver rows are code-built, so their token brushes do not follow a Light/Dark switch on
        // their own. Re-draw them from the rows already fetched (no second catalogue request).
        ActualThemeChanged += (_, _) =>
        {
            RenderAddonRows(InstalledAddonPanel, _installedRows);
            RenderAddonRows(AvailableAddonPanel, _availableRows);
        };

        if (!AddonPolicy.AddonsEnabled)
        {
            // The Store edition has no add-on surface: no index, no downloaded assemblies.
            AddonsNavItem.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- page lifecycle

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The shell owns the theme preference and its save timer, so Settings borrows the live
        // view model instead of opening a second store against the same SQLite file.
        if (e.Parameter is StudioViewModel studio)
        {
            _studio = studio;
        }

        SyncThemeChoice();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        App.Window.SetTitleBar(SettingsTitleBar);
        DatabasePathText.Text = LarikPaths.DatabasePath;
        LogPathText.Text = AppLog.DirectoryPath;

        var version = AppInfo.HasVersion ? AppInfo.Version : "0.0.0";
        VersionText.Text = $"Version {version} · MIT licensed";

        if (UpdateService.CanSelfUpdate)
        {
            _loadingUpdates = true;
            try
            {
                CheckForUpdatesToggle.IsOn = _studio?.CheckForUpdates ?? true;
            }
            finally
            {
                _loadingUpdates = false;
            }

            CheckForUpdatesToggle.IsEnabled = _studio is not null;
            UpdateStatusText.Text = CheckForUpdatesToggle.IsOn
                ? "A check runs once when Larik starts."
                : "Automatic checks are off. Use Check now when you want to look.";
        }
        else
        {
            // A packaged copy (the Store edition, or a debug run through winapp) owns no writable
            // files here, so the Store delivers its updates instead.
            UpdatesCard.Visibility = Visibility.Collapsed;
            ManagedUpdatesNote.Visibility = Visibility.Visible;
        }

        if (DialectChips.Children.Count == 0)
        {
            BuildDialectChips();
        }

        if (AddonPolicy.AddonsEnabled &&
            string.Equals(InitialSection, "drivers", StringComparison.Ordinal))
        {
            InitialSection = null;
            SettingsNav.SelectedItem = AddonsNavItem;
        }

        if (SettingsNav.SelectedItem is null)
        {
            SettingsNav.SelectedItem = SnippetsNavItem;
        }

        // A cached page can come back with the agents section already selected; its timer was
        // stopped on Unloaded, so start one for this instance.
        if (AgentsSection.Visibility == Visibility.Visible)
        {
            StartAgentActivityTimer();
        }

        SyncThemeChoice();
        RefreshCategories();
        RefreshList();

        if (ReferenceEquals(SettingsNav.SelectedItem, AddonsNavItem))
        {
            _ = RefreshAddonsAsync();
        }
    }

    private void SettingsTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(MainPage));
        }
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (SnippetsSection is null || AboutSection is null || AppearanceSection is null || AddonsSection is null)
        {
            return;
        }

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        var about = string.Equals(tag, "about", StringComparison.Ordinal);
        var appearance = string.Equals(tag, "appearance", StringComparison.Ordinal);
        var agents = string.Equals(tag, "agents", StringComparison.Ordinal);
        var drivers = AddonPolicy.AddonsEnabled &&
            string.Equals(tag, "drivers", StringComparison.Ordinal);

        AppearanceSection.Visibility = appearance ? Visibility.Visible : Visibility.Collapsed;
        AgentsSection.Visibility = agents ? Visibility.Visible : Visibility.Collapsed;
        SnippetsSection.Visibility = about || appearance || agents || drivers
            ? Visibility.Collapsed
            : Visibility.Visible;
        AboutSection.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        AddonsSection.Visibility = drivers ? Visibility.Visible : Visibility.Collapsed;

        if (appearance)
        {
            SyncThemeChoice();
        }

        if (agents)
        {
            RefreshAgentsSection();
            StartAgentActivityTimer();
        }
        else
        {
            StopAgentActivityTimer();
        }

        if (drivers)
        {
            _ = RefreshAddonsAsync();
        }
    }

    private void SyncThemeChoice()
    {
        if (ThemeChoice is null)
        {
            return;
        }

        _syncingTheme = true;
        try
        {
            ThemeChoice.SelectedIndex = (int)(_studio?.Theme ?? ThemeService.Preference);
        }
        finally
        {
            _syncingTheme = false;
        }
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTheme || ThemeChoice.SelectedIndex < 0)
        {
            return;
        }

        var theme = ThemeChoice.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        if (_studio is not null)
        {
            _studio.SetTheme(theme);
        }
        else
        {
            ThemeService.Apply(theme);
        }
    }

    // ---------------------------------------------------------------- updates

    private void CheckForUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdates || _studio is null)
        {
            return;
        }

        _studio.CheckForUpdates = CheckForUpdatesToggle.IsOn;
        UpdateStatusText.Text = CheckForUpdatesToggle.IsOn
            ? "A check runs once when Larik starts."
            : "Automatic checks are off. Use Check now when you want to look.";
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is not MainWindow window)
        {
            return;
        }

        CheckNowButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";

        try
        {
            var status = await window.Update.CheckNowAsync();
            UpdateStatusText.Text = status switch
            {
                UpdateCheckStatus.Available =>
                    "An update is available. Use the notice at the bottom of the window to download it.",
                UpdateCheckStatus.UpToDate => "You're running the latest version.",
                UpdateCheckStatus.Failed =>
                    "Couldn't check for updates. Check your connection and try again.",
                _ => "Couldn't check for updates right now. Try again in a moment.",
            };
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Opens the diagnostics folder in Explorer. The path is shown beside the button, so the user
    /// can also copy it when the shell refuses to start another process.
    /// </summary>
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLog.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("settings", "opening the log folder failed", ex);
            OpenLogFolderButton.Content = "Couldn't open it";
        }
    }

    // ---------------------------------------------------------------- add-ons

    // ---------------------------------------------------------------- AI agents (MCP)

    private StudioStore? _mcpStore;
    private bool _agentsLoading;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _agentsActivityTimer;

    /// <summary>
    /// The store opens a connection per call, so a second handle here is safe: the MCP server is a
    /// separate process doing exactly the same thing while the app is open.
    /// </summary>
    private StudioStore McpStore => _mcpStore ??= new StudioStore();

    private static readonly (string Id, string Label)[] AgentClients =
    [
        ("claude", "Claude Code"),
        ("cursor", "Cursor"),
        ("vscode", "VS Code"),
        ("codex", "Codex"),
    ];

    private void RefreshAgentsSection()
    {
        var enabled = McpStore.IsMcpEnabled();

        _agentsLoading = true;
        try
        {
            AgentsToggle.IsOn = enabled;
        }
        finally
        {
            _agentsLoading = false;
        }

        AgentsStatusText.Text = enabled
            ? "On — the agents you configure below can read the workspaces you expose, even while Larik is closed."
            : "Off — no agent can connect. Nothing is listed and nothing is served.";

        AgentsWorkspacesCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AgentsConnectCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AgentsActivityCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (AgentClientChoice.Items.Count == 0)
        {
            foreach (var client in AgentClients)
            {
                AgentClientChoice.Items.Add(new ComboBoxItem { Content = client.Label, Tag = client.Id });
            }
        }

        if (AgentClientChoice.SelectedIndex < 0)
        {
            AgentClientChoice.SelectedIndex = 0;
        }

        if (!enabled)
        {
            return;
        }

        RenderAgentWorkspaces();
        RenderAgentActivity();
        SyncAgentCommand();
    }

    private void AgentsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_agentsLoading)
        {
            return;
        }

        McpStore.SetMcpEnabled(AgentsToggle.IsOn);
        RefreshAgentsSection();
    }

    private void RenderAgentWorkspaces()
    {
        AgentsWorkspacePanel.Children.Clear();

        var spaces = McpStore.GetMcpSpaces().Where(space => space.HasConnection).ToList();
        AgentsWorkspaceEmptyText.Visibility = spaces.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var space in spaces)
        {
            var row = new Grid
            {
                Padding = new Thickness(0, 2, 0, 2),
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = space.Name,
                FontSize = 13,
                Foreground = Resource("StudioTextPrimaryBrush", StudioColors.Muted),
            });

            var provider = Larik.Data.DatabaseProviderCatalog.Find(space.ProviderId);
            text.Children.Add(new TextBlock
            {
                Text = provider?.DisplayName ?? $"{space.ProviderId} (not installed)",
                FontSize = 10,
                Foreground = Resource("StudioTextMutedBrush", StudioColors.Muted),
            });
            row.Children.Add(text);

            var choice = new ComboBox
            {
                MinWidth = 150,
                Tag = space.Id,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(choice, $"{space.Name} agent access");
            choice.Items.Add(new ComboBoxItem { Content = "Off", Tag = McpAccess.Off });
            choice.Items.Add(new ComboBoxItem { Content = "Read only", Tag = McpAccess.Read });
            choice.Items.Add(new ComboBoxItem { Content = "Read and write", Tag = McpAccess.Write });
            choice.SelectedIndex = space.Access switch
            {
                McpAccess.Read => 1,
                McpAccess.Write => 2,
                _ => 0,
            };

            // Attached after the initial selection so loading the list cannot look like a change.
            choice.SelectionChanged += AgentAccess_SelectionChanged;
            Grid.SetColumn(choice, 1);
            row.Children.Add(choice);

            AgentsWorkspacePanel.Children.Add(row);
        }
    }

    private void AgentAccess_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string spaceId } choice ||
            choice.SelectedItem is not ComboBoxItem { Tag: McpAccess access })
        {
            return;
        }

        McpStore.SetSpaceMcpAccess(spaceId, access);
    }

    private void RenderAgentActivity()
    {
        AgentsActivityPanel.Children.Clear();

        var entries = McpStore.GetMcpLog(30);
        AgentsActivityEmptyText.Visibility = entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var entry in entries)
        {
            var row = new Grid
            {
                Padding = new Thickness(0, 3, 0, 3),
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = entry.SpaceName is null ? entry.Tool : $"{entry.Tool} · {entry.SpaceName}",
                FontSize = 12,
                Foreground = Resource("StudioTextPrimaryBrush", StudioColors.Muted),
            });

            var detail = entry.Reason is { Length: > 0 } reason
                ? $"{entry.RecordedAt} UTC · {reason}"
                : $"{entry.RecordedAt} UTC · {entry.RowsReturned} row(s) · {entry.DurationMs} ms";
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 10,
                Foreground = Resource("StudioTextMutedBrush", StudioColors.Muted),
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(text);

            // Status is text plus colour, never colour alone.
            var status = new TextBlock
            {
                Text = entry.Status switch
                {
                    "denied" => "Refused",
                    "error" => "Failed",
                    _ => "Delivered",
                },
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Resource(
                    entry.Status switch
                    {
                        "denied" => "StudioWarningBrush",
                        "error" => "StudioDangerBrush",
                        _ => "StudioSuccessBrush",
                    },
                    StudioColors.Muted),
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);

            // The agent's statement is stored, so the user can pick up its work where it stopped.
            if (!string.IsNullOrWhiteSpace(entry.Statement) && !string.IsNullOrWhiteSpace(entry.SpaceId))
            {
                var open = new Button
                {
                    Content = "Open as tab",
                    Tag = entry,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                AutomationProperties.SetName(open, $"Open the {entry.Tool} statement as a tab");
                open.Click += OpenAuditStatement_Click;
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
            }

            AgentsActivityPanel.Children.Add(row);
        }
    }

    private void OpenAuditStatement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: McpLogRecord entry } || _studio is null)
        {
            return;
        }

        if (!_studio.OpenExternalQuery(entry.SpaceId ?? string.Empty, null, entry.Statement ?? string.Empty))
        {
            return;
        }

        // The tab lives in the shell, so go there and show it. Settings is hosted in the shell's
        // frame; if there is no back entry (the frame was cleared), say where the tab went instead
        // of navigating to a second shell.
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            AgentsStatusText.Text = $"Opened in {entry.SpaceName}. Press Back to see the tab.";
        }
    }

    private async void ClearAgentActivity_Click(object sender, RoutedEventArgs e)
    {
        // The audit list is the only record of what an agent did, and clearing it cannot be undone.
        var dialog = new ContentDialog
        {
            XamlRoot = AgentsSection.XamlRoot,
            Title = "Clear agent activity?",
            Content = new TextBlock
            {
                Text = "This removes the record of every agent call, including the ones Larik refused. " +
                       "It cannot be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        McpStore.ClearMcpLog();
        RenderAgentActivity();
    }

    /// <summary>
    /// The list is written by another process, so a visible section polls it while it is on screen:
    /// a user watching an agent work should not have to navigate away to see what it just did.
    /// </summary>
    private void StartAgentActivityTimer()
    {
        if (_agentsActivityTimer is not null || !McpStore.IsMcpEnabled())
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) =>
        {
            if (AgentsSection.Visibility == Visibility.Visible)
            {
                RenderAgentActivity();
            }
        };
        timer.Start();
        _agentsActivityTimer = timer;
        AppLog.Info("mcp", "activity timer started");
    }

    private void StopAgentActivityTimer()
    {
        _agentsActivityTimer?.Stop();
        _agentsActivityTimer = null;
    }

    private void AgentClientChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SyncAgentCommand();

    private void SyncAgentCommand()
    {
        if (AgentCommandText is null)
        {
            return;
        }

        var client = (AgentClientChoice?.SelectedItem as ComboBoxItem)?.Tag as string ?? "claude";
        var serverPath = FindMcpServerPath();
        var installed = serverPath is not null;

        // Packaged builds expose the server on PATH; portable builds point at the file.
        var exe = serverPath ?? "larik-mcp";

        // A packaged studio keeps its store inside the package, and a client that starts the EXE by
        // path has no package identity to find it. Name the folder explicitly in that case.
        var stateDirectory = LarikPaths.IsPackaged ? LarikPaths.StateDirectory : null;

        var cliTarget = installed ? $"\"{exe}\"" : exe;
        if (stateDirectory is not null)
        {
            cliTarget = $"{cliTarget} --state \"{stateDirectory}\"";
        }

        var commandJson = stateDirectory is null
            ? $"\"command\":\"{JsonPath(exe)}\""
            : $"\"command\":\"{JsonPath(exe)}\",\"args\":[\"--state\",\"{JsonPath(stateDirectory)}\"]";

        AgentCommandText.Text = client switch
        {
            "cursor" => $"{{ \"mcpServers\": {{ \"larik\": {{ {commandJson} }} }} }}",
            "vscode" => $"code --add-mcp '{{\"name\":\"larik\",\"type\":\"stdio\",{commandJson}}}'",
            "codex" => $"codex mcp add larik -- {cliTarget}",
            _ => $"claude mcp add larik -- {cliTarget}",
        };

        AgentCommandNote.Text = serverPath is null
            ? "This build has not installed the MCP server yet, so larik-mcp is only a name here. Packaging the server is the next step."
            : installed
                ? "The command points at this build's MCP server. Packaging puts it on PATH as larik-mcp, so the command gets shorter."
                : "The MCP server is installed next to Larik; the command works as shown.";
    }

    /// <summary>Windows path as it has to appear inside JSON: backslashes escaped.</summary>
    private static string JsonPath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>
    /// The MCP server EXE: in its own <c>mcp\</c> folder beside the app (each apphost owns its
    /// dependencies), or in its project output while developing.
    /// </summary>
    private static string? FindMcpServerPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "mcp", "Larik.Mcp.exe"),
                     Path.Combine(AppContext.BaseDirectory, "Larik.Mcp.exe"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

#if DEBUG
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Larik.Mcp",
                "bin",
                "Debug",
                "net10.0",
                "Larik.Mcp.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
#endif

        return null;
    }

    private void CopyAgentCommand_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(AgentCommandText.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private void RefreshAddons_Click(object sender, RoutedEventArgs e) => _ = RefreshAddonsAsync();
    private async Task RefreshAddonsAsync()
    {
        if (_addonsBusy)
        {
            return;
        }

        SetAddonBusy(true);
        SetAddonStatus("Checking for drivers…");

        try
        {
            var installed = _addonInstaller.ListInstalled(LarikPaths.PluginsDirectory);

            AddonIndex? index = null;
            string? indexError = null;
            try
            {
                index = await _addonIndex.FetchAsync(AddonCatalog.IndexLocation);
            }
            catch (Exception ex)
            {
                indexError = ex.Message;
            }

            IReadOnlyList<AddonIndexEntry> entries = index is null
                ? []
                : index.Addons
                    .Where(entry => entry.Matches(AddonCatalog.AppVersion, AddonCatalog.CurrentPlatform))
                    .ToList();

            var entriesById = entries.ToDictionary(
                entry => entry.Id,
                StringComparer.OrdinalIgnoreCase);

            var installedRows = installed
                .Select(addon => BuildInstalledRow(addon, entriesById.GetValueOrDefault(addon.Id)))
                .ToList();
            _installedRows = installedRows;
            RenderAddonRows(InstalledAddonPanel, installedRows);
            InstalledEmptyText.Visibility = installedRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var available = new List<AddonRow>();
            foreach (var entry in entries)
            {
                var existing = installed.FirstOrDefault(addon =>
                    string.Equals(addon.Id, entry.Id, StringComparison.OrdinalIgnoreCase));

                if (existing is not null && !AddonVersions.IsNewer(entry.Version, existing.Version))
                {
                    continue;
                }

                available.Add(BuildAvailableRow(entry, update: existing is not null));
            }

            _availableRows = available;
            RenderAddonRows(AvailableAddonPanel, available);
            AvailableEmptyText.Visibility = available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SetAddonStatus(indexError is null
                ? $"{installed.Count} installed · {available.Count} available"
                : $"Driver catalogue unavailable: {indexError}");
        }
        catch (Exception ex)
        {
            SetAddonStatus($"Could not read drivers: {ex.Message}", error: true);
        }
        finally
        {
            SetAddonBusy(false);
        }
    }

    private static AddonRow BuildInstalledRow(InstalledAddon addon, AddonIndexEntry? entry) => new()
    {
        Id = addon.Id,
        Name = entry?.Name ?? addon.Id,
        VersionText = $"v{addon.Version}",
        Detail = entry?.Summary ?? $"Provides {string.Join(", ", addon.ProviderIds)}.",
        Status = addon.Enabled ? "Installed" : "Disabled — restart to apply",
        ActionText = "Remove",
        ToggleText = addon.Enabled ? "Disable" : "Enable",
        IsInstalled = true,
        Entry = entry,
        Installed = addon,
    };

    private static AddonRow BuildAvailableRow(AddonIndexEntry entry, bool update) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        VersionText = $"v{entry.Version}",
        Detail = entry.Summary,
        Status = update ? "Update available" : "Not installed",
        ActionText = update ? "Update" : "Install",
        Entry = entry,
    };

    private void RenderAddonRows(StackPanel panel, IReadOnlyList<AddonRow> rows)
    {
        panel.Children.Clear();
        foreach (var row in rows)
        {
            panel.Children.Add(BuildAddonRowElement(row));
        }
    }

    private FrameworkElement BuildAddonRowElement(AddonRow row)
    {
        var grid = new Grid
        {
            Padding = new Thickness(8, 10, 8, 10),
            ColumnSpacing = 12,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Resource("StudioTextPrimaryBrush", StudioColors.Muted),
        });
        title.Children.Add(new TextBlock
        {
            Text = row.VersionText,
            FontSize = 10,
            Foreground = Resource("StudioTextMutedBrush", StudioColors.Muted),
            VerticalAlignment = VerticalAlignment.Center,
        });
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = row.Detail,
            FontSize = 11,
            Foreground = Resource("StudioTextMutedBrush", StudioColors.Muted),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = row.Status,
            FontSize = 10,
            Foreground = Resource("StudioAccentBrush", StudioColors.Muted),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (row.ToggleText is { } toggleText)
        {
            var toggle = new Button { Content = toggleText, Tag = row };
            toggle.Click += AddonToggle_Click;
            actions.Children.Add(toggle);
        }

        var action = new Button
        {
            Content = row.ActionText,
            Tag = row,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        action.Click += AddonAction_Click;
        actions.Children.Add(action);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(text);
        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>
    /// Resolves a design token for the page's live theme. App-level lookup follows the startup
    /// theme, so a runtime Light/Dark switch would leave generated rows with the old colours.
    /// </summary>
    private Brush Resource(string key, Brush fallback) =>
        ThemeService.GetBrush(key, ActualTheme, fallback);

    private async void AddonAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AddonRow row })
        {
            return;
        }

        if (row.IsInstalled)
        {
            await RemoveAddonAsync(row);
            return;
        }

        if (row.Entry is { } entry)
        {
            await InstallAddonAsync(entry);
        }
    }

    private async void AddonToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AddonRow { Installed: { } installed } })
        {
            return;
        }

        var enable = !installed.Enabled;
        if (!await ConfirmAsync(
                enable ? "Enable driver" : "Disable driver",
                $"{(enable ? "Enable" : "Disable")} {installed.Id}? Larik must restart for the change to take effect.",
                enable ? "Enable" : "Disable"))
        {
            return;
        }

        try
        {
            _addonInstaller.SetEnabled(LarikPaths.PluginsDirectory, installed.Id, enable);
        }
        catch (Exception ex)
        {
            SetAddonStatus($"Could not change the driver: {ex.Message}", error: true);
            return;
        }

        await RefreshAddonsAsync();
        SetAddonStatus($"Restart Larik to {(enable ? "enable" : "disable")} {installed.Id}.");
    }

    private async Task InstallAddonAsync(AddonIndexEntry entry)
    {
        SetAddonBusy(true);
        SetAddonStatus($"Installing {entry.Name} {entry.Version}…");

        var added = 0;
        try
        {
            await _addonInstaller.InstallAsync(entry, LarikPaths.PluginsDirectory);
            added = DriverRuntime.LoadInstalled(entry.Id).Count;
            _studio?.RefreshProviderAvailability();
        }
        catch (Exception ex)
        {
            SetAddonBusy(false);
            SetAddonStatus($"Install failed: {ex.Message}", error: true);
            return;
        }

        SetAddonBusy(false);
        await RefreshAddonsAsync();
        SetAddonStatus(added > 0
            ? $"{entry.Name} {entry.Version} installed. It is ready to use."
            : $"{entry.Name} {entry.Version} installed. Restart Larik to use it.");
    }

    private async Task RemoveAddonAsync(AddonRow row)
    {
        if (row.Installed is not { } installed)
        {
            return;
        }

        if (!await ConfirmAsync(
                "Remove driver",
                $"Remove {installed.Id} {installed.Version}? Workspaces keep their saved connection profiles; the driver is unavailable until it is installed again.",
                "Remove"))
        {
            return;
        }

        try
        {
            _addonInstaller.Remove(LarikPaths.PluginsDirectory, installed.Id);
        }
        catch (Exception ex)
        {
            SetAddonStatus($"Could not remove the driver: {ex.Message} Restart Larik and try again.", error: true);
            return;
        }

        await RefreshAddonsAsync();
        SetAddonStatus($"{installed.Id} removed. Restart Larik to unload it.");
    }

    private async void InstallZip_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(file.Path))
        {
            SetAddonStatus("That file has no local path. Copy it to disk and try again.", error: true);
            return;
        }

        SetAddonBusy(true);
        SetAddonStatus($"Installing {file.Name}…");

        var ready = false;
        try
        {
            var installed = await _addonInstaller.InstallZipAsync(file.Path, LarikPaths.PluginsDirectory);
            ready = DriverRuntime.LoadInstalled(installed.Id).Count > 0;
            _studio?.RefreshProviderAvailability();
        }
        catch (Exception ex)
        {
            SetAddonBusy(false);
            SetAddonStatus($"Install failed: {ex.Message}", error: true);
            return;
        }

        SetAddonBusy(false);
        await RefreshAddonsAsync();
        SetAddonStatus(ready
            ? $"{file.Name} installed. It is ready to use."
            : $"{file.Name} installed. Restart Larik to use it.");
    }

    private void SetAddonBusy(bool busy)
    {
        _addonsBusy = busy;
        AddonProgress.IsActive = busy;
        AddonProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshAddonsButton.IsEnabled = !busy;
        InstallZipButton.IsEnabled = !busy;
    }

    private void SetAddonStatus(string text, bool error = false)
    {
        AddonStatusText.Text = text;
        AddonStatusText.Foreground = Resource(
            error ? "StudioDangerBrush" : "StudioTextMutedBrush",
            error ? StudioColors.Danger : StudioColors.Muted);
    }

    // ---------------------------------------------------------------- filtering

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? string.Empty;
        RefreshList();
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategory)
        {
            return;
        }

        _category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        RefreshList();
    }

    private void RefreshCategories()
    {
        _suppressCategory = true;
        try
        {
            var categories = _snippets.All
                .Select(snippet => snippet.CategoryLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CategoryFilter.Items.Clear();
            CategoryFilter.Items.Add(new ComboBoxItem { Content = "All categories", Tag = null });
            foreach (var category in categories)
            {
                CategoryFilter.Items.Add(new ComboBoxItem { Content = category, Tag = category });
            }

            if (_category is not null)
            {
                var match = CategoryFilter.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, _category, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    CategoryFilter.SelectedItem = match;
                    return;
                }

                // The filter's category disappeared (the last snippet in it was renamed or deleted):
                // the combo now says "All categories", so the filter must agree.
                _category = null;
            }

            CategoryFilter.SelectedIndex = 0;
        }
        finally
        {
            _suppressCategory = false;
        }
    }

    private void RefreshList()
    {
        IEnumerable<SnippetRecord> matches = _snippets.All;

        if (_search.Length > 0)
        {
            matches = matches.Where(snippet =>
                snippet.Trigger.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                snippet.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                snippet.Content.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                (snippet.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (_category is not null)
        {
            matches = matches.Where(snippet =>
                string.Equals(snippet.CategoryLabel, _category, StringComparison.OrdinalIgnoreCase));
        }

        var items = matches.ToList();
        var selectedId = _editingId;

        _suppressSelection = true;
        try
        {
            _visible.Clear();
            foreach (var snippet in items)
            {
                _visible.Add(snippet);
            }

            SnippetList.SelectedItem = selectedId is null
                ? null
                : _visible.FirstOrDefault(snippet => string.Equals(snippet.Id, selectedId, StringComparison.Ordinal));
        }
        finally
        {
            _suppressSelection = false;
        }

        EmptyListText.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _snippets.All.Count;
        var builtins = _snippets.All.Count(snippet => snippet.IsBuiltin);
        SnippetCountText.Text = _visible.Count == total
            ? $"{total} snippets · {builtins} built-in · {total - builtins} custom"
            : $"{_visible.Count} of {total} snippets · {builtins} built-in · {total - builtins} custom";
    }

    // ---------------------------------------------------------------- selection

    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (SnippetList.SelectedItem is SnippetRecord snippet)
        {
            LoadEditor(snippet);
        }
        else
        {
            _editingId = null;
            _creating = false;
            HideEditor();
        }
    }

    private void LoadEditor(SnippetRecord snippet)
    {
        _creating = false;
        _editingId = snippet.Id;

        _loadingEditor = true;
        try
        {
            TriggerBox.Text = snippet.Trigger;
            NameBox.Text = snippet.Name;
            CategoryBox.Text = snippet.Category ?? string.Empty;
            DescriptionBox.Text = snippet.Description ?? string.Empty;
            ContentBox.Text = snippet.Content;
            EnabledToggle.IsOn = snippet.IsEnabled;

            _editorDialects.Clear();
            foreach (var dialect in snippet.Dialects ?? [])
            {
                _editorDialects.Add(dialect);
            }

            ApplyDialectChips();
        }
        finally
        {
            _loadingEditor = false;
        }

        EditorHeading.Text = "Edit snippet";
        EditorSubheading.Text = snippet.IsBuiltin
            ? "Built-in snippet — you can edit it, but not delete it."
            : "Custom snippet";
        SaveButton.Content = "Save changes";
        ResetButton.Visibility = snippet.IsBuiltin ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = snippet.IsBuiltin ? Visibility.Collapsed : Visibility.Visible;

        ShowEditor();
    }

    private void NewSnippet_Click(object sender, RoutedEventArgs e)
    {
        _creating = true;
        _editingId = null;

        _suppressSelection = true;
        try
        {
            SnippetList.SelectedItem = null;
        }
        finally
        {
            _suppressSelection = false;
        }

        _loadingEditor = true;
        try
        {
            TriggerBox.Text = string.Empty;
            NameBox.Text = string.Empty;
            CategoryBox.Text = string.Empty;
            DescriptionBox.Text = string.Empty;
            ContentBox.Text = string.Empty;
            EnabledToggle.IsOn = true;

            _editorDialects.Clear();
            ApplyDialectChips();
        }
        finally
        {
            _loadingEditor = false;
        }

        EditorHeading.Text = "New snippet";
        EditorSubheading.Text = "Type the trigger in the editor and press Tab to expand.";
        SaveButton.Content = "Create snippet";
        ResetButton.Visibility = Visibility.Collapsed;
        DeleteButton.Visibility = Visibility.Collapsed;

        ShowEditor();
        TriggerBox.Focus(FocusState.Programmatic);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _creating = false;
        _editingId = null;

        _suppressSelection = true;
        try
        {
            SnippetList.SelectedItem = null;
        }
        finally
        {
            _suppressSelection = false;
        }

        HideEditor();
    }

    private void HideEditor()
    {
        EditorPanel.Visibility = Visibility.Collapsed;
        EditorEmptyText.Visibility = Visibility.Visible;
        EditorError.Visibility = Visibility.Collapsed;
    }

    private void ShowEditor()
    {
        EditorPanel.Visibility = Visibility.Visible;
        EditorEmptyText.Visibility = Visibility.Collapsed;
        EditorError.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- save + delete

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        var trigger = TriggerBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var content = ContentBox.Text ?? string.Empty;

        if (trigger.Length == 0)
        {
            ShowError("A trigger is required.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!trigger.All(character => char.IsLetterOrDigit(character) || character == '_'))
        {
            ShowError("Triggers can only contain letters, digits and underscores.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        if (name.Length == 0)
        {
            ShowError("A name is required.");
            NameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            ShowError("Snippet content is required.");
            ContentBox.Focus(FocusState.Programmatic);
            return;
        }

        var dialects = _editorDialects.ToArray();
        if (_snippets.HasTriggerConflict(trigger, dialects, _editingId))
        {
            ShowError($"The trigger \"{trigger}\" already exists for this database scope.");
            TriggerBox.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            var creating = _creating || _editingId is null;
            var saved = creating
                ? _snippets.Create(trigger, name, content, DescriptionBox.Text, CategoryBox.Text, dialects)
                : _snippets.Update(_editingId!, trigger, name, content, DescriptionBox.Text, CategoryBox.Text, dialects);

            _creating = false;
            _editingId = saved.Id;

            // A new snippet must be visible after saving, even if the current filters would hide it.
            if (creating)
            {
                _search = string.Empty;
                _category = null;
                SearchBox.Text = string.Empty;
            }

            RefreshCategories();
            RefreshList();
            LoadEditor(saved);
        }
        catch (Exception ex)
        {
            ShowError($"Could not save the snippet: {ex.Message}");
        }
    }

    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is not { } id || _snippets.Find(id) is not { } snippet || snippet.IsBuiltin)
        {
            return;
        }

        if (!await ConfirmAsync("Delete snippet", $"Delete \"{snippet.Name}\"?", "Delete"))
        {
            return;
        }

        try
        {
            if (_snippets.Delete(id))
            {
                _editingId = null;
                _creating = false;
                RefreshCategories();
                RefreshList();
                HideEditor();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not delete the snippet: {ex.Message}");
        }
    }

    private async void ResetSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is not { } id || _snippets.Find(id) is not { } snippet || !snippet.IsBuiltin)
        {
            return;
        }

        if (!await ConfirmAsync("Reset snippet", $"Reset \"{snippet.Name}\" to its built-in default?", "Reset"))
        {
            return;
        }

        try
        {
            var reset = _snippets.ResetBuiltin(id);
            if (reset is null)
            {
                ShowError("This built-in snippet has no catalog default any more.");
                return;
            }

            RefreshList();
            LoadEditor(reset);
        }
        catch (Exception ex)
        {
            ShowError($"Could not reset the snippet: {ex.Message}");
        }
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || _creating || _editingId is not { } id)
        {
            return;
        }

        try
        {
            _snippets.SetEnabled(id, EnabledToggle.IsOn);
            RefreshList();
        }
        catch (Exception ex)
        {
            ShowError($"Could not update the snippet: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        EditorError.Text = message;
        EditorError.Visibility = Visibility.Visible;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            XamlRoot = XamlRoot,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---------------------------------------------------------------- dialect chips

    private void BuildDialectChips()
    {
        DialectChips.Children.Clear();
        _dialectButtons.Clear();

        _allDialectsButton = CreateDialectChip("All SQL databases", null);
        DialectChips.Children.Add(_allDialectsButton);

        foreach (var (id, label) in SnippetDialects.Known)
        {
            var button = CreateDialectChip(label, id);
            _dialectButtons[id] = button;
            DialectChips.Children.Add(button);
        }

        ApplyDialectChips();
    }

    private ToggleButton CreateDialectChip(string label, string? dialect)
    {
        var button = new ToggleButton
        {
            Content = label,
            FontSize = 11,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
        };

        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => ToggleDialect(dialect, button);
        return button;
    }

    private void ToggleDialect(string? dialect, ToggleButton button)
    {
        if (_loadingEditor)
        {
            return;
        }

        if (dialect is null)
        {
            _editorDialects.Clear();
        }
        else if (button.IsChecked == true)
        {
            _editorDialects.Add(dialect);
        }
        else
        {
            _editorDialects.Remove(dialect);
        }

        ApplyDialectChips();
    }

    private void ApplyDialectChips()
    {
        if (_allDialectsButton is not null)
        {
            _allDialectsButton.IsChecked = _editorDialects.Count == 0;
        }

        foreach (var (dialect, button) in _dialectButtons)
        {
            button.IsChecked = _editorDialects.Contains(dialect);
        }
    }

    // ---------------------------------------------------------------- placeholder helpers

    private void InsertCursor_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${cursor}");

    private void InsertPlaceholder_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${1:placeholder}");

    private void InsertPaste_Click(object sender, RoutedEventArgs e) => InsertAtCursor("${paste}");

    private void InsertAtCursor(string text)
    {
        var content = ContentBox.Text ?? string.Empty;
        var start = Math.Clamp(ContentBox.SelectionStart, 0, content.Length);
        var length = Math.Clamp(ContentBox.SelectionLength, 0, content.Length - start);

        ContentBox.Text = content[..start] + text + content[(start + length)..];
        ContentBox.SelectionStart = start + text.Length;
        ContentBox.SelectionLength = 0;
        ContentBox.Focus(FocusState.Programmatic);
    }
}


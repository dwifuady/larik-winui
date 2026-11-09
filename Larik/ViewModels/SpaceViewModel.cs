using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Larik.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// A space is a workspace with exactly one connection. That connection exposes several databases;
/// the space's active database supplies the query context shown in the rail, toolbar and status bar.
/// </summary>
public partial class SpaceViewModel : ObservableObject
{
    public SpaceViewModel(string id, string name, string colorHex, ConnectionViewModel connection, string? iconName = null)
    {
        Id = id;
        ColorHex = colorHex;
        Name = name;
        IconName = iconName;
        Connection = connection;

        // Connection state belongs to the space, so its changes surface as space changes.
        Connection.PropertyChanged += (_, args) =>
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ConnectionLine));
            OnPropertyChanged(nameof(Driver));
            OnPropertyChanged(nameof(ConnectedDatabaseText));
            OnPropertyChanged(nameof(ContextBadgeText));
            OnPropertyChanged(nameof(ConnectActionVisibility));
            OnPropertyChanged(nameof(DisconnectActionVisibility));
            OnPropertyChanged(nameof(DatabasePickerVisibility));
        };
    }

    public string Id { get; }

    /// <summary>The workspace colour (a palette hex). Changing it re-tints the rail, tabs and toolbar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccentBrush))]
    [NotifyPropertyChangedFor(nameof(ActiveTintBrush))]
    [NotifyPropertyChangedFor(nameof(IndicatorChipBrush))]
    public partial string ColorHex { get; set; }

    public SolidColorBrush AccentBrush => StudioColors.Brush(ColorHex);

    public SolidColorBrush ActiveTintBrush => StudioColors.Tint(ColorHex);

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    [NotifyPropertyChangedFor(nameof(IndicatorName))]
    [NotifyPropertyChangedFor(nameof(Monogram))]
    public partial string Name { get; set; }

    /// <summary>
    /// The chosen icon's catalog name, or null for the first-letter monogram. Icon, name and colour are
    /// the three parts of a workspace's identity, so they travel together through every surface.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconGlyph))]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    [NotifyPropertyChangedFor(nameof(IconGlyphVisibility))]
    [NotifyPropertyChangedFor(nameof(MonogramVisibility))]
    public partial string? IconName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorOpacity))]
    [NotifyPropertyChangedFor(nameof(IndicatorChipBrush))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDatabaseText))]
    [NotifyPropertyChangedFor(nameof(ContextBadgeText))]
    public partial DatabaseViewModel? ActiveDatabase { get; set; }

    public ObservableCollection<DatabaseViewModel> Databases { get; } = [];

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public DatabaseViewModel? LastActiveDatabase { get; set; }

    public TabViewModel? LastActiveTab { get; set; }

    /// <summary>
    /// Database to reselect when the connection opens. It outlives disconnects (the database
    /// list empties) and restarts, so a space remembers what the user was working on.
    /// </summary>
    public string? PreferredDatabase { get; set; }

    public bool IsConnected => Connection.IsConnected;

    public string Driver => Connection.Dialect;

    public string DatabaseCountLabel => Databases.Count == 1 ? "1 database" : $"{Databases.Count} databases";

    public string TooltipText => $"{Name} — {Connection.Endpoint} — {DatabaseCountLabel}";

    public string StatusText => Connection.StatusText;

    /// <summary>
    /// Why the rail shows the state next to the connect action: the state word alone ("Connected")
    /// does not say what you are connected to when nothing is open yet. Once connected the database
    /// row carries that context, so the line stays short enough to never truncate the state itself.
    /// </summary>
    public string StatusHeadline => IsConnected ? StatusText : $"{StatusText} · {Connection.ProviderName}";

    public SolidColorBrush StatusBrush => Connection.StatusBrush;

    public string ConnectionLine => Connection.StatusLine;

    /// <summary>One action slot in the rail: "Connect" while down, "Disconnect" while up.</summary>
    public Visibility ConnectActionVisibility => IsConnected ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DisconnectActionVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The database picker only exists once the provider names databases, so it is not a dead row.</summary>
    public Visibility DatabasePickerVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;

    public string ConnectedDatabaseText => ActiveDatabase?.Name
        ?? (Connection.IsConnected ? "no database" : "not connected");

    /// <summary>
    /// What the collapsed-rail badge says: the database once connected, the connection state
    /// otherwise. It uses the same state word as the rail, so "Disconnected" never becomes
    /// "not connected" just because the rail is hidden.
    /// </summary>
    public string ContextBadgeText => Connection.IsConnected ? ConnectedDatabaseText : StatusText;

    /// <summary>Finds a database by name, or null when the list is empty or the name is unknown.</summary>
    public DatabaseViewModel? FindDatabase(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Databases.FirstOrDefault(database =>
                string.Equals(database.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---- workspace identity: an icon (or its first letter), tinted by the workspace colour

    /// <summary>The footer/list announce this, so the icon is never the only label.</summary>
    public string IndicatorName => $"Workspace {Name}";

    /// <summary>The Fluent glyph, or empty when the workspace has no icon and shows its monogram.</summary>
    public string IconGlyph => SpaceIcons.Glyph(IconName) ?? string.Empty;

    public bool HasIcon => IconGlyph.Length > 0;

    public Visibility IconGlyphVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MonogramVisibility => HasIcon ? Visibility.Collapsed : Visibility.Visible;

    public string Monogram => SpaceIcons.Monogram(Name);

    /// <summary>Idle workspaces stay quiet; the active one is full strength on a tinted chip.</summary>
    public double IndicatorOpacity => IsActive ? 1 : 0.45;

    /// <summary>The active workspace's chip, so the marker never depends on hue alone.</summary>
    public SolidColorBrush IndicatorChipBrush => IsActive ? ActiveTintBrush : TransparentBrush;

    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// Replaces the database list with what the server reported, accessible databases first and
    /// alphabetical within each group, while keeping the selection by name.
    /// </summary>
    public void UpdateDatabases(IEnumerable<DatabaseInfo> databases)
    {
        var selectedName = ActiveDatabase?.Name ?? PreferredDatabase;
        var incoming = DatabaseInfo.InDisplayOrder(databases).ToList();

        Databases.Clear();
        foreach (var database in incoming)
        {
            Databases.Add(new DatabaseViewModel(database));
        }

        ActiveDatabase = Databases.FirstOrDefault(d => string.Equals(d.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? Databases.FirstOrDefault();

        if (ActiveDatabase is not null)
        {
            ActiveDatabase.IsActive = true;
        }

        LastActiveDatabase = ActiveDatabase;
        OnPropertyChanged(nameof(DatabaseCountLabel));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(ConnectedDatabaseText));
    }
}

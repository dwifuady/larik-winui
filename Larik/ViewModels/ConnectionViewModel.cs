using CommunityToolkit.Mvvm.ComponentModel;
using Larik.Data;
using Larik.Services;
using Microsoft.UI.Xaml.Media;

namespace Larik.ViewModels;

/// <summary>
/// The single connection a space owns. Everything provider-specific lives behind
/// <see cref="IDatabaseConnection"/>, so adding a database means adding a provider assembly.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject, IAsyncDisposable
{
    private IDatabaseConnection? _session;

    public ConnectionViewModel(string providerId, ConnectionProfile profile)
    {
        ProviderId = providerId;
        Profile = profile;
        Provider = DatabaseProviderCatalog.Find(providerId);
        SecretKeys = CaptureSecretKeys(Provider);
    }

    public string ProviderId { get; private set; }

    public ConnectionProfile Profile { get; private set; }

    public IDatabaseProvider? Provider { get; private set; }

    /// <summary>Password field keys captured while the provider is loaded; never persisted.</summary>
    public IReadOnlySet<string> SecretKeys { get; private set; }

    public string ProviderName => Provider?.DisplayName ?? ProviderId;

    /// <summary>Dialect id for snippet/feature scoping ("mssql", "postgres", "mongodb").</summary>
    public string Dialect => Provider?.Dialect ?? ProviderId;

    /// <summary>The provider's editor language, or generic SQL when it exposes none.</summary>
    public IEditorLanguage EditorLanguage => EditorLanguageHelpers.Resolve(Provider);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [NotifyPropertyChangedFor(nameof(ConnectionActionLabel))]
    public partial ConnectionState State { get; set; } = ConnectionState.Disconnected;

    [ObservableProperty]
    public partial string? ServerVersion { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    public bool IsConnected => State == ConnectionState.Connected;

    public string ConnectionActionLabel => IsConnected ? "Disconnect" : "Connect";

    public string StatusText => State switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Faulted => "Connection failed",
        _ => "Disconnected",
    };

    public SolidColorBrush StatusBrush => State switch
    {
        ConnectionState.Connected => StudioColors.Success,
        ConnectionState.Faulted => StudioColors.Danger,
        _ => StudioColors.Warning,
    };

    public string Endpoint => Provider?.DescribeEndpoint(Profile) ?? ProviderName;

    /// <summary>The identity the saved profile connects as, or null when the provider has none to name.</summary>
    public string? Identity => Provider?.DescribeIdentity(Profile);

    /// <summary>Status is colour AND text — never colour alone.</summary>
    public string StatusLine => $"{Endpoint} · {StatusText}";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Provider is null)
        {
            throw new InvalidOperationException(
                $"The '{ProviderId}' provider isn't installed. Install its add-on in Settings > Add-ons.");
        }

        State = ConnectionState.Connecting;
        LastError = null;

        try
        {
            _session ??= Provider.CreateConnection(Profile);
            await _session.OpenAsync(cancellationToken);
            ServerVersion = _session.ServerVersion;
            State = ConnectionState.Connected;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = ConnectionState.Faulted;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null)
        {
            await _session.CloseAsync(cancellationToken);
        }

        ServerVersion = null;
        State = ConnectionState.Disconnected;
    }

    public Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default) =>
        RequireSession().GetDatabaseDetailsAsync(cancellationToken);

    /// <summary>
    /// Creates an unopened session for a query tab. Each tab owns its session, so transactions,
    /// temp tables and <c>SET</c> options stay local to that tab. The workspace keeps only the
    /// control session used for connection state and the database list.
    /// </summary>
    public IDatabaseConnection CreateSession()
    {
        if (Provider is null)
        {
            throw new InvalidOperationException(
                $"The '{ProviderId}' provider isn't installed. Install its add-on in Settings > Add-ons.");
        }

        return Provider.CreateConnection(Profile);
    }

    /// <summary>Swaps the provider/profile (from the connection dialog). Closes any live session.</summary>
    public async Task ReconfigureAsync(string providerId, ConnectionProfile profile)
    {
        await DisconnectAsync();

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        ProviderId = providerId;
        Profile = profile;
        Provider = DatabaseProviderCatalog.Find(providerId);
        SecretKeys = CaptureSecretKeys(Provider);

        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(Dialect));
        OnPropertyChanged(nameof(EditorLanguage));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(Identity));
        OnPropertyChanged(nameof(StatusLine));
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    private static IReadOnlySet<string> CaptureSecretKeys(IDatabaseProvider? provider) =>
        provider?.Settings
            .Where(setting => setting.Kind == ProviderSettingKind.Password)
            .Select(setting => setting.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private IDatabaseConnection RequireSession() =>
        _session ?? throw new InvalidOperationException("Not connected — connect the workspace first.");
}

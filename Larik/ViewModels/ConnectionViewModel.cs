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
    }

    public string ProviderId { get; private set; }

    public ConnectionProfile Profile { get; private set; }

    public IDatabaseProvider? Provider { get; private set; }

    public string ProviderName => Provider?.DisplayName ?? ProviderId;

    /// <summary>Dialect id for snippet/feature scoping ("mssql", "postgres", "mongodb").</summary>
    public string Dialect => Provider?.Dialect ?? ProviderId;

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

    public string Endpoint => ProviderId switch
    {
        "mssql" => $"SQL Server · {Profile.Get("server")}",
        _ => ProviderName,
    };

    /// <summary>Status is colour AND text — never colour alone.</summary>
    public string StatusLine => $"{Endpoint} · {StatusText}";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Provider is null)
        {
            throw new InvalidOperationException($"No database provider is loaded for '{ProviderId}'.");
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

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        RequireSession().GetDatabasesAsync(cancellationToken);

    public Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default) =>
        RequireSession().GetSchemaAsync(database, cancellationToken);

    public Task<QueryResult> ExecuteAsync(string sql, string? database, CancellationToken cancellationToken = default) =>
        RequireSession().ExecuteAsync(sql, database, cancellationToken);

    public Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default) =>
        RequireSession().ExecuteAsync(sql, database, options, cancellationToken);

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

        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(Dialect));
        OnPropertyChanged(nameof(Endpoint));
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

    private IDatabaseConnection RequireSession() =>
        _session ?? throw new InvalidOperationException("Not connected — connect the workspace first.");
}

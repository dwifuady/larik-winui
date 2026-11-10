using Larik.Data;

namespace Larik.Data.Tests.Fixture;

/// <summary>
/// A minimal provider used only by the loader tests. It exists so an assembly outside the app can
/// implement the host's contract and be discovered through <see cref="Addons.AddonLoadContext"/>.
/// </summary>
public sealed class FixtureProvider : IDatabaseProvider
{
    public const string IdValue = "fixture";

    public string Id => IdValue;

    public string DisplayName => "Fixture (add-on)";

    public string Dialect => "mssql";

    public IReadOnlyList<ProviderSetting> Settings => [];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new FixtureConnection();
}

internal sealed class FixtureConnection : IDatabaseConnection
{
    public ConnectionState State => ConnectionState.Disconnected;

    public string? ServerVersion => null;

    public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fixture provider does not execute queries.");

    public Task<IReadOnlyList<TableSchema>> GetSchemaAsync(
        string? database,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TableSchema>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

using Larik.Data;

namespace Larik.Services;

/// <summary>
/// One persistent server session owned by a query tab. Transactions, temp tables and
/// <c>SET</c> options live here, not on the workspace, so switching tabs cannot change another
/// tab's transaction context. The session opens lazily on first use and is rolled back and
/// disposed when the tab closes.
/// </summary>
public sealed class QuerySession
{
    private readonly Func<IDatabaseConnection> _sessionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDatabaseConnection? _session;
    private CancellationTokenSource? _runCancellation;
    private SessionTransactionState _transaction = SessionTransactionState.None;
    private bool _isRunning;

    public QuerySession(Func<IDatabaseConnection> sessionFactory) => _sessionFactory = sessionFactory;

    /// <summary>Raised whenever the running or transaction state changes.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => _isRunning;

    public SessionTransactionState Transaction => _transaction;

    public bool HasOpenTransaction => _transaction.HasTransaction;

    public bool CanCommit => _transaction.CanCommit;

    public bool CanRollback => _transaction.CanRollback;

    public bool IsDoomed => _transaction.IsDoomed;

    /// <summary>Asks the provider to stop the statement that is currently running.</summary>
    public void Cancel() => _runCancellation?.Cancel();

    /// <summary>
    /// Runs one batch on this tab's own session. The provider already serializes commands on a
    /// connection; the gate also keeps schema loads and commit/rollback from interleaving.
    /// </summary>
    public async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureOpenCoreAsync(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = linked;
            SetRunning(true);

            try
            {
                var results = await session.ExecuteBatchAsync(sql, database, options, linked.Token);

                // SqlClient can surface a cancellation as a SqlException ("Operation cancelled
                // by user") instead of OperationCanceledException. Normalize it so the shell
                // reports "Cancelled" rather than a query error, and never keep a half result.
                if (linked.IsCancellationRequested)
                {
                    throw new OperationCanceledException(linked.Token);
                }

                return results;
            }
            finally
            {
                _runCancellation = null;
                SetRunning(false);

                // A failed or cancelled command can still leave a transaction open; refresh with
                // a token that is not already cancelled so the guard reflects the server state.
                await RefreshTransactionCoreAsync(session, CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureOpenCoreAsync(cancellationToken);
            return await session.GetSchemaAsync(database, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureOpenCoreAsync(cancellationToken);
            if (!session.SupportsTransactions)
            {
                throw new NotSupportedException("This provider does not support transactions.");
            }

            await session.CommitTransactionAsync(cancellationToken);
            await RefreshTransactionCoreAsync(session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = await EnsureOpenCoreAsync(cancellationToken);
            if (!session.SupportsTransactions)
            {
                throw new NotSupportedException("This provider does not support transactions.");
            }

            await session.RollbackTransactionAsync(cancellationToken);
            await RefreshTransactionCoreAsync(session, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cancels a running query, rolls back an open transaction, closes the provider session
    /// and forgets it. Called when the tab closes or the workspace disconnects.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Cancel();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                return;
            }

            // Only ask the server for transaction state when this tab actually opened a session;
            // a never-used tab should not pay for a connection just to close it.
            if (_session.State == ConnectionState.Connected && _session.SupportsTransactions)
            {
                try
                {
                    var state = await _session.GetTransactionStateAsync(cancellationToken);
                    if (state.HasTransaction)
                    {
                        await _session.RollbackTransactionAsync(cancellationToken);
                    }
                }
                catch
                {
                    // The session is going away; a failed rollback must not block disposal.
                }
            }

            try
            {
                await _session.CloseAsync(cancellationToken);
            }
            catch
            {
                // Best effort: the connection may already be broken.
            }

            await _session.DisposeAsync();
            _session = null;
            SetRunning(false);
            SetTransaction(SessionTransactionState.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IDatabaseConnection EnsureSession() => _session ??= _sessionFactory();

    private async Task<IDatabaseConnection> EnsureOpenCoreAsync(CancellationToken cancellationToken)
    {
        var session = EnsureSession();
        await session.OpenAsync(cancellationToken);
        return session;
    }

    private async Task RefreshTransactionCoreAsync(
        IDatabaseConnection session,
        CancellationToken cancellationToken)
    {
        if (!session.SupportsTransactions)
        {
            SetTransaction(SessionTransactionState.None);
            return;
        }

        try
        {
            SetTransaction(await session.GetTransactionStateAsync(cancellationToken));
        }
        catch
        {
            // Keep the last known state; the next successful command refreshes it. A broken
            // connection is surfaced by the command itself, not by the guard.
        }
    }

    private void SetRunning(bool value)
    {
        if (_isRunning == value)
        {
            return;
        }

        _isRunning = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTransaction(SessionTransactionState value)
    {
        if (_transaction == value)
        {
            return;
        }

        _transaction = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

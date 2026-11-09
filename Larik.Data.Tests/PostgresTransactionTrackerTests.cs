using Larik.Data;
using Larik.Data.Postgres;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// PostgreSQL has no nested transactions and cannot introspect its own in-transaction state, so the
/// provider derives it from the statements it sent. These golden cases pin the grammar details that
/// are easy to miss: optional WORK/TRANSACTION, AND CHAIN, and ROLLBACK TO SAVEPOINT.
/// </summary>
public sealed class PostgresTransactionTrackerTests
{
    private static readonly SessionTransactionState Open = new(1, 1);
    private static readonly SessionTransactionState Doomed = new(1, -1);

    private static SessionTransactionState Apply(
        string sql,
        SessionTransactionState? current = null,
        bool succeeded = true) =>
        PostgresTransactionTracker.Apply(current ?? SessionTransactionState.None, sql, succeeded);

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("BEGIN;")]
    [InlineData("BEGIN WORK")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("START TRANSACTION")]
    [InlineData("/* leading */ -- comment\n START TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    public void Starts_a_transaction_on_begin_and_start_transaction(string sql)
    {
        Assert.Equal(Open, Apply(sql));
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("COMMIT;")]
    [InlineData("COMMIT WORK")]
    [InlineData("COMMIT TRANSACTION")]
    [InlineData("END")]
    [InlineData("COMMIT PREPARED 'tx1'")]
    [InlineData("ROLLBACK")]
    [InlineData("ROLLBACK WORK")]
    [InlineData("ABORT")]
    [InlineData("ROLLBACK PREPARED 'tx1'")]
    [InlineData("PREPARE TRANSACTION 'tx1'")]
    public void Ends_a_transaction_on_commit_end_rollback_or_abort(string sql)
    {
        Assert.Equal(SessionTransactionState.None, Apply(sql, Open));
    }

    [Theory]
    [InlineData("COMMIT AND CHAIN")]
    [InlineData("COMMIT WORK AND CHAIN")]
    [InlineData("COMMIT TRANSACTION AND CHAIN")]
    [InlineData("ROLLBACK AND CHAIN")]
    [InlineData("ROLLBACK WORK AND CHAIN")]
    public void And_chain_immediately_starts_the_next_transaction(string sql)
    {
        Assert.Equal(Open, Apply(sql, Open));
    }

    [Fact]
    public void And_no_chain_ends_the_transaction()
    {
        Assert.Equal(SessionTransactionState.None, Apply("COMMIT AND NO CHAIN", Open));
    }

    [Theory]
    [InlineData("ROLLBACK TO SAVEPOINT checkpoint")]
    [InlineData("ROLLBACK WORK TO SAVEPOINT checkpoint")]
    [InlineData("ROLLBACK TRANSACTION TO checkpoint")]
    public void Rollback_to_savepoint_keeps_the_transaction_open(string sql)
    {
        Assert.Equal(Open, Apply(sql, Open));
    }

    [Fact]
    public void Rollback_to_savepoint_revives_a_doomed_transaction()
    {
        // PostgreSQL clears the aborted state on a successful savepoint rollback, so COMMIT works again.
        Assert.Equal(Open, Apply("ROLLBACK TO SAVEPOINT checkpoint", Doomed));
    }

    [Fact]
    public void Rollback_to_savepoint_outside_a_transaction_stays_empty()
    {
        Assert.Equal(SessionTransactionState.None, Apply("ROLLBACK TO SAVEPOINT checkpoint"));
    }

    [Fact]
    public void A_failed_statement_inside_a_transaction_makes_it_rollback_only()
    {
        Assert.Equal(Doomed, Apply("SELECT 1/0", Open, succeeded: false));
    }

    [Fact]
    public void A_failed_statement_outside_a_transaction_leaves_state_alone()
    {
        Assert.Equal(SessionTransactionState.None, Apply("SELECT 1/0", succeeded: false));
    }

    [Fact]
    public void A_failed_begin_does_not_open_a_transaction()
    {
        Assert.Equal(SessionTransactionState.None, Apply("BEGIN", succeeded: false));
    }

    [Fact]
    public void Begin_inside_an_existing_transaction_is_idempotent()
    {
        Assert.Equal(Open, Apply("BEGIN", Open));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$")]
    [InlineData("SAVEPOINT checkpoint")]
    [InlineData("RELEASE SAVEPOINT checkpoint")]
    public void Ordinary_statements_do_not_change_transaction_state(string sql)
    {
        Assert.Equal(Open, Apply(sql, Open));
    }
}

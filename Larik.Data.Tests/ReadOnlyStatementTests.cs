using Larik.Data.Demo;
using Larik.Data.SqlServer;
using Larik.Data.Postgres;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The read-only gate an MCP client is held to. Every case here is a security decision, so the
/// matrix leans on refusals: when the scanner cannot read a statement cleanly it must say no.
/// </summary>
public sealed class ReadOnlyStatementTests
{
    private static bool TSql(string sql) => TSqlReadOnlyPolicy.Instance.IsProvenRead(sql);

    private static bool Postgres(string sql) => PostgresReadOnlyPolicy.Instance.IsProvenRead(sql);

    [Theory]
    [InlineData("select 1")]
    [InlineData("SELECT * FROM dbo.customers")]
    [InlineData("select top 10 name from dbo.t order by name")]
    [InlineData("with cte as (select 1 as x) select * from cte")]
    [InlineData("select 'x; drop table t' as value")]
    [InlineData("select 1 -- ; drop table t")]
    [InlineData("select /* ; delete from t */ 1")]
    [InlineData("select [into] from dbo.t")]
    [InlineData("select * from dbo.t;")]
    public void T_Sql_reads_are_allowed(string sql) => Assert.True(TSql(sql));

    [Theory]
    [InlineData("insert into dbo.t values (1)")]
    [InlineData("update dbo.t set x = 1")]
    [InlineData("delete from dbo.t")]
    [InlineData("merge dbo.t as target using dbo.s as source on 1 = 1 when matched then delete;")]
    [InlineData("select 1; delete from dbo.t")]
    [InlineData("exec sp_who")]
    [InlineData("exec('drop table dbo.t')")]
    [InlineData("select * from xp_cmdshell('dir')")]
    [InlineData("select * into #stolen from dbo.t")]
    [InlineData("select * from openrowset('SQLNCLI', 'x', 'select 1')")]
    [InlineData("use master")]
    [InlineData("set nocount on")]
    [InlineData("begin transaction")]
    [InlineData("truncate table dbo.t")]
    [InlineData("dbcc checkdb")]
    [InlineData("with c as (select 1 as x) insert into dbo.t select * from c")]
    [InlineData("select 'unterminated")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void T_Sql_writes_and_malformed_input_are_refused(string sql) => Assert.False(TSql(sql));

    [Theory]
    [InlineData("select * from public.orders")]
    [InlineData("with recent as (select * from orders) select * from recent")]
    [InlineData("values (1), (2)")]
    [InlineData("table orders")]
    public void Postgres_reads_are_allowed(string sql) => Assert.True(Postgres(sql));

    [Theory]
    [InlineData("copy orders to stdout")]
    [InlineData("begin")]
    [InlineData("select nextval('orders_id_seq')")]
    [InlineData("select set_config('default_transaction_read_only', 'off', false)")]
    [InlineData("select pg_terminate_backend(123)")]
    [InlineData("do $$ begin delete from orders; end $$")]
    [InlineData("select 1; drop table orders")]
    public void Postgres_writes_are_refused(string sql) => Assert.False(Postgres(sql));

    [Fact]
    public void Every_built_in_provider_declares_a_policy()
    {
        // A null policy makes the host refuse read-mode queries entirely, which is correct for an
        // unknown provider but wrong for the ones we ship.
        Assert.NotNull(new SqlServerProvider().ReadOnlyPolicy);
        Assert.NotNull(new PostgresProvider().ReadOnlyPolicy);
        Assert.NotNull(new DemoProvider().ReadOnlyPolicy);
    }

    [Fact]
    public void Read_only_execution_is_opt_in()
    {
        // Null keeps the historical read-write behaviour for every existing caller.
        Assert.Null(QueryExecutionOptions.Default.ReadOnly);
        Assert.True(new QueryExecutionOptions { ReadOnly = true }.ReadOnly);
    }
}

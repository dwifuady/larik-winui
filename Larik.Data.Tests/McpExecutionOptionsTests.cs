using Larik.Mcp;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// An approved write has to reach the provider in read-write mode. Passing <c>ReadOnly = true</c>
/// made SQL Server run it inside a transaction it rolls back afterwards, so the agent got
/// "N rows affected", the audit row said ok, and the data was gone. Both branches are pinned
/// because the failure was silent: nothing in the response distinguishes a rolled-back write
/// from a committed one.
/// </summary>
public sealed class McpExecutionOptionsTests
{
    [Fact]
    public void An_approved_write_runs_read_write()
    {
        var options = StudioTools.ExecutionOptions(readOnly: false, limit: 200);

        // null, not false: the contract reads null as "historical read-write behaviour", and
        // QueryExecutionOptions.ReadOnly is bool? for exactly that tri-state.
        Assert.Null(options.ReadOnly);
    }

    [Fact]
    public void A_read_only_exposure_still_asks_for_the_engine_guard()
    {
        var options = StudioTools.ExecutionOptions(readOnly: true, limit: 200);

        Assert.True(options.ReadOnly);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(1000)]
    public void The_row_limit_reaches_the_provider(int limit) =>
        Assert.Equal(limit, StudioTools.ExecutionOptions(readOnly: false, limit).MaxRows);
}

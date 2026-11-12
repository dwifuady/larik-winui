using System.Text.Json;
using Larik.Services;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// One query must not be able to fill an agent's context. The budget shortens results by dropping
/// rows, and keeps the shape (columns, leading rows) so the agent still learns something.
/// </summary>
public sealed class McpResponseBudgetTests
{
    private static List<string?[]> Rows(int count, int width = 2) =>
        [.. Enumerable.Range(0, count).Select(row =>
            Enumerable.Range(0, width).Select(column => (string?)$"{row}-{column}-padding-padding").ToArray())];

    [Fact]
    public void A_result_within_budget_is_untouched()
    {
        var set = Rows(10);
        var dropped = McpResponseBudget.Trim([set], () => JsonSerializer.Serialize(set).Length);

        Assert.Equal(0, dropped);
        Assert.Equal(10, set.Count);
    }

    [Fact]
    public void An_oversized_result_keeps_its_first_rows()
    {
        var set = Rows(2_000);
        var dropped = McpResponseBudget.Trim(
            [set],
            () => JsonSerializer.Serialize(set).Length,
            maxBytes: 8_000);

        Assert.True(dropped > 0);
        Assert.True(set.Count > 0, "the budget removed every row");
        Assert.Equal("0-0-padding-padding", set[0][0]);
        Assert.True(JsonSerializer.Serialize(set).Length <= 8_000);
    }

    [Fact]
    public void The_largest_set_is_trimmed_first()
    {
        var small = Rows(5);
        var large = Rows(1_000);

        McpResponseBudget.Trim(
            [small, large],
            () => JsonSerializer.Serialize(new[] { small, large }).Length,
            maxBytes: 8_000);

        Assert.Equal(5, small.Count);
        Assert.True(large.Count < 1_000);
    }

    [Fact]
    public void An_empty_payload_is_not_a_reason_to_loop()
    {
        var dropped = McpResponseBudget.Trim([], () => 10_000_000, maxBytes: 1_000);

        Assert.Equal(0, dropped);
    }
}

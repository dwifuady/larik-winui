using System.Text.Json;
using Larik.Services;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The approval channel is two processes agreeing on a wire format. Version skew here would mean an
/// agent either hangs or has its write silently approved, so the shapes are pinned.
/// </summary>
public sealed class McpPipeTests
{
    [Fact]
    public void Approval_request_round_trips()
    {
        var request = new McpApprovalRequest
        {
            Id = "abc123",
            SpaceId = "space-1",
            SpaceName = "Staging",
            ProviderId = "mssql",
            Statement = "delete from dbo.t where 1 = 1",
        };

        var json = JsonSerializer.Serialize(request, McpPipe.Json);
        var parsed = JsonSerializer.Deserialize<McpApprovalRequest>(json, McpPipe.Json);

        Assert.NotNull(parsed);
        Assert.Equal(McpPipe.MessageTypeApproval, parsed.Type);
        Assert.Equal(request.Id, parsed.Id);
        Assert.Equal(request.Statement, parsed.Statement);
        Assert.Equal(request.SpaceName, parsed.SpaceName);
    }

    [Fact]
    public void Decision_round_trips_with_its_surface()
    {
        var decision = new McpApprovalDecision
        {
            Id = "abc123",
            Approved = true,
            Surface = "app",
            Status = McpApprovalStatus.Approved,
        };

        var parsed = JsonSerializer.Deserialize<McpApprovalDecision>(
            JsonSerializer.Serialize(decision, McpPipe.Json),
            McpPipe.Json);

        Assert.NotNull(parsed);
        Assert.True(parsed.Approved);
        Assert.Equal("app", parsed.Surface);
        Assert.Equal(McpPipe.MessageTypeDecision, parsed.Type);
    }

    [Fact]
    public void Denials_carry_a_reason_for_the_agent()
    {
        var denied = McpApprovalDecision.Denied("id", McpApprovalStatus.TimedOut, "two minutes passed");

        Assert.False(denied.Approved);
        Assert.Equal(McpApprovalStatus.TimedOut, denied.Status);
        Assert.False(string.IsNullOrWhiteSpace(denied.Reason));
    }

    [Fact]
    public void A_prompt_nobody_answers_is_denied_after_two_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), McpPipe.ApprovalTimeout);
        Assert.True(McpPipe.ConnectTimeout < McpPipe.ApprovalTimeout);
    }

    [Theory]
    [InlineData("read", McpAccess.Read)]
    [InlineData("write", McpAccess.Write)]
    [InlineData("Read", McpAccess.Read)]
    [InlineData(" read ", McpAccess.Read)]
    [InlineData("", McpAccess.Off)]
    [InlineData(null, McpAccess.Off)]
    [InlineData("everything", McpAccess.Off)]
    public void Access_levels_are_fail_closed(string? stored, McpAccess expected) =>
        Assert.Equal(expected, McpAccessText.Parse(stored));

    [Fact]
    public void Access_levels_round_trip_through_storage()
    {
        foreach (var access in Enum.GetValues<McpAccess>())
        {
            Assert.Equal(access, McpAccessText.Parse(McpAccessText.ToStorage(access)));
        }
    }
}

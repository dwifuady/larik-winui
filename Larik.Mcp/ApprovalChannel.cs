using System.IO.Pipes;
using System.Text.Json;
using Larik.Services;

namespace Larik.Mcp;

/// <summary>
/// Talks to the running studio over its current-user named pipe. When no studio is listening the
/// connect fails fast and the caller decides what that means (client elicitation, a refusal, or an
/// actionable error); a broken pipe is never turned into a query result.
/// </summary>
internal static class ApprovalChannel
{
    public static Task<McpApprovalDecision?> TryRequestAsync(
        McpApprovalRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<McpApprovalRequest, McpApprovalDecision>(
            request,
            // The studio enforces its own two-minute limit; this is the backstop for a studio that
            // died mid-dialog, so the agent gets an answer instead of hanging.
            McpPipe.ApprovalTimeout + TimeSpan.FromSeconds(20),
            cancellationToken);

    public static Task<McpOpenResponse?> TryOpenAsync(
        McpOpenRequest request,
        CancellationToken cancellationToken) =>
        // Opening a tab is local work: if the studio is alive it answers at once.
        SendAsync<McpOpenRequest, McpOpenResponse>(request, TimeSpan.FromSeconds(15), cancellationToken);

    private static async Task<TResponse?> SendAsync<TRequest, TResponse>(
        TRequest request,
        TimeSpan waitForReply,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                McpPipe.Name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(McpPipe.ConnectTimeout);
                try
                {
                    await pipe.ConnectAsync(connect.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // No studio listening: the app is closed or MCP is off there.
                    return null;
                }
            }

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, McpPipe.Json));

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(waitForReply);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(wait.Token);
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<TResponse>(line, McpPipe.Json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A misbehaving pipe is "no studio", not an execution failure.
            return null;
        }
    }
}

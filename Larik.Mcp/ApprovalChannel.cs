using System.IO.Pipes;
using System.Text.Json;
using Larik.Services;

namespace Larik.Mcp;

/// <summary>
/// Asks the running studio to show a statement to the user. The pipe is the app's: when no studio is
/// running the connect fails fast and the caller decides what that means (client elicitation, or a
/// refusal). A broken pipe is never turned into a query result.
/// </summary>
internal static class ApprovalChannel
{
    public static async Task<McpApprovalDecision?> TryRequestAsync(
        McpApprovalRequest request,
        CancellationToken cancellationToken)
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

            // The studio enforces its own two-minute limit; this is only the backstop for a studio
            // that died mid-dialog, so the agent gets an answer instead of hanging.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(McpPipe.ApprovalTimeout + TimeSpan.FromSeconds(20));

            using var reader = new StreamReader(pipe, leaveOpen: true);
            var line = await reader.ReadLineAsync(wait.Token);
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<McpApprovalDecision>(line, McpPipe.Json);
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

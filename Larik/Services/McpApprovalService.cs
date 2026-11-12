using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Larik.Services;

/// <summary>
/// The human side of the MCP agent surface: a current-user named pipe, a dialog in this window, and
/// a Windows notification plus a taskbar flash when the window is not in front.
/// </summary>
/// <remarks>
/// It exists only while the app runs. Larik.Mcp treats silence (no pipe, or no answer before the
/// timeout) as "no human available" and refuses the write, so a closed studio can never be used to
/// run something nobody approved.
/// </remarks>
public static class McpApprovalService
{
    /// <summary>One dialog at a time: a queue of modal prompts is a queue of accidents.</summary>
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    private static bool _started;

    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        RegisterNotifications();

        // Requests whose waiting process died while the app was closed can never be answered. This
        // opens the store, so it stays off the launch path: a second connection to a brand-new
        // database must never delay the first window.
        _ = Task.Run(() => new StudioStore().SweepMcpApprovals());
        _ = Task.Run(ListenAsync);
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    McpPipe.Name,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync();
                await HandleAsync(pipe);
            }
            catch (Exception ex)
            {
                // A second instance, a broken client or a closing app: keep serving the next one.
                AppLog.Warning("mcp", "approval channel paused", ex);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var line = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        McpApprovalRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<McpApprovalRequest>(line, McpPipe.Json);
        }
        catch (JsonException)
        {
            return;
        }

        if (request is null)
        {
            return;
        }

        AppLog.Info("mcp", $"approval requested by an agent: space {request.SpaceId}, {request.Statement.Length} characters");

        var decision = await AskAsync(request);

        AppLog.Info("mcp", $"approval {decision.Status} for space {request.SpaceId}");

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(decision, McpPipe.Json));
    }

    private static async Task<McpApprovalDecision> AskAsync(McpApprovalRequest request)
    {
        var completion = new TaskCompletionSource<McpApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!App.DispatcherQueue.TryEnqueue(() => _ = ShowDialogAsync(request, completion)))
        {
            return McpApprovalDecision.Denied(
                request.Id,
                McpApprovalStatus.Denied,
                "The studio could not show its approval prompt, so the statement was refused.");
        }

        return await completion.Task;
    }

    private static async Task ShowDialogAsync(McpApprovalRequest request, TaskCompletionSource<McpApprovalDecision> completion)
    {
        await DialogGate.WaitAsync();
        try
        {
            if ((App.Window?.Content as FrameworkElement)?.XamlRoot is not { } xamlRoot)
            {
                completion.TrySetResult(McpApprovalDecision.Denied(
                    request.Id,
                    McpApprovalStatus.Denied,
                    "The studio window was not ready to show an approval prompt."));
                return;
            }

            Notify(request);

            var statement = new TextBlock
            {
                Text = request.Statement,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"An agent wants to run this in {request.SpaceName}",
                Content = new ScrollViewer { Content = statement, MaxHeight = 320 },
                PrimaryButtonText = "Run it",
                CloseButtonText = "Deny",
                // Deny is the default: Enter on a prompt nobody read must not execute SQL.
                DefaultButton = ContentDialogButton.Close,
            };
            AutomationProperties.SetName(dialog, "Agent approval");

            using var timeout = new CancellationTokenSource(McpPipe.ApprovalTimeout);
            AppLog.Info("mcp", $"approval prompt shown; auto-deny in {McpPipe.ApprovalTimeout}");
            using var registration = timeout.Token.Register(() =>
            {
                // Answer the agent first: a prompt nobody looked at must not leave a caller hanging
                // on a dialog that may not even be visible. Hiding it afterwards is best effort.
                AppLog.Info("mcp", "the approval prompt timed out; denying");
                completion.TrySetResult(McpApprovalDecision.Denied(
                    request.Id,
                    McpApprovalStatus.TimedOut,
                    "The approval prompt timed out after two minutes, so the statement was refused."));

                App.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("mcp", "could not close the timed-out prompt", ex);
                    }
                });
            });

            var result = await dialog.ShowAsync();
            var timedOut = timeout.IsCancellationRequested;
            var approved = result == ContentDialogResult.Primary && !timedOut;

            completion.TrySetResult(new McpApprovalDecision
            {
                Id = request.Id,
                Approved = approved,
                Status = approved
                    ? McpApprovalStatus.Approved
                    : timedOut ? McpApprovalStatus.TimedOut : McpApprovalStatus.Denied,
                Reason = approved
                    ? null
                    : timedOut
                        ? "The approval prompt timed out after two minutes, so the statement was refused."
                        : "The user denied this statement in Larik SQL Studio.",
            });
        }
        finally
        {
            DialogGate.Release();
        }
    }

    /// <summary>
    /// Toast plus taskbar flash, only when the window is not already in front. Both are best effort:
    /// an unpackaged build without notification registration still gets the dialog.
    /// </summary>
    private static void Notify(McpApprovalRequest request)
    {
        if (GetForegroundWindow() == App.WindowHandle)
        {
            return;
        }

        try
        {
            var preview = request.Statement.Length <= 160
                ? request.Statement
                : request.Statement[..160] + "…";

            var notification = new AppNotificationBuilder()
                .AddText("An agent wants to run a statement")
                .AddText($"{request.SpaceName}: {preview}")
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            // No notification platform (portable build, policy): the dialog and the flash remain.
            AppLog.Warning("mcp", "approval notification unavailable", ex);
        }

        FlashTaskbar();
    }

    private static void RegisterNotifications()
    {
        try
        {
            // A packaged app gets its notification plumbing from the manifest, and Register() is the
            // unpackaged path (it writes the registry entries itself). Calling it here throws
            // "No COM servers are registered for this app" on a packaged build.
            if (!LarikPaths.IsPackaged)
            {
                AppNotificationManager.Default.Register();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("mcp", "notifications unavailable in this build", ex);
        }
    }

    private static void FlashTaskbar()
    {
        try
        {
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = App.WindowHandle,
                // Flash the taskbar button and keep going until the window comes forward.
                Flags = FlashWindowTray | FlashWindowTimerNoForeground,
                Count = 3,
            };

            FlashWindowEx(ref info);
        }
        catch
        {
            // Attention is a courtesy; the dialog still blocks the request.
        }
    }

    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}

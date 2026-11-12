using Larik.Data;
using Larik.Data.Demo;
using Larik.Data.Postgres;
using Larik.Data.SqlServer;
using Larik.Mcp;
using Larik.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A packaged studio keeps its store inside the package, and this process only sees that folder when
// it is launched with package identity (the execution alias does that). A client that starts the EXE
// by path therefore gets told where to look: --state <dir>. It must land before anything reads
// LarikPaths, whose directory is resolved once.
for (var index = 0; index < args.Length - 1; index++)
{
    if (string.Equals(args[index], "--state", StringComparison.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable("LARIK_STATE_DIR", args[index + 1]);
        break;
    }
}

// Providers are discovered from the assemblies this EXE carries, plus the same Plugins folder the
// app uses, so an installed add-on works for agents too without a second registration list.
DatabaseProviderCatalog.Initialize(
    [
        typeof(DemoProvider).Assembly,
        typeof(SqlServerProvider).Assembly,
        typeof(PostgresProvider).Assembly,
    ],
    LarikPaths.PluginsDirectory);

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol channel. Every log line has to go to stderr, or a client sees
// corrupted JSON-RPC traffic.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new StudioStore());
builder.Services.AddSingleton<StudioContext>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

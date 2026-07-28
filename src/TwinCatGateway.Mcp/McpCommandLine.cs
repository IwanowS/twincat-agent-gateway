using System;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TwinCatGateway.Mcp;

internal static class McpCommandLine
{
    private const string DefaultGatewayCommand =
        "twincat-gateway";

    public static Task<int> InvokeAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateRootCommand(RunServerAsync)
            .Parse(args)
            .InvokeAsync(
                cancellationToken: cancellationToken);
    }

    internal static RootCommand CreateRootCommand(
        Func<
            GatewayMcpOptions,
            CancellationToken,
            Task> runServer)
    {
        ArgumentNullException.ThrowIfNull(runServer);

        Option<string?> configurationOption =
            new("--config")
            {
                Description =
                    "Explicit twincat-gateway.json path. "
                    + "Default: discover from MCP workspace "
                    + "roots and the current directory.",
                HelpName = "path",
            };
        Option<string?> pipeOption =
            new("--pipe")
            {
                Description =
                    "Override the gateway Named Pipe. "
                    + "Default: TWINCAT_GATEWAY_PIPE when set; "
                    + "otherwise use the project configuration.",
                DefaultValueFactory =
                    _ => Environment.GetEnvironmentVariable(
                        "TWINCAT_GATEWAY_PIPE"),
                HelpName = "name",
            };
        Option<string> gatewayCommandOption =
            new("--gateway-command")
            {
                Description =
                    "Desktop gateway command used by gateway_start.",
                DefaultValueFactory =
                    _ => Environment.GetEnvironmentVariable(
                            "TWINCAT_GATEWAY_COMMAND")
                        ?? GetDefaultGatewayCommand(),
                HelpName = "command",
            };

        RootCommand root = new(
            """
            Stdio MCP adapter for TwinCAT Agent Gateway.

            Examples:
              twincat-gateway-mcp
              twincat-gateway-mcp --config C:\Projects\Machine\twincat-gateway.json
              twincat-gateway-mcp --pipe TwinCatAgentGateway
              twincat-gateway-mcp --gateway-command C:\Tools\twincat-gateway.cmd
            """);
        root.Options.Add(configurationOption);
        root.Options.Add(pipeOption);
        root.Options.Add(gatewayCommandOption);
        root.SetAction(
            async (parseResult, cancellationToken) =>
            {
                GatewayMcpOptions options = new()
                {
                    ExplicitConfigurationPath =
                        parseResult.GetValue(
                            configurationOption),
                    PipeNameOverride =
                        parseResult.GetValue(pipeOption),
                    CurrentDirectory =
                        Environment.CurrentDirectory,
                    GatewayCommand =
                        parseResult.GetValue(
                            gatewayCommandOption)
                        ?? DefaultGatewayCommand,
                };
                await runServer(
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
            });
        return root;
    }

    private static string GetDefaultGatewayCommand()
    {
        string? processPath = Environment.ProcessPath;
        string? mcpDirectory =
            processPath is null
                ? null
                : System.IO.Path.GetDirectoryName(
                    processPath);
        string? applicationDirectory =
            mcpDirectory is null
                ? null
                : System.IO.Path.GetDirectoryName(
                    mcpDirectory);
        if (processPath is not null
            && string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(
                    processPath),
                "twincat-gateway-mcp",
                StringComparison.OrdinalIgnoreCase)
            && applicationDirectory is not null)
        {
            string installedGateway =
                System.IO.Path.Combine(
                    applicationDirectory,
                    "gateway",
                    "twincat-gateway.exe");
            if (System.IO.File.Exists(installedGateway))
            {
                return installedGateway;
            }
        }

        return DefaultGatewayCommand;
    }

    private static async Task RunServerAsync(
        GatewayMcpOptions gatewayOptions,
        CancellationToken cancellationToken)
    {
        HostApplicationBuilder builder =
            Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(
            options =>
                options.LogToStandardErrorThreshold =
                    LogLevel.Trace);

        builder.Services.AddSingleton(gatewayOptions);
        builder.Services.AddSingleton<GatewayMcpRuntime>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TwinCatTools>()
            .WithResources<TwinCatResources>();

        await builder
            .Build()
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

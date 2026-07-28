using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Client;
using TwinCatGateway.Mcp;

HostApplicationBuilder builder =
    Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(
    options =>
        options.LogToStandardErrorThreshold =
            LogLevel.Trace);

string pipeName =
    builder.Configuration["pipe"]
    ?? Environment.GetEnvironmentVariable(
        "TWINCAT_GATEWAY_PIPE")
    ?? "TwinCatAgentGateway";

builder.Services.AddSingleton<ITwinCatGatewayClient>(
    _ => new TwinCatGatewayClient(pipeName));
builder.Services.AddSingleton<GatewayOperationPoller>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<TwinCatTools>()
    .WithResources<TwinCatResources>();

await builder.Build().RunAsync();

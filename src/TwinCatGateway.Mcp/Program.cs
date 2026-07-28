using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Mcp;

HostApplicationBuilder builder =
    Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(
    options =>
        options.LogToStandardErrorThreshold =
            LogLevel.Trace);

GatewayMcpOptions gatewayOptions = new()
{
    ExplicitConfigurationPath =
        builder.Configuration["config"],
    PipeNameOverride =
        builder.Configuration["pipe"]
        ?? Environment.GetEnvironmentVariable(
            "TWINCAT_GATEWAY_PIPE"),
    CurrentDirectory = Environment.CurrentDirectory,
    GatewayCommand =
        builder.Configuration["gateway-command"]
        ?? Environment.GetEnvironmentVariable(
            "TWINCAT_GATEWAY_COMMAND")
        ?? "twincat-gateway",
};
builder.Services.AddSingleton(gatewayOptions);
builder.Services.AddSingleton<GatewayMcpRuntime>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<TwinCatTools>()
    .WithResources<TwinCatResources>();

await builder.Build().RunAsync();

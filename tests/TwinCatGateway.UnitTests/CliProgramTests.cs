using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Cli;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class CliProgramTests
{
    private static readonly string[] ExpectedChangedPaths =
    {
        @"C:\Project\MAIN.TcPOU",
    };

    private static readonly string[] CustomPipeStatusArguments =
    {
        "--pipe",
        "custom-pipe",
        "status",
    };

    [Fact]
    public async Task StatusWritesGatewayJson()
    {
        FakeClient client = new()
        {
            StatusResponse = new GatewayStateSnapshot { State = GatewayProcessState.Ready },
        };

        CliResult result = await RunAsync(
            client,
            "status");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        using JsonDocument json =
            JsonDocument.Parse(result.Output);
        Assert.Equal(
            "ready",
            json.RootElement.GetProperty("state")
                .GetString());
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task XaeMessagesMapsBoundedRead()
    {
        FakeClient client = new()
        {
            ResourceResponse = new ResourceContent
            {
                Uri = "twincat-xae://profile/fixture/messages/current",
                ContentType = "application/json",
                Content = "{\"solution\":\"C:\\\\Project\\\\Fixture.sln\"}",
            },
        };

        CliResult result = await RunAsync(
            client,
            "xae-messages",
            "--profile",
            "fixture");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        Assert.Equal("twincat-xae://profile/fixture/messages/current", client.ResourceUri);
        Assert.Contains(
            "Fixture.sln",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildMapsOptionsAndWaitsForCompletion()
    {
        FakeClient client = new()
        {
            BuildResponse = Operation("build-1", OperationState.Succeeded, new XaeBuildResult { Ok = true }),
        };

        CliResult result = await RunAsync(
            client,
            "build",
            "--profile",
            "fixture",
            "--action",
            "clean",
            "--changed",
            @"C:\Project\MAIN.TcPOU",
            "--scope",
            "plc");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        Assert.Equal("fixture", client.Build?.Profile);
        Assert.Equal(BuildAction.Clean, client.Build?.Action);
        Assert.Equal(XaeBuildScope.Plc, client.Build?.Scope);
        Assert.Equal(
            ExpectedChangedPaths,
            client.Build?.ChangedPaths);
    }

    [Fact]
    public async Task FailedBuildReturnsNonZeroExitCode()
    {
        FakeClient client = new()
        {
            BuildResponse = Operation(
                "build-1",
                OperationState.Failed,
                new XaeBuildResult { Ok = false, Counts = new DiagnosticCounts { Errors = 1 } }),
        };

        CliResult result = await RunAsync(
            client,
            "build",
            "--profile",
            "fixture");

        Assert.Equal(
            CliProgram.OperationFailedExitCode,
            result.ExitCode);
        using JsonDocument json =
            JsonDocument.Parse(result.Output);
        Assert.Equal(
            "failed",
            json.RootElement.GetProperty("completion")
                .GetString());
    }

    [Fact]
    public async Task SucceededBuildWithoutResultFailsClosed()
    {
        FakeClient client = new()
        {
            BuildResponse = Operation("build-1", OperationState.Succeeded),
        };

        CliResult result = await RunAsync(
            client,
            "build",
            "--profile",
            "fixture");

        Assert.Equal(
            CliProgram.OperationFailedExitCode,
            result.ExitCode);
    }

    [Fact]
    public async Task InvalidCommandReturnsUsageExitCode()
    {
        CliResult result = await RunAsync(
            new FakeClient(),
            "unknown");

        Assert.Equal(
            CliProgram.UsageExitCode,
            result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Unknown command", result.Error);
        Assert.Contains("Usage:", result.Error);
    }

    [Fact]
    public async Task GlobalPipeIsPassedToClientFactory()
    {
        string? selectedPipe = null;
        using StringWriter output = new();
        using StringWriter error = new();
        FakeClient client = new()
        {
            StatusResponse = new GatewayStateSnapshot(),
        };

        int exitCode = await CliProgram.RunAsync(
            CustomPipeStatusArguments,
            pipeName =>
            {
                selectedPipe = pipeName;
                return client;
            },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(
            CliProgram.SuccessExitCode,
            exitCode);
        Assert.Equal("custom-pipe", selectedPipe);
    }

    private static async Task<CliResult> RunAsync(
        FakeClient client,
        params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await CliProgram.RunAsync(
            args,
            _ => client,
            output,
            error,
            CancellationToken.None);
        return new CliResult(
            exitCode,
            output.ToString(),
            error.ToString());
    }

    private static OperationResult<XaeBuildResult> Operation(
            string operationId,
            OperationState state,
            XaeBuildResult? result = null)
    {
        return new OperationResult<XaeBuildResult>
        {
            Ok = state == OperationState.Succeeded,
            OperationId = operationId,
            Completion = state == OperationState.Succeeded
                ? OperationCompletion.Succeeded
                : OperationCompletion.Failed,
            Result = result,
        };
    }

    private sealed class CliResult
    {
        public CliResult(
            int exitCode,
            string output,
            string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }

        public int ExitCode { get; }

        public string Output { get; }

        public string Error { get; }
    }

    private sealed class FakeClient : GatewayClientStub
    {
        public GatewayStateSnapshot
            StatusResponse { get; set; } =
                new();

        public OperationResult<XaeBuildResult> BuildResponse { get; set; } =
            Operation("build-1", OperationState.Succeeded, new XaeBuildResult { Ok = true });

        public ResourceContent ResourceResponse { get; set; } = new();

        public XaeBuildParameters? Build { get; private set; }

        public string? ResourceUri { get; private set; }

        public override Task<GatewayStateSnapshot> GetGatewayStateAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StatusResponse);
        }

        public override Task<OperationResult<XaeBuildResult>> BuildXaeAsync(
                XaeBuildParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Build = parameters;
            return Task.FromResult(BuildResponse);
        }

        public override Task<ResourceContent> GetResourceAsync(
                string uri,
                int maximumCharacters = 64 * 1024,
                long offset = 0,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceUri = uri;
            return Task.FromResult(ResourceResponse);
        }
    }
}

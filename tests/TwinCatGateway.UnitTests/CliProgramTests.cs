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
            StatusResponse =
                new GatewayResponse<GatewayStatusResult>
                {
                    Ok = true,
                    Result = new GatewayStatusResult
                    {
                        Gateway = new GatewayStatus
                        {
                            State = GatewayState.Ready,
                        },
                    },
                },
        };

        CliResult result = await RunAsync(
            client,
            "status");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        using JsonDocument json =
            JsonDocument.Parse(result.Output);
        Assert.True(
            json.RootElement.GetProperty("ok")
                .GetBoolean());
        Assert.Equal(
            "ready",
            json.RootElement.GetProperty("result")
                .GetProperty("gateway")
                .GetProperty("state")
                .GetString());
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task XaeMessagesMapsBoundedRead()
    {
        FakeClient client = new()
        {
            XaeMessagesResponse =
                new GatewayResponse<XaeMessagesResult>
                {
                    Ok = true,
                    Result = new XaeMessagesResult
                    {
                        Solution =
                            @"C:\Project\Fixture.sln",
                    },
                },
        };

        CliResult result = await RunAsync(
            client,
            "xae-messages",
            "--max-messages",
            "7");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        Assert.Equal(
            7,
            client.XaeMessages?.MaximumMessages);
        Assert.Contains(
            @"C:\\Project\\Fixture.sln",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildMapsOptionsAndWaitsForCompletion()
    {
        FakeClient client = new()
        {
            BuildAccepted =
                Accepted("build-1"),
        };
        client.BuildOperations.Enqueue(
            Operation(
                "build-1",
                OperationState.Running));
        client.BuildOperations.Enqueue(
            Operation(
                "build-1",
                OperationState.Succeeded,
                new BuildResult
                {
                    Ok = true,
                    Counts = new DiagnosticCounts(),
                }));

        CliResult result = await RunAsync(
            client,
            "build",
            "--profile",
            "fixture",
            "--action",
            "clean",
            "--changed",
            @"C:\Project\MAIN.TcPOU",
            "--timeout",
            "5");

        Assert.Equal(
            CliProgram.SuccessExitCode,
            result.ExitCode);
        Assert.Equal("fixture", client.Build?.Profile);
        Assert.Equal(BuildAction.Clean, client.Build?.Action);
        Assert.Equal(5, client.Build?.TimeoutSeconds);
        Assert.Equal(
            ExpectedChangedPaths,
            client.Build?.ChangedPaths);
        Assert.Equal(2, client.OperationReads);
    }

    [Fact]
    public async Task FailedBuildReturnsNonZeroExitCode()
    {
        FakeClient client = new()
        {
            BuildAccepted = Accepted("build-1"),
        };
        client.BuildOperations.Enqueue(
            Operation(
                "build-1",
                OperationState.Failed,
                new BuildResult
                {
                    Ok = false,
                    Counts = new DiagnosticCounts
                    {
                        Errors = 1,
                    },
                }));

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
            json.RootElement.GetProperty("result")
                .GetProperty("operation")
                .GetProperty("state")
                .GetString());
    }

    [Fact]
    public async Task SucceededBuildWithoutResultFailsClosed()
    {
        FakeClient client = new()
        {
            BuildAccepted = Accepted("build-1"),
        };
        client.BuildOperations.Enqueue(
            Operation(
                "build-1",
                OperationState.Succeeded));

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
            StatusResponse =
                new GatewayResponse<GatewayStatusResult>
                {
                    Ok = true,
                    Result = new GatewayStatusResult(),
                },
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

    private static GatewayResponse<OperationAccepted>
        Accepted(string operationId)
    {
        return new GatewayResponse<OperationAccepted>
        {
            Ok = true,
            Result = new OperationAccepted
            {
                OperationId = operationId,
            },
        };
    }

    private static GatewayResponse<
        OperationDetails<BuildResult>> Operation(
            string operationId,
            OperationState state,
            BuildResult? result = null)
    {
        return new GatewayResponse<
            OperationDetails<BuildResult>>
        {
            Ok = true,
            Result = new OperationDetails<BuildResult>
            {
                Operation = new OperationSummary
                {
                    OperationId = operationId,
                    State = state,
                },
                Result = result,
            },
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

    private sealed class FakeClient : ITwinCatGatewayClient
    {
        public GatewayResponse<GatewayStatusResult>
            StatusResponse { get; set; } =
                new()
                {
                    Ok = true,
                    Result = new GatewayStatusResult(),
                };

        public GatewayResponse<OperationAccepted>
            BuildAccepted { get; set; } =
                Accepted("build-1");

        public GatewayResponse<XaeMessagesResult>
            XaeMessagesResponse { get; set; } =
                new()
                {
                    Ok = true,
                    Result = new XaeMessagesResult(),
                };

        public Queue<GatewayResponse<
            OperationDetails<BuildResult>>>
            BuildOperations { get; } = new();

        public BuildParameters? Build { get; private set; }

        public GetXaeMessagesParameters? XaeMessages
        {
            get;
            private set;
        }

        public int OperationReads { get; private set; }

        public Task<GatewayResponse<GatewayStatusResult>>
            GetStatusAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StatusResponse);
        }

        public Task<GatewayResponse<GatewayShutdownResult>>
            ShutdownAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartBuildAsync(
                BuildParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Build = parameters;
            return Task.FromResult(BuildAccepted);
        }

        public Task<
            GatewayResponse<OperationDetails<TResult>>>
            GetOperationAsync<TResult>(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationReads++;
            object response = BuildOperations.Dequeue();
            return Task.FromResult(
                (GatewayResponse<
                    OperationDetails<TResult>>)response);
        }

        public Task<GatewayResponse<HealthResult>>
            GetHealthAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<XaeMessagesResult>>
            GetXaeMessagesAsync(
                GetXaeMessagesParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XaeMessages = parameters;
            return Task.FromResult(XaeMessagesResponse);
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                GetDiagnosticsParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartActivationAsync(
                ActivateParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<CancelOperationResult>>
            CancelOperationAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<ResourceContent>>
            GetResourceAsync(
                string uri,
                int maximumCharacters = 64 * 1024,
                long offset = 0,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

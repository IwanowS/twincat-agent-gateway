using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class DesktopGatewayHostTests
{
    private static readonly string[] UnknownArguments =
        { "--unknown" };
    private static readonly string[] AgentLaunchArguments =
        { "--launch-source", "agent" };
    private static readonly Type[] ConfigurationContractTypes =
    {
        typeof(GatewayConfiguration),
        typeof(GatewayUiConfiguration),
        typeof(AgentProcessControlConfiguration),
        typeof(ProjectProfile),
        typeof(TargetIdentity),
        typeof(TcUnitProfile),
    };

    [Fact]
    public void HostOptionsParseLaunchIdentityAndUiOverride()
    {
        using TemporaryDirectory temporary = new();
        string configurationPath = Path.Combine(
            temporary.Path,
            GatewayConfigurationDiscovery.FileName);
        File.WriteAllText(configurationPath, "{}");

        GatewayHostOptions options =
            GatewayHostOptions.FromArguments(
                new[]
                {
                    "--config",
                    configurationPath,
                    "--launch-source",
                    "agent",
                    "--ui-mode",
                    "tray",
                },
                temporary.Path);

        Assert.Equal(
            Path.GetFullPath(configurationPath),
            options.ConfigurationPath);
        Assert.Equal(
            GatewayLaunchSource.Agent,
            options.LaunchSource);
        Assert.Equal(
            GatewayUiMode.Tray,
            options.UiModeOverride);
    }

    [Fact]
    public void HostOptionsRejectUnknownArguments()
    {
        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayHostOptions.FromArguments(
                    UnknownArguments,
                    Environment.CurrentDirectory));

        Assert.Equal(
            ErrorCodes.RequestInvalid,
            exception.Code);
        Assert.Equal(
            "gateway.arguments",
            exception.Stage);
    }

    [Fact]
    public void ManualLaunchWithoutConfigurationEntersSetupMode()
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(
            Path.Combine(temporary.Path, ".git"));

        GatewayHostOptions options =
            GatewayHostOptions.FromArguments(
                Array.Empty<string>(),
                temporary.Path);

        Assert.Null(options.ConfigurationPath);
        Assert.Equal(
            GatewayLaunchSource.Manual,
            options.LaunchSource);
    }

    [Fact]
    public void AgentLaunchWithoutConfigurationFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(
            Path.Combine(temporary.Path, ".git"));

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayHostOptions.FromArguments(
                    AgentLaunchArguments,
                    temporary.Path));

        Assert.Equal(
            ErrorCodes.GatewayConfigNotFound,
            exception.Code);
    }

    [Fact]
    public void ExplicitMissingConfigurationFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        string missingPath = Path.Combine(
            temporary.Path,
            "missing.json");

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayHostOptions.FromArguments(
                    new[]
                    {
                        "--config",
                        missingPath,
                    },
                    temporary.Path));

        Assert.Equal(
            ErrorCodes.GatewayConfigNotFound,
            exception.Code);
    }

    [Fact]
    public void DesktopOutputContainsCanonicalSetupInstructions()
    {
        string instructions =
            SetupInstructionsProvider.Read(
                AppContext.BaseDirectory);

        Assert.Contains(
            "twincat-gateway.json",
            instructions);
        Assert.Contains(
            "gateway_start once",
            instructions);
        foreach (Type contractType in ConfigurationContractTypes)
        {
            foreach (System.Reflection.PropertyInfo property in
                contractType.GetProperties())
            {
                string jsonName =
                    char.ToLowerInvariant(property.Name[0])
                    + property.Name.Substring(1);
                Assert.Contains(
                    $"`{jsonName}`",
                    instructions);
            }
        }

        Assert.False(
            string.IsNullOrWhiteSpace(
                GatewayProductVersion.Value));
    }

    [Fact]
    public void DesktopViewModelFailsClosedWithoutConnectedXae()
    {
        using TemporaryDirectory temporary = new();
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "defaultProfile": "fixture",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });

        MainWindowViewModel viewModel = new(host);

        Assert.False(viewModel.CanStartOperation);
        Assert.False(viewModel.CanActivate);
        Assert.True(viewModel.CanReconnect);
        Assert.Equal(
            BuildAction.Rebuild,
            viewModel.SelectedBuildAction);
        Assert.Equal(
            GatewayProductVersion.DisplayText,
            viewModel.Version);
        Assert.Equal("Not run", viewModel.LastBuild);
        Assert.Equal("Not run", viewModel.LastActivation);
        Assert.Equal("Not run", viewModel.LastTest);
        Assert.Throws<InvalidOperationException>(
            () => viewModel.StartActivation());
    }

    [Fact]
    public void RecentOperationRefreshPreservesExistingRows()
    {
        OperationStore store = new();
        DateTimeOffset queuedAt =
            new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
        store.AddQueued(
            "operation-1",
            OperationKind.Build,
            queuedAt);
        ObservableCollection<OperationRow> rows = new();

        MainWindowViewModel.SynchronizeRecentOperations(
            rows,
            store.GetRecent(20));
        OperationRow selectedRow = Assert.Single(rows);

        Assert.True(
            store.TryMarkRunning(
                "operation-1",
                queuedAt.AddSeconds(1)));
        store.AddQueued(
            "operation-2",
            OperationKind.Activate,
            queuedAt.AddSeconds(2));
        MainWindowViewModel.SynchronizeRecentOperations(
            rows,
            store.GetRecent(20));

        Assert.Equal(2, rows.Count);
        Assert.Equal("operation-2", rows[0].OperationId);
        Assert.Same(selectedRow, rows[1]);
        Assert.Equal("Running", selectedRow.State);
    }

    [Fact]
    public void EventJournalRowsKeepOldestAtTopAndAppendNewestAtBottom()
    {
        ObservableCollection<EventRow> rows = new();
        DateTimeOffset occurredAt =
            new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
        GatewayEvent first = new()
        {
            Cursor = 1,
            OccurredAtUtc = occurredAt,
            Type = "gateway.started",
            Severity = DiagnosticSeverity.Info,
            Message = "Gateway started.",
        };
        GatewayEvent second = new()
        {
            Cursor = 2,
            OccurredAtUtc = occurredAt.AddSeconds(1),
            Type = "ui.failure",
            Severity = DiagnosticSeverity.Error,
            Message = "Refresh failed.",
            Error = new GatewayError
            {
                Code = ErrorCodes.UiFailure,
                Message = "Refresh failed.",
                Details =
                    "System.InvalidOperationException: Refresh failed.",
            },
            Properties = new Dictionary<string, string>
            {
                ["exceptionType"] =
                    "System.InvalidOperationException",
            },
        };

        MainWindowViewModel.SynchronizeEvents(
            rows,
            "stream-1",
            new[] { first });
        EventRow firstRow = Assert.Single(rows);
        MainWindowViewModel.SynchronizeEvents(
            rows,
            "stream-1",
            new[] { first, second });

        Assert.Equal(2, rows.Count);
        Assert.Same(firstRow, rows[0]);
        Assert.Equal(2, rows[1].Cursor);
        Assert.Equal(ErrorCodes.UiFailure, rows[1].Code);
        Assert.Equal("Refresh failed.", rows[1].Description);
        Assert.Equal(
            "System.InvalidOperationException",
            rows[1].Exception);
    }

    [Fact]
    public void ManualReconnectIsPublishedWithoutCallingCom()
    {
        using TemporaryDirectory temporary = new();
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "defaultProfile": "fixture",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });

        host.RequestXaeReconnect();

        Assert.True(host.CanReconnectXae);
        Assert.Contains(
            host.ApplicationService.GetDiagnostics().Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes
                .XaeReconnectRequested);
    }

    [Fact]
    public async Task ShutdownRequestIsDeniedByDefault()
    {
        using TemporaryDirectory temporary = new();
        string pipeName =
            "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        using GatewayDesktopHost host =
            CreateShutdownHost(
                temporary,
                pipeName,
                allowShutdown: false);
        host.Start();
        NamedPipeGatewayClient client =
            new(
                pipeName,
                TimeSpan.FromSeconds(5));

        GatewayResponse<GatewayShutdownResult> response =
            await client.SendAsync<
                EmptyParameters,
                GatewayShutdownResult>(
                GatewayMethods.Shutdown,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.GatewayShutdownDisabled,
            response.Error?.Code);
    }

    [Fact]
    public async Task AllowedShutdownIsRaisedAfterSuccessfulResponse()
    {
        using TemporaryDirectory temporary = new();
        string pipeName =
            "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        using GatewayDesktopHost host =
            CreateShutdownHost(
                temporary,
                pipeName,
                allowShutdown: true);
        TaskCompletionSource<bool> requested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.ShutdownRequested +=
            (_, _) => requested.TrySetResult(true);
        host.Start();
        NamedPipeGatewayClient client =
            new(
                pipeName,
                TimeSpan.FromSeconds(5));

        GatewayResponse<GatewayShutdownResult> response =
            await client.SendAsync<
                EmptyParameters,
                GatewayShutdownResult>(
                GatewayMethods.Shutdown,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);
        Task completed = await Task.WhenAny(
            requested.Task,
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(response.Ok);
        Assert.True(response.Result?.ShutdownRequested);
        Assert.Same(requested.Task, completed);
    }

    [Fact]
    public void SingleInstanceGuardRejectsSecondOwnerForCurrentUser()
    {
        string name = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");

        bool firstAcquired = SingleInstanceGuard.TryAcquire(name, out SingleInstanceGuard? first);
        bool secondAcquired = SingleInstanceGuard.TryAcquire(name, out SingleInstanceGuard? second);

        second?.Dispose();
        first?.Dispose();

        Assert.True(firstAcquired);
        Assert.False(secondAcquired);
    }

    [Fact]
    public async Task DesktopHostServesStatusFromValidatedConfiguration()
    {
        using TemporaryDirectory temporary = new();
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(temporary.Path, "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
                LaunchSource = GatewayLaunchSource.Agent,
                UiModeOverride = GatewayUiMode.Tray,
            });
        host.Start();
        NamedPipeGatewayClient client = new(pipeName);
        await WaitForStateAsync(
            host,
            GatewayState.Disconnected,
            TimeSpan.FromSeconds(10));
        await WaitForErrorCodeAsync(
            host,
            ErrorCodes.XaeNotFound,
            TimeSpan.FromSeconds(10));

        GatewayResponse<GatewayStatusResult> response =
            await client.SendAsync<EmptyParameters, GatewayStatusResult>(
                GatewayMethods.Status,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);
        GatewayResponse<GatewayDiagnosticsResult> diagnostics =
            await client.SendAsync<
                GetDiagnosticsParameters,
                GatewayDiagnosticsResult>(
                GatewayMethods.GetDiagnostics,
                new GetDiagnosticsParameters
                {
                    AfterEventCursor = 0,
                },
                wait: true,
                CancellationToken.None);

        await host.StopAsync();

        Assert.True(response.Ok);
        Assert.Equal(GatewayState.Disconnected, response.Result?.Gateway.State);
        Assert.True(response.Result?.Gateway.Ready);
        Assert.Equal(
            Path.GetFullPath(configurationPath),
            response.Result?.Gateway.ConfigurationPath,
            ignoreCase: true);
        Assert.Equal(
            "fixture",
            response.Result?.Gateway.ActiveProfile);
        Assert.Equal(
            solutionPath,
            response.Result?.Gateway.SolutionPath,
            ignoreCase: true);
        Assert.Equal(
            GatewayLaunchSource.Agent,
            response.Result?.Gateway.LaunchSource);
        Assert.Equal(
            GatewayUiMode.Tray,
            response.Result?.Gateway.UiMode);
        Assert.Equal("fixture", host.ActiveProfile?.Name);
        Assert.Null(host.StartupError);
        Assert.True(response.Result?.LatestEventCursor > 0);
        Assert.True(diagnostics.Ok);
        Assert.True(
            diagnostics.Result?.LatestEventCursor
                >= response.Result?.LatestEventCursor);
        Assert.Equal(
            diagnostics.Result?.LatestEventCursor,
            diagnostics.Result?.NextScanCursor);
        Assert.Equal(
            GatewayEventTypes.GatewayStarted,
            diagnostics.Result?.Events[0].Type);
        Assert.Contains(
            diagnostics.Result!.Events,
            gatewayEvent =>
                gatewayEvent.Error?.Code
                    == ErrorCodes.XaeNotFound);
    }

    [XaeFact]
    public async Task DesktopHostPublishesConnectedXaeDiagnostics()
    {
        using TemporaryDirectory temporary = new();
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);

        GatewayResponse<GatewayDiagnosticsResult> response =
            await client.SendAsync<EmptyParameters, GatewayDiagnosticsResult>(
                GatewayMethods.GetDiagnostics,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);

        await host.StopAsync();

        Assert.True(response.Ok);
        Assert.True(response.Result?.Status.Xae.Connected);
        Assert.True(response.Result?.Xae.SysManagerAvailable);
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.ActiveConfiguration));
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.ActivePlatform));
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.Target?.AmsNetId));
        Assert.Empty(
            response.Result!.Xae.InspectionIssues);
        Assert.Equal(
            AdsRuntimeStatusReader.SystemServicePort,
            response.Result.Runtime.Port);
        Assert.Equal(
            "192.168.3.31.1.1",
            response.Result.Runtime.AmsNetId);
        Assert.Null(response.Result.Runtime.ErrorCode);
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result.Runtime.AdsState));
        Assert.NotEqual(
            RuntimeMode.Unknown,
            response.Result.Status.TwinCat.Mode);
        Assert.Contains(
            response.Result.Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes.XaeConnected);
        Assert.Contains(
            response.Result.Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes.RuntimeStateChanged);
        Assert.Contains(
            response.Result!.DteInstances,
            instance => instance.Selected
                && string.Equals(
                    instance.Solution,
                    solutionPath,
                    StringComparison.OrdinalIgnoreCase));
    }

    [XaeFact]
    public async Task DesktopHostBuildCompletesThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string pipeName =
            "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);
        OperationAccepted synchronization =
            host.ApplicationService.StartSynchronization(
                new SynchronizeParameters
                {
                    Profile = "fixture",
                    TimeoutSeconds = 60,
                },
                agentRequest: false);
        OperationDetails<SynchronizeResult> synchronized =
            await WaitForOperationAsync<SynchronizeResult>(
                client,
                synchronization.OperationId,
                TimeSpan.FromSeconds(75));
        Assert.Equal(
            OperationState.Succeeded,
            synchronized.Operation.State);

        GatewayResponse<OperationAccepted> accepted =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = BuildAction.Build,
                    TimeoutSeconds = 60,
                },
                wait: false,
                CancellationToken.None);
        Assert.True(accepted.Ok);
        Assert.NotNull(accepted.Result);
        OperationDetails<BuildResult> completed =
            await WaitForOperationAsync<BuildResult>(
                client,
                accepted.Result!.OperationId,
                TimeSpan.FromSeconds(75));
        Assert.NotNull(completed.Result?.Log);
        GatewayResponse<ResourceContent> log =
            await client.SendAsync<
                GetResourceParameters,
                ResourceContent>(
                GatewayMethods.GetResource,
                new GetResourceParameters
                {
                    Uri = completed.Result!.Log!.Uri,
                    MaximumCharacters = 64 * 1024,
                },
                wait: true,
                CancellationToken.None);
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = Assert.IsType<int>(
            Assert.Single(
                diagnostics.DteInstances,
                instance => instance.Selected)
                .ProcessId);

        await host.StopAsync();

        Assert.Equal(
            OperationState.Succeeded,
            completed.Operation.State);
        Assert.True(completed.Result?.Ok);
        Assert.Equal(BuildAction.Build, completed.Result?.Action);
        Assert.Equal(0, completed.Result?.Counts.Errors);
        Assert.Equal(
            ResourceKind.BuildLog,
            Assert.Single(
                completed.Operation.Resources).Kind);
        Assert.True(log.Ok);
        Assert.False(
            string.IsNullOrWhiteSpace(
                log.Result?.Content));
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.BuildQueued,
                GatewayEventTypes.BuildStarted,
                GatewayEventTypes.BuildSucceeded,
            },
            diagnostics.Events
                .Where(gatewayEvent =>
                    string.Equals(
                        gatewayEvent.OperationId,
                        accepted.Result.OperationId,
                        StringComparison.Ordinal))
                .Select(gatewayEvent => gatewayEvent.Type));
    }

    private static async Task<OperationDetails<TResult>>
        WaitForOperationAsync<TResult>(
            NamedPipeGatewayClient client,
            string operationId,
            TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GatewayResponse<OperationDetails<TResult>> response =
                await client.SendAsync<
                    GetOperationParameters,
                    OperationDetails<TResult>>(
                    GatewayMethods.GetOperation,
                    new GetOperationParameters
                    {
                        OperationId = operationId,
                    },
                    wait: true,
                    CancellationToken.None);
            Assert.True(response.Ok);
            Assert.NotNull(response.Result);
            switch (response.Result!.Operation.State)
            {
                case OperationState.Queued:
                case OperationState.Running:
                    await Task.Delay(100);
                    continue;
                default:
                    return response.Result;
            }
        }

        throw new TimeoutException(
            $"Operation '{operationId}' did not complete.");
    }

    private static async Task WaitForErrorCodeAsync(
        GatewayDesktopHost host,
        string errorCode,
        TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GatewayDiagnosticsResult diagnostics =
                host.ApplicationService.GetDiagnostics();
            if (diagnostics.Events.Any(
                gatewayEvent =>
                    gatewayEvent.Error?.Code == errorCode))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Gateway did not publish error '{errorCode}'.");
    }

    private static async Task WaitForStateAsync(
        GatewayDesktopHost host,
        GatewayState expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (host.ApplicationService.GetStatus().Gateway.State == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        throw new TimeoutException(
            $"Gateway did not reach {expected}; current state is {status.Gateway.State}.");
    }

    private static string EscapeJson(string value)
    {
        return value.Replace(@"\", @"\\").Replace(@"""", @"\""");
    }

    private static GatewayDesktopHost CreateShutdownHost(
        TemporaryDirectory temporary,
        string pipeName,
        bool allowShutdown)
    {
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "defaultProfile": "fixture",
              "agentProcessControl": {
                "allowStart": true,
                "allowShutdown": {{allowShutdown.ToString().ToLowerInvariant()}}
              },
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        return new GatewayDesktopHost(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

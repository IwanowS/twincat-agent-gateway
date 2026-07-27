using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;

namespace TwinCatGateway.Desktop;

internal sealed class XaeSessionCoordinator : IDisposable
{
    private static readonly TimeSpan AttachTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectInterval =
        TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly ProjectProfile _profile;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly StructuredFileLogger _logger;
    private readonly LocalLogStore _logs;
    private readonly XaeSession _session = new();
    private XaeSessionSnapshot _lastSnapshot = new();
    private ComDiagnostics _lastComDiagnostics = new();
    private string? _lastErrorMessage;
    private int? _lastHResult;
    private string? _lastFailureSignature;
    private bool _wasConnected;
    private int _disposed;

    public XaeSessionCoordinator(
        ProjectProfile profile,
        GatewayStatusSnapshotStore status,
        StructuredFileLogger logger,
        LocalLogStore logs)
    {
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool connected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HasActiveChangingOperation())
            {
                await DelayAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (!connected)
                {
                    PublishAttaching();
                }

                XaeSessionSnapshot snapshot = connected
                    ? await _session.VerifyAttachedAsync(
                        _profile.Solution,
                        HealthTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await _session.EnsureAttachedAsync(
                        _profile.Solution,
                        _profile.AllowXaeLaunch,
                        _profile.XaeProgId,
                        AttachTimeout,
                        cancellationToken).ConfigureAwait(false);
                connected = true;
                PublishConnected(snapshot);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                connected = false;
                XaeSessionSnapshot snapshot =
                    await ReadSnapshotAfterFailureAsync().ConfigureAwait(false);
                PublishFailure(snapshot, exception);
                await TryDisconnectAsync().ConfigureAwait(false);
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public GatewayDiagnosticsResult CreateDiagnostics()
    {
        lock (_sync)
        {
            return new GatewayDiagnosticsResult
            {
                DteInstances = _lastSnapshot.DiscoveredInstances
                    .Select(CloneInfo)
                    .ToList(),
                Xae = new XaeDiagnostics
                {
                    SysManagerAvailable =
                        _lastSnapshot.SysManagerAvailable,
                    LastErrorMessages = _lastErrorMessage is null
                        ? new List<string>()
                        : new List<string> { _lastErrorMessage },
                    LastHResult = _lastHResult,
                },
                Com = CloneCom(_lastComDiagnostics),
            };
        }
    }

    public async Task<BuildResult> ExecuteBuildAsync(
        string operationId,
        BuildParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(parameters.Profile)
            && !string.Equals(
                parameters.Profile,
                _profile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' is not active.",
                stage: "build.validate");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Configuration)
            || !string.IsNullOrWhiteSpace(parameters.Platform))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Explicit build configuration and platform selection "
                + "are not implemented yet.",
                stage: "build.validate");
        }

        TimeSpan timeout = TimeSpan.FromSeconds(
            parameters.TimeoutSeconds ?? 120);
        XaeBuildExecutionResult execution =
            await _session.ExecuteBuildAsync(
                parameters.Action,
                parameters.ChangedPaths,
                timeout,
                cancellationToken).ConfigureAwait(false);
        List<BuildDiagnostic> diagnostics =
            execution.Diagnostics.ToList();
        int errors = diagnostics.Count(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        if (execution.FailedProjects > 0
            && errors == 0)
        {
            diagnostics.Add(
                new BuildDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = "xae-build",
                    Message = execution.FailedProjects == 1
                        ? "One project failed to build; XAE Error List "
                            + "did not expose compiler diagnostics."
                        : $"{execution.FailedProjects} projects failed "
                            + "to build; XAE Error List did not expose "
                            + "compiler diagnostics.",
                });
            errors = 1;
        }

        int warnings = diagnostics.Count(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning);
        const int maximumDiagnostics = 50;
        ResourceReference log = _logs.WriteText(
            operationId,
            ResourceKind.BuildLog,
            FormatBuildOutput(execution.Output));
        BuildResult result = new()
        {
            Ok = execution.FailedProjects == 0
                && errors == 0,
            OperationId = operationId,
            Action = execution.Action,
            DurationMs = execution.DurationMs,
            Counts = new DiagnosticCounts
            {
                Errors = errors,
                Warnings = warnings,
            },
            Diagnostics = diagnostics
                .Take(maximumDiagnostics)
                .ToList(),
            MoreDiagnostics = Math.Max(
                0,
                diagnostics.Count - maximumDiagnostics),
            Log = log,
        };
        _logger.Write(
            result.Ok
                ? StructuredLogLevel.Information
                : StructuredLogLevel.Warning,
            "xae.build.completed",
            result.Ok
                ? "XAE build completed successfully."
                : "XAE build completed with errors.",
            operationId,
            properties: new Dictionary<string, string>
            {
                ["action"] = result.Action.ToString(),
                ["failedProjects"] =
                    execution.FailedProjects.ToString(
                        CultureInfo.InvariantCulture),
                ["errors"] = errors.ToString(
                    CultureInfo.InvariantCulture),
                ["warnings"] = warnings.ToString(
                    CultureInfo.InvariantCulture),
                ["synchronizedFiles"] =
                    execution.Synchronization
                        .SynchronizedDocuments.Count.ToString(
                            CultureInfo.InvariantCulture),
            });
        return result;
    }

    private static string FormatBuildOutput(
        IReadOnlyList<XaeOutputDelta> output)
    {
        if (output.Count == 0)
        {
            return "XAE produced no Output pane delta for this operation."
                + Environment.NewLine;
        }

        StringBuilder text = new();
        foreach (XaeOutputDelta pane in output)
        {
            text.Append("=== Output pane: ");
            text.Append(pane.PaneName);
            if (!string.IsNullOrWhiteSpace(pane.PaneGuid))
            {
                text.Append(" [");
                text.Append(pane.PaneGuid);
                text.Append(']');
            }

            text.AppendLine(" ===");
            text.Append(pane.Text);
            if (!pane.Text.EndsWith(
                Environment.NewLine,
                StringComparison.Ordinal))
            {
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
    }

    private bool HasActiveChangingOperation()
    {
        OperationSummary? operation = _status.Read().CurrentOperation;
        return operation is not null
            && (operation.State == OperationState.Queued
                || operation.State == OperationState.Running);
    }

    private void PublishAttaching()
    {
        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping)
            {
                status.Gateway.State = GatewayState.Attaching;
            }

            return status;
        });
    }

    private void PublishConnected(XaeSessionSnapshot snapshot)
    {
        ComDiagnostics diagnostics = _session.GetComDiagnostics();
        bool logConnection;
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            _lastComDiagnostics = CloneCom(diagnostics);
            _lastErrorMessage = null;
            _lastHResult = null;
            _lastFailureSignature = null;
            logConnection = !_wasConnected;
            _wasConnected = true;
        }

        DteInstanceInfo selected = snapshot.SelectedInstance
            ?? throw new InvalidOperationException(
                "A connected XAE snapshot has no selected instance.");
        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping
                && status.CurrentOperation is null)
            {
                status.Gateway.State = GatewayState.Ready;
            }

            status.Xae.Connected = true;
            status.Xae.Version = selected.Version;
            status.Xae.Solution = selected.Solution;
            status.Xae.AgentWorkspaceOwned =
                snapshot.AgentWorkspaceOwned;
            return status;
        });
        if (logConnection)
        {
            _logger.Write(
                StructuredLogLevel.Information,
                "xae.connected",
                "Connected to the configured XAE solution.",
                properties: new Dictionary<string, string>
                {
                    ["processId"] =
                        selected.ProcessId?.ToString(
                            CultureInfo.InvariantCulture)
                        ?? "unknown",
                    ["progId"] = selected.ProgId ?? "unknown",
                    ["solution"] = selected.Solution ?? "unknown",
                    ["agentWorkspaceOwned"] =
                        snapshot.AgentWorkspaceOwned.ToString(),
                    ["closedDocumentCount"] =
                        snapshot.ClosedDocumentCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["discardedDocumentCount"] =
                        snapshot.DiscardedDocumentCount.ToString(
                            CultureInfo.InvariantCulture),
                });
        }
    }

    private void PublishFailure(
        XaeSessionSnapshot snapshot,
        Exception exception)
    {
        string? code =
            (exception as GatewayOperationException)?.Code;
        string signature =
            $"{code ?? exception.GetType().FullName}|{exception.Message}";
        bool newFailure;
        int? hResult = GetMeaningfulHResult(exception);
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            _lastComDiagnostics =
                CloneCom(_session.GetComDiagnostics());
            _lastErrorMessage = exception.Message;
            _lastHResult = hResult;
            newFailure = !string.Equals(
                _lastFailureSignature,
                signature,
                StringComparison.Ordinal);
            _lastFailureSignature = signature;
            _wasConnected = false;
        }

        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping)
            {
                status.Gateway.State =
                    code == ErrorCodes.XaeNotFound
                        ? GatewayState.Disconnected
                        : GatewayState.Faulted;
            }

            status.Xae.Connected = false;
            status.Xae.Version = null;
            status.Xae.Solution = null;
            status.Xae.AgentWorkspaceOwned = false;
            if (newFailure)
            {
                status.UnreadErrors++;
            }

            return status;
        });
        if (newFailure)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.connection.failed",
                "Could not establish or verify the configured XAE session.",
                properties: new Dictionary<string, string>
                {
                    ["code"] = code ?? "UNEXPECTED_EXCEPTION",
                    ["stage"] =
                        (exception as GatewayOperationException)?.Stage
                        ?? "xae.coordinator",
                    ["hresult"] = hResult is null
                        ? "unknown"
                        : $"0x{hResult.Value:X8}",
                },
                exception: exception);
        }
    }

    private async Task<XaeSessionSnapshot> ReadSnapshotAfterFailureAsync()
    {
        try
        {
            return await _session.GetSnapshotAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.snapshot.failed",
                "Could not read the XAE snapshot after a session failure.",
                exception: exception);
            lock (_sync)
            {
                return CloneSnapshot(_lastSnapshot);
            }
        }
    }

    private async Task TryDisconnectAsync()
    {
        try
        {
            await _session.DisconnectAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.disconnect.failed",
                "Could not release the failed XAE session.",
                exception: exception);
        }
    }

    private static int? GetMeaningfulHResult(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current is COMException
            ? current.HResult
            : (int?)null;
    }

    private static async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                ReconnectInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static XaeSessionSnapshot CloneSnapshot(
        XaeSessionSnapshot source)
    {
        return new XaeSessionSnapshot
        {
            Connected = source.Connected,
            SelectedInstance = source.SelectedInstance is null
                ? null
                : CloneInfo(source.SelectedInstance),
            SysManagerAvailable = source.SysManagerAvailable,
            LaunchedByGateway = source.LaunchedByGateway,
            AgentWorkspaceOwned = source.AgentWorkspaceOwned,
            ClosedDocumentCount = source.ClosedDocumentCount,
            DiscardedDocumentCount =
                source.DiscardedDocumentCount,
            DiscoveredInstances = source.DiscoveredInstances
                .Select(CloneInfo)
                .ToArray(),
        };
    }

    private static DteInstanceInfo CloneInfo(DteInstanceInfo source)
    {
        return new DteInstanceInfo
        {
            Moniker = source.Moniker,
            ProgId = source.ProgId,
            ProcessId = source.ProcessId,
            Version = source.Version,
            Solution = source.Solution,
            SolutionLoaded = source.SolutionLoaded,
            Selected = source.Selected,
            SelectionReason = source.SelectionReason,
            InspectionError = source.InspectionError,
            InspectionHResult = source.InspectionHResult,
        };
    }

    private static ComDiagnostics CloneCom(ComDiagnostics source)
    {
        return new ComDiagnostics
        {
            RejectedCallCount = source.RejectedCallCount,
            RetryCount = source.RetryCount,
            LastCallLatencyMs = source.LastCallLatencyMs,
            LastHResult = source.LastHResult,
        };
    }
}

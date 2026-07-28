using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

internal sealed class AdsRuntimeMonitor : IDisposable
{
    private const string SystemRuntimeName = "TwinCAT System";
    private readonly object _sync = new();
    private readonly GatewayStatusSnapshotStore _status;
    private readonly StructuredFileLogger _logger;
    private readonly IGatewayEventSink _events;
    private readonly IAdsRuntimeStatusProbe _probe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _readTimeout;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<int, string> _lastSignatures = new();
    private readonly Dictionary<int, long> _lastEventCursors = new();
    private RuntimeTarget? _target;
    private AdsRuntimeDiagnostics _systemDiagnostics = new();
    private IReadOnlyList<AdsRuntimeDiagnostics> _plcDiagnostics =
        Array.Empty<AdsRuntimeDiagnostics>();
    private RuntimeAlert? _currentAlert;
    private string? _currentAlertSignature;
    private int _disposed;

    public AdsRuntimeMonitor(
        GatewayStatusSnapshotStore status,
        StructuredFileLogger logger,
        IGatewayEventSink events,
        RuntimeMonitoringConfiguration configuration,
        IAdsRuntimeStatusProbe? probe = null)
    {
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        _events = events
            ?? throw new ArgumentNullException(nameof(events));
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        _pollInterval = TimeSpan.FromMilliseconds(
            configuration.PollIntervalMilliseconds);
        _readTimeout = TimeSpan.FromMilliseconds(
            configuration.ReadTimeoutMilliseconds);
        _probe = probe ?? new AdsRuntimeStatusProbe();
    }

    public void UpdateTarget(
        string? amsNetId,
        string? twinCatProjectPath)
    {
        if (string.IsNullOrWhiteSpace(amsNetId))
        {
            return;
        }

        IReadOnlyList<PlcRuntimeTarget> plcTargets =
            DiscoverPlcTargets(twinCatProjectPath);
        RuntimeTarget target = new(
            amsNetId!,
            plcTargets);
        bool changed;
        lock (_sync)
        {
            changed = _target is null
                || !string.Equals(
                    _target.Signature,
                    target.Signature,
                    StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }

            _target = target;
            _lastSignatures.Clear();
            _lastEventCursors.Clear();
            _systemDiagnostics = new AdsRuntimeDiagnostics
            {
                RuntimeName = SystemRuntimeName,
                AmsNetId = target.AmsNetId,
                Port = AdsRuntimeStatusReader.SystemServicePort,
            };
            _plcDiagnostics = Array.Empty<AdsRuntimeDiagnostics>();
            _currentAlert = null;
            _currentAlertSignature = null;
        }

        _status.Update(status =>
        {
            status.TwinCat.Started = null;
            status.TwinCat.Mode = RuntimeMode.Unknown;
            status.TwinCat.SystemMode = RuntimeMode.Unknown;
            status.TwinCat.ObservedAtUtc = null;
            status.TwinCat.Alert = null;
            return status;
        });
        Wake();
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RuntimeTarget? target;
                lock (_sync)
                {
                    target = _target;
                }

                if (target is not null)
                {
                    await PollAsync(
                        target,
                        cancellationToken).ConfigureAwait(false);
                }

                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown may cancel a queued ADS read.
        }
    }

    public AdsRuntimeDiagnostics GetSystemDiagnostics()
    {
        lock (_sync)
        {
            return CloneDiagnostics(_systemDiagnostics);
        }
    }

    public IReadOnlyList<AdsRuntimeDiagnostics>
        GetPlcDiagnostics()
    {
        lock (_sync)
        {
            return _plcDiagnostics
                .Select(CloneDiagnostics)
                .ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _probe.Dispose();
        _wakeSignal.Dispose();
    }

    private async Task PollAsync(
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        AdsRuntimeStatusReadResult system =
            await ReadAsync(
                target.AmsNetId,
                AdsRuntimeStatusReader.SystemServicePort,
                cancellationToken).ConfigureAwait(false);
        system.Diagnostics.RuntimeName =
            SystemRuntimeName;

        List<RuntimeObservation> observations = new()
        {
            new RuntimeObservation(
                SystemRuntimeName,
                isSystem: true,
                system),
        };
        if (system.Diagnostics.ErrorCode is null
            && (system.Status.Mode == RuntimeMode.Run
                || system.Status.Mode == RuntimeMode.Exception))
        {
            Task<RuntimeObservation>[] reads =
                target.PlcTargets
                    .Select(plc => ReadPlcAsync(
                        target.AmsNetId,
                        plc,
                        cancellationToken))
                    .ToArray();
            if (reads.Length != 0)
            {
                observations.AddRange(
                    await Task.WhenAll(reads)
                        .ConfigureAwait(false));
            }
        }

        lock (_sync)
        {
            if (_target is null
                || !string.Equals(
                    _target.Signature,
                    target.Signature,
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        PublishObservations(
            target.Signature,
            observations);
    }

    private async Task<RuntimeObservation> ReadPlcAsync(
        string amsNetId,
        PlcRuntimeTarget plc,
        CancellationToken cancellationToken)
    {
        AdsRuntimeStatusReadResult result =
            await ReadAsync(
                amsNetId,
                plc.AdsPort,
                cancellationToken).ConfigureAwait(false);
        result.Diagnostics.RuntimeName = plc.Name;
        return new RuntimeObservation(
            plc.Name,
            isSystem: false,
            result);
    }

    private Task<AdsRuntimeStatusReadResult> ReadAsync(
        string amsNetId,
        int port,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _probe.Read(
                    amsNetId,
                    port,
                    _readTimeout);
            },
            cancellationToken);
    }

    private void PublishObservations(
        string targetSignature,
        IReadOnlyList<RuntimeObservation> observations)
    {
        lock (_sync)
        {
            if (_target is null
                || !string.Equals(
                    _target.Signature,
                    targetSignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            PublishObservationsLocked(observations);
        }
    }

    private void PublishObservationsLocked(
        IReadOnlyList<RuntimeObservation> observations)
    {
        foreach (RuntimeObservation observation in observations)
        {
            string signature = CreateSignature(
                observation.Result);
            bool changed;
            RuntimeMode? previousMode = null;
            lock (_sync)
            {
                changed = !_lastSignatures.TryGetValue(
                        observation.Result.Diagnostics.Port,
                        out string? previous)
                    || !string.Equals(
                        previous,
                        signature,
                        StringComparison.Ordinal);
                if (changed)
                {
                    previousMode = TryReadMode(previous);
                    _lastSignatures[
                        observation.Result.Diagnostics.Port] =
                            signature;
                }
            }

            if (!changed)
            {
                continue;
            }

            DateTimeOffset occurredAtUtc =
                observation.Result.Diagnostics.ReadAtUtc
                ?? DateTimeOffset.UtcNow;
            GatewayEvent gatewayEvent = CreateEvent(
                observation,
                previousMode);
            long cursor = _events.Record(
                gatewayEvent,
                occurredAtUtc);
            _lastEventCursors[
                observation.Result.Diagnostics.Port] =
                    cursor;
            LogTransition(
                observation,
                previousMode);
        }

        RuntimeObservation system = observations[0];
        RuntimeObservation[] plcs = observations
            .Skip(1)
            .ToArray();
        RuntimeMode aggregateMode =
            AggregateMode(system, plcs);
        RuntimeAlert? alert = CreateAlert(
            system,
            plcs,
            _lastEventCursors);
        DateTimeOffset? observedAtUtc = observations
            .Select(observation =>
                observation.Result.Diagnostics.ReadAtUtc)
            .Where(value => value.HasValue)
            .Max();
        lock (_sync)
        {
            _systemDiagnostics =
                CloneDiagnostics(system.Result.Diagnostics);
            _plcDiagnostics = plcs
                .Select(observation =>
                    CloneDiagnostics(
                        observation.Result.Diagnostics))
                .ToArray();
            alert = PreserveAlertIdentity(alert);
            _currentAlert =
                CloneAlert(alert);
        }

        RuntimeAlert? statusAlert = CloneAlert(alert);
        _status.Update(status =>
        {
            status.TwinCat.Started =
                system.Result.Status.Started;
            status.TwinCat.Mode = aggregateMode;
            status.TwinCat.SystemMode =
                system.Result.Status.Mode;
            status.TwinCat.ObservedAtUtc =
                observedAtUtc;
            status.TwinCat.Alert = statusAlert;
            return status;
        });
    }

    private RuntimeAlert? PreserveAlertIdentity(
        RuntimeAlert? candidate)
    {
        string? signature = candidate is null
            ? null
            : string.Join(
                "|",
                candidate.Code,
                candidate.RuntimeName ?? string.Empty,
                candidate.AdsPort?.ToString(
                    CultureInfo.InvariantCulture)
                    ?? string.Empty);
        if (candidate is not null
            && string.Equals(
                signature,
                _currentAlertSignature,
                StringComparison.Ordinal)
            && _currentAlert is not null)
        {
            candidate.OccurredAtUtc =
                _currentAlert.OccurredAtUtc;
            candidate.EventCursor =
                _currentAlert.EventCursor;
        }

        _currentAlertSignature = signature;
        return candidate;
    }

    private static RuntimeMode AggregateMode(
        RuntimeObservation system,
        IReadOnlyList<RuntimeObservation> plcs)
    {
        if (system.Result.Diagnostics.ErrorCode is not null)
        {
            return RuntimeMode.Unknown;
        }

        if (system.Result.Status.Mode == RuntimeMode.Exception
            || plcs.Any(plc =>
                plc.Result.Status.Mode
                    == RuntimeMode.Exception))
        {
            return RuntimeMode.Exception;
        }

        if (system.Result.Status.Mode != RuntimeMode.Run)
        {
            return system.Result.Status.Mode;
        }

        return plcs.Any(plc =>
                plc.Result.Diagnostics.ErrorCode is not null)
            ? RuntimeMode.Unknown
            : RuntimeMode.Run;
    }

    private static RuntimeAlert? CreateAlert(
        RuntimeObservation system,
        IReadOnlyList<RuntimeObservation> plcs,
        IReadOnlyDictionary<int, long> transitionCursors)
    {
        if (system.Result.Diagnostics.ErrorCode is not null)
        {
            return CreateAlert(
                "RUNTIME_UNAVAILABLE",
                DiagnosticSeverity.Warning,
                "The selected TwinCAT runtime is unavailable.",
                system,
                transitionCursors);
        }

        RuntimeObservation? plcException = plcs
            .FirstOrDefault(plc =>
                plc.Result.Status.Mode
                    == RuntimeMode.Exception);
        if (plcException is not null)
        {
            return CreateAlert(
                "PLC_RUNTIME_EXCEPTION",
                DiagnosticSeverity.Error,
                $"PLC runtime '{plcException.Name}' is in Exception.",
                plcException,
                transitionCursors);
        }

        if (system.Result.Status.Mode == RuntimeMode.Exception)
        {
            return CreateAlert(
                "RUNTIME_EXCEPTION",
                DiagnosticSeverity.Error,
                "The TwinCAT runtime is in Exception.",
                system,
                transitionCursors);
        }

        RuntimeObservation? unavailablePlc = plcs
            .FirstOrDefault(plc =>
                plc.Result.Diagnostics.ErrorCode is not null);
        if (unavailablePlc is not null)
        {
            return CreateAlert(
                "PLC_RUNTIME_UNAVAILABLE",
                DiagnosticSeverity.Warning,
                $"PLC runtime '{unavailablePlc.Name}' is unavailable.",
                unavailablePlc,
                transitionCursors);
        }

        return null;
    }

    private static RuntimeAlert CreateAlert(
        string code,
        DiagnosticSeverity severity,
        string message,
        RuntimeObservation observation,
        IReadOnlyDictionary<int, long> transitionCursors)
    {
        int port = observation.Result.Diagnostics.Port;
        transitionCursors.TryGetValue(
            port,
            out long cursor);
        return new RuntimeAlert
        {
            Code = code,
            Severity = severity,
            Message = message,
            OccurredAtUtc =
                observation.Result.Diagnostics.ReadAtUtc
                ?? DateTimeOffset.UtcNow,
            EventCursor = cursor,
            RuntimeName = observation.Name,
            AdsPort = port,
        };
    }

    private static GatewayEvent CreateEvent(
        RuntimeObservation observation,
        RuntimeMode? previousMode)
    {
        AdsRuntimeStatusReadResult result =
            observation.Result;
        bool failed =
            result.Diagnostics.ErrorCode is not null;
        DiagnosticSeverity severity = failed
            ? DiagnosticSeverity.Warning
            : result.Status.Mode == RuntimeMode.Exception
                ? DiagnosticSeverity.Error
                : result.Status.Mode == RuntimeMode.Unknown
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Info;
        Dictionary<string, string> properties =
            CreateProperties(observation);
        properties["previousMode"] =
            previousMode?.ToString() ?? "unknown";
        return new GatewayEvent
        {
            Type = failed
                ? GatewayEventTypes.RuntimeStatusReadFailed
                : GatewayEventTypes.RuntimeStateChanged,
            Severity = severity,
            Stage = "ads.runtimeMonitor",
            Message = failed
                ? $"Could not read runtime '{observation.Name}' on ADS port {result.Diagnostics.Port}."
                : $"Runtime '{observation.Name}' changed to {result.Status.Mode}.",
            Error = failed
                ? new GatewayError
                {
                    Code = ErrorCodes.TwinCatStateUnknown,
                    Message =
                        $"Could not read runtime '{observation.Name}'.",
                    Retryable = true,
                    Stage = "ads.runtimeMonitor",
                }
                : null,
            Properties = properties,
        };
    }

    private void LogTransition(
        RuntimeObservation observation,
        RuntimeMode? previousMode)
    {
        bool failed =
            observation.Result.Diagnostics.ErrorCode is not null;
        Dictionary<string, string> properties =
            CreateProperties(observation);
        properties["previousMode"] =
            previousMode?.ToString() ?? "unknown";
        _logger.Write(
            failed
                || observation.Result.Status.Mode
                    == RuntimeMode.Exception
                ? StructuredLogLevel.Warning
                : StructuredLogLevel.Information,
            failed
                ? "ads.runtime_status.failed"
                : "ads.runtime_state.changed",
            failed
                ? $"Could not read runtime '{observation.Name}'."
                : $"Runtime '{observation.Name}' changed to "
                    + $"{observation.Result.Status.Mode}.",
            properties: properties,
            exception: observation.Result.Failure);
    }

    private static Dictionary<string, string> CreateProperties(
        RuntimeObservation observation)
    {
        AdsRuntimeStatusReadResult result =
            observation.Result;
        return new Dictionary<string, string>
        {
            ["runtimeName"] = observation.Name,
            ["runtimeKind"] =
                observation.IsSystem ? "system" : "plc",
            ["amsNetId"] =
                result.Diagnostics.AmsNetId ?? "unknown",
            ["adsPort"] =
                result.Diagnostics.Port.ToString(
                    CultureInfo.InvariantCulture),
            ["mode"] = result.Status.Mode.ToString(),
            ["adsState"] =
                result.Diagnostics.AdsState ?? "unknown",
            ["deviceState"] =
                result.Diagnostics.DeviceState?.ToString(
                    CultureInfo.InvariantCulture)
                ?? "unknown",
            ["errorCode"] =
                result.Diagnostics.ErrorCode ?? "none",
        };
    }

    private IReadOnlyList<PlcRuntimeTarget> DiscoverPlcTargets(
        string? twinCatProjectPath)
    {
        if (string.IsNullOrWhiteSpace(twinCatProjectPath))
        {
            return Array.Empty<PlcRuntimeTarget>();
        }

        try
        {
            return TwinCatRuntimeTargetDiscovery.Discover(
                twinCatProjectPath!);
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is UnauthorizedAccessException
            || exception is System.Xml.XmlException)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "ads.runtime_targets.failed",
                "Could not discover PLC runtime ports from the selected TwinCAT project.",
                properties: new Dictionary<string, string>
                {
                    ["twinCatProjectPath"] =
                        twinCatProjectPath!,
                },
                exception: exception);
            return Array.Empty<PlcRuntimeTarget>();
        }
    }

    private async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _wakeSignal.WaitAsync(
                _pollInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is sufficient.
        }
    }

    private static string CreateSignature(
        AdsRuntimeStatusReadResult result)
    {
        return string.Join(
            "|",
            result.Status.Mode.ToString(),
            result.Diagnostics.AdsState ?? "unknown",
            result.Diagnostics.DeviceState?.ToString(
                CultureInfo.InvariantCulture)
                ?? "unknown",
            result.Diagnostics.ErrorCode ?? "none");
    }

    private static RuntimeMode? TryReadMode(
        string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        string raw = signature!.Split('|')[0];
        return Enum.TryParse(
            raw,
            ignoreCase: false,
            out RuntimeMode mode)
            ? mode
            : null;
    }

    private static AdsRuntimeDiagnostics CloneDiagnostics(
        AdsRuntimeDiagnostics source)
    {
        return new AdsRuntimeDiagnostics
        {
            RuntimeName = source.RuntimeName,
            AmsNetId = source.AmsNetId,
            Port = source.Port,
            AdsState = source.AdsState,
            DeviceState = source.DeviceState,
            ErrorCode = source.ErrorCode,
            ReadAtUtc = source.ReadAtUtc,
        };
    }

    private static RuntimeAlert? CloneAlert(
        RuntimeAlert? source)
    {
        return GatewayStatusSnapshotStore.CloneRuntimeAlert(
            source);
    }

    private sealed class RuntimeTarget
    {
        public RuntimeTarget(
            string amsNetId,
            IReadOnlyList<PlcRuntimeTarget> plcTargets)
        {
            AmsNetId = amsNetId;
            PlcTargets = plcTargets;
            Signature = string.Join(
                "|",
                new[] { amsNetId }.Concat(
                    plcTargets.Select(target =>
                        $"{target.Name}:{target.AdsPort}")));
        }

        public string AmsNetId { get; }

        public IReadOnlyList<PlcRuntimeTarget> PlcTargets { get; }

        public string Signature { get; }
    }

    private sealed class RuntimeObservation
    {
        public RuntimeObservation(
            string name,
            bool isSystem,
            AdsRuntimeStatusReadResult result)
        {
            Name = name;
            IsSystem = isSystem;
            Result = result;
        }

        public string Name { get; }

        public bool IsSystem { get; }

        public AdsRuntimeStatusReadResult Result { get; }
    }
}

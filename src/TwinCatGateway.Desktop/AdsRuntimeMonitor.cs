using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

internal sealed class AdsRuntimeMonitor : IDisposable
{
    private static readonly Action<
        ILogger,
        string,
        string,
        Exception?> LogInformation =
            LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(1, "state.observation"),
                "{Message} {Properties}");
    private static readonly Action<
        ILogger,
        string,
        string,
        Exception?> LogWarning =
            LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(2, "state.observation.warning"),
                "{Message} {Properties}");
    private static readonly Action<
        ILogger,
        string,
        string,
        Exception?> LogError =
            LoggerMessage.Define<string, string>(
                LogLevel.Error,
                new EventId(3, "state.observation.error"),
                "{Message} {Properties}");
    private readonly object _sync = new();
    private readonly ProfileObservationStore _observations;
    private readonly ILogger<AdsRuntimeMonitor> _logger;
    private readonly IGatewayEventSink _events;
    private readonly IAdsStateProbe _probe;
    private readonly string _profile;
    private readonly string _amsNetId;
    private readonly string? _tcUnitRuntimeId;
    private readonly int? _tcUnitPort;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _readTimeout;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Dictionary<string, string> _lastSignatures =
        new(StringComparer.Ordinal);
    private IReadOnlyList<PlcRuntimeTarget> _plcTargets =
        Array.Empty<PlcRuntimeTarget>();
    private bool _diverged;
    private int _disposed;

    public AdsRuntimeMonitor(
        ProfileObservationStore observations,
        ILogger<AdsRuntimeMonitor> logger,
        IGatewayEventSink events,
        string profile,
        string amsNetId,
        int pollIntervalMilliseconds,
        int readTimeoutMilliseconds,
        string? tcUnitRuntimeId = null,
        int? tcUnitPort = null,
        IAdsStateProbe? probe = null)
    {
        _observations = observations
            ?? throw new ArgumentNullException(nameof(observations));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        _events = events
            ?? throw new ArgumentNullException(nameof(events));
        _profile = string.IsNullOrWhiteSpace(profile)
            ? throw new ArgumentException(
                "Profile is required.",
                nameof(profile))
            : profile;
        _amsNetId = string.IsNullOrWhiteSpace(amsNetId)
            ? throw new ArgumentException(
                "AMS NetId is required.",
                nameof(amsNetId))
            : amsNetId;
        if (pollIntervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollIntervalMilliseconds));
        }

        if (readTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readTimeoutMilliseconds));
        }

        _pollInterval = TimeSpan.FromMilliseconds(
            pollIntervalMilliseconds);
        _readTimeout = TimeSpan.FromMilliseconds(
            readTimeoutMilliseconds);
        _tcUnitRuntimeId = tcUnitRuntimeId;
        _tcUnitPort = tcUnitPort;
        _probe = probe ?? new AdsStateProbe();
    }

    public ProfileObservationSnapshot Read()
    {
        return _observations.Read();
    }

    public void PublishXaeObservation(
        XaeTwinCatSystemObservation? observation)
    {
        ProfileObservationSnapshot snapshot;
        if (observation is null)
        {
            snapshot = _observations.MarkXaeUnavailable(
                DateTimeOffset.UtcNow,
                new ObservationError
                {
                    Code =
                        ErrorCodes.XaeSystemStateUnavailable,
                    Message =
                        "XAE has no TwinCAT system observation.",
                    Retryable = true,
                });
        }
        else
        {
            snapshot =
                _observations.PublishXae(observation);
        }

        PublishXaeEvent(snapshot.Xae);
        PublishDivergenceEvent(snapshot);
    }

    public void MarkXaeUnavailable(string message)
    {
        ProfileObservationSnapshot snapshot =
            _observations.MarkXaeUnavailable(
                DateTimeOffset.UtcNow,
                new ObservationError
                {
                    Code =
                        ErrorCodes.XaeSystemStateUnavailable,
                    Message = message,
                    Retryable = true,
                });
        PublishXaeEvent(snapshot.Xae);
        PublishDivergenceEvent(snapshot);
    }

    public void UpdateProject(string? twinCatProjectPath)
    {
        IReadOnlyList<PlcRuntimeTarget> targets =
            DiscoverPlcTargets(twinCatProjectPath);
        lock (_sync)
        {
            _plcTargets = targets;
            _lastSignatures
                .Where(pair =>
                    pair.Key.StartsWith(
                        "plc:",
                        StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList()
                .ForEach(key => _lastSignatures.Remove(key));
        }

        _observations.ConfigureRuntimes(targets);
        Wake();
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PollAsync(cancellationToken)
                    .ConfigureAwait(false);
                await DelayAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // Bounded ADS calls finish before cancellation completes.
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
        CancellationToken cancellationToken)
    {
        AdsStateReadResult system = await ReadAsync(
                AdsStateReader.SystemServicePort,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ProfileObservationSnapshot snapshot;
        if (system.Succeeded)
        {
            TargetSystemObservation observation =
                CreateTargetObservation(system);
            snapshot =
                _observations.PublishTarget(observation);
            PublishTargetEvent(observation);
        }
        else
        {
            snapshot =
                _observations.MarkTargetReadFailed(
                    system.ObservedAtUtc,
                    system.Error!);
            PublishTargetEvent(snapshot.Target);
            LogReadFailure(
                "Target System Service",
                system);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PlcRuntimeTarget> targets;
        lock (_sync)
        {
            targets = _plcTargets.ToArray();
        }

        if (snapshot.Target.Freshness
                == ObservationFreshness.Fresh
            && snapshot.Target.State
                == TargetSystemState.Run)
        {
            Task<PlcRead>[] reads = targets
                .Select(target => ReadPlcAsync(
                    target,
                    cancellationToken))
                .ToArray();
            PlcRead[] results = reads.Length == 0
                ? Array.Empty<PlcRead>()
                : await Task.WhenAll(reads)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (PlcRead result in results)
            {
                if (result.Read.Succeeded)
                {
                    PlcRuntimeObservation observation =
                        CreatePlcObservation(
                            result.Target,
                            result.Read);
                    snapshot =
                        _observations.PublishPlc(
                            observation);
                    PublishPlcEvent(observation);
                }
                else
                {
                    snapshot =
                        _observations.MarkPlcReadFailed(
                            result.Target.RuntimeId,
                            result.Target.AdsPort,
                            result.Read.ObservedAtUtc,
                            result.Read.Error!);
                    PublishPlcEvent(
                        snapshot.PlcRuntimes.Single(
                            runtime => string.Equals(
                                runtime.RuntimeId,
                                result.Target.RuntimeId,
                                StringComparison
                                    .OrdinalIgnoreCase)));
                    LogReadFailure(
                        result.Target.RuntimeId,
                        result.Read);
                }
            }
        }
        else
        {
            DateTimeOffset attemptedAtUtc =
                system.ObservedAtUtc;
            foreach (PlcRuntimeTarget target in targets)
            {
                snapshot =
                    _observations.MarkPlcNotObserved(
                        target.RuntimeId,
                        target.AdsPort,
                        attemptedAtUtc);
                PublishPlcEvent(
                    snapshot.PlcRuntimes.Single(
                        runtime => string.Equals(
                            runtime.RuntimeId,
                            target.RuntimeId,
                            StringComparison.OrdinalIgnoreCase)));
            }
        }

        PublishDivergenceEvent(_observations.Read());
    }

    private Task<AdsStateReadResult> ReadAsync(
        int port,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _probe.Read(
                    _amsNetId,
                    port,
                    _readTimeout);
            },
            cancellationToken);
    }

    private async Task<PlcRead> ReadPlcAsync(
        PlcRuntimeTarget target,
        CancellationToken cancellationToken)
    {
        AdsStateReadResult read = await ReadAsync(
                target.AdsPort,
                cancellationToken)
            .ConfigureAwait(false);
        return new PlcRead(target, read);
    }

    private TargetSystemObservation CreateTargetObservation(
        AdsStateReadResult read)
    {
        return new TargetSystemObservation
        {
            Profile = _profile,
            AmsNetId = read.AmsNetId,
            Port = read.Port,
            RawAdsState = read.RawAdsState,
            RawAdsStateName = read.RawAdsStateName,
            RawDeviceState = read.RawDeviceState,
            State = AdsStateMapper.MapSystemService(
                read.RawAdsState!.Value),
            ObservedAtUtc = read.ObservedAtUtc,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    private PlcRuntimeObservation CreatePlcObservation(
        PlcRuntimeTarget target,
        AdsStateReadResult read)
    {
        return new PlcRuntimeObservation
        {
            Profile = _profile,
            RuntimeId = target.RuntimeId,
            Project = target.Project,
            Instance = target.Instance,
            AmsNetId = read.AmsNetId,
            Port = read.Port,
            RawAdsState = read.RawAdsState,
            RawAdsStateName = read.RawAdsStateName,
            RawDeviceState = read.RawDeviceState,
            State = AdsStateMapper.MapPlcRuntime(
                read.RawAdsState!.Value),
            ObservedAtUtc = read.ObservedAtUtc,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    private void PublishXaeEvent(
        XaeTwinCatSystemObservation? observation)
    {
        if (observation is null)
        {
            return;
        }

        Dictionary<string, string> properties = new()
        {
            ["profile"] = _profile,
            ["source"] = observation.Source.ToString(),
            ["selectedTarget"] =
                observation.SelectedTarget ?? "unknown",
            ["state"] = observation.State.ToString(),
            ["rawState"] =
                observation.RawState ?? "unknown",
            ["freshness"] =
                observation.Freshness.ToString(),
            ["errorCode"] =
                observation.Error?.Code ?? "none",
        };
        PublishChangedEvent(
            "xae",
            observation.Error is null
                ? GatewayEventTypes.XaeSystemStateChanged
                : GatewayEventTypes.XaeSystemStateReadFailed,
            GatewayComponent.Xae,
            "xae.systemState.observe",
            observation.Error is null
                ? "XAE TwinCAT system observation changed."
                : "XAE TwinCAT system observation is unavailable.",
            properties,
            observation.ObservedAtUtc,
            observation.Error);
    }

    private void PublishTargetEvent(
        TargetSystemObservation observation)
    {
        Dictionary<string, string> properties =
            CreateAdsProperties(
                observation.AmsNetId,
                observation.Port,
                observation.State.ToString(),
                observation.RawAdsState,
                observation.RawAdsStateName,
                observation.RawDeviceState,
                observation.Freshness,
                observation.Error);
        properties["profile"] = _profile;
        PublishChangedEvent(
            "target",
            observation.Error is null
                ? GatewayEventTypes.TargetSystemStateChanged
                : GatewayEventTypes
                    .TargetSystemStateReadFailed,
            GatewayComponent.Target,
            "ads.target.observe",
            observation.Error is null
                ? "Target System Service observation changed."
                : "Target System Service observation is unavailable.",
            properties,
            observation.ObservedAtUtc,
            observation.Error);
    }

    private void PublishPlcEvent(
        PlcRuntimeObservation observation)
    {
        Dictionary<string, string> properties =
            CreateAdsProperties(
                observation.AmsNetId,
                observation.Port,
                observation.State.ToString(),
                observation.RawAdsState,
                observation.RawAdsStateName,
                observation.RawDeviceState,
                observation.Freshness,
                observation.Error);
        properties["profile"] = _profile;
        properties["runtimeId"] = observation.RuntimeId;
        properties["project"] =
            observation.Project ?? "unknown";
        PublishChangedEvent(
            $"plc:{observation.RuntimeId}",
            observation.Error is null
                ? GatewayEventTypes.PlcRuntimeStateChanged
                : GatewayEventTypes
                    .PlcRuntimeStateReadFailed,
            GatewayComponent.Plc,
            "ads.plc.observe",
            observation.Error is null
                ? $"PLC runtime '{observation.RuntimeId}' observation changed."
                : $"PLC runtime '{observation.RuntimeId}' observation is unavailable.",
            properties,
            observation.ObservedAtUtc,
            observation.Error);
    }

    private void PublishChangedEvent(
        string key,
        string type,
        GatewayComponent component,
        string stage,
        string message,
        Dictionary<string, string> properties,
        DateTimeOffset occurredAtUtc,
        ObservationError? observationError)
    {
        string signature = string.Join(
            "|",
            properties.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal)
                .Select(pair =>
                    $"{pair.Key}={pair.Value}"));
        lock (_sync)
        {
            if (_lastSignatures.TryGetValue(
                    key,
                    out string? previous)
                && string.Equals(
                    previous,
                    signature,
                    StringComparison.Ordinal))
            {
                return;
            }

            _lastSignatures[key] = signature;
        }

        DiagnosticSeverity severity =
            observationError is not null
                ? DiagnosticSeverity.Warning
                : properties.TryGetValue(
                        "state",
                        out string? state)
                    && string.Equals(
                        state,
                        "Exception",
                        StringComparison.Ordinal)
                        ? DiagnosticSeverity.Error
                        : DiagnosticSeverity.Info;
        GatewayError? error = observationError is null
            ? null
            : new GatewayError
            {
                Code = observationError.Code,
                Message = observationError.Message,
                Retryable = observationError.Retryable,
                Component = component,
                Stage = stage,
                SideEffectsStarted = false,
            };
        _events.Record(
            new GatewayEvent
            {
                Type = type,
                Severity = severity,
                Stage = stage,
                Message = message,
                Error = error,
                Properties = properties,
            },
            occurredAtUtc);
        Action<ILogger, string, string, Exception?> log =
            severity == DiagnosticSeverity.Error
                ? LogError
                : observationError is not null
                    ? LogWarning
                    : LogInformation;
        log(
            _logger,
            message,
            FormatProperties(properties),
            null);
    }

    private void PublishDivergenceEvent(
        ProfileObservationSnapshot snapshot)
    {
        bool diverged = snapshot.Divergence is not null;
        lock (_sync)
        {
            if (_diverged == diverged)
            {
                return;
            }

            _diverged = diverged;
        }

        StateObservationDivergence? detail =
            snapshot.Divergence;
        Dictionary<string, string> properties = new()
        {
            ["profile"] = _profile,
            ["amsNetId"] = _amsNetId,
            ["xaeObserved"] =
                detail?.XaeObserved.ToString()
                ?? snapshot.Xae?.State.ToString()
                ?? "unknown",
            ["systemServiceObserved"] =
                detail?.SystemServiceObserved.ToString()
                ?? snapshot.Target.State.ToString(),
        };
        _events.Record(
            new GatewayEvent
            {
                Type = diverged
                    ? GatewayEventTypes
                        .StateObservationsDiverged
                    : GatewayEventTypes
                        .StateObservationsConverged,
                Severity = diverged
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Info,
                Stage = "target.state.compare",
                Message = diverged
                    ? "XAE and direct Target observations diverged."
                    : "XAE and direct Target observations no longer diverge.",
                Error = diverged
                    ? new GatewayError
                    {
                        Code =
                            ErrorCodes.StateObservationsDiverged,
                        Message =
                            "XAE and direct Target observations diverged.",
                        Retryable = true,
                        Component = GatewayComponent.Target,
                        Stage = "target.state.compare",
                        SideEffectsStarted = false,
                    }
                    : null,
                Properties = properties,
            },
            detail?.SystemServiceObservedAtUtc
                ?? DateTimeOffset.UtcNow);
    }

    private void LogReadFailure(
        string device,
        AdsStateReadResult read)
    {
        LogWarning(
            _logger,
            $"Could not read state for '{device}'.",
            FormatProperties(new Dictionary<string, string>
            {
                ["profile"] = _profile,
                ["amsNetId"] = read.AmsNetId,
                ["adsPort"] = read.Port.ToString(
                    CultureInfo.InvariantCulture),
                ["errorCode"] =
                    read.Error?.Code ?? "unknown",
            }),
            read.Failure);
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
                twinCatProjectPath!,
                _tcUnitRuntimeId,
                _tcUnitPort);
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is UnauthorizedAccessException
            || exception is System.Xml.XmlException)
        {
            LogWarning(
                _logger,
                "Could not discover PLC runtime ports from the selected TwinCAT project.",
                FormatProperties(new Dictionary<string, string>
                {
                    ["profile"] = _profile,
                    ["twinCatProjectPath"] =
                        twinCatProjectPath!,
                }),
                exception);
            return Array.Empty<PlcRuntimeTarget>();
        }
    }

    private static Dictionary<string, string>
        CreateAdsProperties(
        string amsNetId,
        int port,
        string state,
        int? rawAdsState,
        string? rawAdsStateName,
        int? rawDeviceState,
        ObservationFreshness freshness,
        ObservationError? error)
    {
        return new Dictionary<string, string>
        {
            ["amsNetId"] = amsNetId,
            ["adsPort"] = port.ToString(
                CultureInfo.InvariantCulture),
            ["state"] = state,
            ["rawAdsState"] =
                rawAdsState?.ToString(
                    CultureInfo.InvariantCulture)
                ?? "unknown",
            ["rawAdsStateName"] =
                rawAdsStateName ?? "unknown",
            ["rawDeviceState"] =
                rawDeviceState?.ToString(
                    CultureInfo.InvariantCulture)
                ?? "unknown",
            ["freshness"] = freshness.ToString(),
            ["errorCode"] = error?.Code ?? "none",
        };
    }

    private static string FormatProperties(
        IReadOnlyDictionary<string, string> properties)
    {
        return string.Join(
            ", ",
            properties
                .OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal)
                .Select(pair =>
                    $"{pair.Key}={pair.Value}"));
    }

    private async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        await _wakeSignal.WaitAsync(
                _pollInterval,
                cancellationToken)
            .ConfigureAwait(false);
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

    private sealed class PlcRead
    {
        public PlcRead(
            PlcRuntimeTarget target,
            AdsStateReadResult read)
        {
            Target = target;
            Read = read;
        }

        public PlcRuntimeTarget Target { get; }

        public AdsStateReadResult Read { get; }
    }
}

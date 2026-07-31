using System;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayStatusSnapshotStore
{
    private readonly object _writeSync = new();
    private GatewayStateSnapshot _snapshot;

    public GatewayStatusSnapshotStore(GatewayStateSnapshot initialSnapshot)
    {
        _snapshot = Clone(initialSnapshot
            ?? throw new ArgumentNullException(nameof(initialSnapshot)));
    }

    public GatewayStateSnapshot Read()
    {
        GatewayStateSnapshot current = Volatile.Read(ref _snapshot);
        return Clone(current);
    }

    public void Replace(GatewayStateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        Interlocked.Exchange(ref _snapshot, Clone(snapshot));
    }

    public void Update(
        Func<GatewayStateSnapshot, GatewayStateSnapshot> update)
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        lock (_writeSync)
        {
            GatewayStateSnapshot current = Clone(_snapshot);
            GatewayStateSnapshot updated = update(current)
                ?? throw new InvalidOperationException(
                    "Status snapshot update returned null.");
            Interlocked.Exchange(ref _snapshot, Clone(updated));
        }
    }

    public static GatewayStateSnapshot CreateInitial(string version)
    {
        return new GatewayStateSnapshot
        {
            State = GatewayProcessState.Starting,
            Version = version ?? throw new ArgumentNullException(nameof(version)),
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static GatewayStateSnapshot Clone(GatewayStateSnapshot source)
    {
        return new GatewayStateSnapshot
        {
            State = source.State,
            Version = source.Version,
            ConfigurationPath = source.ConfigurationPath,
            ActiveProfile = source.ActiveProfile,
            CurrentOperationId = source.CurrentOperationId,
            JournalId = source.JournalId,
            LatestEventCursor = source.LatestEventCursor,
            ObservedAtUtc = source.ObservedAtUtc,
            Error = CloneObservationError(source.Error),
        };
    }

    private static ObservationError? CloneObservationError(
        ObservationError? source)
    {
        return source is null
            ? null
            : new ObservationError
            {
                Code = source.Code,
                Message = source.Message,
                Retryable = source.Retryable,
            };
    }

}

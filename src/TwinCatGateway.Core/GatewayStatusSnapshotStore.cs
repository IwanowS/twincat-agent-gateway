using System;
using System.Linq;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayStatusSnapshotStore
{
    private readonly object _writeSync = new();
    private GatewayStatusResult _snapshot;

    public GatewayStatusSnapshotStore(GatewayStatusResult initialSnapshot)
    {
        _snapshot = Clone(initialSnapshot
            ?? throw new ArgumentNullException(nameof(initialSnapshot)));
    }

    public GatewayStatusResult Read()
    {
        GatewayStatusResult current = Volatile.Read(ref _snapshot);
        return Clone(current);
    }

    public void Replace(GatewayStatusResult snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        Interlocked.Exchange(ref _snapshot, Clone(snapshot));
    }

    public void Update(
        Func<GatewayStatusResult, GatewayStatusResult> update)
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        lock (_writeSync)
        {
            GatewayStatusResult current = Clone(_snapshot);
            GatewayStatusResult updated = update(current)
                ?? throw new InvalidOperationException(
                    "Status snapshot update returned null.");
            Interlocked.Exchange(ref _snapshot, Clone(updated));
        }
    }

    public static GatewayStatusResult CreateInitial(string version)
    {
        return new GatewayStatusResult
        {
            Gateway = new GatewayStatus
            {
                State = GatewayState.Starting,
                Version = version ?? throw new ArgumentNullException(nameof(version)),
            },
            Xae = new XaeStatus(),
            TwinCat = new TwinCatStatus
            {
                Started = null,
                Mode = RuntimeMode.Unknown,
            },
        };
    }

    private static GatewayStatusResult Clone(GatewayStatusResult source)
    {
        return new GatewayStatusResult
        {
            Gateway = new GatewayStatus
            {
                State = source.Gateway.State,
                Version = source.Gateway.Version,
            },
            Xae = new XaeStatus
            {
                Connected = source.Xae.Connected,
                Version = source.Xae.Version,
                Solution = source.Xae.Solution,
                AgentWorkspaceOwned =
                    source.Xae.AgentWorkspaceOwned,
            },
            TwinCat = new TwinCatStatus
            {
                Started = source.TwinCat.Started,
                Mode = source.TwinCat.Mode,
            },
            CurrentOperation = CloneOperation(source.CurrentOperation),
            LastBuild = CloneBuild(source.LastBuild),
            LastActivation = CloneActivation(source.LastActivation),
            LastTest = CloneTest(source.LastTest),
            UnreadErrors = source.UnreadErrors,
        };
    }

    private static OperationSummary? CloneOperation(OperationSummary? source)
    {
        return source is null
            ? null
            : new OperationSummary
            {
                OperationId = source.OperationId,
                Kind = source.Kind,
                State = source.State,
                QueuedAtUtc = source.QueuedAtUtc,
                StartedAtUtc = source.StartedAtUtc,
                CompletedAtUtc = source.CompletedAtUtc,
                Error = CloneError(source.Error),
                Resources = source.Resources.Select(CloneResource).ToList(),
            };
    }

    private static BuildSummary? CloneBuild(BuildSummary? source)
    {
        return source is null
            ? null
            : new BuildSummary
            {
                Ok = source.Ok,
                OperationId = source.OperationId,
                Action = source.Action,
                Errors = source.Errors,
                Warnings = source.Warnings,
            };
    }

    private static ActivationSummary? CloneActivation(ActivationSummary? source)
    {
        return source is null
            ? null
            : new ActivationSummary
            {
                Ok = source.Ok,
                OperationId = source.OperationId,
                Profile = source.Profile,
                Target = CloneTarget(source.Target),
            };
    }

    private static TestSummary? CloneTest(TestSummary? source)
    {
        return source is null
            ? null
            : new TestSummary
            {
                Ok = source.Ok,
                OperationId = source.OperationId,
                Tests = source.Tests,
                Failed = source.Failed,
            };
    }

    private static TargetIdentity CloneTarget(TargetIdentity source)
    {
        return new TargetIdentity
        {
            Name = source.Name,
            AmsNetId = source.AmsNetId,
        };
    }

    private static GatewayError? CloneError(GatewayError? source)
    {
        return source is null
            ? null
            : new GatewayError
            {
                Code = source.Code,
                Message = source.Message,
                Retryable = source.Retryable,
                OperationId = source.OperationId,
                Stage = source.Stage,
                RawLogRef = source.RawLogRef,
            };
    }

    private static ResourceReference CloneResource(ResourceReference source)
    {
        return new ResourceReference
        {
            Uri = source.Uri,
            OperationId = source.OperationId,
            Kind = source.Kind,
        };
    }
}

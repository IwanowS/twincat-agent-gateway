using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum OperationArtifactKind
{
    Build,
    XaeMessages,
    TestXunit,
    ProjectNoise,
}

public sealed class OperationRecord
{
    public string OperationId { get; set; } = string.Empty;

    public OperationKind Kind { get; set; }

    public string? Profile { get; set; }

    public OperationState State { get; set; }

    public DateTimeOffset QueuedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public long? DurationMs { get; set; }

    public GatewayError? Error { get; set; }

    public List<OperationDiagnostic> Diagnostics { get; set; } = new();

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class OperationSnapshot<TResult>
{
    public OperationRecord Operation { get; set; } = new();

    public TResult? Result { get; set; }
}

public sealed class OperationEventPage
{
    public List<GatewayEvent> Events { get; set; } = new();

    public string JournalId { get; set; } = string.Empty;

    public long NextCursor { get; set; }

    public long LatestCursor { get; set; }

    public bool HasMore { get; set; }

    public bool HistoryTruncated { get; set; }
}

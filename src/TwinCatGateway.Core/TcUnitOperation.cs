using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class TcUnitRunPreparation
{
    public string RootOperationId { get; set; } =
        string.Empty;

    public OperationKind RootOperationKind { get; set; } =
        OperationKind.Activate;

    public string ExpectedAmsNetId { get; set; } =
        string.Empty;

    public DateTimeOffset PreparedAtUtc { get; set; }

    public TcUnitReportBaseline ReportBaseline { get; set; } =
        new();

    public TcUnitCompletionBaseline CompletionBaseline { get; set; } =
        new();
}

public sealed class TcUnitCompletionBaseline
{
    public bool Finished { get; set; }

    public int InitializedSuites { get; set; }

    public DateTimeOffset ReadAtUtc { get; set; }
}

public delegate TcUnitRunPreparation
    TcUnitPreparationExecutor(
        string rootOperationId);

public delegate Task<TestResult> TcUnitOperationExecutor(
    string operationId,
    TcUnitRunPreparation preparation,
    CancellationToken cancellationToken);

public delegate ResourceReference TcUnitReportResourceWriter(
    string operationId,
    string xml);

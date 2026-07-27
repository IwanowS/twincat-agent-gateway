using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class TcUnitRunPreparation
{
    public string ActivationOperationId { get; set; } =
        string.Empty;

    public string ExpectedAmsNetId { get; set; } =
        string.Empty;

    public DateTimeOffset PreparedAtUtc { get; set; }

    public TcUnitReportBaseline ReportBaseline { get; set; } =
        new();
}

public delegate TcUnitRunPreparation
    TcUnitPreparationExecutor(
        string activationOperationId);

public delegate Task<TestResult> TcUnitOperationExecutor(
    string operationId,
    string activationOperationId,
    TcUnitRunPreparation preparation,
    CancellationToken cancellationToken);

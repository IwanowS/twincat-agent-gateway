using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class TestFailure
{
    public string Suite { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? File { get; set; }

    public int? Line { get; set; }
}

public sealed class TestCounts
{
    public int Suites { get; set; }

    public int Tests { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }
}

public sealed class TestResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public TestCounts Counts { get; set; } = new();

    public int InitializedSuites { get; set; }

    public List<TestFailure> Failures { get; set; } = new();

    public int MoreFailures { get; set; }

    public ResourceReference? Report { get; set; }
}

public sealed class TestSummary
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public int Tests { get; set; }

    public int Failed { get; set; }
}

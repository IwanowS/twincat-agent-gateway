using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class GetXaeMessagesParameters
{
    public int MaximumMessages { get; set; } = 50;
}

public sealed class XaeMessagesResult
{
    public string Solution { get; set; } = string.Empty;

    public DateTimeOffset ReadAtUtc { get; set; }

    public DiagnosticCounts Counts { get; set; } = new();

    public List<BuildDiagnostic> Messages { get; set; } = new();

    public int MoreMessages { get; set; }
}

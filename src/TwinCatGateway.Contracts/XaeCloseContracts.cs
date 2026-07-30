namespace TwinCatGateway.Contracts;

public enum XaeSaveMode
{
    Save,
    Discard,
    Prompt,
}

public sealed class CloseXaeParameters
{
    public XaeSaveMode SaveMode { get; set; } = XaeSaveMode.Prompt;

    public int? TimeoutSeconds { get; set; }
}

public sealed class CloseXaeResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string Profile { get; set; } = string.Empty;

    public string Solution { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public XaeSaveMode SaveMode { get; set; }

    public bool ProcessExited { get; set; }

    public bool CommandErrorObserved { get; set; }
}

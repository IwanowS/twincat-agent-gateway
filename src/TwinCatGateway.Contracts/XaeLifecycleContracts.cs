namespace TwinCatGateway.Contracts;

public sealed class XaeOpenParameters
{
    public string Profile { get; set; } = string.Empty;
}

public sealed class XaeOpenResult
{
    public bool Attached { get; set; }

    public bool Launched { get; set; }

    public XaeSessionSnapshot State { get; set; } = new();
}

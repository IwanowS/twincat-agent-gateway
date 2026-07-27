namespace TwinCatGateway.Contracts;

public sealed class ResourceContent
{
    public string Uri { get; set; } = string.Empty;

    public string ContentType { get; set; } = "text/plain";

    public string Content { get; set; } = string.Empty;

    public long Offset { get; set; }

    public long? NextOffset { get; set; }

    public bool Truncated { get; set; }
}

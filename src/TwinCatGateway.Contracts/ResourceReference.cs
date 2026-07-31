namespace TwinCatGateway.Contracts;

public sealed class ResourceReference
{
    public string Uri { get; set; } = string.Empty;

    public string? MimeType { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public ResourceKind Kind { get; set; }
}

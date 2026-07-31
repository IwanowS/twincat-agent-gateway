namespace TwinCatGateway.Contracts;

public sealed class ResourceReference
{
    public string Uri { get; set; } = string.Empty;

    public string? MimeType { get; set; }
}

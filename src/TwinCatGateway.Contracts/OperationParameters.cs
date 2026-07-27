namespace TwinCatGateway.Contracts;

public sealed class GetOperationParameters
{
    public string OperationId { get; set; } = string.Empty;
}

public sealed class CancelOperationParameters
{
    public string OperationId { get; set; } = string.Empty;
}

public sealed class GetResourceParameters
{
    public string Uri { get; set; } = string.Empty;

    public int MaximumCharacters { get; set; } = 64 * 1024;

    public long Offset { get; set; }
}

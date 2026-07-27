namespace TwinCatGateway.Core;

public sealed class ConfigurationIssue
{
    public ConfigurationIssue(string path, string message)
    {
        Path = path;
        Message = message;
    }

    public string Path { get; }

    public string Message { get; }
}

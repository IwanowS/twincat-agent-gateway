using System;
using System.IO;
using TwinCatGateway.Mcp;

if (args.Length == 2
    && string.Equals(args[0], "--generate-reference", StringComparison.Ordinal))
{
    McpReferenceGenerator.Write(Path.GetFullPath(args[1]));
    return 0;
}

if (args.Length == 2
    && string.Equals(args[0], "--check-reference", StringComparison.Ordinal))
{
    string reference = File.ReadAllText(Path.GetFullPath(args[1]));
    return McpReferenceGenerator.IsCurrent(reference) ? 0 : 1;
}

return await McpCommandLine.InvokeAsync(args);

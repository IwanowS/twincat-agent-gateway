using System;

namespace TwinCatGateway.Core;

public interface IOperationIdGenerator
{
    string Create();
}

public sealed class GuidOperationIdGenerator : IOperationIdGenerator
{
    public static GuidOperationIdGenerator Instance { get; } = new();

    private GuidOperationIdGenerator()
    {
    }

    public string Create()
    {
        return Guid.NewGuid().ToString("N");
    }
}

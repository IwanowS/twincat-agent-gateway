using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class OperationStoreTests
{
    [Fact]
    public void ReturnedMetadataCannotMutateStoredSnapshot()
    {
        OperationStore store = new();
        DateTimeOffset queued = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        store.AddQueued("operation-1", OperationKind.XaeBuild, null, queued);
        store.TryMarkRunning("operation-1", queued.AddSeconds(1));
        store.TryComplete(
            "operation-1",
            OperationState.Failed,
            queued.AddSeconds(2),
            error: new GatewayError
            {
                Code = ErrorCodes.BuildFailed,
                Message = "Compile failed.",
            },
            resources: new[]
            {
                new ResourceReference
                {
                    Uri = "twincat-log://operation-1/build",
                    MimeType = "text/plain",
                },
            });

        StoredOperation first = Assert.IsType<StoredOperation>(store.Get("operation-1"));
        first.Summary.State = OperationState.Succeeded;
        first.Summary.Error!.Code = "MUTATED";
        first.Summary.Resources[0].Uri = "mutated";

        StoredOperation second = Assert.IsType<StoredOperation>(store.Get("operation-1"));

        Assert.Equal(OperationState.Failed, second.Summary.State);
        Assert.Equal(ErrorCodes.BuildFailed, second.Summary.Error?.Code);
        Assert.Equal("twincat-log://operation-1/build", second.Summary.Resources[0].Uri);
    }

    [Fact]
    public void CapacityTrimsOldestCompletedOperation()
    {
        OperationStore store = new(capacity: 2);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int index = 1; index <= 3; index++)
        {
            string operationId = $"operation-{index}";
            store.AddQueued(
                operationId,
                OperationKind.XaeBuild,
                null,
                now.AddSeconds(index));
            store.TryComplete(
                operationId,
                OperationState.Succeeded,
                now.AddSeconds(index + 1));
        }

        Assert.Null(store.Get("operation-1"));
        Assert.NotNull(store.Get("operation-2"));
        Assert.NotNull(store.Get("operation-3"));
    }
}

using System;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayStatusSnapshotStoreTests
{
    [Fact]
    public void ReturnedSnapshotCannotMutatePublishedState()
    {
        GatewayStateSnapshot initial =
            GatewayStatusSnapshotStore.CreateInitial("0.1.0");
        initial.State = GatewayProcessState.Ready;
        initial.ConfigurationPath =
            @"C:\Project\twincat-gateway.json";
        initial.ActiveProfile = "fixture";
        initial.CurrentOperationId = "operation-1";
        initial.Error = new ObservationError
        {
            Code = "GATEWAY_FAULT",
            Message = "Gateway fault.",
        };
        GatewayStatusSnapshotStore store = new(initial);

        GatewayStateSnapshot first = store.Read();
        first.State = GatewayProcessState.Faulted;
        Assert.IsType<ObservationError>(first.Error).Code = "mutated";

        GatewayStateSnapshot second = store.Read();

        Assert.Equal(GatewayProcessState.Ready, second.State);
        Assert.Equal(
            @"C:\Project\twincat-gateway.json",
            second.ConfigurationPath);
        Assert.Equal("fixture", second.ActiveProfile);
        Assert.Equal("operation-1", second.CurrentOperationId);
        Assert.Equal("GATEWAY_FAULT", second.Error?.Code);
    }

    [Fact]
    public async Task ReadersRemainNonBlockingWhileUpdateIsPrepared()
    {
        GatewayStatusSnapshotStore store = new(
            GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
        TaskCompletionSource<bool> updateStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseUpdate = NewCompletionSource();

        Task writer = Task.Run(() =>
            store.Update(current =>
            {
                updateStarted.SetResult(true);
                releaseUpdate.Task.GetAwaiter().GetResult();
                current.State = GatewayProcessState.Ready;
                return current;
            }));

        await updateStarted.Task;
        Task<GatewayStateSnapshot> read = Task.Run(store.Read);
        Task completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1)));
        releaseUpdate.SetResult(true);
        await writer;

        Assert.Same(read, completed);
        Assert.Equal(GatewayProcessState.Starting, (await read).State);
        Assert.Equal(GatewayProcessState.Ready, store.Read().State);
    }

    [Fact]
    public async Task ReadersObserveOnlyWholeSnapshots()
    {
        GatewayStatusSnapshotStore store = new(CreateStatus(GatewayProcessState.Ready, "ready"));

        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < 1000; index++)
            {
                store.Replace(index % 2 == 0
                    ? CreateStatus(GatewayProcessState.Ready, "ready")
                    : CreateStatus(GatewayProcessState.Unavailable, "unavailable"));
            }
        });

        for (int index = 0; index < 1000; index++)
        {
            GatewayStateSnapshot snapshot = store.Read();
            bool consistent =
                (snapshot.State == GatewayProcessState.Ready
                    && snapshot.ActiveProfile == "ready")
                || (snapshot.State == GatewayProcessState.Unavailable
                    && snapshot.ActiveProfile == "unavailable");
            Assert.True(consistent);
        }

        await writer;
    }

    private static GatewayStateSnapshot CreateStatus(
        GatewayProcessState state,
        string activeProfile)
    {
        GatewayStateSnapshot status =
            GatewayStatusSnapshotStore.CreateInitial("0.1.0");
        status.State = state;
        status.ActiveProfile = activeProfile;
        return status;
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

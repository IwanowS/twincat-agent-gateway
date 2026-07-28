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
        GatewayStatusResult initial =
            GatewayStatusSnapshotStore.CreateInitial("0.1.0");
        initial.Gateway.State = GatewayState.Ready;
        initial.Xae.Connected = true;
        initial.Xae.AgentWorkspaceOwned = true;
        initial.Xae.DiscardedDocumentCount = 3;
        initial.LastActivation = new ActivationSummary
        {
            Ok = true,
            OperationId = "activation-1",
            Profile = "bench",
            Target = new TargetIdentity
            {
                Name = "target",
                AmsNetId = "1.2.3.4.1.1",
            },
        };
        GatewayStatusSnapshotStore store = new(initial);

        GatewayStatusResult first = store.Read();
        first.Gateway.State = GatewayState.Faulted;
        first.Xae.Connected = false;
        first.Xae.AgentWorkspaceOwned = false;
        first.Xae.DiscardedDocumentCount = 0;
        Assert.IsType<ActivationSummary>(first.LastActivation).Target.Name = "mutated";

        GatewayStatusResult second = store.Read();

        Assert.Equal(GatewayState.Ready, second.Gateway.State);
        Assert.True(second.Xae.Connected);
        Assert.True(second.Xae.AgentWorkspaceOwned);
        Assert.Equal(3, second.Xae.DiscardedDocumentCount);
        Assert.Equal("target", second.LastActivation?.Target.Name);
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
                current.Gateway.State = GatewayState.Ready;
                return current;
            }));

        await updateStarted.Task;
        Task<GatewayStatusResult> read = Task.Run(store.Read);
        Task completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1)));
        releaseUpdate.SetResult(true);
        await writer;

        Assert.Same(read, completed);
        Assert.Equal(GatewayState.Starting, (await read).Gateway.State);
        Assert.Equal(GatewayState.Ready, store.Read().Gateway.State);
    }

    [Fact]
    public async Task ReadersObserveOnlyWholeSnapshots()
    {
        GatewayStatusSnapshotStore store = new(CreateStatus(GatewayState.Ready, true));

        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < 1000; index++)
            {
                store.Replace(index % 2 == 0
                    ? CreateStatus(GatewayState.Ready, true)
                    : CreateStatus(GatewayState.Disconnected, false));
            }
        });

        for (int index = 0; index < 1000; index++)
        {
            GatewayStatusResult snapshot = store.Read();
            bool consistent =
                (snapshot.Gateway.State == GatewayState.Ready
                    && snapshot.Xae.Connected)
                || (snapshot.Gateway.State == GatewayState.Disconnected
                    && !snapshot.Xae.Connected);
            Assert.True(consistent);
        }

        await writer;
    }

    private static GatewayStatusResult CreateStatus(
        GatewayState state,
        bool connected)
    {
        GatewayStatusResult status =
            GatewayStatusSnapshotStore.CreateInitial("0.1.0");
        status.Gateway.State = state;
        status.Xae.Connected = connected;
        return status;
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

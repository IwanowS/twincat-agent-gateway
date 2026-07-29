using System;
using System.Runtime.InteropServices;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeSessionCoordinatorHealthTests
{
    [Theory]
    [InlineData(ErrorCodes.ComCallTimeout)]
    [InlineData(ErrorCodes.ComCallRejected)]
    public void TransientComFailureRetainsAttachedSession(
        string code)
    {
        GatewayOperationException exception = new(
            code,
            "Transient XAE health failure.",
            retryable: true,
            stage: "xae.verify");

        bool retained =
            XaeSessionCoordinator
                .ShouldRetainAttachmentAfterFailure(
                    attached: true,
                    exception);

        Assert.True(retained);
    }

    [Theory]
    [InlineData(unchecked((int)0x80010001))]
    [InlineData(unchecked((int)0x8001010A))]
    public void TransientComHResultRetainsAttachedSession(
        int hResult)
    {
        TestComException exception = new(
            "Transient COM rejection.",
            hResult);

        bool retained =
            XaeSessionCoordinator
                .ShouldRetainAttachmentAfterFailure(
                    attached: true,
                    exception);

        Assert.True(retained);
    }

    [Theory]
    [InlineData(ErrorCodes.XaeNotFound)]
    [InlineData(ErrorCodes.SolutionMismatch)]
    public void ProvenAttachmentLossUsesReconnectPath(
        string code)
    {
        GatewayOperationException exception = new(
            code,
            "The exact XAE session is no longer available.",
            retryable: true,
            stage: "xae.verify");

        bool retained =
            XaeSessionCoordinator
                .ShouldRetainAttachmentAfterFailure(
                    attached: true,
                    exception);

        Assert.False(retained);
    }

    [Fact]
    public void AttachFailureNeverRetainsSession()
    {
        GatewayOperationException exception = new(
            ErrorCodes.ComCallTimeout,
            "The initial attach timed out.",
            retryable: true,
            stage: "xae.attach");

        bool retained =
            XaeSessionCoordinator
                .ShouldRetainAttachmentAfterFailure(
                    attached: false,
                    exception);

        Assert.False(retained);
    }

    [Fact]
    public void RetainedHealthFailurePublishesFaultedWithExactIdentity()
    {
        GatewayStatusResult status = CreateConnectedStatus();
        XaeSessionSnapshot snapshot = CreateConnectedSnapshot();

        GatewayStatusResult updated =
            XaeSessionCoordinator.ApplyFailureStatus(
                status,
                snapshot,
                ErrorCodes.ComCallTimeout,
                retainAttachment: true);

        Assert.Equal(GatewayState.Faulted, updated.Gateway.State);
        Assert.NotEqual(
            GatewayState.Attaching,
            updated.Gateway.State);
        Assert.True(updated.Xae.Connected);
        Assert.Equal(
            "15.0",
            updated.Xae.Version);
        Assert.Equal(
            @"C:\work\Exact.sln",
            updated.Xae.Solution);
        Assert.True(updated.Xae.AgentWorkspaceOwned);
        Assert.Equal(
            SynchronizationState.SyncRequired,
            updated.Xae.SynchronizationState);
        Assert.Equal(2, updated.Xae.DirtyDocumentCount);
    }

    [Fact]
    public void ProvenLossClearsIdentityBeforeReconnect()
    {
        GatewayStatusResult status = CreateConnectedStatus();

        GatewayStatusResult updated =
            XaeSessionCoordinator.ApplyFailureStatus(
                status,
                CreateConnectedSnapshot(),
                ErrorCodes.XaeNotFound,
                retainAttachment: false);

        Assert.Equal(
            GatewayState.Disconnected,
            updated.Gateway.State);
        Assert.False(updated.Xae.Connected);
        Assert.Null(updated.Xae.Version);
        Assert.Null(updated.Xae.Solution);
        Assert.False(updated.Xae.AgentWorkspaceOwned);
        Assert.Equal(
            SynchronizationState.Uninitialized,
            updated.Xae.SynchronizationState);
        Assert.Equal(0, updated.Xae.DirtyDocumentCount);
    }

    private static GatewayStatusResult CreateConnectedStatus()
    {
        return new GatewayStatusResult
        {
            Gateway = new GatewayStatus
            {
                State = GatewayState.Ready,
            },
            Xae = new XaeStatus
            {
                Connected = true,
                Version = "15.0",
                Solution = @"C:\work\Exact.sln",
                AgentWorkspaceOwned = true,
                SynchronizationState =
                    SynchronizationState.Confirmed,
            },
        };
    }

    private static XaeSessionSnapshot CreateConnectedSnapshot()
    {
        return new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = new DteInstanceInfo
            {
                Version = "15.0",
                Solution = @"C:\work\Exact.sln",
                Selected = true,
            },
            AgentWorkspaceOwned = true,
            SynchronizationState =
                SynchronizationState.SyncRequired,
            DirtyDocumentCount = 2,
        };
    }

    private sealed class TestComException : COMException
    {
        public TestComException(
            string message,
            int hResult)
            : base(message, hResult)
        {
        }
    }
}

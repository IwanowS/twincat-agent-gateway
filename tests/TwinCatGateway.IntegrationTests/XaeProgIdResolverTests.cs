using System;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeProgIdResolverTests
{
    [Fact]
    public void MissingConfiguredProgIdFailsExplicitly()
    {
        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeProgIdResolver.ResolveCandidates(
                    "TwinCatGateway.Does.Not.Exist"));

        Assert.Equal(
            ErrorCodes.XaeProgIdNotRegistered,
            exception.Code);
        Assert.Equal("xae.resolveProgId", exception.Stage);
    }

    [XaeFact]
    public void DefaultCandidatesResolveToExistingExecutables()
    {
        XaeLaunchCandidate[] candidates =
            XaeProgIdResolver.ResolveCandidates(configuredProgId: null)
                .ToArray();

        Assert.NotEmpty(candidates);
        Assert.All(
            candidates,
            candidate => Assert.True(
                File.Exists(candidate.ExecutablePath),
                candidate.ExecutablePath));
        Assert.Contains(
            candidates,
            candidate => string.Equals(
                candidate.ProgId,
                "TcXaeShell.DTE.15.0",
                StringComparison.OrdinalIgnoreCase));
    }
}

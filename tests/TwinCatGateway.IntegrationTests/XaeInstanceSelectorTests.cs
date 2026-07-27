using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeInstanceSelectorTests
{
    private const string Solution = @"C:\Projects\Machine\Machine.sln";

    [Fact]
    public void ExactNormalizedPathIsSelected()
    {
        DteInstanceInfo[] instances =
        {
            Create(@"C:\Projects\Other\Other.sln"),
            Create(@"C:\Projects\Machine\.\Machine.sln"),
        };

        int selected = XaeInstanceSelector.Select(instances, Solution);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void MissingSolutionDoesNotFallBackToFirstInstance()
    {
        DteInstanceInfo[] instances =
        {
            Create(@"C:\Projects\Other\Other.sln"),
        };

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeInstanceSelector.Select(instances, Solution));

        Assert.Equal(ErrorCodes.XaeNotFound, exception.Code);
    }

    [Fact]
    public void MultipleExactMatchesFailClosed()
    {
        DteInstanceInfo[] instances =
        {
            Create(Solution),
            Create(Solution.ToUpperInvariant()),
        };

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeInstanceSelector.Select(instances, Solution));

        Assert.Equal(ErrorCodes.XaeMultipleMatches, exception.Code);
    }

    private static DteInstanceInfo Create(string solution)
    {
        return new DteInstanceInfo
        {
            Moniker = Guid.NewGuid().ToString(),
            Solution = solution,
            SolutionLoaded = true,
        };
    }
}

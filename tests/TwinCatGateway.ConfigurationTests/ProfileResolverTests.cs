using System;
using System.IO;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.ConfigurationTests;

public sealed class ProfileResolverTests
{
    [Fact]
    public void ResolveReturnsImmutableNormalizedProfile()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Target = CreateTarget();
        ProfileResolver resolver = new(configuration);

        ResolvedProfile first = resolver.Resolve(null);
        configuration.Profiles[0].Xae.Solution =
            @"C:\Changed\Changed.sln";
        configuration.Profiles[0].Target!.AmsNetId =
            "1.2.3.4.5.6";
        ResolvedProfile second = resolver.Resolve("BENCH");

        Assert.Equal(
            Path.GetFullPath(@"C:\Project\Machine.sln"),
            first.Xae.Solution);
        Assert.Same(first, second);
        Assert.Equal(
            "192.168.3.31.1.1",
            second.Target?.AmsNetId);
    }

    [Fact]
    public void ResolveUsesOnlyProfileWhenNoDefaultIsConfigured()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.DefaultProfile = null;

        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve(null);

        Assert.Equal("bench", profile.Name);
    }

    [Fact]
    public void ResolveSelectsConfiguredDefaultAcrossMultipleProfiles()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles.Add(
            new ProjectProfile
            {
                Name = "other",
                Xae = new XaeProfileConfiguration
                {
                    Solution = @"C:\Other\Other.sln",
                },
            });

        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve(null);

        Assert.Equal("bench", profile.Name);
    }

    [Fact]
    public void ResolveRejectsUnknownProfile()
    {
        ProfileResolver resolver = new(CreateConfiguration());

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => resolver.Resolve("missing"));

        Assert.Equal(ErrorCodes.ProfileNotFound, exception.Code);
        Assert.Equal("missing", exception.Expected?.Profile);
    }

    [Fact]
    public void RequireTargetRejectsBuildOnlyProfile()
    {
        ProfileResolver resolver = new(CreateConfiguration());
        ResolvedProfile profile = resolver.Resolve("bench");

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => resolver.RequireTarget(profile));

        Assert.Equal(ErrorCodes.TargetNotConfigured, exception.Code);
        Assert.Equal("bench", exception.Expected?.Profile);
    }

    [Fact]
    public void EnsureSolutionIdentityRejectsMismatchWithEvidence()
    {
        ProfileResolver resolver = new(CreateConfiguration());
        ResolvedProfile profile = resolver.Resolve("bench");

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => resolver.EnsureSolutionIdentity(
                    profile,
                    @"C:\Other\Other.sln",
                    "xae.identity"));

        Assert.Equal(ErrorCodes.XaeSolutionMismatch, exception.Code);
        Assert.Equal(profile.Xae.Solution, exception.Expected?.Solution);
        Assert.Equal(
            @"C:\Other\Other.sln",
            exception.Observed?.Solution);
    }

    [Fact]
    public void EnsureSolutionIdentityAcceptsNormalizedPath()
    {
        ProfileResolver resolver = new(CreateConfiguration());
        ResolvedProfile profile = resolver.Resolve("bench");

        resolver.EnsureSolutionIdentity(
            profile,
            Path.Combine(@"C:\Project", ".", "Machine.sln"),
            "xae.identity");
    }

    [Fact]
    public void EnsureTargetIdentityRejectsMismatchWithEvidence()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Target = CreateTarget();
        ProfileResolver resolver = new(configuration);
        ResolvedProfile profile = resolver.Resolve("bench");

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => resolver.EnsureTargetIdentity(
                    profile,
                    "1.2.3.4.5.6",
                    "xae.target.identity"));

        Assert.Equal(ErrorCodes.XaeTargetMismatch, exception.Code);
        Assert.Equal(
            "192.168.3.31.1.1",
            exception.Expected?.AmsNetId);
        Assert.Equal(
            "1.2.3.4.5.6",
            exception.Observed?.AmsNetId);
    }

    [Fact]
    public void ConstructorRejectsNonCanonicalAmsNetId()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Target = CreateTarget();
        configuration.Profiles[0].Target!.AmsNetId = "01.2.3.4.5.6";

        ArgumentException exception =
            Assert.Throws<ArgumentException>(
                () => new ProfileResolver(configuration));

        Assert.Contains(
            "profiles[0].target.amsNetId",
            exception.Message);
    }

    private static GatewayConfiguration CreateConfiguration()
    {
        return new GatewayConfiguration
        {
            DefaultProfile = "bench",
            Profiles =
            {
                new ProjectProfile
                {
                    Name = "bench",
                    Xae = new XaeProfileConfiguration
                    {
                        Solution = @"C:\Project\Machine.sln",
                    },
                },
            },
        };
    }

    private static TargetProfileConfiguration CreateTarget()
    {
        return new TargetProfileConfiguration
        {
            Name = "WIN-T077ADA",
            AmsNetId = "192.168.3.31.1.1",
        };
    }
}

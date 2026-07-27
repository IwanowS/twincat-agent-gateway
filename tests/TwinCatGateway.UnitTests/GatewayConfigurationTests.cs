using System;
using System.IO;
using System.Text.Json;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayConfigurationTests
{
    [Fact]
    public void LoaderReadsCommentsTrailingCommasAndCamelCaseEnums()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  // Local operator-owned configuration.
                  "schemaVersion": 1,
                  "defaultProfile": "bench",
                  "profiles": [
                    {
                      "name": "bench",
                      "solution": "C:\\Projects\\Machine\\Machine.sln",
                      "allowActivation": true,
                      "expectedTarget": {
                        "name": "WIN-T077ADA",
                        "amsNetId": "192.168.3.31.1.1"
                      },
                      "autoWaitForTcUnit": true,
                      "tcUnit": {
                        "reportPath": "C:\\Reports\\tcunit.xml",
                        "zeroTests": "warn",
                      },
                    },
                  ],
                }
                """);

            GatewayConfiguration configuration =
                new GatewayConfigurationLoader().Load(path);

            ProjectProfile profile = Assert.Single(configuration.Profiles);
            Assert.True(profile.AllowActivation);
            Assert.Equal("192.168.3.31.1.1", profile.ExpectedTarget?.AmsNetId);
            Assert.Equal(ZeroTestsPolicy.Warn, profile.TcUnit?.ZeroTests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidJsonIsNotSilentlyAccepted()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ invalid");

            Assert.Throws<JsonException>(
                () => new GatewayConfigurationLoader().Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ActivationProfileRequiresExactTargetIdentity()
    {
        GatewayConfiguration configuration = CreateValidConfiguration();
        configuration.Profiles[0].ExpectedTarget = new TargetIdentity
        {
            Name = "WIN-T077ADA",
            AmsNetId = null,
        };

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        ConfigurationIssue issue = Assert.Single(
            validation.Issues,
            candidate => candidate.Path.EndsWith(
                ".expectedTarget.amsNetId",
                StringComparison.Ordinal));
        Assert.Contains("six-part AMS NetId", issue.Message);
    }

    [Theory]
    [InlineData("192.168.3.31.1")]
    [InlineData("192.168.3.31.1.256")]
    [InlineData("192.168.3.031.1.1")]
    [InlineData("not-an-ams-net-id")]
    public void InvalidAmsNetIdFailsClosed(string amsNetId)
    {
        GatewayConfiguration configuration = CreateValidConfiguration();
        configuration.Profiles[0].ExpectedTarget!.AmsNetId = amsNetId;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path.EndsWith(
                ".expectedTarget.amsNetId",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TcUnitAutoWaitRequiresConfiguredReport()
    {
        GatewayConfiguration configuration = CreateValidConfiguration();
        configuration.Profiles[0].TcUnit!.ReportPath = string.Empty;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path.EndsWith(
                ".tcUnit.reportPath",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogReturnsDefensiveProfileCopies()
    {
        ProjectProfileCatalog catalog = new(CreateValidConfiguration());

        ProjectProfile first = catalog.GetRequired(null);
        first.ExpectedTarget!.Name = "mutated";
        ProjectProfile second = catalog.GetRequired("BENCH");

        Assert.Equal("WIN-T077ADA", second.ExpectedTarget?.Name);
    }

    private static GatewayConfiguration CreateValidConfiguration()
    {
        return new GatewayConfiguration
        {
            DefaultProfile = "bench",
            LogDirectory = @"C:\GatewayLogs",
            Profiles =
            {
                new ProjectProfile
                {
                    Name = "bench",
                    Solution = @"C:\Projects\Machine\Machine.sln",
                    AllowActivation = true,
                    ExpectedTarget = new TargetIdentity
                    {
                        Name = "WIN-T077ADA",
                        AmsNetId = "192.168.3.31.1.1",
                    },
                    AutoWaitForTcUnit = true,
                    TcUnit = new TcUnitProfile
                    {
                        ReportPath = @"C:\Reports\tcunit.xml",
                    },
                },
            },
        };
    }
}

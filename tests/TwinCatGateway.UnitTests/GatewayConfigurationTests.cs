using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayConfigurationTests
{
    [Fact]
    public void ShippedExampleIsValidAndActivationSafe()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "twincat-gateway.example.json");

        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(path);
        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);
        ProjectProfile profile = Assert.Single(
            configuration.Profiles);

        Assert.True(validation.IsValid);
        Assert.Equal(
            GatewayUiMode.Auto,
            configuration.Ui.Mode);
        Assert.True(
            configuration.AgentProcessControl.AllowStart);
        Assert.False(
            configuration.AgentProcessControl.AllowShutdown);
        Assert.Equal(
            Path.Combine(
                AppContext.BaseDirectory,
                "Machine.sln"),
            profile.Solution,
            ignoreCase: true);
        Assert.False(profile.AllowActivation);
        Assert.Null(profile.ExpectedTarget);
        Assert.Null(profile.TcUnit);
    }

    [Fact]
    public void DocumentedJsonExamplesAreValid()
    {
        string documentation = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "CONFIGURATION.md"));
        MatchCollection examples = Regex.Matches(
            documentation,
            "```json\\s*(.*?)\\s*```",
            RegexOptions.Singleline
                | RegexOptions.CultureInvariant);
        Assert.Equal(2, examples.Count);

        string directory = Path.Combine(
            Path.GetTempPath(),
            "TwinCatGatewayTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (int index = 0; index < examples.Count; index++)
            {
                string path = Path.Combine(
                    directory,
                    $"example-{index}.json");
                File.WriteAllText(
                    path,
                    examples[index].Groups[1].Value);
                GatewayConfiguration configuration =
                    new GatewayConfigurationLoader().Load(path);
                ConfigurationValidationResult validation =
                    GatewayConfigurationValidator.Validate(
                        configuration);

                Assert.True(
                    validation.IsValid,
                    string.Join(
                        Environment.NewLine,
                        validation.Issues));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
                      "allowXaeLaunch": true,
                      "xaeProgId": "VisualStudio.DTE.16.0",
                      "allowActivation": true,
                      "externalChangePolicy": "reloadAll",
                      "allowAgentForceSynchronization": true,
                      "allowDirtyDocumentDiscard": true,
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
            Assert.True(profile.AllowXaeLaunch);
            Assert.Equal("VisualStudio.DTE.16.0", profile.XaeProgId);
            Assert.True(profile.AllowActivation);
            Assert.Equal(
                ExternalChangePolicy.ReloadAll,
                profile.ExternalChangePolicy);
            Assert.True(
                profile.AllowAgentForceSynchronization);
            Assert.True(profile.AllowDirtyDocumentDiscard);
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
    public void SaveAtomicallyPersistsCamelCaseConfiguration()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TwinCatGatewayTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "gateway.json");
        try
        {
            File.WriteAllText(path, "{}");
            GatewayConfiguration configuration =
                CreateValidConfiguration();
            GatewayConfigurationLoader loader = new();

            loader.Save(path, configuration);

            string json = File.ReadAllText(path);
            GatewayConfiguration saved = loader.Load(path);
            Assert.Contains("\"defaultProfile\": \"bench\"", json);
            Assert.DoesNotContain("\"DefaultProfile\"", json);
            Assert.Equal("bench", saved.DefaultProfile);
            Assert.Empty(
                Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveRejectsInvalidConfigurationBeforeReplacingFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            const string original = "operator-owned content";
            File.WriteAllText(path, original);
            GatewayConfiguration configuration =
                CreateValidConfiguration();
            configuration.LogRetentionDays = 0;

            Assert.Throws<ArgumentException>(
                () => new GatewayConfigurationLoader().Save(
                    path,
                    configuration));

            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ActivationProfileRequiresExactAmsNetId()
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

    [Fact]
    public void NullProfilesAreReportedAsInvalid()
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration();
        configuration.Profiles = null!;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(
                configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path == "profiles");
    }

    [Fact]
    public void ActivationProfileAllowsMissingDisplayName()
    {
        GatewayConfiguration configuration = CreateValidConfiguration();
        configuration.Profiles[0].ExpectedTarget!.Name = null;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.DoesNotContain(
            validation.Issues,
            issue => issue.Path.EndsWith(
                ".expectedTarget.name",
                StringComparison.Ordinal));
        Assert.True(validation.IsValid);
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
    public void WhitespaceXaeProgIdIsRejected()
    {
        GatewayConfiguration configuration = CreateValidConfiguration();
        configuration.Profiles[0].XaeProgId = " ";

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path.EndsWith(
                ".xaeProgId",
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
        Assert.Equal(
            ExternalChangePolicy.ReloadAll,
            second.ExternalChangePolicy);
        Assert.True(second.AllowAgentForceSynchronization);
        Assert.True(second.AllowDirtyDocumentDiscard);
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
                    ExternalChangePolicy =
                        ExternalChangePolicy.ReloadAll,
                    AllowAgentForceSynchronization = true,
                    AllowDirtyDocumentDiscard = true,
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

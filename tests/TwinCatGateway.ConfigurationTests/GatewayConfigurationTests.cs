using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.ConfigurationTests;

public sealed class GatewayConfigurationTests
{
    [Fact]
    public void V2DefaultsAreSafe()
    {
        GatewayConfiguration configuration = new();
        ProjectProfile profile = new();

        Assert.Equal(2, configuration.SchemaVersion);
        Assert.True(configuration.Gateway.ProcessControl.AllowStart);
        Assert.False(configuration.Gateway.ProcessControl.AllowShutdown);
        Assert.True(profile.Xae.Capabilities.Launch);
        Assert.True(profile.Xae.Capabilities.Synchronize);
        Assert.True(profile.Xae.Capabilities.Build);
        Assert.False(profile.Xae.Capabilities.Close);
        Assert.False(profile.Xae.Capabilities.DiscardDirtyDocuments);
        Assert.False(profile.Xae.Capabilities.Activate);
        Assert.Null(profile.Target);
    }

    [Fact]
    public void ShippedExampleIsValidAndBuildOnly()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "twincat-gateway.example.json");
        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(path);
        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);
        ProjectProfile profile = Assert.Single(configuration.Profiles);

        Assert.True(
            validation.IsValid,
            string.Join(Environment.NewLine, validation.Issues));
        Assert.Equal(2, configuration.SchemaVersion);
        Assert.Equal("default", profile.Name);
        Assert.EndsWith(
            "Machine.sln",
            profile.Xae.Solution,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(profile.Target);
        Assert.False(profile.Xae.Capabilities.Activate);
    }

    [Fact]
    public void DocumentedJsonExampleIsValid()
    {
        string documentation = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "CONFIGURATION.md"));
        MatchCollection examples = Regex.Matches(
            documentation,
            "```json\\s*(.*?)\\s*```",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Match example = Assert.Single(examples.Cast<Match>());
        using TemporaryDirectory directory = new();
        string path = directory.Write(
            "documented.json",
            example.Groups[1].Value);

        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(path);
        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.True(
            validation.IsValid,
            string.Join(Environment.NewLine, validation.Issues));
    }

    [Fact]
    public void LoaderAcceptsCommentsTrailingCommasAndCamelCaseEnums()
    {
        using TemporaryDirectory directory = new();
        string path = directory.Write(
            "twincat-gateway.json",
            """
            {
              // target schema
              "schemaVersion": 2,
              "gateway": {
                "logging": {
                  "directory": "logs",
                  "minimumLevel": "debug",
                },
              },
              "profiles": [
                {
                  "name": "bench",
                  "xae": {
                    "solution": "Machine.sln",
                    "workspace": {
                      "externalChangePolicy": "reloadAll",
                    },
                  },
                },
              ],
            }
            """);

        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(path);
        ProjectProfile profile = Assert.Single(configuration.Profiles);

        Assert.Equal(
            GatewayLogLevel.Debug,
            configuration.Gateway.Logging.MinimumLevel);
        Assert.Equal(
            Path.Combine(directory.Path, "logs"),
            configuration.Gateway.Logging.Directory);
        Assert.Equal(
            Path.Combine(directory.Path, "Machine.sln"),
            profile.Xae.Solution);
        Assert.Equal(
            ExternalChangePolicy.ReloadAll,
            profile.Xae.Workspace.ExternalChangePolicy);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"profiles\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"profiles\":[]}")]
    [InlineData("{\"schemaVersion\":\"2\",\"profiles\":[]}")]
    public void MissingOrV1SchemaIsRejectedWithStableCode(string json)
    {
        using TemporaryDirectory directory = new();
        string path = directory.Write("unsupported.json", json);

        GatewayConfigurationException exception =
            Assert.Throws<GatewayConfigurationException>(
                () => new GatewayConfigurationLoader().Load(path));

        Assert.Equal(
            ErrorCodes.ConfigVersionUnsupported,
            exception.Code);
    }

    [Theory]
    [InlineData(
        "{\"schemaVersion\":2,\"pipeName\":\"legacy\",\"profiles\":[]}")]
    [InlineData(
        "{\"schemaVersion\":2,\"gateway\":{},\"profiles\":[{\"name\":\"bench\",\"solution\":\"Machine.sln\"}]}")]
    public void MixedV1AndV2ShapeIsRejected(string json)
    {
        using TemporaryDirectory directory = new();
        string path = directory.Write("mixed.json", json);

        GatewayConfigurationException exception =
            Assert.Throws<GatewayConfigurationException>(
                () => new GatewayConfigurationLoader().Load(path));

        Assert.Equal(
            ErrorCodes.ConfigVersionUnsupported,
            exception.Code);
    }

    [Fact]
    public void InvalidJsonIsNotSilentlyAccepted()
    {
        using TemporaryDirectory directory = new();
        string path = directory.Write("invalid.json", "{");

        Assert.ThrowsAny<JsonException>(
            () => new GatewayConfigurationLoader().Load(path));
    }

    [Fact]
    public void SavePersistsOnlyGroupedV2Shape()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "saved.json");
        GatewayConfiguration source = CreateValidConfiguration(directory.Path);
        GatewayConfigurationLoader loader = new();

        loader.Save(path, source);
        string json = File.ReadAllText(path);
        GatewayConfiguration saved = loader.Load(path);

        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.Contains("\"gateway\":", json);
        Assert.Contains("\"xae\":", json);
        Assert.Contains("\"capabilities\":", json);
        Assert.DoesNotContain("\"agentProcessControl\"", json);
        Assert.DoesNotContain("\"runtimeMonitoring\"", json);
        Assert.DoesNotContain("\"expectedTarget\"", json);
        Assert.DoesNotContain("\"recentBuildMaxAgeSeconds\"", json);
        Assert.Equal(
            source.Profiles[0].Xae.Solution,
            saved.Profiles[0].Xae.Solution);
    }

    [Theory]
    [InlineData(65535, 10, 14, false)]
    [InlineData(65536, 10, 14, true)]
    [InlineData(1073741824, 1000, 3650, true)]
    [InlineData(65535, 0, 14, false)]
    [InlineData(65536, 10, 0, false)]
    public void LoggingBoundsAreValidated(
        long size,
        int retained,
        int days,
        bool expectedValid)
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration(@"C:\Project");
        configuration.Gateway.Logging.FileSizeLimitBytes = size;
        configuration.Gateway.Logging.RetainedFileCountLimit = retained;
        configuration.Gateway.Logging.RetentionDays = days;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Equal(expectedValid, validation.IsValid);
    }

    [Theory]
    [InlineData("192.168.3.31.1")]
    [InlineData("192.168.3.31.1.256")]
    [InlineData("192.168.3.31.01.1")]
    [InlineData("machine")]
    public void InvalidAmsNetIdFailsClosed(string amsNetId)
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration(@"C:\Project");
        configuration.Profiles[0].Target = CreateTarget();
        configuration.Profiles[0].Target!.AmsNetId = amsNetId;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path == "profiles[0].target.amsNetId");
    }

    [Fact]
    public void ActivationRequiresTarget()
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration(@"C:\Project");
        configuration.Profiles[0].Xae.Capabilities.Activate = true;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path == "profiles[0].target");
    }

    [Fact]
    public void TcUnitVerificationRequiresConfiguredContract()
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration(@"C:\Project");
        TargetProfileConfiguration target = CreateTarget();
        target.Capabilities.TcUnitVerification = true;
        configuration.Profiles[0].Target = target;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path == "profiles[0].target.tcUnit");
    }

    [Fact]
    public void TcUnitRuntimeIdentityIsRequired()
    {
        GatewayConfiguration configuration =
            CreateValidConfiguration(@"C:\Project");
        TargetProfileConfiguration target = CreateTarget();
        target.TcUnit = new TcUnitProfile
        {
            AdsPort = 851,
            ReportPath = @"C:\Reports\tcunit.xml",
        };
        configuration.Profiles[0].Target = target;

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);

        Assert.Contains(
            validation.Issues,
            issue => issue.Path
                == "profiles[0].target.tcUnit.runtimeId");
    }

    private static GatewayConfiguration CreateValidConfiguration(
        string directory)
    {
        return new GatewayConfiguration
        {
            DefaultProfile = "bench",
            Gateway = new GatewaySettingsConfiguration
            {
                Logging = new GatewayLoggingConfiguration
                {
                    Directory = Path.Combine(directory, "logs"),
                },
            },
            Profiles =
            {
                new ProjectProfile
                {
                    Name = "bench",
                    Xae = new XaeProfileConfiguration
                    {
                        Solution = Path.Combine(
                            directory,
                            "Machine.sln"),
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayConfigurationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

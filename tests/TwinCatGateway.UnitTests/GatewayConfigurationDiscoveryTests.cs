using System;
using System.IO;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayConfigurationDiscoveryTests
{
    [Fact]
    public void NearestConfigurationWins()
    {
        using TemporaryDirectory temporary = new();
        string parent = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "parent")).FullName;
        string child = Directory.CreateDirectory(
            Path.Combine(parent, "child")).FullName;
        string outer = WriteConfiguration(temporary.Path);
        string nearest = WriteConfiguration(parent);

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                explicitPath: null,
                workspaceRoots: null,
                child);

        Assert.Equal(
            nearest,
            location.Path,
            ignoreCase: true);
        Assert.False(
            string.Equals(
                outer,
                location.Path,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            GatewayConfigurationSource.CurrentDirectory,
            location.Source);
    }

    [Fact]
    public void GitRootIsCheckedButSearchDoesNotEscapeIt()
    {
        using TemporaryDirectory temporary = new();
        WriteConfiguration(temporary.Path);
        string repository = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "repository")).FullName;
        Directory.CreateDirectory(
            Path.Combine(repository, ".git"));
        string child = Directory.CreateDirectory(
            Path.Combine(repository, "src", "child")).FullName;

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayConfigurationDiscovery.Discover(
                    explicitPath: null,
                    workspaceRoots: null,
                    child));

        Assert.Equal(
            ErrorCodes.GatewayConfigNotFound,
            exception.Code);
    }

    [Fact]
    public void ConfigurationAtGitRootIsFound()
    {
        using TemporaryDirectory temporary = new();
        string repository = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "repository")).FullName;
        File.WriteAllText(
            Path.Combine(repository, ".git"),
            "gitdir: elsewhere");
        string expected = WriteConfiguration(repository);
        string child = Directory.CreateDirectory(
            Path.Combine(repository, "src", "child")).FullName;

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                explicitPath: null,
                workspaceRoots: null,
                child);

        Assert.Equal(
            expected,
            location.Path,
            ignoreCase: true);
    }

    [Fact]
    public void ExplicitRelativePathUsesCurrentDirectory()
    {
        using TemporaryDirectory temporary = new();
        string configurationDirectory =
            Directory.CreateDirectory(
                Path.Combine(temporary.Path, "config")).FullName;
        string expected = Path.Combine(
            configurationDirectory,
            "custom.json");
        File.WriteAllText(expected, "{}");

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                Path.Combine("config", "custom.json"),
                workspaceRoots: null,
                temporary.Path);

        Assert.Equal(
            Path.GetFullPath(expected),
            location.Path,
            ignoreCase: true);
        Assert.Equal(
            GatewayConfigurationSource.Explicit,
            location.Source);
    }

    [Fact]
    public void MissingConfigurationReturnsStableError()
    {
        using TemporaryDirectory temporary = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayConfigurationDiscovery.Discover(
                    explicitPath: null,
                    workspaceRoots: null,
                    temporary.Path));

        Assert.Equal(
            ErrorCodes.GatewayConfigNotFound,
            exception.Code);
        Assert.Equal(
            "gateway.config.discover",
            exception.Stage);
    }

    [Fact]
    public void DifferentWorkspaceConfigurationsAreAmbiguous()
    {
        using TemporaryDirectory temporary = new();
        string first = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "first")).FullName;
        string second = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "second")).FullName;
        WriteConfiguration(first);
        WriteConfiguration(second);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => GatewayConfigurationDiscovery.Discover(
                    explicitPath: null,
                    workspaceRoots: new[] { first, second },
                    temporary.Path));

        Assert.Equal(
            ErrorCodes.GatewayConfigAmbiguous,
            exception.Code);
    }

    [Fact]
    public void MultipleWorkspaceRootsMayShareOneConfiguration()
    {
        using TemporaryDirectory temporary = new();
        string expected = WriteConfiguration(temporary.Path);
        string first = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "first")).FullName;
        string second = Directory.CreateDirectory(
            Path.Combine(temporary.Path, "second")).FullName;

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                explicitPath: null,
                workspaceRoots: new[] { first, second },
                currentDirectory: first);

        Assert.Equal(
            expected,
            location.Path,
            ignoreCase: true);
        Assert.Equal(
            GatewayConfigurationSource.WorkspaceRoot,
            location.Source);
    }

    [Fact]
    public void LoaderResolvesProjectPathsFromConfigurationDirectory()
    {
        using TemporaryDirectory temporary = new();
        string configurationDirectory =
            Directory.CreateDirectory(
                Path.Combine(temporary.Path, "config")).FullName;
        string configurationPath = Path.Combine(
            configurationDirectory,
            GatewayConfigurationDiscovery.FileName);
        File.WriteAllText(
            configurationPath,
            """
            {
              "schemaVersion": 1,
              "logDirectory": "logs",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "project\\Machine.sln",
                  "allowActivation": false,
                  "tcUnit": {
                    "reportPath": "reports\\tcunit.xml"
                  }
                }
              ]
            }
            """);

        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(
                configurationPath);
        ProjectProfile profile = Assert.Single(
            configuration.Profiles);

        Assert.Equal(
            Path.Combine(configurationDirectory, "logs"),
            configuration.LogDirectory,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(
                configurationDirectory,
                "project",
                "Machine.sln"),
            profile.Solution,
            ignoreCase: true);
        Assert.Equal(
            Path.Combine(
                configurationDirectory,
                "reports",
                "tcunit.xml"),
            profile.TcUnit?.ReportPath,
            ignoreCase: true);
    }

    private static string WriteConfiguration(
        string directory)
    {
        string path = Path.Combine(
            directory,
            GatewayConfigurationDiscovery.FileName);
        File.WriteAllText(path, "{}");
        return Path.GetFullPath(path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

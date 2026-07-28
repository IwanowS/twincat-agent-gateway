using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class McpCommandLineTests
{
    private static readonly string[] ExplicitOptions =
    {
        "--config",
        @"C:\Projects\Machine\twincat-gateway.json",
        "--pipe",
        "FixturePipe",
        "--gateway-command",
        @"C:\Tools\twincat-gateway.cmd",
    };

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpIsGeneratedWithoutStartingServer(
        string helpOption)
    {
        int starts = 0;
        RootCommand root = McpCommandLine.CreateRootCommand(
            (_, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        using StringWriter output =
            new(CultureInfo.InvariantCulture);
        using StringWriter error =
            new(CultureInfo.InvariantCulture);

        int exitCode = await root
            .Parse(new[] { helpOption })
            .InvokeAsync(
                new InvocationConfiguration
                {
                    Output = output,
                    Error = error,
                });
        string help = output.ToString();

        Assert.Equal(0, exitCode);
        Assert.Equal(0, starts);
        Assert.Contains(
            "Stdio MCP adapter for TwinCAT Agent Gateway.",
            help);
        Assert.Contains("[options]", help);
        Assert.Contains("--config <path>", help);
        Assert.Contains("--pipe <name>", help);
        Assert.Contains("--gateway-command <command>", help);
        Assert.Contains("[default: twincat-gateway]", help);
        Assert.Contains("-h, --help", help);
        Assert.Contains("--version", help);
        Assert.Contains("Examples:", help);
        Assert.Contains(
            "twincat-gateway-mcp --config",
            help);
        Assert.Contains(
            "twincat-gateway-mcp --pipe",
            help);
        Assert.Contains(
            "twincat-gateway-mcp --gateway-command",
            help);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task VersionIsGeneratedWithoutStartingServer()
    {
        int starts = 0;
        RootCommand root = McpCommandLine.CreateRootCommand(
            (_, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        using StringWriter output =
            new(CultureInfo.InvariantCulture);

        int exitCode = await root
            .Parse("--version")
            .InvokeAsync(
                new InvocationConfiguration
                {
                    Output = output,
                });

        Assert.Equal(0, exitCode);
        Assert.Equal(0, starts);
        Assert.Matches(
            @"\d+\.\d+\.\d+",
            output.ToString());
    }

    [Fact]
    public async Task ParsedOptionsArePassedToServerAction()
    {
        GatewayMcpOptions? captured = null;
        RootCommand root = McpCommandLine.CreateRootCommand(
            (options, _) =>
            {
                captured = options;
                return Task.CompletedTask;
            });

        int exitCode = await root
            .Parse(ExplicitOptions)
            .InvokeAsync();

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(
            @"C:\Projects\Machine\twincat-gateway.json",
            captured.ExplicitConfigurationPath);
        Assert.Equal(
            "FixturePipe",
            captured.PipeNameOverride);
        Assert.Equal(
            @"C:\Tools\twincat-gateway.cmd",
            captured.GatewayCommand);
    }

    [Fact]
    public async Task InvalidOptionFailsBeforeStartingServer()
    {
        int starts = 0;
        RootCommand root = McpCommandLine.CreateRootCommand(
            (_, _) =>
            {
                starts++;
                return Task.CompletedTask;
            });
        using StringWriter error =
            new(CultureInfo.InvariantCulture);

        int exitCode = await root
            .Parse("--unknown")
            .InvokeAsync(
                new InvocationConfiguration
                {
                    Error = error,
                });

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, starts);
        Assert.Contains("--unknown", error.ToString());
    }
}

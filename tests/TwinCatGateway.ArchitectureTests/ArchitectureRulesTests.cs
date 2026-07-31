using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace TwinCatGateway.ArchitectureTests;

public sealed class ArchitectureRulesTests
{
    private static readonly string[] XaeInteropNamespaces =
    {
        "EnvDTE",
        "EnvDTE80",
        "Microsoft.VisualStudio.Shell.Interop"
    };

    private static readonly string[] AdsNamespaces =
    {
        "TwinCAT.Ads",
        "Beckhoff.TwinCAT.Ads"
    };

    private static readonly string[] McpForbiddenNamespaces = new[]
        {
            "TwinCatGateway.Ads",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Xae"
        }
        .Concat(XaeInteropNamespaces)
        .Concat(AdsNamespaces)
        .ToArray();

    private static readonly Dictionary<string, Types> ProductionTypes = LoadProductionTypes();

    [Fact]
    public void ContractsDoesNotDependOnOtherGatewayAssemblies()
    {
        AssertNoDependencies(
            "TwinCatGateway.Contracts",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Cli",
            "TwinCatGateway.Client",
            "TwinCatGateway.Core",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Ipc",
            "TwinCatGateway.Mcp",
            "TwinCatGateway.Xae");
    }

    [Fact]
    public void CoreKeepsItsDomainBoundary()
    {
        AssertNoDependencies(
            "TwinCatGateway.Core",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Client",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Ipc",
            "TwinCatGateway.Mcp",
            "TwinCatGateway.Xae");
    }

    [Fact]
    public void IpcKeepsItsTransportBoundary()
    {
        AssertNoDependencies(
            "TwinCatGateway.Ipc",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Client",
            "TwinCatGateway.Core",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Mcp",
            "TwinCatGateway.Xae");
    }

#if NET8_0
    [Fact]
    public void ClientDoesNotDependOnHostOrDesktopAssemblies()
    {
        AssertNoDependencies(
            "TwinCatGateway.Client",
            "TwinCatGateway.Core",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Mcp",
            "TwinCatGateway.Xae");
    }

    [Fact]
    public void CliDoesNotDependOnHostOrDesktopAssemblies()
    {
        AssertNoDependencies(
            "TwinCatGateway.Cli",
            "TwinCatGateway.Core",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Mcp",
            "TwinCatGateway.Xae");
    }

    [Fact]
    public void McpDoesNotDependOnDesktopOrAutomationAssemblies()
    {
        AssertNoDependencies(
            "TwinCatGateway.Mcp",
            McpForbiddenNamespaces);
    }
#endif

#if NET48
    [Fact]
    public void AdsKeepsItsAdapterBoundary()
    {
        AssertNoDependencies(
            "TwinCatGateway.Ads",
            "TwinCatGateway.Core",
            "TwinCatGateway.Ipc",
            "TwinCatGateway.Xae",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Client",
            "TwinCatGateway.Mcp");
    }

    [Fact]
    public void XaeKeepsComAutomationInsideItsBoundary()
    {
        AssertNoDependencies(
            "TwinCatGateway.Xae",
            "TwinCatGateway.Ads",
            "TwinCatGateway.Ipc",
            "TwinCatGateway.Desktop",
            "TwinCatGateway.Client",
            "TwinCatGateway.Mcp");
    }

    [Fact]
    public void DesktopPresentationTypesDoNotUseAutomationOrAdsDirectly()
    {
        var forbiddenNamespaces = XaeInteropNamespaces.Concat(AdsNamespaces).ToArray();
        var result = ProductionTypes["TwinCatGateway.Desktop"]
            .That()
            .HaveNameEndingWith("ViewModel")
            .Or()
            .HaveNameEndingWith("Row")
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        AssertSuccessful(
            "Desktop types ending in ViewModel or Row must not use XAE COM or ADS directly.",
            result);
    }
#endif

    [Fact]
    public void NonXaeProductionAssembliesDoNotUseXaeInteropDirectly()
    {
        foreach (var assemblyName in ProductionTypes.Keys.Where(name => name != "TwinCatGateway.Xae"))
        {
            AssertNoDependencies(assemblyName, XaeInteropNamespaces);
        }
    }

    private static Dictionary<string, Types> LoadProductionTypes()
    {
#if NET8_0
        var assemblyNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TwinCatGateway.Contracts"] = "TwinCatGateway.Contracts",
            ["TwinCatGateway.Core"] = "TwinCatGateway.Core",
            ["TwinCatGateway.Ipc"] = "TwinCatGateway.Ipc",
            ["TwinCatGateway.Client"] = "TwinCatGateway.Client",
            ["TwinCatGateway.Mcp"] = "twincat-gateway-mcp"
        };
#elif NET48
        var assemblyNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TwinCatGateway.Contracts"] = "TwinCatGateway.Contracts",
            ["TwinCatGateway.Core"] = "TwinCatGateway.Core",
            ["TwinCatGateway.Ipc"] = "TwinCatGateway.Ipc",
            ["TwinCatGateway.Ads"] = "TwinCatGateway.Ads",
            ["TwinCatGateway.Xae"] = "TwinCatGateway.Xae"
        };
#else
#error Unsupported target framework for architecture tests.
#endif

        var loadedTypes = assemblyNames.ToDictionary(
            pair => pair.Key,
            pair => Types.InAssembly(Assembly.Load(pair.Value)),
            StringComparer.Ordinal);

#if NET8_0
        loadedTypes.Add(
            "TwinCatGateway.Cli",
            LoadExecutableTypes("TwinCatGateway.Cli", "net8.0", "twincat-gateway.dll"));
#elif NET48
        loadedTypes.Add(
            "TwinCatGateway.Desktop",
            LoadExecutableTypes("TwinCatGateway.Desktop", "net48", "twincat-gateway.exe"));
#endif

        return loadedTypes;
    }

    private static Types LoadExecutableTypes(string projectName, string targetFramework, string fileName)
    {
        var targetFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = targetFrameworkDirectory.Parent
            ?? throw new InvalidOperationException("Could not determine the test build configuration directory.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var executablePath = Path.Combine(
            repositoryRoot,
            "src",
            projectName,
            "bin",
            configurationDirectory.Name,
            targetFramework,
            fileName);

        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                $"Architecture input was not built: {executablePath}. Build TwinCatGateway.sln before running architecture tests.");
        }

        return Types.FromFile(executablePath);
    }

    private static void AssertNoDependencies(string assemblyName, params string[] forbiddenNamespaces)
    {
        var result = ProductionTypes[assemblyName]
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        AssertSuccessful(
            $"{assemblyName} must not depend on: {string.Join(", ", forbiddenNamespaces)}.",
            result);
    }

    private static void AssertSuccessful(string rule, TestResult result)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var failures = result.FailingTypes.Select(
            type => $"{type.FullName}: {type.Explanation ?? "dependency explanation unavailable"}");

        Assert.True(result.IsSuccessful, $"{rule}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

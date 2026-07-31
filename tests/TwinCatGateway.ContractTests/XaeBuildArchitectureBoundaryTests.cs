using System;
using System.IO;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class XaeBuildArchitectureBoundaryTests
{
    [Fact]
    public void StandaloneBuildDoesNotReadTargetOrRuntimeState()
    {
        string source = File.ReadAllText(
            RepositoryFile(
                "src",
                "TwinCatGateway.Desktop",
                "XaeSessionCoordinator.cs"));
        string buildMethod = Slice(
            source,
            "public async Task<XaeBuildResult> ExecuteXaeBuildAsync(",
            "public async Task<SynchronizeResult> ExecuteSynchronizationAsync(");

        Assert.DoesNotContain("ReadRuntimeStatus", buildMethod);
        Assert.DoesNotContain("ReadRuntimeExceptionDetailsAsync", buildMethod);
        Assert.DoesNotContain("RuntimeOperationPolicy", buildMethod);
        Assert.DoesNotContain("AdsRuntimeStatusReadResult", buildMethod);
        Assert.DoesNotContain("_runtimeMonitor", buildMethod);
    }

    [Fact]
    public void RuntimeRecoveryPolicyIsRemoved()
    {
        Assert.False(File.Exists(
            RepositoryFile(
                "src",
                "TwinCatGateway.Core",
                "RuntimeOperationPolicy.cs")));
    }

    private static string Slice(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(
            startMarker,
            StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(
            endMarker,
            start,
            StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source.Substring(start, end - start);
    }

    private static string RepositoryFile(params string[] path)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "TwinCatGateway.sln")))
            {
                string[] segments = new string[path.Length + 1];
                segments[0] = current.FullName;
                Array.Copy(path, 0, segments, 1, path.Length);
                return Path.Combine(segments);
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root from the test output.");
    }
}

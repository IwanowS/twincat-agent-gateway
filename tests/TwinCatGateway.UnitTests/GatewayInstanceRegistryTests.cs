using System;
using System.Diagnostics;
using System.IO;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayInstanceRegistryTests
{
    [Fact]
    public void RegistrationPublishesAndRemovesOwnedRecord()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "instance.json");
        GatewayInstanceRegistry registry = new(path);
        GatewayInstanceRecord record =
            CreateRecord("first");

        using (registry.Register(record))
        {
            GatewayInstanceRecord published =
                Assert.IsType<GatewayInstanceRecord>(
                    registry.Read());
            Assert.Equal(
                record.InstanceId,
                published.InstanceId);
            Assert.Equal(
                record.ConfigurationPath,
                published.ConfigurationPath);
        }

        Assert.Null(registry.Read());
    }

    [Fact]
    public void OlderRegistrationCannotDeleteReplacement()
    {
        using TemporaryDirectory temporary = new();
        GatewayInstanceRegistry registry = new(
            Path.Combine(
                temporary.Path,
                "instance.json"));
        GatewayInstanceRegistration first =
            registry.Register(CreateRecord("first"));
        GatewayInstanceRegistration second =
            registry.Register(CreateRecord("second"));

        first.Dispose();

        GatewayInstanceRecord published =
            Assert.IsType<GatewayInstanceRecord>(
                registry.Read());
        Assert.Contains(
            "second",
            published.ConfigurationPath,
            StringComparison.OrdinalIgnoreCase);

        second.Dispose();
        Assert.Null(registry.Read());
    }

    private static GatewayInstanceRecord CreateRecord(
        string project)
    {
        using Process process = Process.GetCurrentProcess();
        return new GatewayInstanceRecord
        {
            ProcessId = process.Id,
            ProcessStartedAtUtc =
                process.StartTime.ToUniversalTime(),
            PipeName = "test-pipe",
            ConfigurationPath = Path.Combine(
                Path.GetTempPath(),
                project,
                "twincat-gateway.json"),
            ActiveProfile = "fixture",
            SolutionPath = Path.Combine(
                Path.GetTempPath(),
                project,
                "fixture.sln"),
            LaunchSource = GatewayLaunchSource.Agent,
            UiMode = GatewayUiMode.Tray,
        };
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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

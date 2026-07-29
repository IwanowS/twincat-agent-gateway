using System;
using System.IO;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Desktop;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class GatewayLoggingTests
{
    [Fact]
    public void StructuredLoggerKeepsDetailedExceptionInLocalLog()
    {
        using TemporaryDirectory temporary = new();
        string path;
        using (GatewayLoggingSession logging =
               GatewayLoggingSession.Create(temporary.Path))
        {
            path = logging.Path;
            OperationExceptionLoggingSink sink = new(
                logging.CreateLogger<GatewayLoggingTests>());
            InvalidOperationException exception =
                new("detailed failure");

            sink.Record("operation-1", exception);
        }

        string content = File.ReadAllText(path);
        Assert.Contains(
            "\"EventName\":\"operation.exception\"",
            content);
        Assert.Contains(
            "\"OperationId\":\"operation-1\"",
            content);
        Assert.Contains(
            "System.InvalidOperationException",
            content);
        Assert.Contains("detailed failure", content);
    }

    [Fact]
    public void StructuredLoggerWritesUnicodeAsReadableUtf8()
    {
        using TemporaryDirectory temporary = new();
        string path;
        using (GatewayLoggingSession logging =
               GatewayLoggingSession.Create(temporary.Path))
        {
            path = logging.Path;
            logging.CreateLogger<GatewayLoggingTests>().Write(
                LogLevel.Warning,
                "build.message",
                "Ошибка сборки");
        }

        string content = File.ReadAllText(path);
        Assert.Contains("Ошибка сборки", content);
        Assert.DoesNotContain("\\u", content);
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

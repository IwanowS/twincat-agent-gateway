using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
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
            OperationExceptionLoggingSink sink = new(
                logging.CreateLogger<GatewayLoggingTests>());
            InvalidOperationException exception =
                new("detailed failure");

            logging.CreateLogger<GatewayLoggingTests>().Write(
                LogLevel.Error,
                "operation.exception",
                "Operation failed with an exception.",
                "operation-1",
                new Dictionary<string, string>
                {
                    ["Stage"] = "build.execute",
                },
                exception);
            path = logging.Path;
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
        using JsonDocument document =
            JsonDocument.Parse(content.Trim());
        JsonElement root = document.RootElement;
        Assert.Equal(
            "build.execute",
            root.GetProperty("Stage").GetString());
        Assert.Equal(
            typeof(GatewayLoggingTests).FullName,
            root.GetProperty("SourceContext").GetString());
    }

    [Fact]
    public void StructuredLoggerWritesUnicodeAsReadableUtf8()
    {
        using TemporaryDirectory temporary = new();
        string path;
        using (GatewayLoggingSession logging =
               GatewayLoggingSession.Create(temporary.Path))
        {
            logging.CreateLogger<GatewayLoggingTests>().Write(
                LogLevel.Warning,
                "build.message",
                "Ошибка сборки");
            path = logging.Path;
        }

        string content = File.ReadAllText(path);
        Assert.Contains("Ошибка сборки", content);
        Assert.DoesNotContain("\\u", content);
    }

    [Fact]
    public void MinimumLevelFiltersLowerSeverityEvents()
    {
        using TemporaryDirectory temporary = new();
        GatewayConfiguration configuration = new()
        {
            LogMinimumLevel = GatewayLogLevel.Warning,
        };
        string path;
        using (GatewayLoggingSession logging =
               GatewayLoggingSession.Create(
                   temporary.Path,
                   configuration))
        {
            ILogger<GatewayLoggingTests> logger =
                logging.CreateLogger<GatewayLoggingTests>();
            logger.Write(
                LogLevel.Information,
                "filtered",
                "filtered information");
            logger.Write(
                LogLevel.Warning,
                "retained",
                "retained warning");
            path = logging.Path;
        }

        string content = File.ReadAllText(path);
        Assert.DoesNotContain("filtered information", content);
        Assert.Contains("retained warning", content);
    }

    [Fact]
    public void SeparateRunsUseSeparateSessionNames()
    {
        using TemporaryDirectory temporary = new();
        string firstPath;
        string secondPath;
        using (GatewayLoggingSession first =
               GatewayLoggingSession.Create(
                   temporary.Path,
                   new GatewayConfiguration(),
                   new FixedClock(
                       new DateTimeOffset(
                           2026,
                           7,
                           29,
                           6,
                           32,
                           45,
                           123,
                           TimeSpan.Zero)),
                   processId: 1234))
        {
            first.CreateLogger<GatewayLoggingTests>().Write(
                LogLevel.Information,
                "gateway.start",
                "First session started.");
            firstPath = first.Path;
        }

        using (GatewayLoggingSession second =
               GatewayLoggingSession.Create(
                   temporary.Path,
                   new GatewayConfiguration(),
                   new FixedClock(
                       new DateTimeOffset(
                           2026,
                           7,
                           29,
                           6,
                           32,
                           46,
                           456,
                           TimeSpan.Zero)),
                   processId: 1234))
        {
            second.CreateLogger<GatewayLoggingTests>().Write(
                LogLevel.Information,
                "gateway.start",
                "Second session started.");
            secondPath = second.Path;
        }

        Assert.EndsWith(
            "gateway-20260729T063245123Z-p1234.ndjson",
            firstPath,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "gateway-20260729T063246456Z-p1234.ndjson",
            secondPath,
            StringComparison.Ordinal);
        Assert.NotEqual(firstPath, secondPath);
    }

    [Fact]
    public void SizeRolloverUpdatesTrackerAndRetainsBoundedSegments()
    {
        using TemporaryDirectory temporary = new();
        GatewayConfiguration configuration = new()
        {
            LogFileSizeLimitBytes = 64 * 1024,
            LogRetainedFileCountLimit = 10,
        };
        string initialPath;
        string rolledPath;
        using (GatewayLoggingSession logging =
               GatewayLoggingSession.Create(
                   temporary.Path,
                   configuration,
                   new FixedClock(
                       new DateTimeOffset(
                           2026,
                           7,
                           29,
                           6,
                           32,
                           45,
                           123,
                           TimeSpan.Zero)),
                   processId: 1234))
        {
            ILogger<GatewayLoggingTests> logger =
                logging.CreateLogger<GatewayLoggingTests>();
            logger.Write(
                LogLevel.Information,
                "gateway.start",
                "Session started.");
            initialPath = logging.Path;
            string payload = new('x', 70 * 1024);
            for (int index = 0; index < 12; index++)
            {
                logger.Write(
                    LogLevel.Information,
                    "rotation.test",
                    payload,
                    properties: new Dictionary<string, string>
                    {
                        ["Index"] =
                            index.ToString(CultureInfo.InvariantCulture),
                    });
            }

            rolledPath = logging.Path;
            Assert.True(File.Exists(rolledPath));
        }

        Assert.NotEqual(initialPath, rolledPath);
        Assert.Matches(
            @"gateway-20260729T063245123Z-p1234_\d{3}\.ndjson$",
            rolledPath);
        Assert.True(
            Directory.GetFiles(
                    temporary.Path,
                    "gateway-*.ndjson")
                .Length
            <= 10);
    }

    [Fact]
    public void RetentionRemovesOnlyOldRecognizedPreviousSessionFiles()
    {
        using TemporaryDirectory temporary = new();
        string currentBase = System.IO.Path.Combine(
            temporary.Path,
            "gateway-20260729T063245123Z-p1234.ndjson");
        string currentSegment = System.IO.Path.Combine(
            temporary.Path,
            "gateway-20260729T063245123Z-p1234_001.ndjson");
        string previousSession = System.IO.Path.Combine(
            temporary.Path,
            "gateway-20260701T010203004Z-p4567.ndjson");
        string legacy = System.IO.Path.Combine(
            temporary.Path,
            "gateway.ndjson");
        string recentSession = System.IO.Path.Combine(
            temporary.Path,
            "gateway-20260728T010203004Z-p4567.ndjson");
        string unrelated = System.IO.Path.Combine(
            temporary.Path,
            "notes.ndjson");
        string similarButInvalid = System.IO.Path.Combine(
            temporary.Path,
            "gateway-20260701-p4567.ndjson");
        string[] paths =
        {
            currentBase,
            currentSegment,
            previousSession,
            legacy,
            recentSession,
            unrelated,
            similarButInvalid,
        };
        foreach (string path in paths)
        {
            File.WriteAllText(path, "test");
        }

        DateTime old = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string path in paths.Where(
                     path => !string.Equals(
                         path,
                         recentSession,
                         StringComparison.OrdinalIgnoreCase)))
        {
            File.SetLastWriteTimeUtc(path, old);
        }

        int removed = GatewaySessionLogRetention.Prune(
            temporary.Path,
            currentBase,
            new DateTimeOffset(
                2026,
                7,
                15,
                0,
                0,
                0,
                TimeSpan.Zero));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(previousSession));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(currentBase));
        Assert.True(File.Exists(currentSegment));
        Assert.True(File.Exists(recentSession));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(similarButInvalid));
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
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

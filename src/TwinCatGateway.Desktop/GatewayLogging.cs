using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.File;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;
using SerilogLogger = Serilog.Core.Logger;

namespace TwinCatGateway.Desktop;

internal sealed class GatewayLoggingSession : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SerilogLogger _serilogLogger;
    private readonly CurrentLogFileTracker _currentPath;
    private int _disposed;

    private GatewayLoggingSession(
        string sessionBasePath,
        CurrentLogFileTracker currentPath,
        ILoggerFactory loggerFactory,
        SerilogLogger serilogLogger)
    {
        SessionBasePath = sessionBasePath;
        _currentPath = currentPath;
        _loggerFactory = loggerFactory;
        _serilogLogger = serilogLogger;
    }

    public string Path =>
        _currentPath.CurrentPath
        ?? throw new InvalidOperationException(
            "The gateway log file has not been opened.");

    internal string SessionBasePath { get; }

    public static GatewayLoggingSession Create(string logDirectory)
    {
        return Create(
            logDirectory,
            new GatewayConfiguration());
    }

    public static GatewayLoggingSession Create(
        string logDirectory,
        GatewayConfiguration configuration,
        IClock? clock = null,
        int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException(
                "Log directory is required.",
                nameof(logDirectory));
        }

        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string fullDirectory = System.IO.Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(fullDirectory);
        DateTimeOffset startedAtUtc =
            (clock ?? SystemClock.Instance).UtcNow.ToUniversalTime();
        int effectiveProcessId =
            processId ?? Process.GetCurrentProcess().Id;
        if (effectiveProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        string sessionId = string.Format(
            CultureInfo.InvariantCulture,
            "gateway-{0}-p{1}",
            startedAtUtc.ToString(
                "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture),
            effectiveProcessId);
        string path = System.IO.Path.Combine(
            fullDirectory,
            sessionId + ".ndjson");
        CurrentLogFileTracker currentPath = new();
        SerilogLogger serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(
                ToSerilogLevel(configuration.Gateway.Logging.MinimumLevel))
            .Enrich.FromLogContext()
            .WriteTo.File(
                new CompactJsonFormatter(),
                path,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes:
                    configuration.Gateway.Logging.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit:
                    configuration.Gateway.Logging.RetainedFileCountLimit,
                shared: false,
                buffered: false,
                hooks: currentPath)
            .CreateLogger();
        ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddSerilog(
                    serilogLogger,
                    dispose: false);
            });
        return new GatewayLoggingSession(
            path,
            currentPath,
            loggerFactory,
            serilogLogger);
    }

    public ILogger<T> CreateLogger<T>()
    {
        return _loggerFactory.CreateLogger<T>();
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(
                ref _disposed,
                1)
            != 0)
        {
            return;
        }

        _loggerFactory.Dispose();
        _serilogLogger.Dispose();
    }

    private static LogEventLevel ToSerilogLevel(
        GatewayLogLevel level)
    {
        return level switch
        {
            GatewayLogLevel.Verbose => LogEventLevel.Verbose,
            GatewayLogLevel.Debug => LogEventLevel.Debug,
            GatewayLogLevel.Information => LogEventLevel.Information,
            GatewayLogLevel.Warning => LogEventLevel.Warning,
            GatewayLogLevel.Error => LogEventLevel.Error,
            GatewayLogLevel.Fatal => LogEventLevel.Fatal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "Unsupported gateway log level."),
        };
    }
}

internal sealed class CurrentLogFileTracker : FileLifecycleHooks
{
    private string? _currentPath;

    public string? CurrentPath => Volatile.Read(ref _currentPath);

    public override Stream OnFileOpened(
        string path,
        Stream underlyingStream,
        Encoding encoding)
    {
        Interlocked.Exchange(
            ref _currentPath,
            System.IO.Path.GetFullPath(path));
        return underlyingStream;
    }
}

internal static class GatewaySessionLogRetention
{
    private static readonly Regex SessionFileName = new(
        @"^gateway-\d{8}T\d{9}Z-p\d+(?:_\d{3})?\.ndjson$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static int Prune(
        string logDirectory,
        string currentSessionBasePath,
        DateTimeOffset olderThanUtc)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException(
                "Log directory is required.",
                nameof(logDirectory));
        }

        if (string.IsNullOrWhiteSpace(currentSessionBasePath))
        {
            throw new ArgumentException(
                "Current session path is required.",
                nameof(currentSessionBasePath));
        }

        string fullDirectory = System.IO.Path.GetFullPath(logDirectory);
        string currentSessionName =
            System.IO.Path.GetFileNameWithoutExtension(
                System.IO.Path.GetFullPath(currentSessionBasePath));
        int removed = 0;
        foreach (string candidate in Directory.EnumerateFiles(fullDirectory))
        {
            string fileName = System.IO.Path.GetFileName(candidate);
            if (!IsRecognized(fileName)
                || IsCurrentSession(fileName, currentSessionName))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || File.GetLastWriteTimeUtc(candidate)
                    >= olderThanUtc.UtcDateTime)
            {
                continue;
            }

            File.Delete(candidate);
            removed++;
        }

        return removed;
    }

    private static bool IsRecognized(string fileName)
    {
        return string.Equals(
                   fileName,
                   "gateway.ndjson",
                   StringComparison.OrdinalIgnoreCase)
               || SessionFileName.IsMatch(fileName);
    }

    private static bool IsCurrentSession(
        string fileName,
        string currentSessionName)
    {
        string fileStem =
            System.IO.Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(
                   fileStem,
                   currentSessionName,
                   StringComparison.OrdinalIgnoreCase)
               || fileStem.StartsWith(
                   currentSessionName + "_",
                   StringComparison.OrdinalIgnoreCase);
    }
}

internal static class GatewayLoggerExtensions
{
    public static void Write(
        this MicrosoftLogger logger,
        LogLevel level,
        string eventName,
        string message,
        string? operationId = null,
        IReadOnlyDictionary<string, string>? properties = null,
        Exception? exception = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException(
                "Event name is required.",
                nameof(eventName));
        }

        GatewayLogState state = new(
            eventName,
            message ?? string.Empty,
            operationId,
            properties);
        logger.Log(
            level,
            default,
            state,
            exception,
            static (value, _) => value.Message);
    }

    public static void RecordException(
        this MicrosoftLogger logger,
        string operationId,
        Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        logger.Write(
            LogLevel.Error,
            "operation.exception",
            "Operation failed with an exception.",
            operationId,
            exception: exception);
    }

    private sealed class GatewayLogState :
        IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _values;

        public GatewayLogState(
            string eventName,
            string message,
            string? operationId,
            IReadOnlyDictionary<string, string>? properties)
        {
            Message = message;
            Dictionary<string, object?> values =
                new(StringComparer.Ordinal);
            if (properties is not null)
            {
                foreach (KeyValuePair<string, string> property in properties)
                {
                    values[property.Key] = property.Value;
                }
            }

            values["EventName"] = eventName;
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                values["OperationId"] = operationId;
            }

            _values = new List<KeyValuePair<string, object?>>(values)
            {
                new(
                    "{OriginalFormat}",
                    EscapeMessageTemplate(message)),
            };
        }

        public string Message { get; }

        public int Count => _values.Count;

        public KeyValuePair<string, object?> this[int index] =>
            _values[index];

        public IEnumerator<KeyValuePair<string, object?>>
            GetEnumerator()
        {
            return _values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string ToString()
        {
            return Message;
        }

        private static string EscapeMessageTemplate(string message)
        {
            return message
                .Replace("{", "{{")
                .Replace("}", "}}");
        }
    }
}

internal sealed class OperationExceptionLoggingSink :
    IOperationExceptionSink
{
    private readonly MicrosoftLogger _logger;

    public OperationExceptionLoggingSink(MicrosoftLogger logger)
    {
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Record(string operationId, Exception exception)
    {
        _logger.RecordException(operationId, exception);
    }
}

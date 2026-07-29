using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using TwinCatGateway.Core;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;
using SerilogLogger = Serilog.Core.Logger;

namespace TwinCatGateway.Desktop;

internal sealed class GatewayLoggingSession : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SerilogLogger _serilogLogger;
    private int _disposed;

    private GatewayLoggingSession(
        string path,
        ILoggerFactory loggerFactory,
        SerilogLogger serilogLogger)
    {
        Path = path;
        _loggerFactory = loggerFactory;
        _serilogLogger = serilogLogger;
    }

    public string Path { get; }

    public static GatewayLoggingSession Create(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException(
                "Log directory is required.",
                nameof(logDirectory));
        }

        string fullDirectory = System.IO.Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(fullDirectory);
        string path = System.IO.Path.Combine(
            fullDirectory,
            "gateway.ndjson");
        SerilogLogger serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                new CompactJsonFormatter(),
                path,
                rollingInterval: RollingInterval.Infinite,
                shared: false,
                buffered: false)
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

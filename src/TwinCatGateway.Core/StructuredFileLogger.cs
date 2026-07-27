using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TwinCatGateway.Core;

public enum StructuredLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed class StructuredFileLogger : IOperationExceptionSink
{
    private readonly object _sync = new();
    private readonly IClock _clock;
    private readonly string _path;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public StructuredFileLogger(
        string logDirectory,
        IClock? clock = null)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException(
                "Log directory is required.",
                nameof(logDirectory));
        }

        string fullDirectory = System.IO.Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(fullDirectory);
        _path = System.IO.Path.Combine(fullDirectory, "gateway.ndjson");
        _clock = clock ?? SystemClock.Instance;
    }

    public string Path => _path;

    public void Write(
        StructuredLogLevel level,
        string eventName,
        string message,
        string? operationId = null,
        IReadOnlyDictionary<string, string>? properties = null,
        Exception? exception = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        StructuredLogEntry entry = new()
        {
            TimestampUtc = _clock.UtcNow,
            Level = level.ToString(),
            EventName = eventName,
            Message = message ?? string.Empty,
            OperationId = operationId,
            Properties = properties is null
                ? null
                : properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
            Exception = exception?.ToString(),
        };
        string json = JsonSerializer.Serialize(entry, _serializerOptions);

        lock (_sync)
        {
            File.AppendAllText(
                _path,
                json + Environment.NewLine,
                new UTF8Encoding(false));
        }
    }

    public void Record(string operationId, Exception exception)
    {
        Write(
            StructuredLogLevel.Error,
            "operation.exception",
            "Operation failed with an exception.",
            operationId,
            exception: exception);
    }

    private sealed class StructuredLogEntry
    {
        public DateTimeOffset TimestampUtc { get; set; }

        public string Level { get; set; } = string.Empty;

        public string EventName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string? OperationId { get; set; }

        public Dictionary<string, string>? Properties { get; set; }

        public string? Exception { get; set; }
    }
}

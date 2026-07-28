using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayInstanceRecord
{
    public int SchemaVersion { get; set; } = 1;

    public string InstanceId { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public DateTimeOffset ProcessStartedAtUtc { get; set; }

    public string PipeName { get; set; } = string.Empty;

    public string ConfigurationPath { get; set; } = string.Empty;

    public string? ActiveProfile { get; set; }

    public string? SolutionPath { get; set; }

    public GatewayLaunchSource LaunchSource { get; set; }

    public GatewayUiMode UiMode { get; set; }
}

public sealed class GatewayInstanceRegistry
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private readonly string _path;

    public GatewayInstanceRegistry(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? GetDefaultPath()
            : System.IO.Path.GetFullPath(path!);
    }

    public string Path => _path;

    public GatewayInstanceRecord? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        string json = File.ReadAllText(_path);
        GatewayInstanceRecord record =
            JsonSerializer.Deserialize<GatewayInstanceRecord>(
                json,
                SerializerOptions)
            ?? throw new InvalidDataException(
                "Gateway instance record contains no JSON object.");
        Validate(record);
        return record;
    }

    public GatewayInstanceRegistration Register(
        GatewayInstanceRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        record.InstanceId =
            string.IsNullOrWhiteSpace(record.InstanceId)
                ? Guid.NewGuid().ToString("N")
                : record.InstanceId;
        Validate(record);

        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "Gateway instance registry has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath =
            _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    record,
                    SerializerOptions));
            if (File.Exists(_path))
            {
                File.Replace(
                    temporaryPath,
                    _path,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new GatewayInstanceRegistration(
            this,
            record.InstanceId);
    }

    internal void RemoveIfOwned(string instanceId)
    {
        GatewayInstanceRecord? current;
        try
        {
            current = Read();
        }
        catch (IOException)
        {
            return;
        }

        if (current is null
            || !string.Equals(
                current.InstanceId,
                instanceId,
                StringComparison.Ordinal))
        {
            return;
        }

        File.Delete(_path);
    }

    private static string GetDefaultPath()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TwinCatAgentGateway",
            "gateway-instance.json");
    }

    private static void Validate(GatewayInstanceRecord record)
    {
        if (record.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(record.InstanceId)
            || record.ProcessId <= 0
            || record.ProcessStartedAtUtc == default
            || string.IsNullOrWhiteSpace(record.PipeName)
            || string.IsNullOrWhiteSpace(record.ConfigurationPath)
            || !System.IO.Path.IsPathRooted(
                record.ConfigurationPath))
        {
            throw new InvalidDataException(
                "Gateway instance record is invalid.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class GatewayInstanceRegistration : IDisposable
{
    private readonly GatewayInstanceRegistry _registry;
    private readonly string _instanceId;
    private int _disposed;

    internal GatewayInstanceRegistration(
        GatewayInstanceRegistry registry,
        string instanceId)
    {
        _registry = registry;
        _instanceId = instanceId;
    }

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        _registry.RemoveIfOwned(_instanceId);
    }
}

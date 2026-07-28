using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayConfigurationLoader
{
    private readonly JsonSerializerOptions _serializerOptions;

    public GatewayConfigurationLoader()
    {
        _serializerOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };
        _serializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public GatewayConfiguration Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Configuration path is required.",
                nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        string json = File.ReadAllText(fullPath);
        GatewayConfiguration configuration =
            JsonSerializer.Deserialize<GatewayConfiguration>(
                json,
                _serializerOptions)
            ?? throw new InvalidDataException(
                "Gateway configuration contains no JSON object.");
        ResolveRelativePaths(
            configuration,
            Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException(
                    "Gateway configuration has no parent directory."));
        return configuration;
    }

    public void Save(
        string path,
        GatewayConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Configuration path is required.",
                nameof(path));
        }

        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            string message = string.Join(
                Environment.NewLine,
                validation.Issues.Select(
                    issue => $"{issue.Path}: {issue.Message}"));
            throw new ArgumentException(
                message,
                nameof(configuration));
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Configuration directory '{directory}' was not found.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            string json = JsonSerializer.Serialize(
                configuration,
                _serializerOptions);
            File.WriteAllText(temporaryPath, json);
            if (File.Exists(fullPath))
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ResolveRelativePaths(
        GatewayConfiguration configuration,
        string configurationDirectory)
    {
        configuration.LogDirectory = ResolveOptionalPath(
            configuration.LogDirectory,
            configurationDirectory);
        if (configuration.Profiles is null)
        {
            return;
        }

        foreach (ProjectProfile? profile in configuration.Profiles)
        {
            if (profile is null)
            {
                continue;
            }

            profile.Solution = ResolveRequiredPath(
                profile.Solution,
                configurationDirectory);
            if (profile.TcUnit is not null)
            {
                profile.TcUnit.ReportPath = ResolveRequiredPath(
                    profile.TcUnit.ReportPath,
                    configurationDirectory);
            }
        }
    }

    private static string ResolveRequiredPath(
        string path,
        string configurationDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(
            Path.Combine(configurationDirectory, path));
    }

    private static string? ResolveOptionalPath(
        string? path,
        string configurationDirectory)
    {
        return path is null
            ? null
            : ResolveRequiredPath(
                path,
                configurationDirectory);
    }
}

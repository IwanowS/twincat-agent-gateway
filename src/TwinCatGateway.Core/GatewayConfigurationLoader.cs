using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayConfigurationLoader
{
    private const int CurrentSchemaVersion = 2;
    private static readonly HashSet<string> V1RootProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pipeName",
            "logDirectory",
            "logMinimumLevel",
            "logFileSizeLimitBytes",
            "logRetainedFileCountLimit",
            "logRetentionDays",
            "agentProcessControl",
            "runtimeMonitoring",
        };
    private static readonly HashSet<string> V1ProfileProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "solution",
            "xaeProgId",
            "configuration",
            "platform",
            "assumeAttachedXaeSynchronized",
            "externalChangePolicy",
            "allowXaeLaunch",
            "allowCloseXae",
            "allowDirtyDocumentDiscard",
            "allowForceSynchronization",
            "allowActivation",
            "expectedTarget",
            "requireRecentSuccessfulBuild",
            "recentBuildMaxAgeSeconds",
            "autoWaitForTcUnit",
            "tcUnit",
        };
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
        ValidateSchemaVersion(json);
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
        if (configuration.Gateway?.Logging is not null)
        {
            configuration.Gateway.Logging.Directory = ResolveOptionalPath(
                configuration.Gateway.Logging.Directory,
                configurationDirectory);
        }

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

            if (profile.Xae is not null)
            {
                profile.Xae.Solution = ResolveRequiredPath(
                    profile.Xae.Solution,
                    configurationDirectory);
            }

            if (profile.Target?.TcUnit is not null)
            {
                profile.Target.TcUnit.ReportPath = ResolveRequiredPath(
                    profile.Target.TcUnit.ReportPath,
                    configurationDirectory);
            }
        }
    }

    private static void ValidateSchemaVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(
                root,
                "schemaVersion",
                out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int parsed)
            || parsed != CurrentSchemaVersion)
        {
            throw UnsupportedVersion(
                "Gateway configuration must declare schemaVersion 2.");
        }

        string? rootV1Property = FindProperty(root, V1RootProperties);
        if (rootV1Property is not null)
        {
            throw UnsupportedVersion(
                $"Gateway configuration property '{rootV1Property}' belongs to schema v1.");
        }

        if (!TryGetProperty(root, "profiles", out JsonElement profiles)
            || profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement profile in profiles.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? profileV1Property =
                FindProperty(profile, V1ProfileProperties);
            if (profileV1Property is not null)
            {
                throw UnsupportedVersion(
                    $"Profile property '{profileV1Property}' belongs to schema v1.");
            }
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(
                property.Name,
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? FindProperty(
        JsonElement element,
        HashSet<string> names)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (names.Contains(property.Name))
            {
                return property.Name;
            }
        }

        return null;
    }

    private static GatewayConfigurationException UnsupportedVersion(
        string message)
    {
        return new GatewayConfigurationException(
            ErrorCodes.ConfigVersionUnsupported,
            message);
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

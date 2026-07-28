using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class GatewayConfigurationValidator
{
    private const int CurrentSchemaVersion = 1;
    private static readonly char[] PathSeparators = { '\\', '/' };

    public static ConfigurationValidationResult Validate(
        GatewayConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        List<ConfigurationIssue> issues = new();
        ValidateGatewaySettings(configuration, issues);
        ValidateProfiles(configuration, issues);
        return new ConfigurationValidationResult(issues);
    }

    private static void ValidateGatewaySettings(
        GatewayConfiguration configuration,
        ICollection<ConfigurationIssue> issues)
    {
        if (configuration.SchemaVersion != CurrentSchemaVersion)
        {
            Add(
                issues,
                "schemaVersion",
                $"Only schema version {CurrentSchemaVersion} is supported.");
        }

        if (string.IsNullOrWhiteSpace(configuration.PipeName))
        {
            Add(issues, "pipeName", "Pipe name is required.");
        }
        else if (configuration.PipeName.IndexOfAny(PathSeparators) >= 0)
        {
            Add(
                issues,
                "pipeName",
                "Pipe name must not contain path separators.");
        }

        if (configuration.LogRetentionDays <= 0
            || configuration.LogRetentionDays > 3650)
        {
            Add(
                issues,
                "logRetentionDays",
                "Log retention must be between 1 and 3650 days.");
        }

        if (!string.IsNullOrWhiteSpace(configuration.LogDirectory)
            && !Path.IsPathRooted(configuration.LogDirectory))
        {
            Add(
                issues,
                "logDirectory",
                "Log directory must be an absolute path.");
        }

        if (configuration.Ui is null)
        {
            Add(issues, "ui", "UI settings are required.");
        }
        else if (!Enum.IsDefined(
                     typeof(GatewayUiMode),
                     configuration.Ui.Mode))
        {
            Add(issues, "ui.mode", "UI mode is invalid.");
        }

        if (configuration.AgentProcessControl is null)
        {
            Add(
                issues,
                "agentProcessControl",
                "Agent process-control settings are required.");
        }
    }

    private static void ValidateProfiles(
        GatewayConfiguration configuration,
        ICollection<ConfigurationIssue> issues)
    {
        if (configuration.Profiles is null)
        {
            Add(issues, "profiles", "Profiles are required.");
            return;
        }

        if (configuration.Profiles.Count == 0)
        {
            Add(issues, "profiles", "At least one project profile is required.");
            return;
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < configuration.Profiles.Count; index++)
        {
            ProjectProfile? profile = configuration.Profiles[index];
            string path = $"profiles[{index}]";
            if (profile is null)
            {
                Add(issues, path, "Profile must not be null.");
                continue;
            }

            ValidateProfile(profile, path, names, issues);
        }

        if (string.IsNullOrWhiteSpace(configuration.DefaultProfile))
        {
            if (configuration.Profiles.Count > 1)
            {
                Add(
                    issues,
                    "defaultProfile",
                    "Default profile is required when multiple profiles are configured.");
            }
        }
        else if (!names.Contains(configuration.DefaultProfile!))
        {
            Add(
                issues,
                "defaultProfile",
                $"Profile '{configuration.DefaultProfile}' does not exist.");
        }
    }

    private static void ValidateProfile(
        ProjectProfile profile,
        string path,
        HashSet<string> names,
        ICollection<ConfigurationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            Add(issues, $"{path}.name", "Profile name is required.");
        }
        else if (!names.Add(profile.Name))
        {
            Add(
                issues,
                $"{path}.name",
                $"Profile name '{profile.Name}' is duplicated.");
        }

        if (string.IsNullOrWhiteSpace(profile.Solution))
        {
            Add(issues, $"{path}.solution", "Solution path is required.");
        }
        else
        {
            if (!Path.IsPathRooted(profile.Solution))
            {
                Add(
                    issues,
                    $"{path}.solution",
                    "Solution path must be absolute.");
            }

            if (!string.Equals(
                Path.GetExtension(profile.Solution),
                ".sln",
                StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    issues,
                    $"{path}.solution",
                    "Solution path must identify a .sln file.");
            }
        }

        ValidateOptionalNonWhitespace(
            profile.Configuration,
            $"{path}.configuration",
            issues);
        ValidateOptionalNonWhitespace(
            profile.Platform,
            $"{path}.platform",
            issues);
        ValidateOptionalNonWhitespace(
            profile.XaeProgId,
            $"{path}.xaeProgId",
            issues);

        if (profile.RequireRecentSuccessfulBuild
            && profile.RecentBuildMaxAgeSeconds <= 0)
        {
            Add(
                issues,
                $"{path}.recentBuildMaxAgeSeconds",
                "Recent build maximum age must be positive.");
        }

        if (profile.AllowActivation)
        {
            ValidateActivationTarget(profile.ExpectedTarget, path, issues);
        }

        if (profile.AutoWaitForTcUnit && profile.TcUnit is null)
        {
            Add(
                issues,
                $"{path}.tcUnit",
                "TcUnit settings are required when automatic waiting is enabled.");
        }

        if (profile.TcUnit is not null)
        {
            ValidateTcUnit(profile.TcUnit, $"{path}.tcUnit", issues);
        }
    }

    private static void ValidateActivationTarget(
        TargetIdentity? target,
        string profilePath,
        ICollection<ConfigurationIssue> issues)
    {
        if (target is null)
        {
            Add(
                issues,
                $"{profilePath}.expectedTarget",
                "Activation requires an expected target identity.");
            return;
        }

        if (!IsValidAmsNetId(target.AmsNetId))
        {
            Add(
                issues,
                $"{profilePath}.expectedTarget.amsNetId",
                "Activation requires a six-part AMS NetId with byte values.");
        }
    }

    private static void ValidateTcUnit(
        TcUnitProfile tcUnit,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        if (tcUnit.AdsPort <= 0 || tcUnit.AdsPort > ushort.MaxValue)
        {
            Add(
                issues,
                $"{path}.adsPort",
                "TcUnit ADS port must be between 1 and 65535.");
        }

        ValidateRequiredText(
            tcUnit.FinishedSymbol,
            $"{path}.finishedSymbol",
            "TcUnit finished symbol is required.",
            issues);
        ValidateRequiredText(
            tcUnit.SuiteCountSymbol,
            $"{path}.suiteCountSymbol",
            "TcUnit suite-count symbol is required.",
            issues);

        if (string.IsNullOrWhiteSpace(tcUnit.ReportPath))
        {
            Add(issues, $"{path}.reportPath", "TcUnit report path is required.");
        }
        else if (!Path.IsPathRooted(tcUnit.ReportPath))
        {
            Add(
                issues,
                $"{path}.reportPath",
                "TcUnit report path must be absolute.");
        }
        else if (tcUnit.AllowDeleteExistingReport
            && IsRootPath(tcUnit.ReportPath))
        {
            Add(
                issues,
                $"{path}.reportPath",
                "A filesystem root cannot be used as a deletable report path.");
        }

        if (tcUnit.CompletionTimeoutSeconds <= 0)
        {
            Add(
                issues,
                $"{path}.completionTimeoutSeconds",
                "TcUnit completion timeout must be positive.");
        }
    }

    private static bool IsValidAmsNetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value!.Split('.');
        return parts.Length == 6
            && parts.All(part =>
                byte.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte parsed)
                && string.Equals(
                    parsed.ToString(CultureInfo.InvariantCulture),
                    part,
                    StringComparison.Ordinal));
    }

    private static bool IsRootPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        return string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateOptionalNonWhitespace(
        string? value,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            Add(issues, path, "Value must be null or non-whitespace.");
        }
    }

    private static void ValidateRequiredText(
        string value,
        string path,
        string message,
        ICollection<ConfigurationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(issues, path, message);
        }
    }

    private static void Add(
        ICollection<ConfigurationIssue> issues,
        string path,
        string message)
    {
        issues.Add(new ConfigurationIssue(path, message));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class GatewayConfigurationValidator
{
    private const int CurrentSchemaVersion = 2;
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

        if (configuration.Gateway is null)
        {
            Add(issues, "gateway", "Gateway settings are required.");
        }
        else
        {
            ValidateGatewayCore(configuration.Gateway, issues);
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
    }

    private static void ValidateGatewayCore(
        GatewaySettingsConfiguration gateway,
        ICollection<ConfigurationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(gateway.PipeName))
        {
            Add(issues, "gateway.pipeName", "Pipe name is required.");
        }
        else if (gateway.PipeName.IndexOfAny(PathSeparators) >= 0)
        {
            Add(
                issues,
                "gateway.pipeName",
                "Pipe name must not contain path separators.");
        }

        if (gateway.ProcessControl is null)
        {
            Add(
                issues,
                "gateway.processControl",
                "Gateway process-control settings are required.");
        }

        if (gateway.Logging is null)
        {
            Add(
                issues,
                "gateway.logging",
                "Gateway logging settings are required.");
            return;
        }

        GatewayLoggingConfiguration logging = gateway.Logging;
        if (!Enum.IsDefined(typeof(GatewayLogLevel), logging.MinimumLevel))
        {
            Add(
                issues,
                "gateway.logging.minimumLevel",
                "Log minimum level is invalid.");
        }

        if (logging.FileSizeLimitBytes < 64 * 1024
            || logging.FileSizeLimitBytes > 1024L * 1024 * 1024)
        {
            Add(
                issues,
                "gateway.logging.fileSizeLimitBytes",
                "Log file size limit must be between 65536 and 1073741824 bytes.");
        }

        if (logging.RetainedFileCountLimit < 1
            || logging.RetainedFileCountLimit > 1000)
        {
            Add(
                issues,
                "gateway.logging.retainedFileCountLimit",
                "Retained log file count must be between 1 and 1000.");
        }

        if (logging.RetentionDays <= 0 || logging.RetentionDays > 3650)
        {
            Add(
                issues,
                "gateway.logging.retentionDays",
                "Log retention must be between 1 and 3650 days.");
        }

        if (!string.IsNullOrWhiteSpace(logging.Directory)
            && !Path.IsPathRooted(logging.Directory))
        {
            Add(
                issues,
                "gateway.logging.directory",
                "Log directory must be an absolute path.");
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

        if (profile.Xae is null)
        {
            Add(issues, $"{path}.xae", "XAE settings are required.");
            return;
        }

        ValidateXae(profile.Xae, $"{path}.xae", issues);
        if (profile.Target is not null)
        {
            ValidateTarget(profile.Target, $"{path}.target", issues);
        }

        if (profile.Xae.Capabilities?.Activate == true
            && profile.Target is null)
        {
            Add(
                issues,
                $"{path}.target",
                "XAE activation requires a configured Target System.");
        }
    }

    private static void ValidateXae(
        XaeProfileConfiguration xae,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(xae.Solution))
        {
            Add(issues, $"{path}.solution", "Solution path is required.");
        }
        else
        {
            if (!Path.IsPathRooted(xae.Solution))
            {
                Add(
                    issues,
                    $"{path}.solution",
                    "Solution path must be absolute.");
            }

            if (!string.Equals(
                Path.GetExtension(xae.Solution),
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
            xae.ProgId,
            $"{path}.progId",
            issues);
        ValidateOptionalNonWhitespace(
            xae.Configuration,
            $"{path}.configuration",
            issues);
        ValidateOptionalNonWhitespace(
            xae.Platform,
            $"{path}.platform",
            issues);

        if (xae.Workspace is null)
        {
            Add(
                issues,
                $"{path}.workspace",
                "XAE workspace settings are required.");
        }
        else if (!Enum.IsDefined(
                     typeof(ExternalChangePolicy),
                     xae.Workspace.ExternalChangePolicy))
        {
            Add(
                issues,
                $"{path}.workspace.externalChangePolicy",
                "External change policy is invalid.");
        }

        if (xae.Capabilities is null)
        {
            Add(
                issues,
                $"{path}.capabilities",
                "XAE capability settings are required.");
        }
    }

    private static void ValidateTarget(
        TargetProfileConfiguration target,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        ValidateOptionalNonWhitespace(target.Name, $"{path}.name", issues);
        if (!IsValidAmsNetId(target.AmsNetId))
        {
            Add(
                issues,
                $"{path}.amsNetId",
                "Target AMS NetId must contain six canonical byte values.");
        }

        if (target.Monitoring is null)
        {
            Add(
                issues,
                $"{path}.monitoring",
                "Target monitoring settings are required.");
        }
        else
        {
            ValidateMonitoring(
                target.Monitoring,
                $"{path}.monitoring",
                issues);
        }

        if (target.Capabilities is null)
        {
            Add(
                issues,
                $"{path}.capabilities",
                "Target capability settings are required.");
        }
        else if (target.Capabilities.TcUnitVerification
            && target.TcUnit is null)
        {
            Add(
                issues,
                $"{path}.tcUnit",
                "TcUnit settings are required when verification is enabled.");
        }

        if (target.TcUnit is not null)
        {
            ValidateTcUnit(target.TcUnit, $"{path}.tcUnit", issues);
        }
    }

    private static void ValidateMonitoring(
        TargetMonitoringConfiguration monitoring,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        if (monitoring.PollIntervalMilliseconds < 100
            || monitoring.PollIntervalMilliseconds > 60000)
        {
            Add(
                issues,
                $"{path}.pollIntervalMilliseconds",
                "Runtime polling interval must be between 100 and 60000 milliseconds.");
        }

        if (monitoring.ReadTimeoutMilliseconds < 100
            || monitoring.ReadTimeoutMilliseconds > 10000)
        {
            Add(
                issues,
                $"{path}.readTimeoutMilliseconds",
                "Runtime read timeout must be between 100 and 10000 milliseconds.");
        }
    }

    private static void ValidateTcUnit(
        TcUnitProfile tcUnit,
        string path,
        ICollection<ConfigurationIssue> issues)
    {
        ValidateRequiredText(
            tcUnit.RuntimeId,
            $"{path}.runtimeId",
            "TcUnit runtime id is required.",
            issues);
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

        if (!Enum.IsDefined(typeof(ZeroTestsPolicy), tcUnit.ZeroTests))
        {
            Add(issues, $"{path}.zeroTests", "Zero-tests policy is invalid.");
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
            fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            root?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
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

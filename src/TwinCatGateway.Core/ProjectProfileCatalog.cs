using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class ProjectProfileCatalog
{
    private readonly Dictionary<string, ProjectProfile> _profiles;
    private readonly string? _defaultProfile;

    public ProjectProfileCatalog(
        GatewayConfiguration configuration)
    {
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
                validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}"));
            throw new ArgumentException(message, nameof(configuration));
        }

        _profiles = configuration.Profiles.ToDictionary(
            profile => profile.Name,
            CloneProfile,
            StringComparer.OrdinalIgnoreCase);
        _defaultProfile = configuration.DefaultProfile
            ?? (configuration.Profiles.Count == 1
                ? configuration.Profiles[0].Name
                : null);
    }

    public ProjectProfile GetRequired(string? name)
    {
        string? selectedName = string.IsNullOrWhiteSpace(name)
            ? _defaultProfile
            : name;
        if (selectedName is null
            || !_profiles.TryGetValue(selectedName, out ProjectProfile? profile))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{selectedName ?? "<default>"}' was not found.");
        }

        return CloneProfile(profile);
    }

    private static ProjectProfile CloneProfile(ProjectProfile source)
    {
        return new ProjectProfile
        {
            Name = source.Name,
            Xae = new XaeProfileConfiguration
            {
                Solution = source.Xae.Solution,
                ProgId = source.Xae.ProgId,
                Configuration = source.Xae.Configuration,
                Platform = source.Xae.Platform,
                Workspace = new XaeWorkspaceConfiguration
                {
                    AssumeAttachedSynchronized =
                        source.Xae.Workspace.AssumeAttachedSynchronized,
                    ExternalChangePolicy =
                        source.Xae.Workspace.ExternalChangePolicy,
                    AutoSynchronizeBeforeOperation =
                        source.Xae.Workspace.AutoSynchronizeBeforeOperation,
                },
                Capabilities = new XaeCapabilitiesConfiguration
                {
                    Launch = source.Xae.Capabilities.Launch,
                    Close = source.Xae.Capabilities.Close,
                    Synchronize = source.Xae.Capabilities.Synchronize,
                    DiscardDirtyDocuments =
                        source.Xae.Capabilities.DiscardDirtyDocuments,
                    Build = source.Xae.Capabilities.Build,
                    Activate = source.Xae.Capabilities.Activate,
                },
            },
            Target = source.Target is null
                ? null
                : new TargetProfileConfiguration
                {
                    Name = source.Target.Name,
                    AmsNetId = source.Target.AmsNetId,
                    Monitoring = new TargetMonitoringConfiguration
                    {
                        PollIntervalMilliseconds =
                            source.Target.Monitoring.PollIntervalMilliseconds,
                        ReadTimeoutMilliseconds =
                            source.Target.Monitoring.ReadTimeoutMilliseconds,
                    },
                    Capabilities = new TargetCapabilitiesConfiguration
                    {
                        Config = source.Target.Capabilities.Config,
                        StartRestart =
                            source.Target.Capabilities.StartRestart,
                        TcUnitVerification =
                            source.Target.Capabilities.TcUnitVerification,
                    },
                    TcUnit = source.Target.TcUnit is null
                        ? null
                        : new TcUnitProfile
                        {
                            RuntimeId = source.Target.TcUnit.RuntimeId,
                            AdsPort = source.Target.TcUnit.AdsPort,
                            FinishedSymbol =
                                source.Target.TcUnit.FinishedSymbol,
                            SuiteCountSymbol =
                                source.Target.TcUnit.SuiteCountSymbol,
                            ReportPath = source.Target.TcUnit.ReportPath,
                            AllowDeleteExistingReport =
                                source.Target.TcUnit
                                    .AllowDeleteExistingReport,
                            CompletionTimeoutSeconds =
                                source.Target.TcUnit
                                    .CompletionTimeoutSeconds,
                            ZeroTests = source.Target.TcUnit.ZeroTests,
                        },
                },
        };
    }
}

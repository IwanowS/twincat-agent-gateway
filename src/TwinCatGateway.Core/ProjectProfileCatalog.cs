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
            Solution = source.Solution,
            AllowXaeLaunch = source.AllowXaeLaunch,
            XaeProgId = source.XaeProgId,
            AllowActivation = source.AllowActivation,
            ExpectedTarget = source.ExpectedTarget is null
                ? null
                : new TargetIdentity
                {
                    Name = source.ExpectedTarget.Name,
                    AmsNetId = source.ExpectedTarget.AmsNetId,
                },
            Configuration = source.Configuration,
            Platform = source.Platform,
            UnsavedDocuments = source.UnsavedDocuments,
            RequireRecentSuccessfulBuild = source.RequireRecentSuccessfulBuild,
            RecentBuildMaxAgeSeconds = source.RecentBuildMaxAgeSeconds,
            AutoWaitForTcUnit = source.AutoWaitForTcUnit,
            TcUnit = source.TcUnit is null
                ? null
                : new TcUnitProfile
                {
                    AdsPort = source.TcUnit.AdsPort,
                    FinishedSymbol = source.TcUnit.FinishedSymbol,
                    SuiteCountSymbol = source.TcUnit.SuiteCountSymbol,
                    ReportPath = source.TcUnit.ReportPath,
                    AllowDeleteExistingReport = source.TcUnit.AllowDeleteExistingReport,
                    CompletionTimeoutSeconds = source.TcUnit.CompletionTimeoutSeconds,
                    ZeroTests = source.TcUnit.ZeroTests,
                },
        };
    }
}

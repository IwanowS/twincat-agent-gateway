using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class ResolvedXaeWorkspace
{
    internal ResolvedXaeWorkspace(XaeWorkspaceConfiguration source)
    {
        AssumeAttachedSynchronized = source.AssumeAttachedSynchronized;
        ExternalChangePolicy = source.ExternalChangePolicy;
        AutoSynchronizeBeforeOperation =
            source.AutoSynchronizeBeforeOperation;
    }

    public bool AssumeAttachedSynchronized { get; }

    public ExternalChangePolicy ExternalChangePolicy { get; }

    public bool AutoSynchronizeBeforeOperation { get; }
}

public sealed class ResolvedXaeProfile
{
    internal ResolvedXaeProfile(XaeProfileConfiguration source)
    {
        Solution = NormalizeSolution(source.Solution);
        ProgId = source.ProgId;
        Configuration = source.Configuration;
        Platform = source.Platform;
        Workspace = new ResolvedXaeWorkspace(source.Workspace);
    }

    public string Solution { get; }

    public string? ProgId { get; }

    public string? Configuration { get; }

    public string? Platform { get; }

    public ResolvedXaeWorkspace Workspace { get; }

    internal static string NormalizeSolution(string path)
    {
        return Path.GetFullPath(path);
    }
}

public sealed class ResolvedTcUnitProfile
{
    internal ResolvedTcUnitProfile(TcUnitProfile source)
    {
        RuntimeId = source.RuntimeId;
        AdsPort = source.AdsPort;
        FinishedSymbol = source.FinishedSymbol;
        SuiteCountSymbol = source.SuiteCountSymbol;
        ReportPath = Path.GetFullPath(source.ReportPath);
        AllowDeleteExistingReport = source.AllowDeleteExistingReport;
        CompletionTimeoutSeconds = source.CompletionTimeoutSeconds;
        ZeroTests = source.ZeroTests;
    }

    public string RuntimeId { get; }

    public int AdsPort { get; }

    public string FinishedSymbol { get; }

    public string SuiteCountSymbol { get; }

    public string ReportPath { get; }

    public bool AllowDeleteExistingReport { get; }

    public int CompletionTimeoutSeconds { get; }

    public ZeroTestsPolicy ZeroTests { get; }
}

public sealed class ResolvedTargetProfile
{
    internal ResolvedTargetProfile(TargetProfileConfiguration source)
    {
        Name = source.Name;
        AmsNetId = source.AmsNetId;
        PollIntervalMilliseconds =
            source.Monitoring.PollIntervalMilliseconds;
        ReadTimeoutMilliseconds =
            source.Monitoring.ReadTimeoutMilliseconds;
        TcUnit = source.TcUnit is null
            ? null
            : new ResolvedTcUnitProfile(source.TcUnit);
    }

    public string? Name { get; }

    public string AmsNetId { get; }

    public int PollIntervalMilliseconds { get; }

    public int ReadTimeoutMilliseconds { get; }

    public ResolvedTcUnitProfile? TcUnit { get; }
}

public sealed class ResolvedProfile
{
    private readonly IReadOnlyDictionary<CapabilityKey, bool> _capabilities;

    internal ResolvedProfile(ProjectProfile source)
    {
        Name = source.Name;
        Xae = new ResolvedXaeProfile(source.Xae);
        Target = source.Target is null
            ? null
            : new ResolvedTargetProfile(source.Target);
        _capabilities = new ReadOnlyDictionary<CapabilityKey, bool>(
            new Dictionary<CapabilityKey, bool>
            {
                [CapabilityKey.XaeLaunch] =
                    source.Xae.Capabilities.Launch,
                [CapabilityKey.XaeClose] =
                    source.Xae.Capabilities.Close,
                [CapabilityKey.XaeSynchronize] =
                    source.Xae.Capabilities.Synchronize,
                [CapabilityKey.XaeDiscardDirtyDocuments] =
                    source.Xae.Capabilities.DiscardDirtyDocuments,
                [CapabilityKey.XaeBuild] =
                    source.Xae.Capabilities.Build,
                [CapabilityKey.XaeActivate] =
                    source.Xae.Capabilities.Activate,
                [CapabilityKey.TargetConfig] =
                    source.Target?.Capabilities.Config == true,
                [CapabilityKey.TargetStartRestart] =
                    source.Target?.Capabilities.StartRestart == true,
                [CapabilityKey.TargetTcUnitVerification] =
                    source.Target?.Capabilities.TcUnitVerification == true,
            });
    }

    public string Name { get; }

    public ResolvedXaeProfile Xae { get; }

    public ResolvedTargetProfile? Target { get; }

    internal bool IsConfigured(CapabilityKey key)
    {
        return _capabilities.TryGetValue(key, out bool configured)
            && configured;
    }
}

public sealed class ProfileResolver
{
    private readonly string? _defaultProfile;
    private readonly Dictionary<string, ResolvedProfile> _profiles;

    public ProfileResolver(GatewayConfiguration configuration)
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
                validation.Issues.Select(
                    issue => $"{issue.Path}: {issue.Message}"));
            throw new ArgumentException(message, nameof(configuration));
        }

        _profiles = configuration.Profiles.ToDictionary(
            profile => profile.Name,
            profile => new ResolvedProfile(profile),
            StringComparer.OrdinalIgnoreCase);
        _defaultProfile = configuration.DefaultProfile
            ?? (configuration.Profiles.Count == 1
                ? configuration.Profiles[0].Name
                : null);
    }

    public ResolvedProfile Resolve(string? name)
    {
        string? selectedName = string.IsNullOrWhiteSpace(name)
            ? _defaultProfile
            : name;
        if (selectedName is null
            || !_profiles.TryGetValue(
                selectedName,
                out ResolvedProfile? profile))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{selectedName ?? "<default>"}' was not found.",
                stage: "profile.resolve",
                component: GatewayComponent.Profile,
                expected: new IdentityEvidence
                {
                    Profile = selectedName,
                });
        }

        return profile;
    }

    public ResolvedTargetProfile RequireTarget(ResolvedProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        return profile.Target
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{profile.Name}' has no configured Target System.",
                stage: "profile.target.resolve",
                component: GatewayComponent.Profile,
                expected: new IdentityEvidence
                {
                    Profile = profile.Name,
                });
    }

    public void EnsureSolutionIdentity(
        ResolvedProfile profile,
        string? observedSolution,
        string stage)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException(
                "Identity-check stage is required.",
                nameof(stage));
        }

        string? normalizedObserved = TryNormalizeSolution(observedSolution);
        if (normalizedObserved is not null
            && string.Equals(
                profile.Xae.Solution,
                normalizedObserved,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeSolutionMismatch,
            $"The active XAE solution does not match profile '{profile.Name}'.",
            stage: stage,
            component: GatewayComponent.Xae,
            expected: new IdentityEvidence
            {
                Profile = profile.Name,
                Solution = profile.Xae.Solution,
            },
            observed: new IdentityEvidence
            {
                Solution = observedSolution,
            });
    }

    public void EnsureTargetIdentity(
        ResolvedProfile profile,
        string? observedAmsNetId,
        string stage)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException(
                "Identity-check stage is required.",
                nameof(stage));
        }

        ResolvedTargetProfile target = RequireTarget(profile);
        if (string.Equals(
            target.AmsNetId,
            observedAmsNetId,
            StringComparison.Ordinal))
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeTargetMismatch,
            $"The XAE-selected target does not match profile '{profile.Name}'.",
            stage: stage,
            component: GatewayComponent.Xae,
            expected: new IdentityEvidence
            {
                Profile = profile.Name,
                AmsNetId = target.AmsNetId,
            },
            observed: new IdentityEvidence
            {
                AmsNetId = observedAmsNetId,
            });
    }

    private static string? TryNormalizeSolution(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return ResolvedXaeProfile.NormalizeSolution(path!);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException)
        {
            return null;
        }
    }
}

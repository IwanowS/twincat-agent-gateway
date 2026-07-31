using System;
using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class V2ContractSerializationTests
{
    [Fact]
    public void StateObservationsPreserveSourceAndRawEvidence()
    {
        TargetSystemObservation source = new()
        {
            Profile = "bench",
            AmsNetId = "192.168.3.31.1.1",
            RawAdsState = 5,
            RawAdsStateName = "Run",
            RawDeviceState = 2,
            State = TargetSystemState.Run,
            ObservedAtUtc = ObservedAt,
            Freshness = ObservationFreshness.Fresh,
            PlcRuntimeResources =
            {
                new ResourceReference
                {
                    Uri = "twincat-plc://profile/bench/plc-851/state",
                    MimeType = "application/json",
                },
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        TargetSystemObservation? result =
            JsonSerializer.Deserialize<TargetSystemObservation>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(ObservationSource.SystemService, result.Source);
        Assert.Equal(10000, result.Port);
        Assert.Equal(5, result.RawAdsState);
        Assert.Equal("Run", result.RawAdsStateName);
        Assert.Equal(2, result.RawDeviceState);
        Assert.Equal(TargetSystemState.Run, result.State);
        Assert.DoesNotContain("\"mode\"", json);
        Assert.Contains("\"source\":\"systemService\"", json);
    }

    [Fact]
    public void XaeSnapshotKeepsEngineeringObservationSeparate()
    {
        XaeSessionSnapshot source = new()
        {
            Profile = "bench",
            ProcessId = 42,
            Ownership = XaeProcessOwnership.Attached,
            DteAvailable = true,
            Solution = @"C:\Projects\Machine\Machine.sln",
            SolutionLoaded = true,
            ActiveConfiguration = "Debug",
            ActivePlatform = "TwinCAT RT (x64)",
            SynchronizationState = SynchronizationState.Confirmed,
            ObservedAtUtc = ObservedAt,
            TwinCatSystem = new XaeTwinCatSystemObservation
            {
                State = TargetSystemState.Config,
                RawState = "Config",
                SelectedTarget = "bench",
                ObservedAtUtc = ObservedAt,
                Freshness = ObservationFreshness.Fresh,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        XaeSessionSnapshot? result =
            JsonSerializer.Deserialize<XaeSessionSnapshot>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(XaeProcessOwnership.Attached, result.Ownership);
        Assert.Equal(
            ObservationSource.Xae,
            result.TwinCatSystem?.Source);
        Assert.Equal(
            TargetSystemState.Config,
            result.TwinCatSystem?.State);
    }

    [Fact]
    public void CapabilityAndSourceManifestRoundTrip()
    {
        CapabilityState capability = new()
        {
            Key = CapabilityKey.XaeClose,
            Configured = true,
            SessionConsented = false,
            Effective = false,
            Reason = CapabilityDenialReason.XaeCloseConsentRequired,
        };
        SourceManifest manifest = new()
        {
            Profile = "bench",
            DiscoveryState = SourceDiscoveryState.Confirmed,
            SolutionDirectory = @"C:\Projects\Machine",
            FileCount = 1,
            FilesRef = "twincat-profile://bench/sources/files",
            ObservedAtUtc = ObservedAt,
            Roots =
            {
                new SourceRootEntry
                {
                    Path = @"C:\Sources\Plc",
                    Role = "plc-source",
                    Project = "MachinePlc",
                    ProjectFile =
                        @"C:\Projects\Machine\Machine.tsproj",
                    Exists = true,
                    OutsideSolutionDirectory = true,
                    Extensions = { ".TcPOU", ".TcGVL" },
                },
            },
        };

        string capabilityJson = JsonSerializer.Serialize(
            capability,
            ContractJson.SerializerOptions);
        string manifestJson = JsonSerializer.Serialize(
            manifest,
            ContractJson.SerializerOptions);

        CapabilityState? capabilityResult =
            JsonSerializer.Deserialize<CapabilityState>(
                capabilityJson,
                ContractJson.SerializerOptions);
        SourceManifest? manifestResult =
            JsonSerializer.Deserialize<SourceManifest>(
                manifestJson,
                ContractJson.SerializerOptions);

        Assert.Equal(
            CapabilityDenialReason.XaeCloseConsentRequired,
            capabilityResult?.Reason);
        Assert.Single(manifestResult?.Roots ?? []);
        Assert.True(manifestResult?.Roots[0].OutsideSolutionDirectory);
    }

    [Fact]
    public void OperationEnvelopePreservesStructuredFailureEvidence()
    {
        OperationResult<object> source = new()
        {
            Ok = false,
            OperationId = "operation-1",
            Component = GatewayComponent.Xae,
            Stage = "xae.attach.identity",
            Completion = OperationCompletion.Failed,
            SideEffectsStarted = false,
            Error = new GatewayError
            {
                Code = "XAE_SOLUTION_MISMATCH",
                Message = "The loaded solution does not match the profile.",
                Retryable = false,
                Component = GatewayComponent.Xae,
                SideEffectsStarted = false,
                Expected = new IdentityEvidence
                {
                    Profile = "bench",
                    Solution = @"C:\Expected\Machine.sln",
                },
                Observed = new IdentityEvidence
                {
                    Solution = @"C:\Other\Machine.sln",
                },
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        OperationResult<object>? result =
            JsonSerializer.Deserialize<OperationResult<object>>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Equal(GatewayComponent.Xae, result.Component);
        Assert.Equal(OperationCompletion.Failed, result.Completion);
        Assert.False(result.SideEffectsStarted);
        Assert.Equal(
            @"C:\Expected\Machine.sln",
            result.Error?.Expected?.Solution);
        Assert.Equal(
            @"C:\Other\Machine.sln",
            result.Error?.Observed?.Solution);
        Assert.Contains("\"component\":\"xae\"", json);
        Assert.Contains("\"completion\":\"failed\"", json);
    }

    private static DateTimeOffset ObservedAt { get; } =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
}

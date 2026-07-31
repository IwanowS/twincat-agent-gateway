using System;
using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class V2ContractSerializationTests
{
    [Fact]
    public void OperatorLockKeysUseStableStringNames()
    {
        string json = JsonSerializer.Serialize(
            OperatorLockKey.XaeSynchronizationBuild,
            ContractJson.SerializerOptions);
        OperatorLockKey result =
            JsonSerializer.Deserialize<OperatorLockKey>(
                json,
                ContractJson.SerializerOptions);

        Assert.Equal("\"xaeSynchronizationBuild\"", json);
        Assert.Equal(
            OperatorLockKey.XaeSynchronizationBuild,
            result);
    }

    [Fact]
    public void GatewaySnapshotPreservesFaultEvidence()
    {
        GatewayStateSnapshot source = new()
        {
            State = GatewayProcessState.Faulted,
            Version = "2.0.0",
            ConfigurationPath = @"C:\Gateway\twincat-gateway.json",
            ActiveProfile = "bench",
            JournalId = "journal-1",
            LatestEventCursor = 17,
            ObservedAtUtc = ObservedAt,
            Error = new ObservationError
            {
                Code = "GATEWAY_FAULTED",
                Message = "Gateway state could not be refreshed.",
                Retryable = true,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        GatewayStateSnapshot? result =
            JsonSerializer.Deserialize<GatewayStateSnapshot>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(GatewayProcessState.Faulted, result.State);
        Assert.Equal(17, result.LatestEventCursor);
        Assert.Equal("GATEWAY_FAULTED", result.Error?.Code);
        Assert.True(result.Error?.Retryable);
    }

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
    public void PlcObservationPreservesAddressFreshnessAndReadError()
    {
        PlcRuntimeObservation source = new()
        {
            Profile = "bench",
            RuntimeId = "plc-851",
            Project = "MachinePlc",
            Instance = "PLC1",
            AmsNetId = "192.168.3.31.1.1",
            Port = 851,
            RawAdsState = 15,
            RawAdsStateName = "Exception",
            RawDeviceState = 4,
            State = PlcRuntimeState.Exception,
            ObservedAtUtc = ObservedAt,
            Freshness = ObservationFreshness.Stale,
            Error = new ObservationError
            {
                Code = "ADS_READ_TIMEOUT",
                Message = "The PLC state read timed out.",
                Retryable = true,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        PlcRuntimeObservation? result =
            JsonSerializer.Deserialize<PlcRuntimeObservation>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(ObservationSource.Plc, result.Source);
        Assert.Equal("192.168.3.31.1.1", result.AmsNetId);
        Assert.Equal(851, result.Port);
        Assert.Equal(15, result.RawAdsState);
        Assert.Equal(4, result.RawDeviceState);
        Assert.Equal(ObservationFreshness.Stale, result.Freshness);
        Assert.Equal("ADS_READ_TIMEOUT", result.Error?.Code);
    }

    [Fact]
    public void DivergencePreservesBothSourcesAndTimestamps()
    {
        StateObservationDivergence source = new()
        {
            Profile = "bench",
            AmsNetId = "192.168.3.31.1.1",
            XaeObserved = TargetSystemState.Run,
            SystemServiceObserved = TargetSystemState.Config,
            XaeObservedAtUtc = ObservedAt,
            SystemServiceObservedAtUtc =
                ObservedAt.AddMilliseconds(25),
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        StateObservationDivergence? result =
            JsonSerializer.Deserialize<StateObservationDivergence>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(
            ErrorCodes.StateObservationsDiverged,
            result.Code);
        Assert.Equal(GatewayComponent.Target, result.Component);
        Assert.Equal(TargetSystemState.Run, result.XaeObserved);
        Assert.Equal(
            TargetSystemState.Config,
            result.SystemServiceObserved);
        Assert.NotEqual(
            result.XaeObservedAtUtc,
            result.SystemServiceObservedAtUtc);
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
    public void XaeBuildRequestDefaultsToPlcScope()
    {
        XaeBuildParameters source = new()
        {
            Profile = "bench",
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        XaeBuildParameters? result =
            JsonSerializer.Deserialize<XaeBuildParameters>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(BuildAction.Rebuild, result.Action);
        Assert.Equal(XaeBuildScope.Plc, result.Scope);
        Assert.Null(result.Project);
        Assert.DoesNotContain("configuration", json);
        Assert.DoesNotContain("platform", json);
        Assert.DoesNotContain("discardDirtyDocuments", json);
        Assert.DoesNotContain("timeoutSeconds", json);
    }

    [Fact]
    public void XaeBuildResultPreservesResolvedProjectIdentity()
    {
        XaeBuildResult source = new()
        {
            Ok = true,
            OperationId = "build-1",
            Action = BuildAction.Build,
            Scope = XaeBuildScope.Plc,
            Project = "MachinePlc",
            Counts = new DiagnosticCounts
            {
                Errors = 0,
                Warnings = 1,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        XaeBuildResult? result =
            JsonSerializer.Deserialize<XaeBuildResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(XaeBuildScope.Plc, result.Scope);
        Assert.Equal("MachinePlc", result.Project);
        Assert.Equal(1, result.Counts.Warnings);
        Assert.Contains("\"scope\":\"plc\"", json);
    }

    [Fact]
    public void XaeBuildSolutionScopeKeepsProjectNull()
    {
        XaeBuildParameters source = new()
        {
            Profile = "bench",
            Action = BuildAction.Clean,
            Scope = XaeBuildScope.Solution,
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        XaeBuildParameters? result =
            JsonSerializer.Deserialize<XaeBuildParameters>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(XaeBuildScope.Solution, result.Scope);
        Assert.Null(result.Project);
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

    [Fact]
    public void StageEnvelopePreservesTypedResultDiagnosticsAndResources()
    {
        OperationStageResult<StageEvidence> source = new()
        {
            OperationId = "operation-1",
            Component = GatewayComponent.Verification,
            Stage = "verification.tcunit",
            Completion = OperationCompletion.Succeeded,
            SideEffectsStarted = true,
            Result = new StageEvidence
            {
                ReportFound = true,
            },
            Diagnostics =
            {
                new OperationDiagnostic
                {
                    Code = "TCUNIT_REPORT_FRESH",
                    Component = GatewayComponent.Verification,
                    Stage = "verification.tcunit.report",
                    Severity = DiagnosticSeverity.Info,
                    Message = "A fresh report was observed.",
                    OccurredAtUtc = ObservedAt,
                },
            },
            Resources =
            {
                new ResourceReference
                {
                    Uri = "twincat-operation://operation-1/test",
                    MimeType = "application/json",
                },
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        OperationStageResult<StageEvidence>? result =
            JsonSerializer.Deserialize<OperationStageResult<StageEvidence>>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("operation-1", result.OperationId);
        Assert.True(result.Result?.ReportFound);
        Assert.Single(result.Diagnostics);
        Assert.Equal(
            "TCUNIT_REPORT_FRESH",
            result.Diagnostics[0].Code);
        Assert.Equal(
            "twincat-operation://operation-1/test",
            Assert.Single(result.Resources).Uri);
    }

    [Fact]
    public void TargetConfigContractPreservesDirectObservations()
    {
        TargetConfigResult source = new()
        {
            Ok = true,
            OperationId = "target-config-1",
            Profile = "bench",
            Action = TargetTransitionAction.Config,
            Before = new TargetSystemObservation
            {
                Profile = "bench",
                AmsNetId = "192.168.3.31.1.1",
                Port = 10000,
                RawAdsState = 15,
                RawAdsStateName = "Exception",
                RawDeviceState = 4,
                State = TargetSystemState.Exception,
                ObservedAtUtc = ObservedAt,
                Freshness = ObservationFreshness.Fresh,
            },
            After = new TargetSystemObservation
            {
                Profile = "bench",
                AmsNetId = "192.168.3.31.1.1",
                Port = 10000,
                RawAdsState = 16,
                RawAdsStateName = "Config",
                RawDeviceState = 2,
                State = TargetSystemState.Config,
                ObservedAtUtc = ObservedAt.AddSeconds(2),
                Freshness = ObservationFreshness.Fresh,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        TargetConfigResult? result =
            JsonSerializer.Deserialize<TargetConfigResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(TargetTransitionAction.Config, result.Action);
        Assert.Equal(15, result.Before.RawAdsState);
        Assert.Equal(4, result.Before.RawDeviceState);
        Assert.Equal(TargetSystemState.Config, result.After.State);
        Assert.Contains("\"action\":\"config\"", json);
    }

    [Fact]
    public void TargetStartRestartContractPreservesNonIdempotentAction()
    {
        TargetStartRestartResult source = new()
        {
            Ok = true,
            OperationId = "target-restart-1",
            Profile = "bench",
            Action = TargetTransitionAction.Restart,
            Before = new TargetSystemObservation
            {
                Profile = "bench",
                AmsNetId = "192.168.3.31.1.1",
                Port = 10000,
                State = TargetSystemState.Run,
                ObservedAtUtc = ObservedAt,
                Freshness = ObservationFreshness.Fresh,
            },
            After = new TargetSystemObservation
            {
                Profile = "bench",
                AmsNetId = "192.168.3.31.1.1",
                Port = 10000,
                State = TargetSystemState.Run,
                ObservedAtUtc = ObservedAt.AddSeconds(5),
                Freshness = ObservationFreshness.Fresh,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        TargetStartRestartResult? result =
            JsonSerializer.Deserialize<TargetStartRestartResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(TargetTransitionAction.Restart, result.Action);
        Assert.Equal(TargetSystemState.Run, result.Before.State);
        Assert.Equal(TargetSystemState.Run, result.After.State);
        Assert.Contains("\"action\":\"restart\"", json);
    }

    [Fact]
    public void TargetTransitionCutoverHasNoRecoveryContractAlias()
    {
        Assert.False(Enum.TryParse(
            "RecoverToConfig",
            ignoreCase: false,
            out OperationKind _));
        Assert.Null(typeof(GatewayMethods).GetField(
            "RecoverToConfig"));
        Assert.Equal(
            "target.config.succeeded",
            GatewayEventTypes.TargetConfigSucceeded);
        Assert.Equal(
            "target.startRestart.succeeded",
            GatewayEventTypes.TargetStartRestartSucceeded);
    }

    private sealed class StageEvidence
    {
        public bool ReportFound { get; set; }
    }

    private static DateTimeOffset ObservedAt { get; } =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class MvpContractSerializationTests
{
    [Fact]
    public void RecoverToConfigContractsRoundTrip()
    {
        RecoverToConfigParameters parameters = new()
        {
            Profile = "fixture",
            TimeoutSeconds = 30,
        };
        RecoverToConfigResult source = new()
        {
            Ok = true,
            OperationId = "recover-1",
            Profile = "fixture",
            Solution = @"C:\Project\Machine.sln",
            Target = new TargetIdentity
            {
                AmsNetId = "192.168.3.31.1.1",
            },
            InitialRuntimeMode = RuntimeMode.Exception,
            ObservedRuntimeMode = RuntimeMode.Config,
            TransitionRequested = true,
        };

        string parametersJson = JsonSerializer.Serialize(
            parameters,
            ContractJson.SerializerOptions);
        string resultJson = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        RecoverToConfigParameters parametersResult =
            JsonSerializer.Deserialize<RecoverToConfigParameters>(
                parametersJson,
                ContractJson.SerializerOptions)
            ?? throw new InvalidOperationException(
                "Recovery parameters did not deserialize.");
        RecoverToConfigResult result =
            JsonSerializer.Deserialize<RecoverToConfigResult>(
                resultJson,
                ContractJson.SerializerOptions)
            ?? throw new InvalidOperationException(
                "Recovery result did not deserialize.");

        Assert.Equal("fixture", parametersResult.Profile);
        Assert.Equal(30, parametersResult.TimeoutSeconds);
        Assert.Equal(
            RuntimeMode.Exception,
            result.InitialRuntimeMode);
        Assert.Equal(
            RuntimeMode.Config,
            result.ObservedRuntimeMode);
        Assert.True(result.TransitionRequested);
        Assert.Contains(
            "\"observedRuntimeMode\":\"config\"",
            resultJson);
    }

    [Fact]
    public void SynchronizeContractsRoundTrip()
    {
        SynchronizeParameters parameters = new()
        {
            Profile = "fixture",
            ChangedPaths = { @"C:\Project\MAIN.TcPOU" },
            DiscardDirtyDocuments = true,
            TimeoutSeconds = 45,
        };
        string json = JsonSerializer.Serialize(
            parameters,
            ContractJson.SerializerOptions);
        SynchronizeParameters result =
            JsonSerializer.Deserialize<SynchronizeParameters>(
                json,
                ContractJson.SerializerOptions)
            ?? throw new InvalidOperationException(
                "Synchronization parameters did not deserialize.");

        Assert.Equal("fixture", result.Profile);
        Assert.True(result.DiscardDirtyDocuments);
        Assert.Equal(45, result.TimeoutSeconds);
        Assert.Single(result.ChangedPaths);
        Assert.Contains(
            "\"discardDirtyDocuments\":true",
            json);
    }

    [Fact]
    public void GatewayStartResultRoundTrips()
    {
        GatewayResponse<GatewayStartResult> source = new()
        {
            Ok = true,
            Result = new GatewayStartResult
            {
                Started = true,
                AlreadyRunning = false,
                ProcessId = 1234,
                Status = new GatewayStatusResult
                {
                    Gateway = new GatewayStatus
                    {
                        Ready = true,
                        ConfigurationPath =
                            @"C:\Project\twincat-gateway.json",
                        ActiveProfile = "fixture",
                        SolutionPath =
                            @"C:\Project\Machine.sln",
                        LaunchSource =
                            GatewayLaunchSource.Agent,
                        UiMode = GatewayUiMode.Tray,
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        GatewayResponse<GatewayStartResult> result =
            JsonSerializer.Deserialize<
                GatewayResponse<GatewayStartResult>>(
                    json,
                    ContractJson.SerializerOptions)
            ?? throw new InvalidOperationException(
                "Gateway start result did not deserialize.");

        Assert.True(result.Ok);
        Assert.True(result.Result?.Started);
        Assert.Equal(1234, result.Result?.ProcessId);
        Assert.Equal(
            @"C:\Project\Machine.sln",
            result.Result?.Status.Gateway.SolutionPath);
    }

    [Fact]
    public void GatewayShutdownResultRoundTrips()
    {
        GatewayResponse<GatewayShutdownResult> source = new()
        {
            Ok = true,
            Result = new GatewayShutdownResult
            {
                ShutdownRequested = true,
            },
        };

        string json = JsonSerializer.Serialize(
            source,
            ContractJson.SerializerOptions);
        GatewayResponse<GatewayShutdownResult> result =
            JsonSerializer.Deserialize<
                GatewayResponse<GatewayShutdownResult>>(
                    json,
                    ContractJson.SerializerOptions)
            ?? throw new InvalidOperationException(
                "Gateway shutdown result did not deserialize.");

        Assert.True(result.Ok);
        Assert.True(result.Result?.ShutdownRequested);
    }

    [Fact]
    public void BuildResultRoundTripsAsCompactContract()
    {
        BuildResult build = new()
        {
            Ok = false,
            OperationId = "operation-build",
            Action = BuildAction.Rebuild,
            DurationMs = 18472,
            Counts = new DiagnosticCounts
            {
                Errors = 2,
                Warnings = 1,
            },
            Diagnostics =
            {
                new BuildDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = "plc-compiler",
                    Code = "C0032",
                    Message = "Cannot convert type.",
                    File = "Plc/POUs/FB_Test.TcPOU",
                    Line = 48,
                    Column = 17,
                },
            },
            ExpectedProjectNoise =
            {
                new ProjectChangeSummary
                {
                    File = "Machine.tsproj",
                    Classification = ProjectChangeClassification.ExpectedReorderOnly,
                    MovedBlocks = 18,
                    DoNotInspectFullFile = true,
                },
                new ProjectChangeSummary
                {
                    File = "PLC/Machine.tmc",
                    Classification = ProjectChangeClassification.ExpectedGeneratedArtifact,
                    DoNotInspectFullFile = true,
                },
            },
            Log = new ResourceReference
            {
                Uri = "twincat-log://operation-build/build",
                OperationId = "operation-build",
                Kind = ResourceKind.BuildLog,
            },
        };

        string json = JsonSerializer.Serialize(build, ContractJson.SerializerOptions);
        BuildResult? result =
            JsonSerializer.Deserialize<BuildResult>(json, ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Equal(BuildAction.Rebuild, result.Action);
        Assert.Equal(2, result.Counts.Errors);
        Assert.Equal("C0032", Assert.Single(result.Diagnostics).Code);
        Assert.All(
            result.ExpectedProjectNoise,
            item => Assert.True(item.DoNotInspectFullFile));
        Assert.DoesNotContain("fullOutput", json);
        Assert.Contains("\"classification\":\"expectedReorderOnly\"", json);
        Assert.Contains(
            "\"classification\":\"expectedGeneratedArtifact\"",
            json);
    }

    [Fact]
    public void StatusPreservesUnknownRuntimeEvidence()
    {
        GatewayStatusResult status = new()
        {
            Gateway = new GatewayStatus
            {
                State = GatewayState.Ready,
                Version = "0.1.0",
                Ready = true,
                ConfigurationPath =
                    @"C:\Projects\Machine\twincat-gateway.json",
                ActiveProfile = "bench",
                SolutionPath = @"C:\TwinCAT\Machine.sln",
                LaunchSource = GatewayLaunchSource.Agent,
                UiMode = GatewayUiMode.Tray,
            },
            Xae = new XaeStatus
            {
                Connected = true,
                Version = "16.0",
                Solution = @"C:\Projects\Machine\Machine.sln",
                AgentWorkspaceOwned = true,
                DiscardedDocumentCount = 2,
            },
            TwinCat = new TwinCatStatus
            {
                Started = null,
                Mode = RuntimeMode.Unknown,
                SystemMode = RuntimeMode.Run,
                ObservedAtUtc = new DateTimeOffset(
                    2026,
                    7,
                    29,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                Alert = new RuntimeAlert
                {
                    Code = "PLC_RUNTIME_EXCEPTION",
                    Severity = DiagnosticSeverity.Error,
                    Message =
                        "PLC runtime 'PlcProject2' is in Exception.",
                    Details =
                        "Page Fault in PlcProject2 on ADS port 852.",
                    OccurredAtUtc = new DateTimeOffset(
                        2026,
                        7,
                        29,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    EventCursor = 41,
                    RuntimeName = "PlcProject2",
                    AdsPort = 852,
                },
            },
            EventStreamId = "stream-42",
            LatestEventCursor = 42,
        };

        string json = JsonSerializer.Serialize(status, ContractJson.SerializerOptions);
        GatewayStatusResult? result =
            JsonSerializer.Deserialize<GatewayStatusResult>(json, ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Null(result.TwinCat.Started);
        Assert.Equal(RuntimeMode.Unknown, result.TwinCat.Mode);
        Assert.Equal(
            RuntimeMode.Run,
            result.TwinCat.SystemMode);
        Assert.Equal(
            "PLC_RUNTIME_EXCEPTION",
            result.TwinCat.Alert?.Code);
        Assert.Equal(852, result.TwinCat.Alert?.AdsPort);
        Assert.Equal(
            "Page Fault in PlcProject2 on ADS port 852.",
            result.TwinCat.Alert?.Details);
        Assert.True(result.Gateway.Ready);
        Assert.Equal(
            @"C:\Projects\Machine\twincat-gateway.json",
            result.Gateway.ConfigurationPath);
        Assert.Equal("bench", result.Gateway.ActiveProfile);
        Assert.Equal(
            @"C:\TwinCAT\Machine.sln",
            result.Gateway.SolutionPath);
        Assert.Equal(
            GatewayLaunchSource.Agent,
            result.Gateway.LaunchSource);
        Assert.Equal(
            GatewayUiMode.Tray,
            result.Gateway.UiMode);
        Assert.True(result.Xae.AgentWorkspaceOwned);
        Assert.Equal(2, result.Xae.DiscardedDocumentCount);
        Assert.Contains("\"discardedDocumentCount\":2", json);
        Assert.Equal("stream-42", result.EventStreamId);
        Assert.Equal(42, result.LatestEventCursor);
        Assert.Contains("\"mode\":\"unknown\"", json);
        Assert.DoesNotContain("unreadErrors", json);
        Assert.DoesNotContain("latestErrorCursor", json);
    }

    [Fact]
    public void DiagnosticsPreserveUnsynchronizedFileDetails()
    {
        GatewayDiagnosticsResult diagnostics = new()
        {
            Xae = new XaeDiagnostics
            {
                UnsynchronizedFiles = new List<UnsynchronizedFileInfo>
                {
                    new()
                    {
                        Path =
                            @"C:\Projects\Machine\PlcProject\MAIN.TcPOU",
                        ChangeKind =
                            SynchronizationChangeKind.Modified,
                        Role = SynchronizationFileRole.PlcSource,
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(
            diagnostics,
            ContractJson.SerializerOptions);
        GatewayDiagnosticsResult? result =
            JsonSerializer.Deserialize<GatewayDiagnosticsResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        UnsynchronizedFileInfo file =
            Assert.Single(result.Xae.UnsynchronizedFiles);
        Assert.Equal(
            @"C:\Projects\Machine\PlcProject\MAIN.TcPOU",
            file.Path);
        Assert.Equal(
            SynchronizationChangeKind.Modified,
            file.ChangeKind);
        Assert.Equal(
            SynchronizationFileRole.PlcSource,
            file.Role);
        Assert.Contains(
            "\"changeKind\":\"modified\"",
            json);
    }

    [Fact]
    public void ActivationProfileRoundTripsExpectedTargetAndFixedTcUnitSymbols()
    {
        ProjectProfile profile = new()
        {
            Name = "bench-remote",
            Solution = @"C:\Projects\Machine\Machine.sln",
            XaeProgId = "VisualStudio.DTE.16.0",
            AllowActivation = true,
            AssumeAttachedXaeSynchronized = false,
            AutoSynchronizeBeforeOperation = false,
            ExpectedTarget = new TargetIdentity
            {
                Name = "WIN-T077ADA",
                AmsNetId = "192.168.3.31.1.1",
            },
            AutoWaitForTcUnit = true,
            TcUnit = new TcUnitProfile
            {
                ReportPath = @"C:\TwinCAT\3.1\Boot\tcunit_xunit_testresults.xml",
            },
        };

        string json = JsonSerializer.Serialize(profile, ContractJson.SerializerOptions);
        ProjectProfile? result =
            JsonSerializer.Deserialize<ProjectProfile>(json, ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("VisualStudio.DTE.16.0", result.XaeProgId);
        Assert.DoesNotContain("\"unsavedDocuments\"", json);
        Assert.True(result.AllowActivation);
        Assert.False(result.AssumeAttachedXaeSynchronized);
        Assert.False(result.AutoSynchronizeBeforeOperation);
        Assert.Contains(
            "\"assumeAttachedXaeSynchronized\":false",
            json);
        Assert.Contains(
            "\"autoSynchronizeBeforeOperation\":false",
            json);
        Assert.Equal("WIN-T077ADA", result.ExpectedTarget?.Name);
        Assert.Equal("192.168.3.31.1.1", result.ExpectedTarget?.AmsNetId);
        Assert.Equal(
            "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished",
            result.TcUnit?.FinishedSymbol);
        Assert.Equal(ZeroTestsPolicy.Fail, result.TcUnit?.ZeroTests);
    }

    [Fact]
    public void ActivationResultUsesEventsAndLogInsteadOfEmbeddedTimeline()
    {
        ActivationResult activation = new()
        {
            Ok = true,
            OperationId = "operation-activate",
            DurationMs = 4321,
            Profile = "bench-remote",
            Solution = @"C:\Projects\Machine\Machine.sln",
            Target = new TargetIdentity
            {
                Name = "WIN-T077ADA",
                AmsNetId = "192.168.3.31.1.1",
            },
            RecoveryAttempted = true,
            RunAfterActivation = false,
            Completion = ActivationCompletion.RestartSkipped,
            ActiveConfigurationVerified = false,
            ObservedRuntimeMode = RuntimeMode.Run,
            AutostartBootProjects =
                AutostartBootProjectSelection.PartiallyEnabled,
            Resources =
            {
                new ResourceReference
                {
                    Uri =
                        "twincat-log://operation-activate/activation",
                    OperationId = "operation-activate",
                    Kind = ResourceKind.ActivationLog,
                },
            },
        };

        string json = JsonSerializer.Serialize(
            activation,
            ContractJson.SerializerOptions);
        ActivationResult? result =
            JsonSerializer.Deserialize<ActivationResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.True(result.RecoveryAttempted);
        Assert.False(result.RunAfterActivation);
        Assert.Equal(
            ActivationCompletion.RestartSkipped,
            result.Completion);
        Assert.False(result.ActiveConfigurationVerified);
        Assert.Equal(
            RuntimeMode.Run,
            result.ObservedRuntimeMode);
        Assert.Equal(
            AutostartBootProjectSelection.PartiallyEnabled,
            result.AutostartBootProjects);
        Assert.Contains(
            "\"completion\":\"restartSkipped\"",
            json);
        Assert.Contains(
            "\"observedRuntimeMode\":\"run\"",
            json);
        Assert.Contains(
            "\"autostartBootProjects\":\"partiallyEnabled\"",
            json);
        Assert.Equal(
            ResourceKind.ActivationLog,
            Assert.Single(result.Resources).Kind);
        Assert.DoesNotContain("\"timeline\"", json);
    }

    [Fact]
    public void TestResultLinksBackToActivation()
    {
        TestResult test = new()
        {
            Ok = false,
            OperationId = "operation-test",
            ActivationOperationId =
                "operation-activate",
            DurationMs = 1200,
            Counts = new TestCounts
            {
                Suites = 2,
                Tests = 5,
                Passed = 4,
                Failed = 1,
            },
            InitializedSuites = 2,
            Failures =
            {
                new TestFailure
                {
                    Suite = "MotionTests",
                    Name = "Stops",
                    Message = "Expected stop.",
                },
            },
        };

        string json = JsonSerializer.Serialize(
            test,
            ContractJson.SerializerOptions);
        TestResult? result =
            JsonSerializer.Deserialize<TestResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(
            "operation-activate",
            result.ActivationOperationId);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void DteInspectionFailureRoundTripsWithoutAStackTrace()
    {
        DteInstanceInfo instance = new()
        {
            Moniker = "!VisualStudio.DTE.16.0:1234",
            ProgId = "VisualStudio.DTE.16.0",
            InspectionError = "The DTE instance could not be inspected.",
            InspectionHResult = unchecked((int)0x80010001),
        };

        string json = JsonSerializer.Serialize(
            instance,
            ContractJson.SerializerOptions);
        DteInstanceInfo? result =
            JsonSerializer.Deserialize<DteInstanceInfo>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(instance.InspectionError, result.InspectionError);
        Assert.Equal(instance.InspectionHResult, result.InspectionHResult);
        Assert.DoesNotContain("stackTrace", json);
    }

    [Fact]
    public void DetailedXaeDiagnosticsRoundTripTypedHealthEvidence()
    {
        XaeDiagnostics diagnostics = new()
        {
            SysManagerAvailable = true,
            ActiveConfiguration = "Debug",
            ActivePlatform = "TwinCAT RT (x64)",
            Target = new TargetIdentity
            {
                Name = "WIN-T077ADA",
                AmsNetId = "192.168.3.31.1.1",
            },
            LastErrorMessages =
            {
                "Previous TwinCAT subsystem error.",
            },
            InspectionIssues =
            {
                "activeSolutionConfiguration: COM call failed "
                + "(HRESULT 0x80010001).",
            },
            LastHResult = unchecked((int)0x80010001),
        };

        string json = JsonSerializer.Serialize(
            diagnostics,
            ContractJson.SerializerOptions);
        XaeDiagnostics? result =
            JsonSerializer.Deserialize<XaeDiagnostics>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("Debug", result.ActiveConfiguration);
        Assert.Equal(
            "TwinCAT RT (x64)",
            result.ActivePlatform);
        Assert.Equal(
            "192.168.3.31.1.1",
            result.Target?.AmsNetId);
        Assert.Single(result.LastErrorMessages);
        Assert.Single(result.InspectionIssues);
        Assert.DoesNotContain("stackTrace", json);
    }

    [Fact]
    public void AdsRuntimeDiagnosticsRoundTripRawStateEvidence()
    {
        AdsRuntimeDiagnostics diagnostics = new()
        {
            RuntimeName = "TwinCAT System",
            AmsNetId = "192.168.3.31.1.1",
            Port = 10000,
            AdsState = "Exception",
            DeviceState = 7,
            ReadAtUtc = new DateTimeOffset(
                2026,
                7,
                28,
                1,
                2,
                3,
                TimeSpan.Zero),
        };

        string json = JsonSerializer.Serialize(
            diagnostics,
            ContractJson.SerializerOptions);
        AdsRuntimeDiagnostics? result =
            JsonSerializer.Deserialize<AdsRuntimeDiagnostics>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("TwinCAT System", result.RuntimeName);
        Assert.Equal("192.168.3.31.1.1", result.AmsNetId);
        Assert.Equal(10000, result.Port);
        Assert.Equal("Exception", result.AdsState);
        Assert.Equal((short)7, result.DeviceState);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void DiagnosticsEventCursorRoundTripsWithoutReadMutation()
    {
        GatewayDiagnosticsResult diagnostics = new()
        {
            EventStreamId = "stream-10",
            NextScanCursor = 8,
            LatestEventCursor = 10,
            MoreMatchingEventsAvailable = true,
            EventHistoryTruncated = false,
            Events =
            {
                new GatewayEvent
                {
                    Cursor = 8,
                    OccurredAtUtc = new DateTimeOffset(
                        2026,
                        7,
                        28,
                        1,
                        2,
                        3,
                        TimeSpan.Zero),
                    Type = "build.failed",
                    Severity = DiagnosticSeverity.Error,
                    OperationId = "operation-8",
                    OperationKind = OperationKind.Build,
                    Stage = "build.verify",
                    Message = "Build failed.",
                    Error = new GatewayError
                    {
                        Code = ErrorCodes.BuildFailed,
                        Message = "Build failed.",
                        OperationId = "operation-8",
                    },
                    Properties =
                    {
                        ["action"] = "rebuild",
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(
            diagnostics,
            ContractJson.SerializerOptions);
        GatewayDiagnosticsResult? result =
            JsonSerializer.Deserialize<GatewayDiagnosticsResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("stream-10", result.EventStreamId);
        Assert.Equal(8, result.NextScanCursor);
        Assert.Equal(10, result.LatestEventCursor);
        Assert.True(result.MoreMatchingEventsAvailable);
        GatewayEvent entry = Assert.Single(result.Events);
        Assert.Equal(8, entry.Cursor);
        Assert.Equal("build.failed", entry.Type);
        Assert.Equal(ErrorCodes.BuildFailed, entry.Error?.Code);
        Assert.Equal("rebuild", entry.Properties["action"]);
        Assert.DoesNotContain("stackTrace", json);
    }

    [Fact]
    public void DiagnosticsRequestRoundTripsSharedEventCursorAndFilter()
    {
        GetDiagnosticsParameters parameters = new()
        {
            EventStreamId = "stream-10",
            AfterEventCursor = 8,
            MaximumEvents = 25,
            MinimumSeverity = DiagnosticSeverity.Error,
        };

        string json = JsonSerializer.Serialize(
            parameters,
            ContractJson.SerializerOptions);
        GetDiagnosticsParameters? result =
            JsonSerializer.Deserialize<GetDiagnosticsParameters>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal("stream-10", result.EventStreamId);
        Assert.Equal(8, result.AfterEventCursor);
        Assert.Equal(25, result.MaximumEvents);
        Assert.Equal(
            DiagnosticSeverity.Error,
            result.MinimumSeverity);
        Assert.Contains("\"minimumSeverity\":\"error\"", json);
    }

    [Fact]
    public void XaeMessagesRoundTripWithBoundedDiagnostics()
    {
        XaeMessagesResult messages = new()
        {
            Solution = @"C:\Project\Fixture.sln",
            ReadAtUtc = new DateTimeOffset(
                2026,
                7,
                29,
                0,
                0,
                0,
                TimeSpan.Zero),
            Counts = new DiagnosticCounts
            {
                Errors = 1,
                Warnings = 1,
            },
            Messages =
            {
                new BuildDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = "xae-error-list",
                    Message =
                        "Exception Code 0xc0000005.",
                },
                new BuildDiagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Source = "xae-error-list",
                    Message = "Unused variable.",
                },
            },
            MoreMessages = 3,
        };

        string json = JsonSerializer.Serialize(
            messages,
            ContractJson.SerializerOptions);
        XaeMessagesResult? result =
            JsonSerializer.Deserialize<XaeMessagesResult>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(1, result.Counts.Errors);
        Assert.Equal(1, result.Counts.Warnings);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(3, result.MoreMessages);
        Assert.Contains(
            "\"source\":\"xae-error-list\"",
            json);
    }
}

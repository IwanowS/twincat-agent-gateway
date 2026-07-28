using System;
using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class MvpContractSerializationTests
{
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
        Assert.True(Assert.Single(result.ExpectedProjectNoise).DoNotInspectFullFile);
        Assert.DoesNotContain("fullOutput", json);
        Assert.Contains("\"classification\":\"expectedReorderOnly\"", json);
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
    public void ActivationProfileRoundTripsExpectedTargetAndFixedTcUnitSymbols()
    {
        ProjectProfile profile = new()
        {
            Name = "bench-remote",
            Solution = @"C:\Projects\Machine\Machine.sln",
            XaeProgId = "VisualStudio.DTE.16.0",
            AllowActivation = true,
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
}

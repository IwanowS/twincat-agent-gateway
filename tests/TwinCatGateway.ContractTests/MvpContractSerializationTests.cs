using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class MvpContractSerializationTests
{
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
            },
            Xae = new XaeStatus
            {
                Connected = true,
                Version = "16.0",
                Solution = @"C:\Projects\Machine\Machine.sln",
                AgentWorkspaceOwned = true,
            },
            TwinCat = new TwinCatStatus
            {
                Started = null,
                Mode = RuntimeMode.Unknown,
            },
        };

        string json = JsonSerializer.Serialize(status, ContractJson.SerializerOptions);
        GatewayStatusResult? result =
            JsonSerializer.Deserialize<GatewayStatusResult>(json, ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Null(result.TwinCat.Started);
        Assert.Equal(RuntimeMode.Unknown, result.TwinCat.Mode);
        Assert.True(result.Xae.AgentWorkspaceOwned);
        Assert.Contains("\"mode\":\"unknown\"", json);
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
}

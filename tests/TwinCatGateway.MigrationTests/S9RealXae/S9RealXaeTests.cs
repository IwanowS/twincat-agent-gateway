using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.S9RealXaeTests;

[AttributeUsage(AttributeTargets.Method)]
public class S9RealXaeFactAttribute : FactAttribute
{
    public S9RealXaeFactAttribute()
    {
        if (Missing("TWINCAT_GATEWAY_S9_CONFIG")
            || Missing("TWINCAT_GATEWAY_S9_PIPE")
            || Missing("TWINCAT_GATEWAY_S9_PROFILE")
            || Missing("TWINCAT_GATEWAY_XAE_SOLUTION"))
        {
            Skip = "Requires an already running checkout-built v2 Gateway and explicit S9 config, pipe, profile, and XAE solution variables.";
        }
    }

    protected static bool Missing(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class S9RemoteTcUnitFactAttribute : S9RealXaeFactAttribute
{
    public S9RemoteTcUnitFactAttribute()
    {
        if (Skip is null
            && !string.Equals(Environment.GetEnvironmentVariable("TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION"), "1", StringComparison.Ordinal))
        {
            Skip = "Requires TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION=1 in addition to the S9 checkout identity variables.";
        }
    }
}

public sealed class S9RealXaeTests
{
    private static readonly JsonSerializerOptions JsonOptions = GatewayJson.CreateSerializerOptions();

    [S9RealXaeFact]
    public async Task PlcBuildUsesExactCheckoutProfileAndOperationId()
    {
        TwinCatGatewayClient client = CreateClient();
        await VerifyCheckoutIdentityAsync(client);
        OperationResult<XaeBuildResult> build = await client.BuildXaeAsync(
            new XaeBuildParameters
            {
                Profile = Required("TWINCAT_GATEWAY_S9_PROFILE"),
                Action = BuildAction.Build,
                Scope = XaeBuildScope.Plc,
                Project = Environment.GetEnvironmentVariable("TWINCAT_GATEWAY_S9_PLC_PROJECT"),
            },
            CancellationToken.None);

        Assert.True(build.Ok, build.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(build.OperationId));
        Assert.Equal(build.OperationId, build.Result?.OperationId);
    }

    [S9RemoteTcUnitFact]
    public async Task ActivationThenRestartProduceFreshReportsAndFinalRun()
    {
        TwinCatGatewayClient client = CreateClient();
        await VerifyCheckoutIdentityAsync(client);
        string profile = Required("TWINCAT_GATEWAY_S9_PROFILE");
        OperationResult<ActivationResult> activation = await client.ActivateXaeAsync(
            new ActivateParameters
            {
                Profile = profile,
                FinalTargetMode = ActivationFinalTargetMode.Run,
                Verification = VerificationMode.TcUnit,
                TimeoutSeconds = 180,
            },
            CancellationToken.None);
        Assert.True(activation.Ok, activation.Error?.Message);
        ResourceReference activationReport = Assert.IsType<TestResult>(activation.Result?.Verification.Result).Report!;
        Assert.Contains(activation.OperationId, activationReport.Uri, StringComparison.Ordinal);

        OperationResult<TargetStartRestartResult> restart = await client.StartRestartTargetAsync(
            new TargetStartRestartParameters { Profile = profile, Verification = VerificationMode.TcUnit },
            CancellationToken.None);
        Assert.True(restart.Ok, restart.Error?.Message);
        Assert.NotEqual(activation.OperationId, restart.OperationId);
        ResourceReference restartReport = Assert.IsType<TestResult>(restart.Result?.Verification.Result).Report!;
        Assert.Contains(restart.OperationId, restartReport.Uri, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace((await client.GetResourceAsync(activationReport.Uri)).Content));
        Assert.False(string.IsNullOrWhiteSpace((await client.GetResourceAsync(restartReport.Uri)).Content));
        TargetSystemObservation target = await ReadJsonAsync<TargetSystemObservation>(
            client,
            $"twincat-target://profile/{Uri.EscapeDataString(profile)}/state");
        Assert.Equal(TargetSystemState.Run, target.State);
        Assert.Equal(ObservationFreshness.Fresh, target.Freshness);
        Assert.Null(target.Error);
    }

    private static TwinCatGatewayClient CreateClient() =>
        new(Required("TWINCAT_GATEWAY_S9_PIPE"), TimeSpan.FromSeconds(10));

    private static async Task VerifyCheckoutIdentityAsync(TwinCatGatewayClient client)
    {
        GatewayStateSnapshot gateway = await client.GetGatewayStateAsync();
        Assert.Equal(Path.GetFullPath(Required("TWINCAT_GATEWAY_S9_CONFIG")), Path.GetFullPath(gateway.ConfigurationPath!), ignoreCase: true);
        string profile = Required("TWINCAT_GATEWAY_S9_PROFILE");
        XaeSessionSnapshot xae = await ReadJsonAsync<XaeSessionSnapshot>(
            client,
            $"twincat-xae://profile/{Uri.EscapeDataString(profile)}/state");
        Assert.Equal(Path.GetFullPath(Required("TWINCAT_GATEWAY_XAE_SOLUTION")), Path.GetFullPath(xae.Solution!), ignoreCase: true);
        Assert.True(xae.DteAvailable);
        Assert.True(xae.SolutionLoaded);
        Assert.Equal(SynchronizationState.Confirmed, xae.SynchronizationState);
        Assert.Empty(xae.DirtyDocuments);
    }

    private static async Task<T> ReadJsonAsync<T>(TwinCatGatewayClient client, string uri)
    {
        ResourceContent resource = await client.GetResourceAsync(uri);
        return JsonSerializer.Deserialize<T>(resource.Content, JsonOptions)
            ?? throw new InvalidDataException($"Resource '{uri}' contained no JSON value.");
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Environment variable '{name}' is required.");
}

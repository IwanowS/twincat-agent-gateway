using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Mcp;

public sealed class GatewayMcpOptions
{
    public string? ExplicitConfigurationPath { get; set; }

    public string? PipeNameOverride { get; set; }

    public string CurrentDirectory { get; set; } =
        Environment.CurrentDirectory;

    public string GatewayCommand { get; set; } =
        "twincat-gateway";
}

public interface IGatewayClientFactory
{
    ITwinCatGatewayClient Create(string pipeName);
}

public sealed class GatewayClientFactory : IGatewayClientFactory
{
    public ITwinCatGatewayClient Create(string pipeName)
    {
        return new TwinCatGatewayClient(
            pipeName,
            TimeSpan.FromMilliseconds(500));
    }
}

public interface IGatewayProcessLauncher
{
    void Launch(
        string command,
        string configurationPath);
}

public sealed class GatewayProcessLauncher
    : IGatewayProcessLauncher
{
    public void Launch(
        string command,
        string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(
                "Gateway command is required.",
                nameof(command));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = command,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments =
                "--config "
                + QuoteArgument(configurationPath)
                + " --launch-source agent",
        };
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The gateway process could not be created.");
    }

    private static string QuoteArgument(string value)
    {
        return "\""
            + value.Replace("\"", "\\\"")
            + "\"";
    }
}

public sealed class GatewayMcpRuntime
{
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(100);
    private readonly GatewayMcpOptions _options;
    private readonly GatewayInstanceRegistry _registry;
    private readonly IGatewayClientFactory _clientFactory;
    private readonly IGatewayProcessLauncher _processLauncher;
    private readonly TimeSpan _pollInterval;

    public GatewayMcpRuntime(GatewayMcpOptions options)
        : this(
            options,
            new GatewayInstanceRegistry(),
            new GatewayClientFactory(),
            new GatewayProcessLauncher(),
            DefaultPollInterval)
    {
    }

    public GatewayMcpRuntime(
        GatewayMcpOptions options,
        GatewayInstanceRegistry registry,
        IGatewayClientFactory clientFactory,
        IGatewayProcessLauncher processLauncher,
        TimeSpan pollInterval)
    {
        _options = options
            ?? throw new ArgumentNullException(nameof(options));
        _registry = registry
            ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory
            ?? throw new ArgumentNullException(nameof(clientFactory));
        _processLauncher = processLauncher
            ?? throw new ArgumentNullException(nameof(processLauncher));
        ArgumentOutOfRangeException
            .ThrowIfLessThanOrEqual(
                pollInterval,
                TimeSpan.Zero);

        _pollInterval = pollInterval;
    }

    public async Task<ITwinCatGatewayClient> ResolveClientAsync(
        McpServer? server,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(
                _options.PipeNameOverride))
        {
            return _clientFactory.Create(
                _options.PipeNameOverride!);
        }

        try
        {
            GatewayProjectContext context =
                await ResolveProjectAsync(
                        server,
                        cancellationToken)
                    .ConfigureAwait(false);
            GatewayInstanceRecord? record =
                ReadLiveInstance();
            if (record is null)
            {
                return UnavailableTwinCatGatewayClient.Create(
                    ErrorCodes.GatewayNotRunning,
                    "TwinCAT Agent Gateway is not running. "
                    + "Call gateway_start once to start it.",
                    retryable: true,
                    stage: "gateway.connect");
            }

            GatewayOperationException? mismatch =
                GetIdentityMismatch(record, context);
            if (mismatch is not null)
            {
                return UnavailableTwinCatGatewayClient.Create(
                    mismatch);
            }

            return _clientFactory.Create(record.PipeName);
        }
        catch (GatewayOperationException exception)
        {
            return UnavailableTwinCatGatewayClient.Create(
                exception);
        }
        catch (Exception exception)
            when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidDataException
                || exception is JsonException)
        {
            return UnavailableTwinCatGatewayClient.Create(
                ErrorCodes.GatewayNotReady,
                "Gateway configuration or instance state "
                + "could not be read.",
                retryable: false,
                stage: "gateway.connect");
        }
    }

    public async Task<GatewayResponse<GatewayStartResult>>
        StartAsync(
            McpServer? server,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException
            .ThrowIfLessThanOrEqual(
                timeout,
                TimeSpan.Zero);

        GatewayProjectContext context;
        try
        {
            context = await ResolveProjectAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GatewayOperationException exception)
        {
            return Failure<GatewayStartResult>(exception);
        }
        catch (Exception exception)
            when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidDataException
                || exception is JsonException)
        {
            return Failure<GatewayStartResult>(
                ErrorCodes.GatewayStartFailed,
                "Gateway configuration could not be loaded: "
                + exception.Message,
                retryable: false,
                stage: "gateway.start.config");
        }

        if (!context.Configuration
                .AgentProcessControl.AllowStart)
        {
            return Failure<GatewayStartResult>(
                ErrorCodes.GatewayStartDisabled,
                "Project policy disables agent-started gateway "
                + "processes.",
                retryable: false,
                stage: "gateway.start.policy");
        }

        GatewayInstanceRecord? existing;
        try
        {
            existing = ReadLiveInstance();
        }
        catch (Exception exception)
            when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidDataException)
        {
            return Failure<GatewayStartResult>(
                ErrorCodes.GatewayStartFailed,
                "The current gateway instance record "
                + "could not be read: "
                + exception.Message,
                retryable: false,
                stage: "gateway.start.instance");
        }

        if (existing is not null)
        {
            GatewayOperationException? mismatch =
                GetIdentityMismatch(existing, context);
            if (mismatch is not null)
            {
                return Failure<GatewayStartResult>(
                    mismatch);
            }

            return await ReadStartResultAsync(
                    existing,
                    context,
                    started: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            _processLauncher.Launch(
                _options.GatewayCommand,
                context.Location.Path);
        }
        catch (Exception exception)
        {
            return Failure<GatewayStartResult>(
                ErrorCodes.GatewayStartFailed,
                "TwinCAT Agent Gateway could not be started: "
                + exception.Message,
                retryable: false,
                stage: "gateway.start.process");
        }

        DateTimeOffset deadline =
            DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayInstanceRecord? record;
            try
            {
                record = ReadLiveInstance();
            }
            catch (Exception exception)
                when (exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is InvalidDataException)
            {
                return Failure<GatewayStartResult>(
                    ErrorCodes.GatewayStartFailed,
                    "The started gateway did not publish "
                    + "a valid instance record: "
                    + exception.Message,
                    retryable: false,
                    stage: "gateway.start.instance");
            }

            if (record is not null)
            {
                GatewayOperationException? mismatch =
                    GetIdentityMismatch(record, context);
                if (mismatch is not null)
                {
                    return Failure<GatewayStartResult>(
                        mismatch);
                }

                GatewayResponse<GatewayStartResult> response =
                    await ReadStartResultAsync(
                            record,
                            context,
                            started: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (response.Ok)
                {
                    return response;
                }

                if (string.Equals(
                    response.Error?.Code,
                    ErrorCodes
                        .GatewayRunningDifferentProject,
                    StringComparison.Ordinal))
                {
                    return response;
                }
            }

            TimeSpan remaining =
                deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                    remaining < _pollInterval
                        ? remaining
                        : _pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Failure<GatewayStartResult>(
            ErrorCodes.GatewayStartTimeout,
            "TwinCAT Agent Gateway did not become ready "
            + "before the start timeout.",
            retryable: true,
            stage: "gateway.start.wait");
    }

    private async Task<GatewayProjectContext>
        ResolveProjectAsync(
            McpServer? server,
            CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string>? workspaceRoots = null;
        if (string.IsNullOrWhiteSpace(
                _options.ExplicitConfigurationPath))
        {
            workspaceRoots =
                await GetWorkspaceRootsAsync(
                        server,
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                _options.ExplicitConfigurationPath,
                workspaceRoots,
                _options.CurrentDirectory);
        GatewayConfiguration configuration =
            new GatewayConfigurationLoader().Load(
                location.Path);
        ConfigurationValidationResult validation =
            GatewayConfigurationValidator.Validate(
                configuration);
        if (!validation.IsValid)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "Gateway configuration is invalid: "
                + string.Join(
                    "; ",
                    validation.Issues.Select(
                        issue =>
                            issue.Path
                            + ": "
                            + issue.Message)),
                stage: "gateway.config.validate");
        }

        ProjectProfile profile =
            new ProjectProfileCatalog(configuration)
                .GetRequired(null);
        return new GatewayProjectContext(
            location,
            configuration,
            profile);
    }

    private static async Task<IReadOnlyCollection<string>>
        GetWorkspaceRootsAsync(
            McpServer? server,
            CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            ListRootsResult result =
                await server.RequestRootsAsync(
                        new ListRootsRequestParams(),
                        cancellationToken)
                    .ConfigureAwait(false);
            return result.Roots
                .Select(root => ToLocalDirectory(root.Uri))
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray();
        }
        catch (McpException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
        catch (NotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    private GatewayInstanceRecord? ReadLiveInstance()
    {
        GatewayInstanceRecord? record = _registry.Read();
        return record is not null
            && IsLive(record)
                ? record
                : null;
    }

    private static bool IsLive(
        GatewayInstanceRecord record)
    {
        try
        {
            using Process process =
                Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            DateTimeOffset processStartedAt =
                process.StartTime.ToUniversalTime();
            return Math.Abs(
                    (processStartedAt
                        - record.ProcessStartedAtUtc)
                    .TotalSeconds)
                < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static GatewayOperationException?
        GetIdentityMismatch(
            GatewayInstanceRecord record,
            GatewayProjectContext context)
    {
        if (!PathsEqual(
                record.ConfigurationPath,
                context.Location.Path)
            || !PathsEqual(
                record.SolutionPath,
                context.Profile.Solution))
        {
            return new GatewayOperationException(
                ErrorCodes
                    .GatewayRunningDifferentProject,
                "A TwinCAT Agent Gateway instance is already "
                + "running for another project. It was not "
                + "closed or switched.",
                stage: "gateway.start.identity");
        }

        return null;
    }

    private async Task<GatewayResponse<GatewayStartResult>>
        ReadStartResultAsync(
            GatewayInstanceRecord record,
            GatewayProjectContext context,
            bool started,
            CancellationToken cancellationToken)
    {
        try
        {
            ITwinCatGatewayClient client =
                _clientFactory.Create(record.PipeName);
            GatewayResponse<GatewayStatusResult> response =
                await client.GetStatusAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!response.Ok
                || response.Result is null)
            {
                return Failure<GatewayStartResult>(
                    response.Error
                    ?? new GatewayError
                    {
                        Code =
                            ErrorCodes.GatewayNotReady,
                        Message =
                            "Gateway status is unavailable.",
                        Retryable = true,
                        Stage =
                            "gateway.start.status",
                    });
            }

            GatewayStatusResult status = response.Result;
            if (!PathsEqual(
                    status.Gateway.ConfigurationPath,
                    context.Location.Path)
                || !PathsEqual(
                    status.Gateway.SolutionPath,
                    context.Profile.Solution))
            {
                return Failure<GatewayStartResult>(
                    ErrorCodes
                        .GatewayRunningDifferentProject,
                    "The running gateway reported another "
                    + "configuration or solution.",
                    retryable: false,
                    stage: "gateway.start.identity");
            }

            if (!status.Gateway.Ready)
            {
                return Failure<GatewayStartResult>(
                    ErrorCodes.GatewayNotReady,
                    "TwinCAT Agent Gateway is running but "
                    + "is not ready.",
                    retryable: true,
                    stage: "gateway.start.status");
            }

            return new GatewayResponse<GatewayStartResult>
            {
                Ok = true,
                Result = new GatewayStartResult
                {
                    Started = started,
                    AlreadyRunning = !started,
                    ProcessId = record.ProcessId,
                    Status = status,
                },
            };
        }
        catch (Exception exception)
            when (exception is IOException
                || exception is TimeoutException
                || exception is UnauthorizedAccessException
                || (exception is OperationCanceledException
                    && !cancellationToken
                        .IsCancellationRequested))
        {
            return Failure<GatewayStartResult>(
                ErrorCodes.GatewayNotReady,
                "TwinCAT Agent Gateway is running but IPC "
                + "is not ready: "
                + exception.Message,
                retryable: true,
                stage: "gateway.start.status");
        }
    }

    private static string? ToLocalDirectory(string uriText)
    {
        return Uri.TryCreate(
                uriText,
                UriKind.Absolute,
                out Uri? uri)
            && uri.IsFile
                ? Path.GetFullPath(uri.LocalPath)
                : null;
    }

    private static bool PathsEqual(
        string? left,
        string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(
                Path.GetFullPath(left!),
                Path.GetFullPath(right!),
                StringComparison.OrdinalIgnoreCase);
    }

    private static GatewayResponse<T> Failure<T>(
        GatewayOperationException exception)
    {
        return Failure<T>(
            exception.Code,
            exception.Message,
            exception.Retryable,
            exception.Stage);
    }

    private static GatewayResponse<T> Failure<T>(
        GatewayError error)
    {
        return new GatewayResponse<T>
        {
            Ok = false,
            Error = error,
        };
    }

    private static GatewayResponse<T> Failure<T>(
        string code,
        string message,
        bool retryable,
        string? stage)
    {
        return Failure<T>(
            new GatewayError
            {
                Code = code,
                Message = message,
                Retryable = retryable,
                Stage = stage,
            });
    }

    private sealed class GatewayProjectContext
    {
        public GatewayProjectContext(
            GatewayConfigurationLocation location,
            GatewayConfiguration configuration,
            ProjectProfile profile)
        {
            Location = location;
            Configuration = configuration;
            Profile = profile;
        }

        public GatewayConfigurationLocation Location
        {
            get;
        }

        public GatewayConfiguration Configuration { get; }

        public ProjectProfile Profile { get; }
    }
}

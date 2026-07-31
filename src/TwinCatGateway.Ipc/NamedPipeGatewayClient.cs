using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public sealed class NamedPipeGatewayClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly JsonSerializerOptions _serializerOptions;

    public NamedPipeGatewayClient(
        string pipeName,
        TimeSpan? connectTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        if (_connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        _serializerOptions = GatewayJson.CreateSerializerOptions();
    }

    public async Task<TResult> SendAsync<TParameters, TResult>(
        string method,
        TParameters parameters,
        bool wait,
        CancellationToken cancellationToken)
    {
        GatewayRequest<TParameters> request = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Method = method,
            Params = parameters,
            Wait = wait,
        };
        string json = JsonSerializer.Serialize(request, _serializerOptions);

        using NamedPipeClientStream pipe = new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource timeout =
            new(_connectTimeout);
        using CancellationTokenSource connection =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

        try
        {
#if NET8_0_OR_GREATER
            await pipe.ConnectAsync(connection.Token).ConfigureAwait(false);
#else
            await pipe.ConnectAsync(
                (int)_connectTimeout.TotalMilliseconds,
                connection.Token).ConfigureAwait(false);
#endif
            await IpcFrameProtocol.WriteAsync(
                pipe,
                json,
                cancellationToken).ConfigureAwait(false);
            string responseJson = await IpcFrameProtocol.ReadAsync(
                pipe,
                cancellationToken).ConfigureAwait(false);
            ProtocolResponse<TResult> response =
                JsonSerializer.Deserialize<ProtocolResponse<TResult>>(
                    responseJson,
                    _serializerOptions)
                ?? throw CreateProtocolFailure(
                    "Gateway returned an empty response.");
            if (response.ProtocolVersion != ProtocolVersion.Current
                || !string.Equals(
                    response.RequestId,
                    request.RequestId,
                    StringComparison.Ordinal))
            {
                throw CreateProtocolFailure(
                    "Gateway response identity does not match the request.");
            }

            if (!response.Ok)
            {
                throw new GatewayClientException(
                    GatewayClientFailureKind.Gateway,
                    response.Error ?? new GatewayError
                    {
                        Code = ErrorCodes.OperationFailed,
                        Message = "Gateway returned a failure without an error.",
                        Component = GatewayComponent.Gateway,
                    });
            }

            return response.Result
                ?? throw CreateProtocolFailure(
                    "Gateway returned a successful response without a result.");
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw CreateTransportFailure(
                ErrorCodes.GatewayNotRunning,
                "Gateway IPC connection timed out.",
                exception,
                retryable: true);
        }
        catch (JsonException exception)
        {
            throw CreateProtocolFailure(
                "Gateway returned invalid IPC JSON.",
                exception);
        }
        catch (IOException exception)
        {
            throw CreateTransportFailure(
                ErrorCodes.GatewayNotRunning,
                "Gateway IPC transport is unavailable.",
                exception,
                retryable: true);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CreateTransportFailure(
                ErrorCodes.OperationFailed,
                "Gateway IPC transport access was denied.",
                exception,
                retryable: false);
        }
    }

    private static GatewayClientException CreateProtocolFailure(
        string message,
        Exception? innerException = null)
    {
        return new GatewayClientException(
            GatewayClientFailureKind.Protocol,
            new GatewayError
            {
                Code = ErrorCodes.IpcVersionMismatch,
                Message = message,
                Component = GatewayComponent.Gateway,
                Retryable = false,
            },
            innerException);
    }

    private static GatewayClientException CreateTransportFailure(
        string code,
        string message,
        Exception innerException,
        bool retryable)
    {
        return new GatewayClientException(
            GatewayClientFailureKind.Transport,
            new GatewayError
            {
                Code = code,
                Message = message,
                Component = GatewayComponent.Gateway,
                Retryable = retryable,
            },
            innerException);
    }

    private sealed class ProtocolResponse<TResult>
    {
        public int ProtocolVersion { get; set; }

        public string RequestId { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public TResult? Result { get; set; }

        public GatewayError? Error { get; set; }
    }
}

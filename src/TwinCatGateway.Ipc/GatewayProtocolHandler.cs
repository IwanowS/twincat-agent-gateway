using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public sealed class GatewayProtocolHandler
{
    private readonly Func<
        GatewayRequestContext,
        CancellationToken,
        Task<GatewayDispatchResult>> _dispatchAsync;
    private readonly Action<string, Exception>? _exceptionSink;
    private readonly JsonSerializerOptions _serializerOptions;

    public GatewayProtocolHandler(
        Func<
            GatewayRequestContext,
            CancellationToken,
            Task<GatewayDispatchResult>> dispatchAsync,
        Action<string, Exception>? exceptionSink = null)
    {
        _dispatchAsync = dispatchAsync
            ?? throw new ArgumentNullException(nameof(dispatchAsync));
        _exceptionSink = exceptionSink;
        _serializerOptions = GatewayJson.CreateSerializerOptions();
    }

    public async Task<string> HandleAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        GatewayProtocolResponse response =
            await HandleForTransportAsync(
                requestJson,
                cancellationToken).ConfigureAwait(false);
        return response.Json;
    }

    internal async Task<GatewayProtocolResponse>
        HandleForTransportAsync(
            string requestJson,
            CancellationToken cancellationToken)
    {
        string requestId = string.Empty;
        try
        {
            GatewayRequestContext request = ParseRequest(requestJson);
            requestId = request.RequestId;
            if (request.ProtocolVersion != ProtocolVersion.Current)
            {
                return new GatewayProtocolResponse(
                    SerializeError(
                        requestId,
                        ErrorCodes.IpcVersionMismatch,
                        $"Protocol version {request.ProtocolVersion} is not supported."),
                    afterResponseWritten: null);
            }

            GatewayDispatchResult dispatch =
                await _dispatchAsync(request, cancellationToken).ConfigureAwait(false);
            ProtocolResponse response = new()
            {
                ProtocolVersion = ProtocolVersion.Current,
                RequestId = request.RequestId,
                Ok = dispatch.Ok,
                Result = dispatch.Result,
                Error = dispatch.Error,
            };
            return new GatewayProtocolResponse(
                JsonSerializer.Serialize(
                    response,
                    _serializerOptions),
                dispatch.AfterResponseWritten);
        }
        catch (JsonException exception)
        {
            RecordException(requestId, exception);
            return new GatewayProtocolResponse(
                SerializeError(
                    requestId,
                    ErrorCodes.RequestInvalid,
                    "The IPC request is not valid JSON or has invalid fields."),
                afterResponseWritten: null);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            RecordException(requestId, exception);
            return new GatewayProtocolResponse(
                SerializeError(
                    requestId,
                    ErrorCodes.OperationFailed,
                    "The gateway request failed unexpectedly. See the local log for details."),
                afterResponseWritten: null);
        }
    }

    private void RecordException(string requestId, Exception exception)
    {
        try
        {
            _exceptionSink?.Invoke(requestId, exception);
        }
        catch (Exception sinkException)
        {
            Trace.TraceError(
                "IPC exception sink failed while recording '{0}': {1}{2}Original: {3}",
                requestId,
                sinkException,
                Environment.NewLine,
                exception);
        }
    }

    private static GatewayRequestContext ParseRequest(string requestJson)
    {
        using JsonDocument document = JsonDocument.Parse(requestJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("IPC request root must be an object.");
        }

        int protocolVersion = GetRequiredInt32(root, "protocolVersion");
        string requestId = GetRequiredString(root, "requestId");
        string method = GetRequiredString(root, "method");
        bool wait = !root.TryGetProperty("wait", out JsonElement waitElement)
            || waitElement.ValueKind == JsonValueKind.True
            || (waitElement.ValueKind == JsonValueKind.False
                ? false
                : throw new JsonException("Property 'wait' must be a boolean."));
        JsonElement? parameters = root.TryGetProperty(
            "params",
            out JsonElement paramsElement)
            ? paramsElement.Clone()
            : null;
        return new GatewayRequestContext(
            protocolVersion,
            requestId,
            method,
            parameters,
            wait);
    }

    private string SerializeError(
        string requestId,
        string code,
        string message)
    {
        ProtocolResponse response = new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            RequestId = requestId,
            Ok = false,
            Error = new GatewayError
            {
                Code = code,
                Message = message,
            },
        };
        return JsonSerializer.Serialize(response, _serializerOptions);
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || !element.TryGetInt32(out int value))
        {
            throw new JsonException(
                $"Property '{propertyName}' must be a 32-bit integer.");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"Property '{propertyName}' must be a string.");
        }

        string? value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"Property '{propertyName}' is required.");
        }

        return value!;
    }

    private sealed class ProtocolResponse
    {
        public int ProtocolVersion { get; set; }

        public string RequestId { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public object? Result { get; set; }

        public GatewayError? Error { get; set; }
    }

    internal sealed class GatewayProtocolResponse
    {
        private Action? _afterResponseWritten;

        public GatewayProtocolResponse(
            string json,
            Action? afterResponseWritten)
        {
            Json = json;
            _afterResponseWritten = afterResponseWritten;
        }

        public string Json { get; }

        public void NotifyResponseWritten()
        {
            Interlocked.Exchange(
                ref _afterResponseWritten,
                null)?.Invoke();
        }
    }
}

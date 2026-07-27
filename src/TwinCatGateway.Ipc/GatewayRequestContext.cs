using System.Text.Json;

namespace TwinCatGateway.Ipc;

public sealed class GatewayRequestContext
{
    internal GatewayRequestContext(
        int protocolVersion,
        string requestId,
        string method,
        JsonElement? parameters,
        bool wait)
    {
        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        Method = method;
        Params = parameters;
        Wait = wait;
    }

    public int ProtocolVersion { get; }

    public string RequestId { get; }

    public string Method { get; }

    public JsonElement? Params { get; }

    public bool Wait { get; }

    public TParameters DeserializeParameters<TParameters>(
        JsonSerializerOptions serializerOptions)
    {
        if (!Params.HasValue)
        {
            return System.Activator.CreateInstance<TParameters>();
        }

        return Params.Value.Deserialize<TParameters>(serializerOptions)
            ?? throw new JsonException(
                $"Parameters for method '{Method}' cannot be null.");
    }
}

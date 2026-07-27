using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayConfigurationLoader
{
    private readonly JsonSerializerOptions _serializerOptions;

    public GatewayConfigurationLoader()
    {
        _serializerOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        _serializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public GatewayConfiguration Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Configuration path is required.",
                nameof(path));
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GatewayConfiguration>(
                json,
                _serializerOptions)
            ?? throw new InvalidDataException(
                "Gateway configuration contains no JSON object.");
    }
}

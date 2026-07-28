using System;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Mcp;

internal static class McpGatewayJson
{
    private static readonly JsonSerializerOptions JsonOptions =
        GatewayJson.CreateSerializerOptions();

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static TEnum ParseEnum<TEnum>(
        string value,
        string parameterName)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: true, out TEnum result)
            && Enum.IsDefined(result))
        {
            return result;
        }

        throw new McpException(
            $"Invalid {parameterName} '{value}'.");
    }

    public static bool? ParseOptionalBoolean(
        string value,
        string parameterName)
    {
        if (string.Equals(
            value,
            "auto",
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(value, out bool result))
        {
            return result;
        }

        throw new McpException(
            $"Invalid {parameterName} '{value}'; "
            + "expected auto, true, or false.");
    }

    public static void RequirePositive(
        int value,
        string parameterName)
    {
        if (value <= 0)
        {
            throw new McpException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} must be positive.",
                    parameterName));
        }
    }

    public static void RequireNonNegative(
        long value,
        string parameterName)
    {
        if (value < 0)
        {
            throw new McpException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} must be non-negative.",
                    parameterName));
        }
    }

    public static DiagnosticSeverity? ParseSeverity(
        string value)
    {
        return string.Equals(
            value,
            "all",
            StringComparison.OrdinalIgnoreCase)
                ? null
                : ParseEnum<DiagnosticSeverity>(
                    value,
                    "minimumSeverity");
    }
}

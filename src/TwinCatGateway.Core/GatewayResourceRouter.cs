using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public enum GatewayResourceRouteKind
{
    GatewayState,
    GatewayDiagnostics,
    ProfileCapabilities,
    ProfileSources,
    ProfileSourceFiles,
    XaeState,
    XaeDiagnostics,
    XaeMessages,
    TargetState,
    TargetDiagnostics,
    PlcState,
    PlcDiagnostics,
    OperationSummary,
    OperationEvents,
    OperationArtifact,
    CurrentGatewayLog,
}

public sealed class GatewayResourceRoute
{
    public GatewayResourceRouteKind Kind { get; set; }

    public string CanonicalUri { get; set; } = string.Empty;

    public string? Profile { get; set; }

    public string? RuntimeId { get; set; }

    public string? OperationId { get; set; }

    public OperationArtifactKind? Artifact { get; set; }
}

public static class GatewayResourceRouter
{
    private static readonly char[] ForbiddenUriCharacters = { '?', '#', '\\' };
    private static readonly char[] ForbiddenIdentityCharacters = { '/', '\\', '\0' };

    public static GatewayResourceRoute Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(ForbiddenUriCharacters) >= 0)
        {
            throw InvalidUri();
        }

        int separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw InvalidUri();
        }

        string scheme = value.Substring(0, separator);
        if (!IsCanonicalToken(scheme))
        {
            throw InvalidUri();
        }

        string remainder = value.Substring(separator + 3);
        string[] rawSegments = remainder.Split('/');
        if (rawSegments.Length == 0
            || rawSegments.Any(segment => segment.Length == 0))
        {
            throw InvalidUri();
        }

        string[] segments = rawSegments.Select(DecodeCanonical).ToArray();
        GatewayResourceRoute route = scheme switch
        {
            "twincat-gateway" => ParseGateway(segments),
            "twincat-profile" => ParseProfile(segments),
            "twincat-xae" => ParseXae(segments),
            "twincat-target" => ParseTarget(segments),
            "twincat-plc" => ParsePlc(segments),
            "twincat-operation" => ParseOperation(segments),
            "twincat-log" => ParseLog(segments),
            _ => throw InvalidUri(),
        };
        route.CanonicalUri = CreateCanonicalUri(scheme, segments);
        if (!string.Equals(route.CanonicalUri, value, StringComparison.Ordinal))
        {
            throw InvalidUri();
        }

        return route;
    }

    private static GatewayResourceRoute ParseGateway(string[] segments)
    {
        RequireCount(segments, 1);
        return segments[0] switch
        {
            "state" => new GatewayResourceRoute
            {
                Kind = GatewayResourceRouteKind.GatewayState,
            },
            "diagnostics" => new GatewayResourceRoute
            {
                Kind = GatewayResourceRouteKind.GatewayDiagnostics,
            },
            _ => throw InvalidUri(),
        };
    }

    private static GatewayResourceRoute ParseProfile(string[] segments)
    {
        if (segments.Length != 2 && segments.Length != 3)
        {
            throw InvalidUri();
        }

        string profile = RequireIdentity(segments[0]);
        if (segments.Length == 2 && segments[1] == "capabilities")
        {
            return ProfileRoute(GatewayResourceRouteKind.ProfileCapabilities, profile);
        }

        if (segments[1] != "sources")
        {
            throw InvalidUri();
        }

        return ProfileRoute(
            segments.Length == 2
                ? GatewayResourceRouteKind.ProfileSources
                : segments[2] == "files"
                    ? GatewayResourceRouteKind.ProfileSourceFiles
                    : throw InvalidUri(),
            profile);
    }

    private static GatewayResourceRoute ParseXae(string[] segments)
    {
        if (segments.Length != 3 && segments.Length != 4)
        {
            throw InvalidUri();
        }
        if (segments[0] != "profile")
        {
            throw InvalidUri();
        }

        string profile = RequireIdentity(segments[1]);
        GatewayResourceRouteKind kind = segments.Length == 4
            && segments[2] == "messages"
            && segments[3] == "current"
                ? GatewayResourceRouteKind.XaeMessages
                : segments[2] switch
        {
            "state" => GatewayResourceRouteKind.XaeState,
            "diagnostics" => GatewayResourceRouteKind.XaeDiagnostics,
            _ => throw InvalidUri(),
        };
        return ProfileRoute(kind, profile);
    }

    private static GatewayResourceRoute ParseTarget(string[] segments)
    {
        RequireCount(segments, 3);
        if (segments[0] != "profile")
        {
            throw InvalidUri();
        }

        GatewayResourceRouteKind kind = segments[2] switch
        {
            "state" => GatewayResourceRouteKind.TargetState,
            "diagnostics" => GatewayResourceRouteKind.TargetDiagnostics,
            _ => throw InvalidUri(),
        };
        return ProfileRoute(kind, RequireIdentity(segments[1]));
    }

    private static GatewayResourceRoute ParsePlc(string[] segments)
    {
        RequireCount(segments, 4);
        if (segments[0] != "profile")
        {
            throw InvalidUri();
        }

        return new GatewayResourceRoute
        {
            Kind = segments[3] switch
            {
                "state" => GatewayResourceRouteKind.PlcState,
                "diagnostics" => GatewayResourceRouteKind.PlcDiagnostics,
                _ => throw InvalidUri(),
            },
            Profile = RequireIdentity(segments[1]),
            RuntimeId = RequireIdentity(segments[2]),
        };
    }

    private static GatewayResourceRoute ParseOperation(string[] segments)
    {
        string operationId = RequireOperationId(segments[0]);
        if (segments.Length == 1)
        {
            return OperationRoute(GatewayResourceRouteKind.OperationSummary, operationId);
        }

        if (segments.Length == 2 && segments[1] == "events")
        {
            return OperationRoute(GatewayResourceRouteKind.OperationEvents, operationId);
        }

        OperationArtifactKind artifact;
        if (segments.Length == 2 && segments[1] == "build")
        {
            artifact = OperationArtifactKind.Build;
        }
        else if (segments.Length == 2 && segments[1] == "xae-messages")
        {
            artifact = OperationArtifactKind.XaeMessages;
        }
        else if (segments.Length == 2 && segments[1] == "project-noise")
        {
            artifact = OperationArtifactKind.ProjectNoise;
        }
        else if (segments.Length == 3
            && segments[1] == "test"
            && segments[2] == "xunit")
        {
            artifact = OperationArtifactKind.TestXunit;
        }
        else
        {
            throw InvalidUri();
        }
        GatewayResourceRoute route = OperationRoute(
            GatewayResourceRouteKind.OperationArtifact,
            operationId);
        route.Artifact = artifact;
        return route;
    }

    private static GatewayResourceRoute ParseLog(string[] segments)
    {
        RequireCount(segments, 2);
        if (segments[0] != "gateway" || segments[1] != "current")
        {
            throw InvalidUri();
        }

        return new GatewayResourceRoute
        {
            Kind = GatewayResourceRouteKind.CurrentGatewayLog,
        };
    }

    private static GatewayResourceRoute ProfileRoute(
        GatewayResourceRouteKind kind,
        string profile) =>
        new()
        {
            Kind = kind,
            Profile = profile,
        };

    private static GatewayResourceRoute OperationRoute(
        GatewayResourceRouteKind kind,
        string operationId) =>
        new()
        {
            Kind = kind,
            OperationId = operationId,
        };

    private static string DecodeCanonical(string raw)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException exception)
        {
            throw InvalidUri(exception);
        }

        if (decoded.Length == 0
            || decoded is "." or ".."
            || decoded.IndexOfAny(ForbiddenIdentityCharacters) >= 0
            || !string.Equals(Uri.EscapeDataString(decoded), raw, StringComparison.Ordinal))
        {
            throw InvalidUri();
        }

        return decoded;
    }

    private static string RequireIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw InvalidUri();
        }

        return value;
    }

    private static string RequireOperationId(string value)
    {
        RequireIdentity(value);
        if (value.Any(character =>
            !(character >= 'a' && character <= 'z'
                || character >= 'A' && character <= 'Z'
                || character >= '0' && character <= '9'
                || character is '-' or '_')))
        {
            throw InvalidUri();
        }

        return value;
    }

    private static bool IsCanonicalToken(string value) =>
        value.All(character =>
            character >= 'a' && character <= 'z' || character == '-');

    private static string CreateCanonicalUri(
        string scheme,
        IEnumerable<string> segments) =>
        scheme + "://" + string.Join("/", segments.Select(Uri.EscapeDataString));

    private static void RequireCount(IReadOnlyCollection<string> segments, int count)
    {
        if (segments.Count != count)
        {
            throw InvalidUri();
        }
    }

    private static ArgumentException InvalidUri(Exception? inner = null) =>
        new("Resource URI is not a canonical Gateway resource.", inner);
}

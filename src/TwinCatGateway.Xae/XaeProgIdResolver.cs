using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public static class XaeProgIdResolver
{
    private static readonly string[] PreferredProgIds =
    {
        "TcXaeShell.DTE.15.0",
        "VisualStudio.DTE.16.0",
    };

    public static IReadOnlyList<XaeLaunchCandidate> ResolveCandidates(
        string? configuredProgId)
    {
        string[] progIds = string.IsNullOrWhiteSpace(configuredProgId)
            ? PreferredProgIds
            : new[] { configuredProgId! };
        XaeLaunchCandidate[] candidates = progIds
            .Select(TryResolve)
            .Where(candidate => candidate is not null)
            .Cast<XaeLaunchCandidate>()
            .ToArray();
        if (candidates.Length != 0)
        {
            return candidates;
        }

        string message = string.IsNullOrWhiteSpace(configuredProgId)
            ? "No supported TwinCAT XAE ProgID is registered as a launchable local server."
            : $"TwinCAT XAE ProgID '{configuredProgId}' is not registered as a launchable local server.";
        throw new GatewayOperationException(
            ErrorCodes.XaeProgIdNotRegistered,
            message,
            stage: "xae.resolveProgId");
    }

    private static XaeLaunchCandidate? TryResolve(string progId)
    {
        Type? automationType = Type.GetTypeFromProgID(
            progId,
            throwOnError: false);
        if (automationType is null)
        {
            return null;
        }

        string registryPath =
            $@"CLSID\{automationType.GUID:B}\LocalServer32";
        using RegistryKey? key =
            Registry.ClassesRoot.OpenSubKey(registryPath);
        string? command = key?.GetValue(null) as string;
        string? executablePath = ParseExecutablePath(command);
        return executablePath is not null && File.Exists(executablePath)
            ? new XaeLaunchCandidate(progId, executablePath)
            : null;
    }

    private static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string value = command!.Trim();
        if (value[0] == '"')
        {
            int closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1
                ? value.Substring(1, closingQuote - 1)
                : null;
        }

        int executableEnd = value.IndexOf(
            ".exe",
            StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0
            ? value.Substring(0, executableEnd + 4).Trim()
            : value;
    }
}

public sealed class XaeLaunchCandidate
{
    internal XaeLaunchCandidate(
        string progId,
        string executablePath)
    {
        ProgId = progId;
        ExecutablePath = executablePath;
    }

    public string ProgId { get; }

    public string ExecutablePath { get; }
}

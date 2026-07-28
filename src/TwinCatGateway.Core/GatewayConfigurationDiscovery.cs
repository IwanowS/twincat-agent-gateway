using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public enum GatewayConfigurationSource
{
    Explicit,
    WorkspaceRoot,
    CurrentDirectory,
}

public sealed class GatewayConfigurationLocation
{
    public GatewayConfigurationLocation(
        string path,
        GatewayConfigurationSource source)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Source = source;
    }

    public string Path { get; }

    public GatewayConfigurationSource Source { get; }
}

public static class GatewayConfigurationDiscovery
{
    public const string FileName = "twincat-gateway.json";

    public static GatewayConfigurationLocation Discover(
        string? explicitPath,
        IEnumerable<string>? workspaceRoots,
        string currentDirectory)
    {
        string normalizedCurrentDirectory =
            NormalizeDirectory(currentDirectory);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string path = NormalizeExplicitPath(
                explicitPath!,
                normalizedCurrentDirectory);
            if (!File.Exists(path))
            {
                throw NotFound(
                    $"Gateway configuration '{path}' was not found.");
            }

            return new GatewayConfigurationLocation(
                path,
                GatewayConfigurationSource.Explicit);
        }

        string[] roots = (workspaceRoots
                ?? Enumerable.Empty<string>())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] workspaceMatches = roots
            .Select(SearchNearest)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (workspaceMatches.Length > 1)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayConfigAmbiguous,
                "MCP workspace roots resolve to different "
                + "TwinCAT gateway configurations: "
                + string.Join(", ", workspaceMatches),
                stage: "gateway.config.discover");
        }

        if (workspaceMatches.Length == 1)
        {
            return new GatewayConfigurationLocation(
                workspaceMatches[0],
                GatewayConfigurationSource.WorkspaceRoot);
        }

        string? currentMatch =
            SearchNearest(normalizedCurrentDirectory);
        if (currentMatch is not null)
        {
            return new GatewayConfigurationLocation(
                currentMatch,
                GatewayConfigurationSource.CurrentDirectory);
        }

        throw NotFound(
            $"No '{FileName}' was found from "
            + $"'{normalizedCurrentDirectory}'.");
    }

    private static string? SearchNearest(string startDirectory)
    {
        DirectoryInfo? directory =
            new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                FileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            string gitMarker = Path.Combine(
                directory.FullName,
                ".git");
            if (Directory.Exists(gitMarker)
                || File.Exists(gitMarker))
            {
                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string NormalizeExplicitPath(
        string path,
        string currentDirectory)
    {
        string combined = Path.IsPathRooted(path)
            ? path
            : Path.Combine(currentDirectory, path);
        return Path.GetFullPath(combined);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "A search directory is required.",
                nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static GatewayOperationException NotFound(
        string message)
    {
        return new GatewayOperationException(
            ErrorCodes.GatewayConfigNotFound,
            message,
            stage: "gateway.config.discover");
    }
}

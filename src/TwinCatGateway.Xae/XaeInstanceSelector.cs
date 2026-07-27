using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public static class XaeInstanceSelector
{
    public static int Select(
        IReadOnlyList<DteInstanceInfo> instances,
        string solutionPath)
    {
        if (instances is null)
        {
            throw new ArgumentNullException(nameof(instances));
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException(
                "Solution path is required.",
                nameof(solutionPath));
        }

        string normalizedSolution = Path.GetFullPath(solutionPath);
        int[] matches = instances
            .Select((instance, index) => new { instance, index })
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.instance.Solution)
                && string.Equals(
                    Path.GetFullPath(item.instance.Solution),
                    normalizedSolution,
                    StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                $"No running XAE instance has solution '{normalizedSolution}' open.");
        }

        if (matches.Length > 1)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeMultipleMatches,
                $"Multiple XAE instances have solution '{normalizedSolution}' open.");
        }

        return matches[0];
    }
}

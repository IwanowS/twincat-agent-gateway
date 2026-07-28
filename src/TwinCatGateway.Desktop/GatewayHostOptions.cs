using System;
using System.Collections.Generic;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

public sealed class GatewayHostOptions
{
    public string? ConfigurationPath { get; set; }

    public GatewayLaunchSource LaunchSource { get; set; } =
        GatewayLaunchSource.Manual;

    public GatewayUiMode? UiModeOverride { get; set; }

    public static GatewayHostOptions FromArguments(
        IReadOnlyList<string> arguments,
        string? currentDirectory = null)
    {
        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        string? configuredPath = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(
                arguments[index],
                "--config",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count
                || string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new ArgumentException(
                    "The --config option requires a file path.",
                    nameof(arguments));
            }

            configuredPath = arguments[++index];
        }

        GatewayConfigurationLocation location =
            GatewayConfigurationDiscovery.Discover(
                configuredPath,
                workspaceRoots: null,
                currentDirectory
                    ?? Environment.CurrentDirectory);
        return new GatewayHostOptions
        {
            ConfigurationPath = location.Path,
        };
    }
}

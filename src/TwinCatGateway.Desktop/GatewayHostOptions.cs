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
        GatewayLaunchSource launchSource =
            GatewayLaunchSource.Manual;
        GatewayUiMode? uiModeOverride = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(
                argument,
                "--config",
                StringComparison.OrdinalIgnoreCase))
            {
                configuredPath = ReadValue(
                    arguments,
                    ref index,
                    "--config");
                continue;
            }

            if (string.Equals(
                argument,
                "--launch-source",
                StringComparison.OrdinalIgnoreCase))
            {
                launchSource = ParseEnum<GatewayLaunchSource>(
                    ReadValue(
                        arguments,
                        ref index,
                        "--launch-source"),
                    "--launch-source");
                continue;
            }

            if (string.Equals(
                argument,
                "--ui-mode",
                StringComparison.OrdinalIgnoreCase))
            {
                uiModeOverride = ParseEnum<GatewayUiMode>(
                    ReadValue(
                        arguments,
                        ref index,
                        "--ui-mode"),
                    "--ui-mode");
                continue;
            }

            throw InvalidArgument(
                $"Unknown gateway argument '{argument}'.");
        }

        GatewayConfigurationLocation? location;
        try
        {
            location = GatewayConfigurationDiscovery.Discover(
                configuredPath,
                workspaceRoots: null,
                currentDirectory
                    ?? Environment.CurrentDirectory);
        }
        catch (GatewayOperationException exception)
            when (launchSource
                    == GatewayLaunchSource.Manual
                && string.IsNullOrWhiteSpace(configuredPath)
                && exception.Code
                    == ErrorCodes.GatewayConfigNotFound)
        {
            location = null;
        }

        return new GatewayHostOptions
        {
            ConfigurationPath = location?.Path,
            LaunchSource = launchSource,
            UiModeOverride = uiModeOverride,
        };
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count
            || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw InvalidArgument(
                $"{option} requires a value.");
        }

        return arguments[++index];
    }

    private static T ParseEnum<T>(
        string value,
        string option)
        where T : struct
    {
        if (Enum.TryParse(
                value,
                ignoreCase: true,
                out T result)
            && Enum.IsDefined(typeof(T), result))
        {
            return result;
        }

        throw InvalidArgument(
            $"{option} has unsupported value '{value}'.");
    }

    private static GatewayOperationException InvalidArgument(
        string message)
    {
        return new GatewayOperationException(
            ErrorCodes.RequestInvalid,
            message,
            stage: "gateway.arguments");
    }
}

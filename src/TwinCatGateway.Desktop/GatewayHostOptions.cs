using System;
using System.Collections.Generic;
using System.IO;

namespace TwinCatGateway.Desktop;

public sealed class GatewayHostOptions
{
    public string? ConfigurationPath { get; set; }

    public static GatewayHostOptions FromArguments(
        IReadOnlyList<string> arguments)
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

        configuredPath ??= Environment.GetEnvironmentVariable(
            "TWINCAT_GATEWAY_CONFIG");
        configuredPath ??= FindDefaultConfiguration();
        return new GatewayHostOptions
        {
            ConfigurationPath = configuredPath,
        };
    }

    private static string? FindDefaultConfiguration()
    {
        string currentDirectory = Path.Combine(
            Environment.CurrentDirectory,
            "appsettings.Local.json");
        if (File.Exists(currentDirectory))
        {
            return currentDirectory;
        }

        string applicationDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "appsettings.Local.json");
        return File.Exists(applicationDirectory)
            ? applicationDirectory
            : null;
    }
}

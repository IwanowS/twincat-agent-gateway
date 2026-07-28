using System;
using System.IO;

namespace TwinCatGateway.Desktop;

public static class SetupInstructionsProvider
{
    public const string FileName =
        "SETUP_INSTRUCTIONS.txt";
    public const string ConfigurationFileName =
        "CONFIGURATION.md";

    public static string Read(
        string? applicationDirectory = null)
    {
        string directory =
            string.IsNullOrWhiteSpace(applicationDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetFullPath(applicationDirectory!);
        string setupPath = Path.Combine(
            directory,
            FileName);
        if (!File.Exists(setupPath))
        {
            throw new FileNotFoundException(
                "Canonical gateway setup instructions "
                + "were not installed.",
                setupPath);
        }

        string configurationPath = Path.Combine(
            directory,
            ConfigurationFileName);
        if (!File.Exists(configurationPath))
        {
            throw new FileNotFoundException(
                "Gateway configuration reference "
                + "was not installed.",
                configurationPath);
        }

        return File.ReadAllText(setupPath).TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + File.ReadAllText(configurationPath);
    }

    public static bool TryRead(
        out string instructions,
        out string? error,
        string? applicationDirectory = null)
    {
        try
        {
            instructions = Read(applicationDirectory);
            error = null;
            return true;
        }
        catch (Exception exception)
            when (exception is IOException
                || exception is UnauthorizedAccessException)
        {
            instructions = string.Empty;
            error = exception.Message;
            return false;
        }
    }
}

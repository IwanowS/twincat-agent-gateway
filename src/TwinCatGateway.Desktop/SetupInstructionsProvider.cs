using System;
using System.IO;

namespace TwinCatGateway.Desktop;

public static class SetupInstructionsProvider
{
    public const string FileName =
        "SETUP_INSTRUCTIONS.txt";

    public static string Read(
        string? applicationDirectory = null)
    {
        string directory =
            string.IsNullOrWhiteSpace(applicationDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetFullPath(applicationDirectory!);
        string path = Path.Combine(
            directory,
            FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Canonical gateway setup instructions "
                + "were not installed.",
                path);
        }

        return File.ReadAllText(path);
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

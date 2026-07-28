using System;
using System.Reflection;

namespace TwinCatGateway.Desktop;

public static class GatewayProductVersion
{
    private static readonly Lazy<string> Current =
        new(ReadVersion);

    public static string Value => Current.Value;

    public static string DisplayText => "Version " + Value;

    private static string ReadVersion()
    {
        Assembly assembly = typeof(GatewayProductVersion).Assembly;
        string? informationalVersion =
            assembly.GetCustomAttribute<
                AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion!;
        }

        Version? version = assembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

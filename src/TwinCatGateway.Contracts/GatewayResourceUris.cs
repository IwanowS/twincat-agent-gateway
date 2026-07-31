using System;

namespace TwinCatGateway.Contracts;

public static class GatewayResourceUris
{
    private const string ProfilePrefix = "twincat-profile://";
    private const string SourcesSuffix = "/sources";
    private const string SourceFilesSuffix = "/sources/files";

    public const string CurrentGatewayLog =
        "twincat-log://gateway/current";

    public static string ProfileSources(string profile)
    {
        return ProfilePrefix + EscapeProfile(profile) + SourcesSuffix;
    }

    public static string ProfileSourceFiles(string profile)
    {
        return ProfilePrefix + EscapeProfile(profile) + SourceFilesSuffix;
    }

    public static bool TryParseProfileSources(
        string uri,
        out string profile,
        out bool files)
    {
        profile = string.Empty;
        files = false;
        if (string.IsNullOrWhiteSpace(uri)
            || !uri.StartsWith(
                ProfilePrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        string suffix;
        if (uri.EndsWith(
            SourceFilesSuffix,
            StringComparison.Ordinal))
        {
            suffix = SourceFilesSuffix;
            files = true;
        }
        else if (uri.EndsWith(
            SourcesSuffix,
            StringComparison.Ordinal))
        {
            suffix = SourcesSuffix;
        }
        else
        {
            return false;
        }

        string escapedProfile = uri.Substring(
            ProfilePrefix.Length,
            uri.Length - ProfilePrefix.Length - suffix.Length);
        if (escapedProfile.Length == 0
            || escapedProfile.Contains("/"))
        {
            return false;
        }

        try
        {
            profile = Uri.UnescapeDataString(escapedProfile);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile)
            || !string.Equals(
                files
                    ? ProfileSourceFiles(profile)
                    : ProfileSources(profile),
                uri,
                StringComparison.Ordinal))
        {
            profile = string.Empty;
            files = false;
            return false;
        }

        return true;
    }

    private static string EscapeProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new ArgumentException(
                "Profile name is required.",
                nameof(profile));
        }

        return Uri.EscapeDataString(profile);
    }
}

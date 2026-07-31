using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class SourceManifestResourceReader
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private readonly SourceManifestStore _store;

    public SourceManifestResourceReader(SourceManifestStore store)
    {
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
    }

    public ResourceContent ReadManifest(
        string profile,
        int maximumCharacters,
        long offset)
    {
        EnsureProfileIdentity(profile);
        ValidatePaging(maximumCharacters, offset);
        if (offset != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The compact source manifest is an atomic resource.");
        }

        string uri = GatewayResourceUris.ProfileSources(profile);
        string content = JsonSerializer.Serialize(
            _store.ReadManifest(),
            SerializerOptions);
        if (content.Length > maximumCharacters)
        {
            throw new ArgumentException(
                "Maximum characters is too small for the atomic "
                    + "source manifest.",
                nameof(maximumCharacters));
        }

        return new ResourceContent
        {
            Uri = uri,
            ContentType = "application/json",
            Content = content,
            Offset = 0,
            NextOffset = null,
            Truncated = false,
        };
    }

    public ResourceContent ReadFiles(
        string profile,
        int maximumCharacters,
        long offset)
    {
        EnsureProfileIdentity(profile);
        ValidatePaging(maximumCharacters, offset);
        IReadOnlyList<SourceFileEntry> files = _store.ReadFiles();
        if (offset > files.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The source file offset exceeds the file count.");
        }

        int index = checked((int)offset);
        List<string> entries = new();
        int length = 2;
        while (index < files.Count)
        {
            string serialized = JsonSerializer.Serialize(
                files[index],
                SerializerOptions);
            int candidateLength = length
                + (entries.Count == 0 ? 0 : 1)
                + serialized.Length;
            if (candidateLength > maximumCharacters)
            {
                if (entries.Count == 0)
                {
                    throw new ArgumentException(
                        "Maximum characters is too small for one "
                            + "source file entry.",
                        nameof(maximumCharacters));
                }

                break;
            }

            entries.Add(serialized);
            length = candidateLength;
            index++;
        }

        string content = "[" + string.Join(",", entries) + "]";
        bool truncated = index < files.Count;
        return new ResourceContent
        {
            Uri = GatewayResourceUris.ProfileSourceFiles(profile),
            ContentType = "application/json",
            Content = content,
            Offset = offset,
            NextOffset = truncated ? index : null,
            Truncated = truncated,
        };
    }

    private static void ValidatePaging(
        int maximumCharacters,
        long offset)
    {
        if (maximumCharacters < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters));
        }

        if (offset < 0 || offset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private void EnsureProfileIdentity(string profile)
    {
        if (string.Equals(
            profile,
            _store.Profile,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeSolutionMismatch,
            $"Profile '{profile}' is not the active XAE context.",
            stage: "profile.sources.read",
            component: GatewayComponent.Profile,
            sideEffectsStarted: false,
            expected: new IdentityEvidence
            {
                Profile = profile,
            },
            observed: new IdentityEvidence
            {
                Profile = _store.Profile,
            });
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

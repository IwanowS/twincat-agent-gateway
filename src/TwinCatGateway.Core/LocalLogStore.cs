using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class LocalLogStore
{
    private const int MaximumPageCharacters = 1024 * 1024;

    private static readonly Dictionary<OperationArtifactKind, ResourceDescriptor>
        ResourceDescriptors =
            new Dictionary<OperationArtifactKind, ResourceDescriptor>
            {
                [OperationArtifactKind.Build] =
                    new("build", "build.log", "text/plain"),
                [OperationArtifactKind.XaeMessages] =
                    new("xae-messages", "xae-messages.json", "application/json"),
                [OperationArtifactKind.TestXunit] =
                    new("test/xunit", "xunit.xml", "application/xml"),
                [OperationArtifactKind.ProjectNoise] =
                    new(
                        "project-noise",
                        "project-noise.json",
                        "application/json"),
            };

    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, object> _fileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalLogStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Log root is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public ResourceReference WriteText(
        string operationId,
        OperationArtifactKind kind,
        string content)
    {
        return Write(operationId, kind, content, append: false);
    }

    public ResourceReference AppendText(
        string operationId,
        OperationArtifactKind kind,
        string content)
    {
        return Write(operationId, kind, content, append: true);
    }

    public ResourceContent Read(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0)
    {
        if (maximumCharacters <= 0
            || maximumCharacters > MaximumPageCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        ParsedResource resource = ParseResourceUri(uri);
        string path = GetResourcePath(resource.OperationId, resource.Kind);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Resource '{uri}' was not found.",
                path);
        }

        char[] buffer = new char[maximumCharacters + 1];
        int read;
        lock (GetFileLock(path))
        {
            using StreamReader reader = new(
                path,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            SkipCharacters(reader, offset);
            read = reader.ReadBlock(buffer, 0, buffer.Length);
        }

        bool truncated = read > maximumCharacters;
        int contentLength = Math.Min(read, maximumCharacters);
        return new ResourceContent
        {
            Uri = uri,
            ContentType = GetDescriptor(resource.Kind).ContentType,
            Content = new string(buffer, 0, contentLength),
            Offset = offset,
            NextOffset = truncated ? offset + contentLength : null,
            Truncated = truncated,
        };
    }

    public int Prune(DateTimeOffset olderThanUtc)
    {
        int removed = 0;
        foreach (string candidate in Directory.EnumerateDirectories(_rootDirectory))
        {
            string fullPath = EnsureInsideRoot(candidate);
            string operationId = Path.GetFileName(fullPath);
            if (!IsValidOperationId(operationId))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            DateTime lastWriteUtc = Directory.GetLastWriteTimeUtc(fullPath);
            if (lastWriteUtc >= olderThanUtc.UtcDateTime)
            {
                continue;
            }

            Directory.Delete(fullPath, recursive: true);
            removed++;
        }

        return removed;
    }

    private ResourceReference Write(
        string operationId,
        OperationArtifactKind kind,
        string content,
        bool append)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        string path = GetResourcePath(operationId, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (GetFileLock(path))
        {
            if (append)
            {
                File.AppendAllText(path, content, new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }

        ResourceDescriptor descriptor = GetDescriptor(kind);
        return new ResourceReference
        {
            Uri = $"twincat-operation://{operationId}/{descriptor.ResourceName}",
            MimeType = descriptor.ContentType,
        };
    }

    private string GetResourcePath(string operationId, OperationArtifactKind kind)
    {
        if (!IsValidOperationId(operationId))
        {
            throw new ArgumentException(
                "Operation ID contains unsupported characters.",
                nameof(operationId));
        }

        ResourceDescriptor descriptor = GetDescriptor(kind);
        return EnsureInsideRoot(
            Path.Combine(_rootDirectory, operationId, descriptor.FileName));
    }

    private object GetFileLock(string path)
    {
        return _fileLocks.GetOrAdd(path, _ => new object());
    }

    private static void SkipCharacters(TextReader reader, long count)
    {
        char[] buffer = new char[4096];
        long remaining = count;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = reader.ReadBlock(buffer, 0, requested);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }

    private string EnsureInsideRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string rootPrefix = _rootDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Resolved log path escapes the configured log root.");
        }

        return fullPath;
    }

    private static ParsedResource ParseResourceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Resource URI is invalid.", nameof(value));
        }

        string resourceName = uri.AbsolutePath.Trim('/');
        foreach (KeyValuePair<OperationArtifactKind, ResourceDescriptor> pair in ResourceDescriptors)
        {
            if (string.Equals(uri.Scheme, "twincat-operation", StringComparison.Ordinal)
                && string.Equals(
                    resourceName,
                    pair.Value.ResourceName,
                    StringComparison.Ordinal))
            {
                if (!IsValidOperationId(uri.Host))
                {
                    break;
                }

                return new ParsedResource(uri.Host, pair.Key);
            }
        }

        throw new ArgumentException(
            "Resource URI does not identify a supported gateway artifact.",
            nameof(value));
    }

    private static ResourceDescriptor GetDescriptor(OperationArtifactKind kind)
    {
        return ResourceDescriptors.TryGetValue(kind, out ResourceDescriptor? descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static bool IsValidOperationId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value!.Length > 128)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool valid =
                character >= 'a' && character <= 'z'
                || character >= 'A' && character <= 'Z'
                || character >= '0' && character <= '9'
                || character == '-'
                || character == '_';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ResourceDescriptor
    {
        public ResourceDescriptor(
            string resourceName,
            string fileName,
            string contentType)
        {
            ResourceName = resourceName;
            FileName = fileName;
            ContentType = contentType;
        }

        public string ResourceName { get; }

        public string FileName { get; }

        public string ContentType { get; }
    }

    private sealed class ParsedResource
    {
        public ParsedResource(string operationId, OperationArtifactKind kind)
        {
            OperationId = operationId;
            Kind = kind;
        }

        public string OperationId { get; }

        public OperationArtifactKind Kind { get; }
    }
}

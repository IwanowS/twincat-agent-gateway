using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace TwinCatGateway.Core;

public enum ProjectFileChangeKind
{
    Added,
    Modified,
    Deleted,
}

public sealed class ProjectFileFingerprint
{
    internal ProjectFileFingerprint(
        string path,
        long length,
        DateTime lastWriteTimeUtc,
        string sha256)
    {
        Path = path;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Sha256 = sha256;
    }

    public string Path { get; }

    public long Length { get; }

    public DateTime LastWriteTimeUtc { get; }

    public string Sha256 { get; }
}

public sealed class ProjectFileFingerprintSnapshot
{
    private readonly IReadOnlyDictionary<string, ProjectFileFingerprint> _files;
    private readonly IReadOnlyList<ProjectFileFingerprint> _orderedFiles;

    internal ProjectFileFingerprintSnapshot(
        IDictionary<string, ProjectFileFingerprint> files)
    {
        _files = new ReadOnlyDictionary<string, ProjectFileFingerprint>(
            new Dictionary<string, ProjectFileFingerprint>(
                files,
                StringComparer.OrdinalIgnoreCase));
        _orderedFiles = files.Values
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ProjectFileFingerprint> Files =>
        _orderedFiles;

    internal IReadOnlyDictionary<string, ProjectFileFingerprint> ByPath =>
        _files;
}

public sealed class ProjectFileChange
{
    internal ProjectFileChange(
        string path,
        ProjectFileChangeKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    public ProjectFileChangeKind Kind { get; }
}

public static class ProjectFileFingerprintScanner
{
    private const int BufferSize = 64 * 1024;
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".TcDUT",
            ".TcGVL",
            ".TcPOU",
        };

    public static ProjectFileFingerprintSnapshot Capture(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException(
                "Solution path is required.",
                nameof(solutionPath));
        }

        string fullSolutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullSolutionPath))
        {
            throw new FileNotFoundException(
                "Solution file was not found.",
                fullSolutionPath);
        }

        string root = Path.GetDirectoryName(fullSolutionPath)
            ?? throw new ArgumentException(
                "Solution path has no parent directory.",
                nameof(solutionPath));
        Dictionary<string, ProjectFileFingerprint> files =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateSupportedFiles(
            root,
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectFileFingerprint fingerprint =
                CaptureFile(path, cancellationToken);
            files.Add(fingerprint.Path, fingerprint);
        }

        return new ProjectFileFingerprintSnapshot(files);
    }

    public static IReadOnlyList<ProjectFileChange> Compare(
        ProjectFileFingerprintSnapshot baseline,
        ProjectFileFingerprintSnapshot current)
    {
        if (baseline is null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        List<ProjectFileChange> changes = new();
        foreach (ProjectFileFingerprint file in current.Files)
        {
            if (!baseline.ByPath.TryGetValue(
                file.Path,
                out ProjectFileFingerprint? previous))
            {
                changes.Add(
                    new ProjectFileChange(
                        file.Path,
                        ProjectFileChangeKind.Added));
            }
            else if (!string.Equals(
                previous.Sha256,
                file.Sha256,
                StringComparison.Ordinal))
            {
                changes.Add(
                    new ProjectFileChange(
                        file.Path,
                        ProjectFileChangeKind.Modified));
            }
        }

        foreach (ProjectFileFingerprint file in baseline.Files)
        {
            if (!current.ByPath.ContainsKey(file.Path))
            {
                changes.Add(
                    new ProjectFileChange(
                        file.Path,
                        ProjectFileChangeKind.Deleted));
            }
        }

        return changes
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsSupportedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    private static IEnumerable<string> EnumerateSupportedFiles(
        string root,
        CancellationToken cancellationToken)
    {
        Stack<string> directories = new();
        directories.Push(root);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = directories.Pop();
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupportedPath(path))
                {
                    yield return Path.GetFullPath(path);
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    directories.Push(child);
                }
            }
        }
    }

    private static ProjectFileFingerprint CaptureFile(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        long length = info.Length;
        DateTime lastWriteTimeUtc = info.LastWriteTimeUtc;
        string sha256;
        using (FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan))
        using (SHA256 algorithm = SHA256.Create())
        {
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = stream.Read(
                buffer,
                0,
                buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                algorithm.TransformBlock(
                    buffer,
                    0,
                    read,
                    outputBuffer: buffer,
                    outputOffset: 0);
            }

            algorithm.TransformFinalBlock(
                Array.Empty<byte>(),
                0,
                0);
            sha256 = ToHex(
                algorithm.Hash
                    ?? throw new CryptographicException(
                        "SHA-256 did not produce a hash."));
        }

        info.Refresh();
        if (length != info.Length
            || lastWriteTimeUtc != info.LastWriteTimeUtc)
        {
            throw new IOException(
                $"Project file changed while it was fingerprinted: '{path}'.");
        }

        return new ProjectFileFingerprint(
            Path.GetFullPath(path),
            length,
            lastWriteTimeUtc,
            sha256);
    }

    private static string ToHex(byte[] value)
    {
        return BitConverter
            .ToString(value)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }
}

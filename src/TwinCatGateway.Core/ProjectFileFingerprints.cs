using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;

namespace TwinCatGateway.Core;

public enum ProjectFileChangeKind
{
    Added,
    Modified,
    Deleted,
}

public enum ProjectGraphFileRole
{
    TwinCatProject,
    PlcProject,
    PlcSource,
    GeneratedArtifact,
}

public sealed class ProjectFileFingerprint
{
    internal ProjectFileFingerprint(
        string path,
        ProjectGraphFileRole role,
        long length,
        DateTime lastWriteTimeUtc,
        string sha256)
    {
        Path = path;
        Role = role;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Sha256 = sha256;
    }

    public string Path { get; }

    public ProjectGraphFileRole Role { get; }

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
    public ProjectFileChange(
        string path,
        ProjectFileChangeKind kind,
        ProjectGraphFileRole role)
    {
        Path = path;
        Kind = kind;
        Role = role;
    }

    public string Path { get; }

    public ProjectFileChangeKind Kind { get; }

    public ProjectGraphFileRole Role { get; }
}

public static class ProjectFileFingerprintScanner
{
    private const int BufferSize = 64 * 1024;
    public static ProjectFileFingerprintSnapshot Capture(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        return Capture(
            solutionPath,
            Array.Empty<string>(),
            cancellationToken);
    }

    public static ProjectFileFingerprintSnapshot Capture(
        string solutionPath,
        IEnumerable<string> additionalRoots,
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
        string[] roots = NormalizeRoots(root, additionalRoots);
        Dictionary<string, ProjectFileFingerprint> files =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string workspaceRoot in roots)
        {
            foreach (string path in EnumerateSupportedFiles(
                workspaceRoot,
                cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectFileFingerprint fingerprint =
                    CaptureFile(
                        path,
                        ProjectGraphFileRole.PlcSource,
                        cancellationToken);
                files.Add(fingerprint.Path, fingerprint);
            }
        }

        return new ProjectFileFingerprintSnapshot(files);
    }

    public static ProjectFileFingerprintSnapshot CaptureProjectGraph(
        string solutionPath,
        string twinCatProjectPath,
        CancellationToken cancellationToken)
    {
        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                cancellationToken);
        TwinCatProjectGraphEntry? missingRequired =
            graph.Entries.FirstOrDefault(entry =>
                !entry.Exists
                && (entry.Role
                        == ProjectGraphFileRole.TwinCatProject
                    || entry.Role
                        == ProjectGraphFileRole.PlcProject
                    || entry.Kind
                        == TwinCatGateway.Contracts
                            .SourceEntryKind.Editable));
        if (!graph.IsComplete || missingRequired is not null)
        {
            string missingPath = missingRequired?.Path
                ?? twinCatProjectPath;
            throw new FileNotFoundException(
                "A required project graph file was not found.",
                missingPath);
        }

        Dictionary<string, ProjectFileFingerprint> files =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (TwinCatProjectGraphEntry entry in graph.Entries
            .Where(entry =>
                entry.Kind
                    != TwinCatGateway.Contracts
                        .SourceEntryKind.Unsupported
                || entry.Role
                    == ProjectGraphFileRole.TwinCatProject
                || entry.Role
                    == ProjectGraphFileRole.PlcProject)
            .GroupBy(
                entry => entry.Path,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Role == ProjectGraphFileRole.GeneratedArtifact
                && !entry.Exists)
            {
                continue;
            }

            ProjectFileFingerprint fingerprint = CaptureFile(
                entry.Path,
                entry.Role,
                cancellationToken);
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
                        ProjectFileChangeKind.Added,
                        file.Role));
            }
            else if (!string.Equals(
                previous.Sha256,
                file.Sha256,
                StringComparison.Ordinal))
            {
                changes.Add(
                    new ProjectFileChange(
                        file.Path,
                        ProjectFileChangeKind.Modified,
                        file.Role));
            }
        }

        foreach (ProjectFileFingerprint file in baseline.Files)
        {
            if (!current.ByPath.ContainsKey(file.Path))
            {
                changes.Add(
                    new ProjectFileChange(
                        file.Path,
                        ProjectFileChangeKind.Deleted,
                        file.Role));
            }
        }

        return changes
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsSupportedPath(string path)
    {
        return TwinCatProjectGraphResolver.IsSupportedSourcePath(path);
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

    private static string[] NormalizeRoots(
        string solutionRoot,
        IEnumerable<string> additionalRoots)
    {
        if (additionalRoots is null)
        {
            throw new ArgumentNullException(nameof(additionalRoots));
        }

        List<string> roots = new();
        foreach (string candidate in new[] { solutionRoot }
            .Concat(additionalRoots)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length))
        {
            if (!Directory.Exists(candidate))
            {
                throw new DirectoryNotFoundException(
                    $"Project workspace directory was not found: '{candidate}'.");
            }

            if (roots.Any(root => IsInsideRoot(candidate, root)))
            {
                continue;
            }

            roots.Add(candidate);
        }

        return roots.ToArray();
    }

    private static bool IsInsideRoot(
        string path,
        string root)
    {
        string rootPrefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return string.Equals(
                path,
                root,
                StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectFileFingerprint CaptureFile(
        string path,
        ProjectGraphFileRole role,
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
            role,
            length,
            lastWriteTimeUtc,
            sha256);
    }

    private static XDocument LoadXml(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "A file referenced by the selected TwinCAT project graph "
                + "was not found.",
                path);
        }

        using (FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete))
        {
            return XDocument.Load(
                stream,
                LoadOptions.PreserveWhitespace
                    | LoadOptions.SetLineInfo);
        }
    }

    private static string ResolveReference(
        string ownerPath,
        string reference)
    {
        string normalized = reference.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        return Path.GetFullPath(
            Path.IsPathRooted(normalized)
                ? normalized
                : Path.Combine(
                    Path.GetDirectoryName(ownerPath)
                        ?? throw new InvalidOperationException(
                            "Project file has no parent directory."),
                    normalized));
    }

    private static string ToHex(byte[] value)
    {
        return BitConverter
            .ToString(value)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }
}

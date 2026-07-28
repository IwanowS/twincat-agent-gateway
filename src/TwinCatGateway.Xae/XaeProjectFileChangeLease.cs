using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using OleServiceProvider =
    Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace TwinCatGateway.Xae;

public sealed class XaeProjectFileChangeResult
{
    internal XaeProjectFileChangeResult(
        string path,
        ProjectChangeClassification classification,
        int movedBlocks,
        int contentChanges,
        string reason)
    {
        Path = path;
        Classification = classification;
        MovedBlocks = movedBlocks;
        ContentChanges = contentChanges;
        Reason = reason;
    }

    public string Path { get; }

    public ProjectChangeClassification Classification { get; }

    public int MovedBlocks { get; }

    public int ContentChanges { get; }

    public string Reason { get; }
}

internal sealed class XaeProjectFileChangeLease : IDisposable
{
    private readonly IVsFileChangeEx _fileChange;
    private readonly EnvDTE80.DTE2 _dte;
    private readonly string _solutionPath;
    private readonly IReadOnlyList<ProjectFileState> _files;
    private bool _disposed;

    private XaeProjectFileChangeLease(
        IVsFileChangeEx fileChange,
        EnvDTE80.DTE2 dte,
        string solutionPath,
        IReadOnlyList<ProjectFileState> files)
    {
        _fileChange = fileChange;
        _dte = dte;
        _solutionPath = solutionPath;
        _files = files;
    }

    public static XaeProjectFileChangeLease Acquire(
        EnvDTE80.DTE2 dte,
        string solutionPath)
    {
        string normalizedSolution = Path.GetFullPath(solutionPath);
        string[] paths = EnumerateProjectFiles(
            dte,
            normalizedSolution);
        IVsFileChangeEx? fileChange = null;
        List<ProjectFileState> files = new();
        try
        {
            fileChange = QueryService<IVsFileChangeEx>(
                dte,
                typeof(SVsFileChangeEx).GUID);
            foreach (string path in paths)
            {
                byte[] content = ReadAllBytes(path);
                ProjectFileState state = new(
                    path,
                    content,
                    ComputeSha256(content));
                Marshal.ThrowExceptionForHR(
                    fileChange.IgnoreFile(
                        0,
                        path,
                        1));
                files.Add(state);
            }

            XaeProjectFileChangeLease lease =
                new(
                    fileChange,
                    dte,
                    normalizedSolution,
                    files);
            fileChange = null;
            return lease;
        }
        catch
        {
            if (fileChange is not null)
            {
                RestoreNotifications(
                    fileChange,
                    files);
            }

            throw;
        }
        finally
        {
            ComObject.Release(fileChange);
        }
    }

    public IReadOnlyList<XaeProjectFileChangeResult>
        ClassifyChangesAndRelease(BuildAction action)
    {
        try
        {
            List<XaeProjectFileChangeResult> changes = new();
            HashSet<string> baselinePaths = new(
                _files.Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
            foreach (ProjectFileState file in _files)
            {
                if (!File.Exists(file.Path))
                {
                    XaeProjectFileChangeResult removed =
                        IsGeneratedArtifact(file.Path)
                            ? CreateExpectedGeneratedArtifact(
                                file.Path,
                                "The PLC TMC build artifact was removed "
                                + "during the tracked XAE operation.")
                            : new XaeProjectFileChangeResult(
                                file.Path,
                                ProjectChangeClassification.Unknown,
                                movedBlocks: 0,
                                contentChanges: 0,
                                "The TwinCAT project file was removed "
                                + "during the operation.");
                    file.AcknowledgeWhileIgnored = true;
                    changes.Add(
                        removed);
                    continue;
                }

                byte[] current = ReadAllBytes(file.Path);
                if (string.Equals(
                    file.Sha256,
                    ComputeSha256(current),
                    StringComparison.Ordinal))
                {
                    file.AcknowledgeWhileIgnored = true;
                    continue;
                }

                XaeProjectFileChangeResult classification =
                    ClassifyChangedProject(
                        action,
                        file.Path,
                        file.BaselineContent,
                        current);
                file.AcknowledgeWhileIgnored = true;
                changes.Add(classification);
            }

            foreach (string addedPath in EnumerateProjectFiles(
                _dte,
                _solutionPath)
                .Where(path => !baselinePaths.Contains(path)))
            {
                changes.Add(
                    IsGeneratedArtifact(addedPath)
                        ? CreateExpectedGeneratedArtifact(
                            addedPath,
                            "The PLC TMC build artifact was generated "
                            + "during the tracked XAE operation.")
                        : new XaeProjectFileChangeResult(
                            addedPath,
                            ProjectChangeClassification.Unknown,
                            movedBlocks: 0,
                            contentChanges: 0,
                            "A TwinCAT project file was added during "
                            + "the operation."));
            }

            return changes;
        }
        finally
        {
            Dispose();
        }
    }

    internal static XaeProjectFileChangeResult
        ClassifyChangedProject(
            BuildAction action,
            string path,
            byte[] baseline,
            byte[] current)
    {
        if (IsGeneratedArtifact(path))
        {
            return CreateExpectedGeneratedArtifact(
                path,
                "The PLC TMC file is a compiler-generated artifact. "
                + "Changes are expected and the file must not be merged.");
        }

        if (action == BuildAction.Clean)
        {
            return new XaeProjectFileChangeResult(
                path,
                ProjectChangeClassification.Unknown,
                movedBlocks: 0,
                contentChanges: 0,
                "Clean changed the TwinCAT project file. Clean has no "
                + "compiler-authoritative output order, so the change "
                + "cannot be classified as expected generated noise.");
        }

        TsProjectClassificationResult classification =
            TsProjectNoiseClassifier.Classify(
                baseline,
                current);
        return new XaeProjectFileChangeResult(
            path,
            classification.Classification,
            classification.MovedBlocks,
            classification.ContentChanges,
            classification.Reason);
    }

    private static XaeProjectFileChangeResult
        CreateExpectedGeneratedArtifact(
        string path,
        string reason)
    {
        return new XaeProjectFileChangeResult(
            path,
            ProjectChangeClassification.ExpectedGeneratedArtifact,
            movedBlocks: 0,
            contentChanges: 0,
            reason);
    }

    private static bool IsGeneratedArtifact(string path)
    {
        return string.Equals(
            Path.GetExtension(path),
            ".tmc",
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            RestoreNotifications(
                _fileChange,
                _files);
        }
        finally
        {
            ComObject.Release(_fileChange);
        }
    }

    private static string ComputeSha256(string path)
    {
        return ComputeSha256(ReadAllBytes(path));
    }

    private static string ComputeSha256(byte[] content)
    {
        using SHA256 sha256 = SHA256.Create();
        return BitConverter
            .ToString(sha256.ComputeHash(content))
            .Replace("-", string.Empty);
    }

    private static byte[] ReadAllBytes(string path)
    {
        FileInfo before = new(path);
        long length = before.Length;
        DateTime lastWriteTimeUtc = before.LastWriteTimeUtc;
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (length > int.MaxValue)
        {
            throw new IOException(
                $"TwinCAT project file is too large to classify: '{path}'.");
        }

        byte[] content = new byte[(int)length];
        int offset = 0;
        while (offset < content.Length)
        {
            int read = stream.Read(
                content,
                offset,
                content.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"TwinCAT project file changed while it was read: "
                    + $"'{path}'.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException(
                $"TwinCAT project file changed while it was read: '{path}'.");
        }

        before.Refresh();
        if (length != before.Length
            || lastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new IOException(
                $"TwinCAT project file changed while it was read: '{path}'.");
        }

        return content;
    }

    private static string[] EnumerateProjectFiles(
        EnvDTE80.DTE2 dte,
        string solutionPath)
    {
        EnvDTE.Solution? solution = null;
        EnvDTE.Projects? projects = null;
        List<string> paths = new();
        try
        {
            solution = dte.Solution;
            if (!string.Equals(
                NormalizeOptionalPath(solution.FullName),
                solutionPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "The selected XAE solution changed while "
                    + "TwinCAT project files were tracked.",
                    retryable: true,
                    stage: "xae.build.projectFiles");
            }

            projects = solution.Projects;
            int count = projects.Count;
            for (int index = 1; index <= count; index++)
            {
                EnvDTE.Project? project = null;
                try
                {
                    project = projects.Item(index);
                    string? path = project.FullName;
                    if (string.Equals(
                        Path.GetExtension(path),
                        ".tsproj",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        string twinCatProjectPath =
                            Path.GetFullPath(path);
                        paths.Add(twinCatProjectPath);
                        paths.AddRange(
                            ProjectFileFingerprintScanner
                                .CaptureProjectGraph(
                                    solutionPath,
                                    twinCatProjectPath,
                                    System.Threading
                                        .CancellationToken.None)
                                .Files
                                .Where(file =>
                                    file.Role
                                        == ProjectGraphFileRole
                                            .GeneratedArtifact)
                                .Select(file => file.Path));
                    }
                }
                finally
                {
                    ComObject.Release(project);
                }
            }
        }
        finally
        {
            ComObject.Release(projects);
            ComObject.Release(solution);
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }

    private static void RestoreNotifications(
        IVsFileChangeEx fileChange,
        IEnumerable<ProjectFileState> files)
    {
        Exception? failure = null;
        foreach (ProjectFileState file in files.Reverse())
        {
            try
            {
                bool exists = File.Exists(file.Path);
                bool unchanged = exists
                    && string.Equals(
                        file.Sha256,
                        ComputeSha256(file.Path),
                        StringComparison.Ordinal);
                if (exists
                    && (unchanged || file.AcknowledgeWhileIgnored))
                {
                    Marshal.ThrowExceptionForHR(
                        fileChange.SyncFile(file.Path));
                }

                Marshal.ThrowExceptionForHR(
                    fileChange.IgnoreFile(
                        0,
                        file.Path,
                        0));
                if (exists
                    && !unchanged
                    && !file.AcknowledgeWhileIgnored)
                {
                    Marshal.ThrowExceptionForHR(
                        fileChange.SyncFile(file.Path));
                }
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(
                        failure,
                        exception);
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static T QueryService<T>(
        EnvDTE80.DTE2 dte,
        Guid service)
        where T : class
    {
        IntPtr unknownPointer = IntPtr.Zero;
        IntPtr providerPointer = IntPtr.Zero;
        OleServiceProvider? serviceProvider = null;
        IntPtr servicePointer = IntPtr.Zero;
        try
        {
            unknownPointer = Marshal.GetIUnknownForObject(dte);
            Guid providerIid = typeof(OleServiceProvider).GUID;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(
                    unknownPointer,
                    ref providerIid,
                    out providerPointer));
            serviceProvider = (OleServiceProvider)
                Marshal.GetTypedObjectForIUnknown(
                    providerPointer,
                    typeof(OleServiceProvider));
            Guid iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(
                serviceProvider.QueryService(
                    ref service,
                    ref iid,
                    out servicePointer));
            if (servicePointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"XAE service returned no {typeof(T).Name}.");
            }

            return (T)Marshal.GetTypedObjectForIUnknown(
                servicePointer,
                typeof(T));
        }
        finally
        {
            ComObject.Release(serviceProvider);
            if (servicePointer != IntPtr.Zero)
            {
                Marshal.Release(servicePointer);
            }

            if (providerPointer != IntPtr.Zero)
            {
                Marshal.Release(providerPointer);
            }

            if (unknownPointer != IntPtr.Zero)
            {
                Marshal.Release(unknownPointer);
            }
        }
    }

    private sealed class ProjectFileState
    {
        public ProjectFileState(
            string path,
            byte[] baselineContent,
            string sha256)
        {
            Path = path;
            BaselineContent = baselineContent;
            Sha256 = sha256;
        }

        public string Path { get; }

        public byte[] BaselineContent { get; }

        public string Sha256 { get; }

        public bool AcknowledgeWhileIgnored { get; set; }
    }
}

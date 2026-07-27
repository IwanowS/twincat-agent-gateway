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

internal sealed class XaeProjectFileChangeLease : IDisposable
{
    private readonly IVsFileChangeEx _fileChange;
    private readonly IReadOnlyList<ProjectFileState> _files;
    private bool _disposed;

    private XaeProjectFileChangeLease(
        IVsFileChangeEx fileChange,
        IReadOnlyList<ProjectFileState> files)
    {
        _fileChange = fileChange;
        _files = files;
    }

    public static XaeProjectFileChangeLease Acquire(
        EnvDTE80.DTE2 dte,
        string solutionPath)
    {
        string root = Path.GetDirectoryName(
            Path.GetFullPath(solutionPath))
            ?? throw new ArgumentException(
                "Solution path has no parent directory.",
                nameof(solutionPath));
        string[] paths = Directory
            .EnumerateFiles(
                root,
                "*.tsproj",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IVsFileChangeEx? fileChange = null;
        List<ProjectFileState> files = new();
        try
        {
            fileChange = QueryService<IVsFileChangeEx>(
                dte,
                typeof(SVsFileChangeEx).GUID);
            foreach (string path in paths)
            {
                ProjectFileState state = new(
                    path,
                    ComputeSha256(path));
                Marshal.ThrowExceptionForHR(
                    fileChange.IgnoreFile(
                        0,
                        path,
                        1));
                files.Add(state);
            }

            XaeProjectFileChangeLease lease =
                new(fileChange, files);
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

    public void VerifyUnchangedAndRelease()
    {
        string? changedPath = null;
        foreach (ProjectFileState file in _files)
        {
            if (!File.Exists(file.Path)
                || !string.Equals(
                    file.Sha256,
                    ComputeSha256(file.Path),
                    StringComparison.Ordinal))
            {
                changedPath = file.Path;
                break;
            }
        }

        Dispose();
        if (changedPath is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditUnsupported,
                $"TwinCAT project file content changed during the "
                + $"operation and remains unclassified: '{changedPath}'.",
                stage: "xae.build.project-file");
        }
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
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return BitConverter
            .ToString(sha256.ComputeHash(stream))
            .Replace("-", string.Empty);
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
                bool unchanged = File.Exists(file.Path)
                    && string.Equals(
                        file.Sha256,
                        ComputeSha256(file.Path),
                        StringComparison.Ordinal);
                if (unchanged)
                {
                    Marshal.ThrowExceptionForHR(
                        fileChange.SyncFile(file.Path));
                }

                Marshal.ThrowExceptionForHR(
                    fileChange.IgnoreFile(
                        0,
                        file.Path,
                        0));
                if (!unchanged)
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
            string sha256)
        {
            Path = path;
            Sha256 = sha256;
        }

        public string Path { get; }

        public string Sha256 { get; }
    }
}

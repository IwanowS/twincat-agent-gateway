using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using OleServiceProvider =
    Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace TwinCatGateway.Xae;

public sealed class ExternalChangeSynchronizationResult
{
    internal ExternalChangeSynchronizationResult(
        IEnumerable<ProjectFileChange> detectedChanges,
        IEnumerable<string> synchronizedDocuments,
        IEnumerable<string> discardedDocuments,
        SynchronizationScope scope = SynchronizationScope.None)
    {
        DetectedChanges = detectedChanges.ToArray();
        SynchronizedDocuments = synchronizedDocuments.ToArray();
        DiscardedDocuments = discardedDocuments.ToArray();
        Scope = scope;
    }

    public IReadOnlyList<ProjectFileChange> DetectedChanges { get; }

    public IReadOnlyList<string> SynchronizedDocuments { get; }

    public IReadOnlyList<string> DiscardedDocuments { get; }

    public SynchronizationScope Scope { get; }
}

internal sealed class XaeDocumentSynchronizationResult
{
    internal XaeDocumentSynchronizationResult(
        IEnumerable<string> synchronizedDocuments,
        IEnumerable<string> discardedDocuments)
    {
        SynchronizedDocuments = synchronizedDocuments.ToArray();
        DiscardedDocuments = discardedDocuments.ToArray();
    }

    public IReadOnlyList<string> SynchronizedDocuments { get; }

    public IReadOnlyList<string> DiscardedDocuments { get; }
}

internal static class ExternalChangeSynchronizer
{
    public static XaeDocumentSynchronizationResult Synchronize(
        DTE2 dte,
        IEnumerable<string> changedPaths,
        IEnumerable<string> projectGraphPaths,
        bool discardDirtyDocuments)
    {
        if (dte is null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        string[] paths = changedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AgentWorkspaceOwnershipResult ownership =
            AgentWorkspaceOwnership.EnsureClean(
                dte,
                projectGraphPaths,
                discardDirtyDocuments);
        if (paths.Length == 0)
        {
            return new XaeDocumentSynchronizationResult(
                Array.Empty<string>(),
                ownership.DiscardedDocuments);
        }

        Documents? documents = null;
        IVsRunningDocumentTable? table = null;
        List<string> synchronized = new();
        try
        {
            documents = dte.Documents;
            table = QueryRunningDocumentTable(dte);
            foreach (string path in paths)
            {
                Document? document = null;
                bool wasOpen = IsOpen(documents, path);
                try
                {
                    document = documents.Open(path);
                    ReloadDocumentData(table, path);
                }
                catch (Exception exception)
                {
                    throw new GatewayOperationException(
                        ErrorCodes.ExternalEditSyncFailed,
                        $"XAE could not reload the externally changed "
                        + $"project file '{path}'.",
                        retryable: true,
                        stage: "xae.workspace.synchronize",
                        innerException: exception);
                }
                finally
                {
                    ComObject.Release(document);
                }

                if (!wasOpen)
                {
                    CloseIfStillOpen(documents, path);
                }
                synchronized.Add(path);
            }

            return new XaeDocumentSynchronizationResult(
                synchronized,
                ownership.DiscardedDocuments);
        }
        finally
        {
            ComObject.Release(table);
            ComObject.Release(documents);
        }
    }

    private static void ReloadDocumentData(
        IVsRunningDocumentTable table,
        string path)
    {
        IVsHierarchy? hierarchy = null;
        IntPtr documentDataPointer = IntPtr.Zero;
        IntPtr persistPointer = IntPtr.Zero;
        IVsPersistDocData? persist = null;
        try
        {
            Marshal.ThrowExceptionForHR(
                table.FindAndLockDocument(
                    (uint)_VSRDTFLAGS.RDT_NoLock,
                    path,
                    out hierarchy,
                    out _,
                    out documentDataPointer,
                    out _));
            if (documentDataPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "XAE did not register the opened document "
                    + "in the Running Document Table.");
            }

            Guid persistIid = typeof(IVsPersistDocData).GUID;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(
                    documentDataPointer,
                    ref persistIid,
                    out persistPointer));
            persist = (IVsPersistDocData)
                Marshal.GetTypedObjectForIUnknown(
                    persistPointer,
                    typeof(IVsPersistDocData));
            uint reloadFlags = (uint)(
                _VSRELOADDOCDATA.RDD_IgnoreNextFileChange
                | _VSRELOADDOCDATA.RDD_RemoveUndoStack);
            Marshal.ThrowExceptionForHR(
                persist.ReloadDocData(reloadFlags));
        }
        finally
        {
            ComObject.Release(persist);
            if (persistPointer != IntPtr.Zero)
            {
                Marshal.Release(persistPointer);
            }

            if (documentDataPointer != IntPtr.Zero)
            {
                Marshal.Release(documentDataPointer);
            }

            ComObject.Release(hierarchy);
        }
    }

    private static IVsRunningDocumentTable QueryRunningDocumentTable(
        DTE2 dte)
    {
        IntPtr unknownPointer = IntPtr.Zero;
        IntPtr providerPointer = IntPtr.Zero;
        OleServiceProvider? serviceProvider = null;
        Guid service = typeof(SVsRunningDocumentTable).GUID;
        Guid iid = typeof(IVsRunningDocumentTable).GUID;
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
            Marshal.ThrowExceptionForHR(
                serviceProvider.QueryService(
                    ref service,
                    ref iid,
                    out servicePointer));
            if (servicePointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "XAE Running Document Table service "
                    + "returned no interface.");
            }

            return (IVsRunningDocumentTable)
                Marshal.GetTypedObjectForIUnknown(
                    servicePointer,
                    typeof(IVsRunningDocumentTable));
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

    private static void CloseIfStillOpen(
        Documents documents,
        string path)
    {
        int count = documents.Count;
        for (int index = count; index >= 1; index--)
        {
            Document? document = null;
            try
            {
                document = documents.Item(index);
                if (string.Equals(
                    NormalizeOptionalPath(document.FullName),
                    path,
                    StringComparison.OrdinalIgnoreCase))
                {
                    document.Close(
                        vsSaveChanges.vsSaveChangesNo);
                    return;
                }
            }
            finally
            {
                ComObject.Release(document);
            }
        }
    }

    private static bool IsOpen(
        Documents documents,
        string path)
    {
        int count = documents.Count;
        for (int index = 1; index <= count; index++)
        {
            Document? document = null;
            try
            {
                document = documents.Item(index);
                if (string.Equals(
                    NormalizeOptionalPath(document.FullName),
                    path,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            finally
            {
                ComObject.Release(document);
            }
        }

        return false;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }
}

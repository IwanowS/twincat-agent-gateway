using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;

namespace TwinCatGateway.Xae;

internal static class XaeRunningDocumentDirtyProbe
{
    private const int NoInterface = unchecked((int)0x80004002);

    public static IReadOnlyList<string> FindDirtyDocuments(DTE2 dte)
    {
        IVsRunningDocumentTable? table = null;
        IVsRunningDocumentTable3? dirtyStateTable = null;
        IEnumRunningDocuments? documents = null;
        List<string> dirty = new();
        try
        {
            table = XaeSession.QueryService<IVsRunningDocumentTable>(
                dte,
                typeof(SVsRunningDocumentTable).GUID);
            dirtyStateTable =
                XaeSession.QueryService<IVsRunningDocumentTable3>(
                    dte,
                    typeof(SVsRunningDocumentTable).GUID);
            Marshal.ThrowExceptionForHR(
                table.GetRunningDocumentsEnum(out documents));
            uint[] cookies = new uint[1];
            while (true)
            {
                int result = documents.Next(1, cookies, out uint fetched);
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }

                if (fetched == 0)
                {
                    break;
                }

                string? path = ReadDirtyDocumentPath(
                    table,
                    dirtyStateTable,
                    cookies[0]);
                if (path is not null)
                {
                    dirty.Add(path);
                }
            }
        }
        finally
        {
            ComObject.Release(documents);
            ComObject.Release(dirtyStateTable);
            ComObject.Release(table);
        }

        return dirty;
    }

    public static bool HasAnyDirtyDocument(DTE2 dte) =>
        FindDirtyDocuments(dte).Count != 0;

    public static void DiscardDirtyDocuments(
        DTE2 dte,
        IEnumerable<string> paths)
    {
        IVsRunningDocumentTable? table = null;
        try
        {
            table = XaeSession.QueryService<IVsRunningDocumentTable>(
                dte,
                typeof(SVsRunningDocumentTable).GUID);
            foreach (string path in paths)
            {
                ReloadDirtyDocument(table, Path.GetFullPath(path));
            }
        }
        finally
        {
            ComObject.Release(table);
        }
    }

    private static string? ReadDirtyDocumentPath(
        IVsRunningDocumentTable table,
        IVsRunningDocumentTable3 dirtyStateTable,
        uint cookie)
    {
        IVsHierarchy? hierarchy = null;
        IntPtr documentDataPointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(
                table.GetDocumentInfo(
                    cookie,
                    out _,
                    out _,
                    out _,
                    out string moniker,
                    out hierarchy,
                    out uint itemId,
                    out documentDataPointer));
            dirtyStateTable.UpdateDirtyState(cookie);
            bool isDirty = dirtyStateTable.IsDocumentDirty(cookie);
            if (!isDirty && documentDataPointer != IntPtr.Zero)
            {
                isDirty = IsDirty(documentDataPointer);
            }

            if (!isDirty
                || !TryResolvePath(
                    moniker,
                    hierarchy,
                    itemId,
                    out string path))
            {
                return null;
            }

            return path;
        }
        finally
        {
            if (documentDataPointer != IntPtr.Zero)
            {
                Marshal.Release(documentDataPointer);
            }

            ComObject.Release(hierarchy);
        }
    }

    private static void ReloadDirtyDocument(
        IVsRunningDocumentTable table,
        string path)
    {
        IVsHierarchy? hierarchy = null;
        IntPtr documentDataPointer = IntPtr.Zero;
        try
        {
            int result = table.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock,
                path,
                out hierarchy,
                out _,
                out documentDataPointer,
                out _);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            if (documentDataPointer == IntPtr.Zero
                || !IsDirty(documentDataPointer))
            {
                return;
            }

            WithPersistDocData(
                documentDataPointer,
                persist => Marshal.ThrowExceptionForHR(
                    persist.ReloadDocData(
                        (uint)(_VSRELOADDOCDATA.RDD_IgnoreNextFileChange
                            | _VSRELOADDOCDATA.RDD_RemoveUndoStack))));
        }
        finally
        {
            if (documentDataPointer != IntPtr.Zero)
            {
                Marshal.Release(documentDataPointer);
            }

            ComObject.Release(hierarchy);
        }
    }

    private static bool IsDirty(IntPtr documentDataPointer)
    {
        bool dirty = false;
        WithPersistDocData(
            documentDataPointer,
            persist =>
            {
                Marshal.ThrowExceptionForHR(
                    persist.IsDocDataDirty(out int isDirty));
                dirty = isDirty != 0;
            });
        return dirty;
    }

    private static void WithPersistDocData(
        IntPtr documentDataPointer,
        Action<IVsPersistDocData> action)
    {
        IntPtr persistPointer = IntPtr.Zero;
        IVsPersistDocData? persist = null;
        try
        {
            Guid iid = typeof(IVsPersistDocData).GUID;
            int result = Marshal.QueryInterface(
                documentDataPointer,
                ref iid,
                out persistPointer);
            if (result == NoInterface)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(result);
            persist = (IVsPersistDocData)
                Marshal.GetTypedObjectForIUnknown(
                    persistPointer,
                    typeof(IVsPersistDocData));
            action(persist);
        }
        finally
        {
            ComObject.Release(persist);
            if (persistPointer != IntPtr.Zero)
            {
                Marshal.Release(persistPointer);
            }
        }
    }

    private static bool TryResolvePath(
        string? moniker,
        IVsHierarchy? hierarchy,
        uint itemId,
        out string path)
    {
        if (TryNormalizePath(moniker, out path))
        {
            return true;
        }

        if (hierarchy is IVsProject project)
        {
            int result = project.GetMkDocument(
                itemId,
                out string projectMoniker);
            if (result >= 0
                && TryNormalizePath(projectMoniker, out path))
            {
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static bool TryNormalizePath(
        string? moniker,
        out string path)
    {
        path = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(moniker)
                || !Path.IsPathRooted(moniker))
            {
                return false;
            }

            path = Path.GetFullPath(moniker);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            || exception is NotSupportedException
            || exception is PathTooLongException
            || exception is System.Security.SecurityException)
        {
            return false;
        }
    }
}

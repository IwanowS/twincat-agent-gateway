using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using OleServiceProvider =
    Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace TwinCatGateway.Xae;

internal sealed class XaeWorkspaceGuardPath
{
    public XaeWorkspaceGuardPath(
        string path,
        bool protectOpenDocument)
    {
        Path = System.IO.Path.GetFullPath(path);
        ProtectOpenDocument = protectOpenDocument;
    }

    public string Path { get; }

    public bool ProtectOpenDocument { get; }
}

internal interface IXaeWorkspaceFileChangeBackend : IDisposable
{
    void StartDocumentTracking(
        Action<string> documentOpened,
        Action<string> documentClosing,
        Action<string?, Exception> trackingFailed);

    void StopDocumentTracking();

    IReadOnlyList<string> GetOpenDocumentPaths();

    void IgnoreProjectFile(string path, bool ignore);

    bool IgnoreDocumentFileChanges(string path, bool ignore);

    void SyncProjectFile(string path);
}

internal interface IXaeProjectFileChangeGuard
{
    bool IsProjectFileGuarded(string path);

    void SyncProjectFile(string path);
}

internal sealed class XaeWorkspaceFileChangeGuard :
    IXaeProjectFileChangeGuard,
    IDisposable
{
    private readonly IXaeWorkspaceFileChangeBackend _backend;
    private readonly Dictionary<string, XaeWorkspaceGuardPath> _paths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _projectGuardOrder = new();
    private readonly HashSet<string> _protectedDocuments =
        new(StringComparer.OrdinalIgnoreCase);
    private GatewayOperationException? _fault;
    private bool _trackingStarted;
    private bool _disposed;

    private XaeWorkspaceFileChangeGuard(
        IXaeWorkspaceFileChangeBackend backend)
    {
        _backend = backend;
    }

    public bool IsActive =>
        !_disposed
        && _fault is null
        && _trackingStarted;

    public static XaeWorkspaceFileChangeGuard Acquire(
        DTE2 dte,
        IEnumerable<ProjectFileFingerprint> files)
    {
        if (dte is null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        XaeWorkspaceFileChangeBackend? backend = null;
        try
        {
            backend = XaeWorkspaceFileChangeBackend.Create(dte);
            XaeWorkspaceFileChangeGuard guard =
                Acquire(
                    backend,
                    CreatePaths(files));
            backend = null;
            return guard;
        }
        finally
        {
            backend?.Dispose();
        }
    }

    internal static XaeWorkspaceFileChangeGuard Acquire(
        IXaeWorkspaceFileChangeBackend backend,
        IEnumerable<XaeWorkspaceGuardPath> paths)
    {
        if (backend is null)
        {
            throw new ArgumentNullException(nameof(backend));
        }

        XaeWorkspaceFileChangeGuard guard = new(backend);
        try
        {
            guard.Initialize(paths);
            return guard;
        }
        catch (Exception exception)
        {
            Exception failure = exception;
            try
            {
                guard.Dispose();
            }
            catch (Exception cleanupException)
            {
                failure = Combine(failure, cleanupException);
            }

            throw CreateFailure(string.Empty, failure);
        }
    }

    public bool IsProjectFileGuarded(string path)
    {
        ThrowIfFaulted();
        return _paths.ContainsKey(Path.GetFullPath(path));
    }

    public void SyncProjectFile(string path)
    {
        ThrowIfFaulted();
        string normalized = Path.GetFullPath(path);
        if (!_paths.ContainsKey(normalized))
        {
            throw CreateFailure(
                normalized,
                new InvalidOperationException(
                    "The file is outside the guarded project graph."));
        }

        try
        {
            _backend.SyncProjectFile(normalized);
        }
        catch (Exception exception)
        {
            throw CreateFailure(normalized, exception);
        }
    }

    public void UpdatePaths(
        IEnumerable<ProjectFileFingerprint> files)
    {
        UpdatePaths(CreatePaths(files));
    }

    internal void UpdatePaths(
        IEnumerable<XaeWorkspaceGuardPath> paths)
    {
        ThrowIfFaulted();
        Dictionary<string, XaeWorkspaceGuardPath> requested =
            NormalizePaths(paths);
        List<string> added = new();
        try
        {
            foreach (XaeWorkspaceGuardPath path in requested.Values
                .Where(path => !_paths.ContainsKey(path.Path))
                .OrderBy(path => path.Path, StringComparer.OrdinalIgnoreCase))
            {
                _backend.IgnoreProjectFile(path.Path, ignore: true);
                _paths.Add(path.Path, path);
                _projectGuardOrder.Add(path.Path);
                added.Add(path.Path);
            }

            foreach (XaeWorkspaceGuardPath path in requested.Values)
            {
                _paths[path.Path] = path;
            }

            foreach (string openPath in _backend.GetOpenDocumentPaths())
            {
                ProtectDocumentIfRequired(openPath);
            }

            foreach (string protectedPath in _protectedDocuments
                .Where(path =>
                    !requested.TryGetValue(
                        path,
                        out XaeWorkspaceGuardPath? requestedPath)
                    || !requestedPath.ProtectOpenDocument)
                .ToArray())
            {
                UnprotectDocument(protectedPath);
            }

            foreach (string removedPath in _projectGuardOrder
                .Where(path => !requested.ContainsKey(path))
                .Reverse()
                .ToArray())
            {
                _backend.IgnoreProjectFile(
                    removedPath,
                    ignore: false);
                _paths.Remove(removedPath);
                _projectGuardOrder.Remove(removedPath);
            }
        }
        catch (Exception exception)
        {
            Exception rollbackFailure = exception;
            foreach (string addedPath in added.AsEnumerable().Reverse())
            {
                try
                {
                    _backend.IgnoreProjectFile(
                        addedPath,
                        ignore: false);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure = Combine(
                        rollbackFailure,
                        rollbackException);
                }

                _paths.Remove(addedPath);
                _projectGuardOrder.Remove(addedPath);
            }

            GatewayOperationException failure =
                CreateFailure(
                    added.LastOrDefault()
                        ?? requested.Keys.FirstOrDefault()
                        ?? string.Empty,
                    rollbackFailure);
            _fault = failure;
            throw failure;
        }
    }

    public void ThrowIfFaulted()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(XaeWorkspaceFileChangeGuard));
        }

        if (_fault is not null)
        {
            ExceptionDispatchInfo.Capture(_fault).Throw();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Exception? failure = null;
        if (_trackingStarted)
        {
            try
            {
                _backend.StopDocumentTracking();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            _trackingStarted = false;
        }

        foreach (string path in _protectedDocuments
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray())
        {
            try
            {
                _backend.IgnoreDocumentFileChanges(
                    path,
                    ignore: false);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }

        _protectedDocuments.Clear();
        foreach (string path in _projectGuardOrder
            .AsEnumerable()
            .Reverse()
            .ToArray())
        {
            try
            {
                _backend.IgnoreProjectFile(path, ignore: false);
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }

        _projectGuardOrder.Clear();
        _paths.Clear();
        try
        {
            _backend.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void Initialize(
        IEnumerable<XaeWorkspaceGuardPath> paths)
    {
        Dictionary<string, XaeWorkspaceGuardPath> normalized =
            NormalizePaths(paths);
        foreach (XaeWorkspaceGuardPath path in normalized.Values)
        {
            _backend.IgnoreProjectFile(path.Path, ignore: true);
            _paths.Add(path.Path, path);
            _projectGuardOrder.Add(path.Path);
        }

        _backend.StartDocumentTracking(
            OnDocumentOpened,
            OnDocumentClosing,
            OnDocumentTrackingFailed);
        _trackingStarted = true;
        foreach (string path in _backend.GetOpenDocumentPaths())
        {
            ProtectDocumentIfRequired(path);
        }
    }

    private void OnDocumentOpened(string path)
    {
        try
        {
            ProtectDocumentIfRequired(path);
        }
        catch (Exception exception)
        {
            _fault = CreateFailure(path, exception);
        }
    }

    private void OnDocumentClosing(string path)
    {
        try
        {
            UnprotectDocument(path);
        }
        catch (Exception exception)
        {
            _fault = CreateFailure(path, exception);
        }
    }

    private void OnDocumentTrackingFailed(
        string? path,
        Exception exception)
    {
        _fault = CreateFailure(path ?? string.Empty, exception);
    }

    private void ProtectDocumentIfRequired(string path)
    {
        string normalized = Path.GetFullPath(path);
        if (!_paths.TryGetValue(
                normalized,
                out XaeWorkspaceGuardPath? guarded)
            || !guarded.ProtectOpenDocument
            || _protectedDocuments.Contains(normalized))
        {
            return;
        }

        if (_backend.IgnoreDocumentFileChanges(
            normalized,
            ignore: true))
        {
            _protectedDocuments.Add(normalized);
        }
    }

    private void UnprotectDocument(string path)
    {
        string normalized = Path.GetFullPath(path);
        if (!_protectedDocuments.Remove(normalized))
        {
            return;
        }

        _backend.IgnoreDocumentFileChanges(
            normalized,
            ignore: false);
    }

    private static XaeWorkspaceGuardPath[] CreatePaths(
        IEnumerable<ProjectFileFingerprint> files)
    {
        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }

        return files
            .Where(file =>
                file.Role != ProjectGraphFileRole.GeneratedArtifact)
            .Select(file =>
                new XaeWorkspaceGuardPath(
                    file.Path,
                    file.Role == ProjectGraphFileRole.PlcSource))
            .ToArray();
    }

    private static Dictionary<string, XaeWorkspaceGuardPath>
        NormalizePaths(
            IEnumerable<XaeWorkspaceGuardPath> paths)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        return paths
            .GroupBy(
                path => path.Path,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                new XaeWorkspaceGuardPath(
                    group.Key,
                    group.Any(path =>
                        path.ProtectOpenDocument)))
            .OrderBy(
                path => path.Path,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => path.Path,
                StringComparer.OrdinalIgnoreCase);
    }

    private static GatewayOperationException CreateFailure(
        string path,
        Exception exception)
    {
        string suffix = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : $" for '{path}'";
        return exception as GatewayOperationException
            ?? new GatewayOperationException(
                ErrorCodes.XaeFileChangeGuardFailed,
                $"XAE file-change guard failed{suffix}.",
                retryable: true,
                stage: "xae.workspace.guard",
                innerException: exception);
    }

    private static Exception Combine(
        Exception? current,
        Exception next)
    {
        return current is null
            ? next
            : new AggregateException(current, next);
    }
}

internal sealed class XaeWorkspaceFileChangeBackend :
    IXaeWorkspaceFileChangeBackend
{
    private readonly DTE2 _dte;
    private readonly IVsFileChangeEx _fileChange;
    private readonly IVsRunningDocumentTable _runningDocuments;
    private RunningDocumentEventSink? _eventSink;
    private uint _eventCookie;
    private bool _disposed;

    private XaeWorkspaceFileChangeBackend(
        DTE2 dte,
        IVsFileChangeEx fileChange,
        IVsRunningDocumentTable runningDocuments)
    {
        _dte = dte;
        _fileChange = fileChange;
        _runningDocuments = runningDocuments;
    }

    public static XaeWorkspaceFileChangeBackend Create(DTE2 dte)
    {
        IVsFileChangeEx? fileChange = null;
        IVsRunningDocumentTable? runningDocuments = null;
        try
        {
            fileChange = QueryService<IVsFileChangeEx>(
                dte,
                typeof(SVsFileChangeEx).GUID);
            runningDocuments = QueryService<IVsRunningDocumentTable>(
                dte,
                typeof(SVsRunningDocumentTable).GUID);
            XaeWorkspaceFileChangeBackend backend =
                new(dte, fileChange, runningDocuments);
            fileChange = null;
            runningDocuments = null;
            return backend;
        }
        finally
        {
            ComObject.Release(runningDocuments);
            ComObject.Release(fileChange);
        }
    }

    public void StartDocumentTracking(
        Action<string> documentOpened,
        Action<string> documentClosing,
        Action<string?, Exception> trackingFailed)
    {
        ThrowIfDisposed();
        if (_eventSink is not null)
        {
            throw new InvalidOperationException(
                "Running Document Table tracking is already active.");
        }

        _eventSink = new RunningDocumentEventSink(
            _runningDocuments,
            documentOpened,
            documentClosing,
            trackingFailed);
        try
        {
            Marshal.ThrowExceptionForHR(
                _runningDocuments.AdviseRunningDocTableEvents(
                    _eventSink,
                    out _eventCookie));
        }
        catch
        {
            _eventSink = null;
            _eventCookie = 0;
            throw;
        }
    }

    public void StopDocumentTracking()
    {
        ThrowIfDisposed();
        if (_eventSink is null)
        {
            return;
        }

        uint cookie = _eventCookie;
        _eventCookie = 0;
        _eventSink = null;
        Marshal.ThrowExceptionForHR(
            _runningDocuments.UnadviseRunningDocTableEvents(cookie));
    }

    public IReadOnlyList<string> GetOpenDocumentPaths()
    {
        ThrowIfDisposed();
        Documents? documents = null;
        List<string> paths = new();
        try
        {
            documents = _dte.Documents;
            for (int index = 1; index <= documents.Count; index++)
            {
                Document? document = null;
                try
                {
                    document = documents.Item(index);
                    if (TryNormalizeFileMoniker(
                            document.FullName,
                            out string path))
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    ComObject.Release(document);
                }
            }
        }
        finally
        {
            ComObject.Release(documents);
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool TryNormalizeFileMoniker(
        string? moniker,
        out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(moniker))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathRooted(moniker))
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

    public void IgnoreProjectFile(string path, bool ignore)
    {
        ThrowIfDisposed();
        Marshal.ThrowExceptionForHR(
            _fileChange.IgnoreFile(
                0,
                path,
                ignore ? 1 : 0));
    }

    public bool IgnoreDocumentFileChanges(
        string path,
        bool ignore)
    {
        ThrowIfDisposed();
        IVsHierarchy? hierarchy = null;
        IntPtr documentDataPointer = IntPtr.Zero;
        IntPtr controlPointer = IntPtr.Zero;
        IVsDocDataFileChangeControl? control = null;
        try
        {
            Marshal.ThrowExceptionForHR(
                _runningDocuments.FindAndLockDocument(
                    (uint)_VSRDTFLAGS.RDT_NoLock,
                    path,
                    out hierarchy,
                    out _,
                    out documentDataPointer,
                    out _));
            if (documentDataPointer == IntPtr.Zero)
            {
                return false;
            }

            Guid controlIid =
                typeof(IVsDocDataFileChangeControl).GUID;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(
                    documentDataPointer,
                    ref controlIid,
                    out controlPointer));
            control = (IVsDocDataFileChangeControl)
                Marshal.GetTypedObjectForIUnknown(
                    controlPointer,
                    typeof(IVsDocDataFileChangeControl));
            Marshal.ThrowExceptionForHR(
                control.IgnoreFileChanges(ignore ? 1 : 0));
            return true;
        }
        finally
        {
            ComObject.Release(control);
            if (controlPointer != IntPtr.Zero)
            {
                Marshal.Release(controlPointer);
            }

            if (documentDataPointer != IntPtr.Zero)
            {
                Marshal.Release(documentDataPointer);
            }

            ComObject.Release(hierarchy);
        }
    }

    public void SyncProjectFile(string path)
    {
        ThrowIfDisposed();
        Marshal.ThrowExceptionForHR(_fileChange.SyncFile(path));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_eventSink is not null)
        {
            uint cookie = _eventCookie;
            _eventCookie = 0;
            _eventSink = null;
            try
            {
                Marshal.ThrowExceptionForHR(
                    _runningDocuments
                        .UnadviseRunningDocTableEvents(cookie));
            }
            catch
            {
                ComObject.Release(_runningDocuments);
                ComObject.Release(_fileChange);
                _disposed = true;
                throw;
            }
        }

        ComObject.Release(_runningDocuments);
        ComObject.Release(_fileChange);
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(XaeWorkspaceFileChangeBackend));
        }
    }

    private static T QueryService<T>(
        DTE2 dte,
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
            Guid providerIid =
                typeof(OleServiceProvider).GUID;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(
                    unknownPointer,
                    ref providerIid,
                    out providerPointer));
            serviceProvider = (OleServiceProvider)
                Marshal.GetTypedObjectForIUnknown(
                    providerPointer,
                    typeof(OleServiceProvider));
            Guid interfaceId = typeof(T).GUID;
            Guid serviceId = service;
            Marshal.ThrowExceptionForHR(
                serviceProvider.QueryService(
                    ref serviceId,
                    ref interfaceId,
                    out servicePointer));
            return (T)Marshal.GetObjectForIUnknown(servicePointer);
        }
        finally
        {
            if (servicePointer != IntPtr.Zero)
            {
                Marshal.Release(servicePointer);
            }

            ComObject.Release(serviceProvider);
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

    private sealed class RunningDocumentEventSink :
        IVsRunningDocTableEvents
    {
        private readonly Action<string> _documentOpened;
        private readonly Action<string> _documentClosing;
        private readonly Action<string?, Exception> _trackingFailed;
        private readonly IVsRunningDocumentTable _runningDocuments;

        public RunningDocumentEventSink(
            IVsRunningDocumentTable runningDocuments,
            Action<string> documentOpened,
            Action<string> documentClosing,
            Action<string?, Exception> trackingFailed)
        {
            _runningDocuments = runningDocuments;
            _documentOpened = documentOpened;
            _documentClosing = documentClosing;
            _trackingFailed = trackingFailed;
        }

        public int OnAfterFirstDocumentLock(
            uint docCookie,
            uint dwRDTLockType,
            uint dwReadLocksRemaining,
            uint dwEditLocksRemaining)
        {
            Notify(docCookie, _documentOpened);
            return 0;
        }

        public int OnBeforeLastDocumentUnlock(
            uint docCookie,
            uint dwRDTLockType,
            uint dwReadLocksRemaining,
            uint dwEditLocksRemaining)
        {
            Notify(docCookie, _documentClosing);
            return 0;
        }

        public int OnAfterSave(uint docCookie)
        {
            return 0;
        }

        public int OnAfterAttributeChange(
            uint docCookie,
            uint grfAttribs)
        {
            return 0;
        }

        public int OnBeforeDocumentWindowShow(
            uint docCookie,
            int fFirstShow,
            IVsWindowFrame pFrame)
        {
            return 0;
        }

        public int OnAfterDocumentWindowHide(
            uint docCookie,
            IVsWindowFrame pFrame)
        {
            return 0;
        }

        private void Notify(
            uint docCookie,
            Action<string> callback)
        {
            IVsHierarchy? hierarchy = null;
            IntPtr documentDataPointer = IntPtr.Zero;
            string? moniker = null;
            try
            {
                int result = _runningDocuments.GetDocumentInfo(
                    docCookie,
                    out _,
                    out _,
                    out _,
                    out moniker,
                    out hierarchy,
                    out _,
                    out documentDataPointer);
                if (result >= 0
                    && TryNormalizeFileMoniker(
                        moniker,
                        out string path))
                {
                    callback(path);
                }
            }
            catch (Exception exception)
            {
                _trackingFailed(moniker, exception);
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
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;
using OleServiceProvider =
    Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeExternalEditTests
{
    [XaeLaunchFact]
    public async Task BuildActionsCompleteFromTypedEvents()
    {
        string sourceSolution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using TemporarySolution copy =
            TemporarySolution.Create(sourceSolution);
        XaeSession session = new();
        try
        {
            XaeSessionSnapshot snapshot = await session.LaunchAsync(
                copy.SolutionPath,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);
            int processId = Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId);
            BuildAction[] actions =
            {
                BuildAction.Build,
                BuildAction.Clean,
                BuildAction.Rebuild,
            };
            foreach (BuildAction action in actions)
            {
                XaeBuildExecutionResult build =
                    await session.ExecuteBuildAsync(
                        action,
                        changedPaths: null,
                        TimeSpan.FromSeconds(60),
                        CancellationToken.None);

                Assert.Equal(action, build.Action);
                Assert.Equal(0, build.FailedProjects);
                Assert.Equal(
                    vsBuildState.vsBuildStateDone,
                    build.BuildState);
                Assert.Equal(
                    action == BuildAction.Clean
                        ? vsBuildAction.vsBuildActionClean
                        : action == BuildAction.Rebuild
                            ? vsBuildAction
                                .vsBuildActionRebuildAll
                            : vsBuildAction.vsBuildActionBuild,
                    build.EventAction);
                Assert.Empty(
                    XaeWindowProbe.FindModalDialogs(processId));
            }
        }
        finally
        {
            await CloseSessionAsync(session, copy);
        }
    }

    [XaeLaunchFact]
    public async Task FingerprintChangesAreSynchronizedBeforeBuild()
    {
        string sourceSolution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using TemporarySolution copy =
            TemporarySolution.Create(sourceSolution);
        string documentPath = Path.Combine(
            Path.GetDirectoryName(copy.SolutionPath)!,
            "TC3_SimpleProject",
            "PlcProject1",
            "POUs",
            "MAIN.TcPOU");
        using ComStaDispatcher dispatcher = new();
        XaeSession session = new(dispatcher);
        try
        {
            XaeSessionSnapshot snapshot = await session.LaunchAsync(
                copy.SolutionPath,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);
            int processId = Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId);
            string source = File.ReadAllText(documentPath);
            Assert.Contains(
                "bToggle := NOT bToggle;",
                source);
            File.WriteAllText(
                documentPath,
                source.Replace(
                    "bToggle := NOT bToggle;",
                    "bToggle := ;"));
            XaeBuildExecutionResult build =
                await session.ExecuteBuildAsync(
                    BuildAction.Build,
                    changedPaths: null,
                    TimeSpan.FromSeconds(60),
                    CancellationToken.None);
            Assert.False(
                await IsDocumentOpenAsync(
                    dispatcher,
                    processId,
                    documentPath));
            ProjectFileChange change = Assert.Single(
                build.Synchronization.DetectedChanges);
            Assert.Equal(
                ProjectFileChangeKind.Modified,
                change.Kind);
            Assert.Equal(documentPath, change.Path);
            Assert.Contains(
                documentPath,
                build.Synchronization.SynchronizedDocuments,
                StringComparer.OrdinalIgnoreCase);

            Assert.True(build.FailedProjects > 0);
            BuildDiagnostic diagnostic = Assert.Single(
                build.Diagnostics,
                item => item.Severity
                    == DiagnosticSeverity.Error
                    && string.Equals(
                        item.File,
                        documentPath,
                        StringComparison.OrdinalIgnoreCase));
            Assert.Equal(7, diagnostic.Line);
            Assert.Contains(
                "Expression expected",
                diagnostic.Message);
            Assert.Equal(
                vsBuildState.vsBuildStateDone,
                build.BuildState);
            Assert.Empty(
                XaeWindowProbe.FindModalDialogs(processId));
        }
        finally
        {
            await CloseSessionAsync(session, copy);
        }
    }

    [XaeLaunchFact]
    public async Task ExternalEditIsReportedUntilDiskMatchesBaseline()
    {
        string sourceSolution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using TemporarySolution copy =
            TemporarySolution.Create(sourceSolution);
        string documentPath = Path.Combine(
            Path.GetDirectoryName(copy.SolutionPath)!,
            "TC3_SimpleProject",
            "PlcProject1",
            "POUs",
            "MAIN.TcPOU");
        XaeSession session = new();
        try
        {
            await session.LaunchAsync(
                copy.SolutionPath,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);
            string source = File.ReadAllText(documentPath);
            File.WriteAllText(
                documentPath,
                source + Environment.NewLine);

            XaeSessionSnapshot changed =
                session.RefreshSynchronizationStatus(
                    CancellationToken.None);

            Assert.Equal(
                SynchronizationState.SyncRequired,
                changed.SynchronizationState);
            ProjectFileChange change = Assert.Single(
                changed.UnsynchronizedFiles);
            Assert.Equal(
                ProjectFileChangeKind.Modified,
                change.Kind);
            Assert.Equal(
                ProjectGraphFileRole.PlcSource,
                change.Role);
            Assert.Equal(documentPath, change.Path);

            File.WriteAllText(documentPath, source);
            XaeSessionSnapshot restored =
                session.RefreshSynchronizationStatus(
                    CancellationToken.None);

            Assert.Equal(
                SynchronizationState.Confirmed,
                restored.SynchronizationState);
            Assert.Empty(restored.UnsynchronizedFiles);
        }
        finally
        {
            await CloseSessionAsync(session, copy);
        }
    }

    private static async Task CloseSessionAsync(
        XaeSession session,
        TemporarySolution copy)
    {
        bool closed = await session.CloseGatewayLaunchedAsync(
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        if (!closed)
        {
            copy.Preserve();
        }

        session.Dispose();
    }

    private static Task<bool> IsDocumentOpenAsync(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath)
    {
        return dispatcher.InvokeAsync(
            () =>
            {
                using RotScanResult scan =
                    RunningObjectTableScanner.Scan(
                        requiredProcessId: processId);
                RunningXaeCandidate candidate =
                    Assert.Single(
                        scan.Candidates,
                        item => item.Info.ProcessId == processId);
                DTE2 dte = candidate.TakeDte();
                Documents? documents = null;
                try
                {
                    documents = dte.Documents;
                    int count = documents.Count;
                    for (int index = 1; index <= count; index++)
                    {
                        Document? document = null;
                        try
                        {
                            document = documents.Item(index);
                            if (string.Equals(
                                Path.GetFullPath(document.FullName),
                                documentPath,
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
                finally
                {
                    ComObject.Release(documents);
                    ComObject.Release(dte);
                }
            },
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
    }

    [XaeLaunchFact]
    public async Task OpenSavedDocumentCanBeReloadedWithoutModal()
    {
        string sourceSolution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using TemporarySolution copy =
            TemporarySolution.Create(sourceSolution);
        string documentPath = Path.Combine(
            Path.GetDirectoryName(copy.SolutionPath)!,
            "TC3_SimpleProject",
            "PlcProject1",
            "POUs",
            "MAIN.TcPOU");
        using ComStaDispatcher dispatcher = new();
        XaeSession session = new(dispatcher);
        try
        {
            XaeSessionSnapshot snapshot = await session.LaunchAsync(
                copy.SolutionPath,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);
            int processId = Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId);

            bool initiallySaved = await ReadDocumentSavedAsync(
                dispatcher,
                processId,
                documentPath,
                openIfMissing: true);
            string source = File.ReadAllText(documentPath);
            const string implementationMarker =
                "<ST><![CDATA[";
            Assert.Contains(
                implementationMarker,
                source);
            File.WriteAllText(
                documentPath,
                source.Replace(
                    implementationMarker,
                    implementationMarker
                        + "gatewayGuardInvalid := ;\r\n"));
            DateTimeOffset observationDeadline =
                DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < observationDeadline)
            {
                Assert.Empty(
                    XaeWindowProbe.FindModalDialogs(processId));
                await Task.Delay(100);
            }

            string[] dialogs =
                XaeWindowProbe.FindModalDialogs(processId);
            XaeBuildExecutionResult build =
                await session.ExecuteBuildAsync(
                    BuildAction.Build,
                    changedPaths: null,
                    TimeSpan.FromSeconds(60),
                    CancellationToken.None);

            Assert.True(initiallySaved);
            Assert.Empty(dialogs);
            Assert.True(build.FailedProjects > 0);
            Assert.Contains(
                build.Synchronization.SynchronizedDocuments,
                path => string.Equals(
                    path,
                    documentPath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Empty(
                XaeWindowProbe.FindModalDialogs(processId));
        }
        finally
        {
            await CloseSessionAsync(session, copy);
        }
    }

    [XaeLaunchFact]
    public async Task AttachReportsButDoesNotDiscardDirtyDocument()
    {
        string sourceSolution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using TemporarySolution copy =
            TemporarySolution.Create(sourceSolution);
        string documentPath = Path.Combine(
            Path.GetDirectoryName(copy.SolutionPath)!,
            "TC3_SimpleProject",
            "PlcProject1",
            "POUs",
            "MAIN.TcPOU");
        using ComStaDispatcher dispatcher = new();
        XaeSession session = new(dispatcher);
        try
        {
            XaeSessionSnapshot snapshot = await session.LaunchAsync(
                copy.SolutionPath,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);
            int processId = Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId);
            await ReadDocumentSavedAsync(
                dispatcher,
                processId,
                documentPath,
                openIfMissing: true);
            await SetDocumentSavedAsync(
                dispatcher,
                processId,
                documentPath,
                saved: false);

            bool dirty = await ReadDocumentDirtyAsync(
                dispatcher,
                processId,
                documentPath);
            using XaeSession attachedSession = new();
            XaeSessionSnapshot attached =
                await attachedSession.AttachAsync(
                    copy.SolutionPath,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);

            Assert.True(dirty);
            Assert.True(attached.AgentWorkspaceOwned);
            Assert.Equal(0, attached.ClosedDocumentCount);
            Assert.Equal(0, attached.DiscardedDocumentCount);
            Assert.Equal(1, attached.DirtyDocumentCount);
            Assert.Equal(
                SynchronizationState.SyncRequired,
                attached.SynchronizationState);
            GatewayOperationException blocked =
                await Assert.ThrowsAsync<GatewayOperationException>(
                    () => attachedSession.ExecuteBuildAsync(
                        BuildAction.Build,
                        changedPaths: null,
                        TimeSpan.FromSeconds(60),
                        CancellationToken.None));
            Assert.Equal(
                ErrorCodes.DirtyXaeDocument,
                blocked.Code);
            Assert.True(
                await ReadDocumentDirtyAsync(
                    dispatcher,
                    processId,
                    documentPath));
            Assert.True(
                await IsDocumentOpenAsync(
                    dispatcher,
                    processId,
                    documentPath));
            Assert.Empty(
                XaeWindowProbe.FindModalDialogs(processId));
        }
        finally
        {
            await CloseSessionAsync(session, copy);
        }
    }

    private static Task<bool> ReadDocumentDirtyAsync(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath)
    {
        return WithDocumentDataAsync(
            dispatcher,
            processId,
            documentPath,
            (persist, _) =>
            {
                Marshal.ThrowExceptionForHR(
                    persist.IsDocDataDirty(out int dirty));
                return dirty != 0;
            });
    }

    private static Task<bool> SetDocumentSavedAsync(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath,
        bool saved)
    {
        return WithDocumentAsync(
            dispatcher,
            processId,
            documentPath,
            document =>
            {
                document.Saved = saved;
                return true;
            });
    }

    private static Task<TResult> WithDocumentDataAsync<TResult>(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath,
        Func<
            IVsPersistDocData,
            IVsDocDataFileChangeControl,
            TResult> action)
    {
        return dispatcher.InvokeAsync(
            () =>
            {
                using RotScanResult scan =
                    RunningObjectTableScanner.Scan(
                        requiredProcessId: processId);
                RunningXaeCandidate candidate =
                    Assert.Single(
                        scan.Candidates,
                        item => item.Info.ProcessId == processId);
                DTE2 dte = candidate.TakeDte();
                IVsRunningDocumentTable? table = null;
                IVsHierarchy? hierarchy = null;
                IntPtr documentDataPointer = IntPtr.Zero;
                IntPtr persistPointer = IntPtr.Zero;
                IntPtr controlPointer = IntPtr.Zero;
                IVsPersistDocData? persist = null;
                IVsDocDataFileChangeControl? control = null;
                try
                {
                    table = QueryRunningDocumentTable(dte);
                    Marshal.ThrowExceptionForHR(
                        table.FindAndLockDocument(
                            (uint)_VSRDTFLAGS.RDT_NoLock,
                            documentPath,
                            out hierarchy,
                            out _,
                            out documentDataPointer,
                            out _));
                    Assert.NotEqual(
                        IntPtr.Zero,
                        documentDataPointer);
                    Guid persistIid =
                        typeof(IVsPersistDocData).GUID;
                    Marshal.ThrowExceptionForHR(
                        Marshal.QueryInterface(
                            documentDataPointer,
                            ref persistIid,
                            out persistPointer));
                    persist = (IVsPersistDocData)
                        Marshal.GetTypedObjectForIUnknown(
                            persistPointer,
                            typeof(IVsPersistDocData));
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
                    return action(persist, control);
                }
                finally
                {
                    ComObject.Release(control);
                    ComObject.Release(persist);
                    if (controlPointer != IntPtr.Zero)
                    {
                        Marshal.Release(controlPointer);
                    }

                    if (persistPointer != IntPtr.Zero)
                    {
                        Marshal.Release(persistPointer);
                    }

                    if (documentDataPointer != IntPtr.Zero)
                    {
                        Marshal.Release(documentDataPointer);
                    }

                    ComObject.Release(hierarchy);
                    ComObject.Release(table);
                    ComObject.Release(dte);
                }
            },
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
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
            Marshal.ThrowExceptionForHR(
                serviceProvider.QueryService(
                    ref service,
                    ref iid,
                    out servicePointer));
            Assert.NotEqual(IntPtr.Zero, servicePointer);
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

    private static Task<bool> ReadDocumentSavedAsync(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath,
        bool openIfMissing)
    {
        return WithDocumentAsync(
            dispatcher,
            processId,
            documentPath,
            document => document.Saved,
            openIfMissing);
    }

    private static Task<TResult> WithDocumentAsync<TResult>(
        ComStaDispatcher dispatcher,
        int processId,
        string documentPath,
        Func<Document, TResult> action,
        bool openIfMissing = false)
    {
        return dispatcher.InvokeAsync(
            () =>
            {
                using RotScanResult scan =
                    RunningObjectTableScanner.Scan(
                        requiredProcessId: processId);
                RunningXaeCandidate candidate =
                    Assert.Single(
                        scan.Candidates,
                        item => item.Info.ProcessId == processId);
                DTE2 dte = candidate.TakeDte();
                Documents? documents = null;
                Document? document = null;
                try
                {
                    documents = dte.Documents;
                    int count = documents.Count;
                    for (int index = 1; index <= count; index++)
                    {
                        Document? candidateDocument = null;
                        try
                        {
                            candidateDocument =
                                documents.Item(index);
                            if (string.Equals(
                                Path.GetFullPath(
                                    candidateDocument.FullName),
                                documentPath,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                document = candidateDocument;
                                candidateDocument = null;
                                break;
                            }
                        }
                        finally
                        {
                            ComObject.Release(candidateDocument);
                        }
                    }

                    if (document is null && openIfMissing)
                    {
                        document = documents.Open(documentPath);
                    }

                    Assert.NotNull(document);
                    return action(document);
                }
                finally
                {
                    ComObject.Release(document);
                    ComObject.Release(documents);
                    ComObject.Release(dte);
                }
            },
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
    }

    private sealed class TemporarySolution : IDisposable
    {
        private TemporarySolution(
            string root,
            string solutionPath)
        {
            Root = root;
            SolutionPath = solutionPath;
        }

        public string Root { get; }

        public string SolutionPath { get; }

        private bool PreserveOnDispose { get; set; }

        public static TemporarySolution Create(
            string sourceSolution)
        {
            string sourceRoot =
                Path.GetDirectoryName(sourceSolution)!;
            string root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            CopyDirectory(sourceRoot, root);
            return new TemporarySolution(
                root,
                Path.Combine(
                    root,
                    Path.GetFileName(sourceSolution)));
        }

        public void Dispose()
        {
            if (!PreserveOnDispose && Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public void Preserve()
        {
            PreserveOnDispose = true;
        }

        private static void CopyDirectory(
            string source,
            string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                if (file.EndsWith(
                    ".project.~u",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(
                    file,
                    Path.Combine(
                        destination,
                        Path.GetFileName(file)));
            }

            foreach (string directory in
                Directory.GetDirectories(source))
            {
                string directoryName =
                    Path.GetFileName(directory);
                if (IsGeneratedDirectory(directoryName))
                {
                    continue;
                }

                CopyDirectory(
                    directory,
                    Path.Combine(
                        destination,
                        directoryName));
            }
        }

        private static bool IsGeneratedDirectory(
            string directoryName)
        {
            return string.Equals(
                    directoryName,
                    ".vs",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    directoryName,
                    "_Boot",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    directoryName,
                    "_CompileInfo",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    directoryName,
                    "_Config",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    directoryName,
                    "_Libraries",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}

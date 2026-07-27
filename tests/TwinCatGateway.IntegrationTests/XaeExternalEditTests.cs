using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeExternalEditTests
{
    [XaeLaunchFact]
    public async Task OpenSavedDocumentExternalEditDoesNotPresentModal()
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
            File.AppendAllText(
                documentPath,
                Environment.NewLine + "<!-- external edit probe -->");
            DateTimeOffset observationDeadline =
                DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < observationDeadline)
            {
                Assert.Empty(
                    XaeWindowProbe.FindModalDialogs(processId));
                await Task.Delay(100);
            }

            bool savedAfterEdit = await ReadDocumentSavedAsync(
                dispatcher,
                processId,
                documentPath,
                openIfMissing: false);
            string[] dialogs =
                XaeWindowProbe.FindModalDialogs(processId);

            Assert.True(initiallySaved);
            Assert.True(savedAfterEdit);
            Assert.Empty(dialogs);
        }
        finally
        {
            await session.CloseGatewayLaunchedAsync(
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            session.Dispose();
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
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void CopyDirectory(
            string source,
            string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(
                    file,
                    Path.Combine(
                        destination,
                        Path.GetFileName(file)));
            }

            foreach (string directory in
                Directory.GetDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(
                        destination,
                        Path.GetFileName(directory)));
            }
        }
    }
}

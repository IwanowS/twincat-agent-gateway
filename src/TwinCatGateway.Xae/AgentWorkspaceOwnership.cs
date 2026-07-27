using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class AgentWorkspaceOwnershipResult
{
    internal AgentWorkspaceOwnershipResult(
        IEnumerable<string> closedDocuments,
        IEnumerable<string> discardedDocuments)
    {
        ClosedDocuments = closedDocuments.ToArray();
        DiscardedDocuments = discardedDocuments.ToArray();
    }

    public IReadOnlyList<string> ClosedDocuments { get; }

    public IReadOnlyList<string> DiscardedDocuments { get; }
}

internal static class AgentWorkspaceOwnership
{
    public static AgentWorkspaceOwnershipResult Acquire(
        DTE2 dte,
        string solutionPath)
    {
        if (dte is null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        string solutionRoot = Path.GetDirectoryName(
            Path.GetFullPath(solutionPath))
            ?? throw new ArgumentException(
                "Solution path has no parent directory.",
                nameof(solutionPath));
        string rootPrefix = solutionRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        List<string> closed = new();
        List<string> discarded = new();
        Documents? documents = null;
        try
        {
            documents = dte.Documents;
            for (int index = documents.Count; index >= 1; index--)
            {
                Document? document = null;
                try
                {
                    document = documents.Item(index);
                    string? path = TryGetOwnedSourcePath(
                        document,
                        rootPrefix);
                    if (path is null)
                    {
                        continue;
                    }

                    bool wasSaved = document.Saved;
                    document.Close(vsSaveChanges.vsSaveChangesNo);
                    closed.Add(path);
                    if (!wasSaved)
                    {
                        discarded.Add(path);
                    }
                }
                catch (Exception exception)
                {
                    throw new GatewayOperationException(
                        ErrorCodes.XaeWorkspaceOwnershipFailed,
                        "XAE project editors could not be closed "
                        + "without saving.",
                        retryable: true,
                        stage: "xae.workspace.acquire",
                        innerException: exception);
                }
                finally
                {
                    ComObject.Release(document);
                }
            }

            string[] stillOpen = FindOpenOwnedSources(
                documents,
                rootPrefix);
            if (stillOpen.Length != 0)
            {
                throw new GatewayOperationException(
                    ErrorCodes.XaeWorkspaceOwnershipFailed,
                    "One or more XAE project editors remained open.",
                    retryable: true,
                    stage: "xae.workspace.acquire");
            }

            return new AgentWorkspaceOwnershipResult(
                closed.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase),
                discarded.OrderBy(
                    path => path,
                    StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            ComObject.Release(documents);
        }
    }

    private static string[] FindOpenOwnedSources(
        Documents documents,
        string rootPrefix)
    {
        List<string> paths = new();
        int count = documents.Count;
        for (int index = 1; index <= count; index++)
        {
            Document? document = null;
            try
            {
                document = documents.Item(index);
                string? path = TryGetOwnedSourcePath(
                    document,
                    rootPrefix);
                if (path is not null)
                {
                    paths.Add(path);
                }
            }
            finally
            {
                ComObject.Release(document);
            }
        }

        return paths.ToArray();
    }

    private static string? TryGetOwnedSourcePath(
        Document document,
        string rootPrefix)
    {
        string? path = document.FullName;
        if (string.IsNullOrWhiteSpace(path)
            || !ProjectFileFingerprintScanner.IsSupportedPath(path))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(
            rootPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }
}

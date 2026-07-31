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
    public static bool HasAnyDirtyDocument(DTE2 dte)
    {
        if (dte is null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        Documents? documents = null;
        try
        {
            documents = dte.Documents;
            for (int index = 1; index <= documents.Count; index++)
            {
                Document? document = null;
                try
                {
                    document = documents.Item(index);
                    if (!document.Saved)
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
        }
    }

    public static IReadOnlyList<string> FindDirtyDocuments(
        DTE2 dte,
        IEnumerable<string> projectGraphPaths)
    {
        HashSet<string> graph = new(
            projectGraphPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        List<string> dirty = new();
        Documents? documents = null;
        try
        {
            documents = dte.Documents;
            for (int index = 1; index <= documents.Count; index++)
            {
                Document? document = null;
                try
                {
                    document = documents.Item(index);
                    string? path = NormalizeOptionalPath(
                        document.FullName);
                    if (path is not null
                        && graph.Contains(path)
                        && !document.Saved)
                    {
                        dirty.Add(path);
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

        return dirty
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static AgentWorkspaceOwnershipResult EnsureClean(
        DTE2 dte,
        IEnumerable<string> projectGraphPaths,
        bool discardDirtyDocuments)
    {
        if (dte is null)
        {
            throw new ArgumentNullException(nameof(dte));
        }

        HashSet<string> graph = new(
            projectGraphPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        List<string> dirty = new();
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
                    string? path = NormalizeOptionalPath(
                        document.FullName);
                    if (path is null
                        || !graph.Contains(path)
                        || document.Saved)
                    {
                        continue;
                    }

                    dirty.Add(path);
                    if (discardDirtyDocuments)
                    {
                        document.Close(
                            vsSaveChanges.vsSaveChangesNo);
                    }
                }
                finally
                {
                    ComObject.Release(document);
                }
            }
        }
        catch (GatewayOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeWorkspaceOwnershipFailed,
                "XAE dirty-document state could not be inspected.",
                retryable: true,
                stage: "xae.workspace.dirty",
                innerException: exception);
        }
        finally
        {
            ComObject.Release(documents);
        }

        string[] ordered = dirty
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ordered.Length != 0 && !discardDirtyDocuments)
        {
            throw new GatewayOperationException(
                ErrorCodes.DirtyXaeDocument,
                ordered.Length == 1
                    ? $"XAE document has unsaved changes: '{ordered[0]}'."
                    : $"{ordered.Length} XAE documents have unsaved changes.",
                retryable: false,
                stage: "xae.workspace.dirty");
        }

        return new AgentWorkspaceOwnershipResult(
            Array.Empty<string>(),
            discardDirtyDocuments
                ? ordered
                : Array.Empty<string>());
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }
}

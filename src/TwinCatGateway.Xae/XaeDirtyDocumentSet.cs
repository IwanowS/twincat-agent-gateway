using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TwinCatGateway.Xae;

internal static class XaeDirtyDocumentSet
{
    public static IReadOnlyList<string> MergeProjectGraphPaths(
        IEnumerable<string> projectGraphPaths,
        IEnumerable<string> dteDirtyPaths,
        IEnumerable<string> runningDocumentDirtyPaths)
    {
        HashSet<string> graph = new(
            projectGraphPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        return dteDirtyPaths
            .Concat(runningDocumentDirtyPaths)
            .Select(Path.GetFullPath)
            .Where(graph.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

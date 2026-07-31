using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class TwinCatProjectGraphEntry
{
    internal TwinCatProjectGraphEntry(
        string path,
        ProjectGraphFileRole role,
        string project,
        string projectFile,
        SourceEntryKind kind,
        bool exists,
        bool outsideSolutionDirectory)
    {
        Path = path;
        Role = role;
        Project = project;
        ProjectFile = projectFile;
        Kind = kind;
        Exists = exists;
        OutsideSolutionDirectory = outsideSolutionDirectory;
    }

    public string Path { get; }

    public ProjectGraphFileRole Role { get; }

    public string Project { get; }

    public string ProjectFile { get; }

    public SourceEntryKind Kind { get; }

    public bool Exists { get; }

    public bool OutsideSolutionDirectory { get; }
}

public sealed class TwinCatProjectGraphSnapshot
{
    internal TwinCatProjectGraphSnapshot(
        string solutionPath,
        string twinCatProjectPath,
        IEnumerable<TwinCatProjectGraphEntry> entries,
        bool isComplete)
    {
        SolutionPath = solutionPath;
        TwinCatProjectPath = twinCatProjectPath;
        SolutionDirectory = Path.GetDirectoryName(solutionPath)
            ?? string.Empty;
        Entries = new ReadOnlyCollection<TwinCatProjectGraphEntry>(
            entries
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    entry => entry.Project,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Role)
                .ToArray());
        IsComplete = isComplete;
    }

    public string SolutionPath { get; }

    public string TwinCatProjectPath { get; }

    public string SolutionDirectory { get; }

    public IReadOnlyList<TwinCatProjectGraphEntry> Entries { get; }

    public bool IsComplete { get; }
}

public static class TwinCatProjectGraphResolver
{
    private static readonly HashSet<string> SupportedSourceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".TcDUT",
            ".TcGVL",
            ".TcPOU",
        };

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new ReadOnlyCollection<string>(
            SupportedSourceExtensions
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    public static TwinCatProjectGraphSnapshot Resolve(
        string solutionPath,
        string twinCatProjectPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException(
                "Solution path is required.",
                nameof(solutionPath));
        }

        if (string.IsNullOrWhiteSpace(twinCatProjectPath))
        {
            throw new ArgumentException(
                "TwinCAT project path is required.",
                nameof(twinCatProjectPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string fullSolutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullSolutionPath))
        {
            throw new FileNotFoundException(
                "Solution file was not found.",
                fullSolutionPath);
        }

        string fullTwinCatProjectPath =
            Path.GetFullPath(twinCatProjectPath);
        string solutionDirectory =
            Path.GetDirectoryName(fullSolutionPath)
            ?? throw new ArgumentException(
                "Solution path has no parent directory.",
                nameof(solutionPath));
        List<TwinCatProjectGraphEntry> entries = new();
        bool complete = File.Exists(fullTwinCatProjectPath);
        AddEntry(
            entries,
            fullTwinCatProjectPath,
            ProjectGraphFileRole.TwinCatProject,
            Path.GetFileNameWithoutExtension(fullTwinCatProjectPath),
            fullTwinCatProjectPath,
            SourceEntryKind.Unsupported,
            solutionDirectory);
        if (!complete)
        {
            return new TwinCatProjectGraphSnapshot(
                fullSolutionPath,
                fullTwinCatProjectPath,
                entries,
                isComplete: false);
        }

        XDocument twinCatProject = LoadXml(
            fullTwinCatProjectPath,
            cancellationToken);
        foreach (XElement project in twinCatProject
            .Descendants()
            .Where(element =>
                string.Equals(
                    element.Name.LocalName,
                    "Project",
                    StringComparison.Ordinal)
                && element.Attribute("PrjFilePath") is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reference =
                ((string?)project.Attribute("PrjFilePath"))!;
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            string plcProjectPath = ResolveReference(
                fullTwinCatProjectPath,
                reference);
            string projectName =
                ((string?)project.Attribute("Name"))?.Trim()
                ?? string.Empty;
            if (projectName.Length == 0)
            {
                projectName =
                    Path.GetFileNameWithoutExtension(plcProjectPath);
            }

            AddEntry(
                entries,
                plcProjectPath,
                ProjectGraphFileRole.PlcProject,
                projectName,
                plcProjectPath,
                SourceEntryKind.Unsupported,
                solutionDirectory);
            if (!File.Exists(plcProjectPath))
            {
                complete = false;
                AddTwinCatGeneratedEntries(
                    entries,
                    project,
                    fullTwinCatProjectPath,
                    projectName,
                    plcProjectPath,
                    solutionDirectory);
                continue;
            }

            AddTwinCatGeneratedEntries(
                entries,
                project,
                fullTwinCatProjectPath,
                projectName,
                plcProjectPath,
                solutionDirectory);
            XDocument plcProject = LoadXml(
                plcProjectPath,
                cancellationToken);
            foreach (XElement item in plcProject.Descendants())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string itemType = item.Name.LocalName;
                if (!string.Equals(
                        itemType,
                        "Compile",
                        StringComparison.Ordinal)
                    && !string.Equals(
                        itemType,
                        "None",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string? include =
                    (string?)item.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                string itemPath = ResolveReference(
                    plcProjectPath,
                    include!);
                string extension = Path.GetExtension(itemPath);
                SourceEntryKind kind;
                ProjectGraphFileRole role;
                if (string.Equals(
                        itemType,
                        "Compile",
                        StringComparison.Ordinal)
                    && IsSupportedSourcePath(itemPath))
                {
                    kind = SourceEntryKind.Editable;
                    role = ProjectGraphFileRole.PlcSource;
                    if (!File.Exists(itemPath))
                    {
                        complete = false;
                    }
                }
                else if (string.Equals(
                    extension,
                    ".tmc",
                    StringComparison.OrdinalIgnoreCase))
                {
                    kind = SourceEntryKind.Generated;
                    role = ProjectGraphFileRole.GeneratedArtifact;
                }
                else
                {
                    kind = SourceEntryKind.Unsupported;
                    role = ProjectGraphFileRole.PlcSource;
                }

                AddEntry(
                    entries,
                    itemPath,
                    role,
                    projectName,
                    plcProjectPath,
                    kind,
                    solutionDirectory);
            }
        }

        return new TwinCatProjectGraphSnapshot(
            fullSolutionPath,
            fullTwinCatProjectPath,
            DistinctEntries(entries),
            complete);
    }

    public static bool IsSupportedSourcePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && SupportedSourceExtensions.Contains(
                Path.GetExtension(path));
    }

    private static void AddTwinCatGeneratedEntries(
        ICollection<TwinCatProjectGraphEntry> entries,
        XElement project,
        string twinCatProjectPath,
        string projectName,
        string plcProjectPath,
        string solutionDirectory)
    {
        foreach (XAttribute attribute in project
            .DescendantsAndSelf()
            .Attributes()
            .Where(attribute =>
                string.Equals(
                    attribute.Name.LocalName,
                    "TmcFilePath",
                    StringComparison.Ordinal)
                || string.Equals(
                    attribute.Name.LocalName,
                    "TmcPath",
                    StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(attribute.Value))
            {
                continue;
            }

            AddEntry(
                entries,
                ResolveReference(
                    twinCatProjectPath,
                    attribute.Value),
                ProjectGraphFileRole.GeneratedArtifact,
                projectName,
                plcProjectPath,
                SourceEntryKind.Generated,
                solutionDirectory);
        }
    }

    private static void AddEntry(
        ICollection<TwinCatProjectGraphEntry> entries,
        string path,
        ProjectGraphFileRole role,
        string project,
        string projectFile,
        SourceEntryKind kind,
        string solutionDirectory)
    {
        string fullPath = Path.GetFullPath(path);
        entries.Add(
            new TwinCatProjectGraphEntry(
                fullPath,
                role,
                project,
                Path.GetFullPath(projectFile),
                kind,
                File.Exists(fullPath),
                !IsInsideRoot(fullPath, solutionDirectory)));
    }

    private static IEnumerable<TwinCatProjectGraphEntry> DistinctEntries(
        IEnumerable<TwinCatProjectGraphEntry> entries)
    {
        return entries
            .GroupBy(
                entry => string.Join(
                    "\0",
                    entry.Path,
                    entry.Project,
                    entry.ProjectFile,
                    entry.Role.ToString()),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static XDocument LoadXml(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        XDocument document = XDocument.Load(
            stream,
            LoadOptions.PreserveWhitespace);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private static string ResolveReference(
        string projectPath,
        string reference)
    {
        string normalized = reference
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
        return Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(projectPath)
                    ?? string.Empty,
                normalized));
    }

    private static bool IsInsideRoot(
        string path,
        string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        return string.Equals(
                fullPath,
                fullRoot,
                StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
    }
}

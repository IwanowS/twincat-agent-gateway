using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class SourceManifestStore
{
    private readonly object _sync = new();
    private readonly string _profile;
    private readonly string _solutionPath;
    private readonly string _filesRef;
    private readonly IClock _clock;
    private SourceManifest _manifest;
    private SourceFileEntry[] _files = Array.Empty<SourceFileEntry>();

    public SourceManifestStore(
        string profile,
        string solutionPath,
        IClock? clock = null)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new ArgumentException(
                "Profile name is required.",
                nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException(
                "Solution path is required.",
                nameof(solutionPath));
        }

        _profile = profile;
        _solutionPath = Path.GetFullPath(solutionPath);
        _filesRef = GatewayResourceUris.ProfileSourceFiles(profile);
        _clock = clock ?? SystemClock.Instance;
        _manifest = CreateEmpty(SourceDiscoveryState.Unknown, error: null);
    }

    public SourceDiscoveryState DiscoveryState
    {
        get
        {
            lock (_sync)
            {
                return _manifest.DiscoveryState;
            }
        }
    }

    public string Profile => _profile;

    public SourceManifest ReadManifest()
    {
        lock (_sync)
        {
            return CloneManifest(_manifest);
        }
    }

    public IReadOnlyList<SourceFileEntry> ReadFiles()
    {
        lock (_sync)
        {
            return _files.Select(CloneFile).ToArray();
        }
    }

    public void Refresh(TwinCatProjectGraphSnapshot graph)
    {
        if (graph is null)
        {
            throw new ArgumentNullException(nameof(graph));
        }

        if (!string.Equals(
            Path.GetFullPath(graph.SolutionPath),
            _solutionPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The project graph belongs to another solution.",
                nameof(graph));
        }

        SourceFileEntry[] files = graph.Entries
            .Select(entry => new SourceFileEntry
            {
                Path = entry.Path,
                Role = GetRole(entry.Role),
                Project = entry.Project,
                Exists = entry.Exists,
                OutsideSolutionDirectory =
                    entry.OutsideSolutionDirectory,
                Kind = entry.Kind,
            })
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                entry => entry.Project,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Kind)
            .ToArray();
        SourceDiscoveryState state = graph.IsComplete
            ? SourceDiscoveryState.Confirmed
            : SourceDiscoveryState.Incomplete;
        ObservationError? error = graph.IsComplete
            ? null
            : new ObservationError
            {
                Code = ErrorCodes.ProjectGraphInvalid,
                Message =
                    "One or more required project graph files are missing.",
                Retryable = true,
            };
        SourceManifest manifest = new()
        {
            Profile = _profile,
            DiscoveryState = state,
            SolutionDirectory = graph.SolutionDirectory,
            Roots = CreateRoots(graph),
            FileCount = files.Length,
            FilesRef = _filesRef,
            ObservedAtUtc = _clock.UtcNow,
            Error = error,
        };

        lock (_sync)
        {
            _files = files;
            _manifest = manifest;
        }
    }

    public void MarkStale(
        string code,
        string message,
        bool retryable = true)
    {
        ObservationError error = CreateError(
            code,
            message,
            retryable);
        lock (_sync)
        {
            SourceManifest stale = CloneManifest(_manifest);
            stale.DiscoveryState = SourceDiscoveryState.Stale;
            stale.ObservedAtUtc = _clock.UtcNow;
            stale.Error = error;
            _manifest = stale;
        }
    }

    public void MarkUnavailable(
        string code,
        string message,
        bool retryable = true)
    {
        ObservationError error = CreateError(
            code,
            message,
            retryable);
        lock (_sync)
        {
            _files = Array.Empty<SourceFileEntry>();
            _manifest = CreateEmpty(
                SourceDiscoveryState.Unavailable,
                error);
        }
    }

    private SourceManifest CreateEmpty(
        SourceDiscoveryState state,
        ObservationError? error)
    {
        return new SourceManifest
        {
            Profile = _profile,
            DiscoveryState = state,
            SolutionDirectory =
                Path.GetDirectoryName(_solutionPath) ?? string.Empty,
            Roots = new List<SourceRootEntry>(),
            FileCount = 0,
            FilesRef = _filesRef,
            ObservedAtUtc = _clock.UtcNow,
            Error = error,
        };
    }

    private static List<SourceRootEntry> CreateRoots(
        TwinCatProjectGraphSnapshot graph)
    {
        List<SourceRootEntry> roots = new();
        foreach (IGrouping<string, TwinCatProjectGraphEntry> project
            in graph.Entries
                .Where(entry =>
                    entry.Kind == SourceEntryKind.Editable)
                .GroupBy(
                    entry => string.Join(
                        "\0",
                        entry.Project,
                        entry.ProjectFile),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    group => group.Key,
                    StringComparer.OrdinalIgnoreCase))
        {
            TwinCatProjectGraphEntry first = project.First();
            string[] directories = project
                .Select(entry =>
                    Path.GetDirectoryName(entry.Path)
                    ?? graph.SolutionDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            List<string> minimal = new();
            foreach (string directory in directories)
            {
                if (!minimal.Any(root =>
                    IsInsideRoot(directory, root)))
                {
                    minimal.Add(directory);
                }
            }

            foreach (string root in minimal)
            {
                string[] extensions = project
                    .Where(entry => IsInsideRoot(entry.Path, root))
                    .Select(entry => Path.GetExtension(entry.Path))
                    .Where(extension => extension.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        extension => extension,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                roots.Add(
                    new SourceRootEntry
                    {
                        Path = root,
                        Role = "plc-source",
                        Project = first.Project,
                        ProjectFile = first.ProjectFile,
                        Exists = Directory.Exists(root),
                        OutsideSolutionDirectory =
                            !IsInsideRoot(
                                root,
                                graph.SolutionDirectory),
                        Extensions = extensions.ToList(),
                    });
            }
        }

        return roots
            .OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(root => root.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetRole(ProjectGraphFileRole role)
    {
        return role switch
        {
            ProjectGraphFileRole.TwinCatProject =>
                "twincat-project",
            ProjectGraphFileRole.PlcProject => "plc-project",
            ProjectGraphFileRole.PlcSource => "plc-source",
            ProjectGraphFileRole.GeneratedArtifact =>
                "generated-artifact",
            _ => "unknown",
        };
    }

    private static ObservationError CreateError(
        string code,
        string message,
        bool retryable)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "Error code is required.",
                nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Error message is required.",
                nameof(message));
        }

        return new ObservationError
        {
            Code = code,
            Message = message,
            Retryable = retryable,
        };
    }

    private static bool IsInsideRoot(string path, string root)
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

    private static SourceManifest CloneManifest(SourceManifest source)
    {
        return new SourceManifest
        {
            Profile = source.Profile,
            DiscoveryState = source.DiscoveryState,
            SolutionDirectory = source.SolutionDirectory,
            Roots = source.Roots.Select(CloneRoot).ToList(),
            FileCount = source.FileCount,
            FilesRef = source.FilesRef,
            ObservedAtUtc = source.ObservedAtUtc,
            Error = CloneError(source.Error),
        };
    }

    private static SourceRootEntry CloneRoot(SourceRootEntry source)
    {
        return new SourceRootEntry
        {
            Path = source.Path,
            Role = source.Role,
            Project = source.Project,
            ProjectFile = source.ProjectFile,
            Exists = source.Exists,
            OutsideSolutionDirectory =
                source.OutsideSolutionDirectory,
            Extensions = source.Extensions.ToList(),
        };
    }

    private static SourceFileEntry CloneFile(SourceFileEntry source)
    {
        return new SourceFileEntry
        {
            Path = source.Path,
            Role = source.Role,
            Project = source.Project,
            Exists = source.Exists,
            OutsideSolutionDirectory =
                source.OutsideSolutionDirectory,
            Kind = source.Kind,
        };
    }

    private static ObservationError? CloneError(
        ObservationError? source)
    {
        return source is null
            ? null
            : new ObservationError
            {
                Code = source.Code,
                Message = source.Message,
                Retryable = source.Retryable,
            };
    }
}

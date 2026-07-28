using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class XaeAcceptedProjectGraphChanges
{
    internal XaeAcceptedProjectGraphChanges(
        ProjectFileFingerprintSnapshot snapshot,
        IEnumerable<ProjectFileChange> changes,
        int watcherEventCount,
        bool watcherOverflow,
        long settleDurationMs)
    {
        Snapshot = snapshot;
        Changes = changes.ToArray();
        WatcherEventCount = watcherEventCount;
        WatcherOverflow = watcherOverflow;
        SettleDurationMs = settleDurationMs;
    }

    public IReadOnlyList<ProjectFileChange> Changes { get; }

    public int WatcherEventCount { get; }

    public bool WatcherOverflow { get; }

    public long SettleDurationMs { get; }

    internal ProjectFileFingerprintSnapshot Snapshot { get; }
}

public sealed class XaeProjectGraphChangeScope : IDisposable
{
    private static readonly TimeSpan DefaultQuietPeriod =
        TimeSpan.FromMilliseconds(500);
    private readonly object _sync = new();
    private readonly ProjectFileFingerprintSnapshot _baseline;
    private readonly FileSystemWatcher[] _watchers;
    private readonly TimeSpan _quietPeriod;
    private TaskCompletionSource<long> _activity =
        CreateActivitySource();
    private DateTimeOffset _lastActivityUtc;
    private long _activityVersion;
    private int _watcherEventCount;
    private bool _watcherOverflow;
    private int _disposed;

    internal XaeProjectGraphChangeScope(
        string solutionPath,
        string twinCatProjectPath,
        ProjectFileFingerprintSnapshot baseline)
        : this(
            solutionPath,
            twinCatProjectPath,
            baseline,
            DefaultQuietPeriod)
    {
    }

    internal XaeProjectGraphChangeScope(
        string solutionPath,
        string twinCatProjectPath,
        ProjectFileFingerprintSnapshot baseline,
        TimeSpan quietPeriod)
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

        _baseline = baseline
            ?? throw new ArgumentNullException(nameof(baseline));
        if (quietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quietPeriod));
        }

        SolutionPath = Path.GetFullPath(solutionPath);
        TwinCatProjectPath = Path.GetFullPath(twinCatProjectPath);
        _quietPeriod = quietPeriod;
        _lastActivityUtc = DateTimeOffset.UtcNow;
        _watchers = CreateWatchers(
            SolutionPath,
            TwinCatProjectPath,
            baseline);
        try
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string SolutionPath { get; }

    internal string TwinCatProjectPath { get; }

    internal async Task<XaeAcceptedProjectGraphChanges> SettleAsync(
        Func<CancellationToken, ProjectFileFingerprintSnapshot> capture,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (capture is null)
        {
            throw new ArgumentNullException(nameof(capture));
        }

        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivityState activity = ReadActivity();
            TimeSpan quietRemaining =
                _quietPeriod
                - (DateTimeOffset.UtcNow - activity.LastActivityUtc);
            if (quietRemaining > TimeSpan.Zero)
            {
                await WaitForActivityAsync(
                    activity.Signal,
                    quietRemaining,
                    timeout - stopwatch.Elapsed,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            ProjectFileFingerprintSnapshot current;
            try
            {
                current = capture(cancellationToken);
            }
            catch (IOException)
            {
                RecordActivity(overflow: false);
                EnsureTimeRemaining(timeout, stopwatch.Elapsed);
                continue;
            }

            ActivityState verified = ReadActivity();
            if (verified.Version != activity.Version
                || DateTimeOffset.UtcNow - verified.LastActivityUtc
                    < _quietPeriod)
            {
                EnsureTimeRemaining(timeout, stopwatch.Elapsed);
                continue;
            }

            return new XaeAcceptedProjectGraphChanges(
                current,
                ProjectFileFingerprintScanner.Compare(
                    _baseline,
                    current),
                verified.EventCount,
                verified.Overflow,
                stopwatch.ElapsedMilliseconds);
        }
    }

    internal void NotifyWatcherOverflow()
    {
        RecordActivity(overflow: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private FileSystemWatcher[] CreateWatchers(
        string solutionPath,
        string twinCatProjectPath,
        ProjectFileFingerprintSnapshot baseline)
    {
        string solutionRoot = Path.GetDirectoryName(solutionPath)
            ?? throw new ArgumentException(
                "Solution path has no parent directory.",
                nameof(solutionPath));
        IEnumerable<string> candidateRoots = baseline.Files
            .Select(file => Path.GetDirectoryName(file.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Concat(
                new[]
                {
                    solutionRoot,
                    Path.GetDirectoryName(twinCatProjectPath)
                        ?? solutionRoot,
                })
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length);
        List<string> roots = new();
        foreach (string candidate in candidateRoots)
        {
            if (roots.Any(root => IsInsideRoot(candidate, root)))
            {
                continue;
            }

            roots.Add(candidate);
        }

        return roots
            .Select(root =>
            {
                FileSystemWatcher watcher = new(root)
                {
                    Filter = "*",
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter =
                        NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                return watcher;
            })
            .ToArray();

        void OnChanged(
            object sender,
            FileSystemEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            RecordActivity(overflow: false);
        }

        void OnRenamed(
            object sender,
            RenamedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            RecordActivity(overflow: false);
        }

        void OnError(
            object sender,
            ErrorEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            NotifyWatcherOverflow();
        }
    }

    private static bool IsInsideRoot(
        string path,
        string root)
    {
        string prefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return string.Equals(
                path,
                root,
                StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private void RecordActivity(bool overflow)
    {
        TaskCompletionSource<long> signal;
        long version;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            version = ++_activityVersion;
            _lastActivityUtc = DateTimeOffset.UtcNow;
            _watcherEventCount++;
            _watcherOverflow |= overflow;
            signal = _activity;
            _activity = CreateActivitySource();
        }

        signal.TrySetResult(version);
    }

    private ActivityState ReadActivity()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new ActivityState(
                _activityVersion,
                _lastActivityUtc,
                _watcherEventCount,
                _watcherOverflow,
                _activity.Task);
        }
    }

    private static async Task WaitForActivityAsync(
        Task activity,
        TimeSpan quietRemaining,
        TimeSpan timeoutRemaining,
        CancellationToken cancellationToken)
    {
        EnsureTimeRemaining(timeoutRemaining, TimeSpan.Zero);
        TimeSpan delay = quietRemaining < timeoutRemaining
            ? quietRemaining
            : timeoutRemaining;
        Task timer = Task.Delay(delay, cancellationToken);
        Task completed = await Task.WhenAny(
            activity,
            timer).ConfigureAwait(false);
        if (completed == activity)
        {
            await activity.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (delay == timeoutRemaining)
        {
            throw CreateSettleTimeout();
        }
    }

    private static void EnsureTimeRemaining(
        TimeSpan timeout,
        TimeSpan elapsed)
    {
        if (timeout - elapsed <= TimeSpan.Zero)
        {
            throw CreateSettleTimeout();
        }
    }

    private static GatewayOperationException CreateSettleTimeout()
    {
        return new GatewayOperationException(
            ErrorCodes.OperationTimeout,
            "XAE project files did not become quiet before the "
                + "operation deadline.",
            retryable: true,
            stage: "xae.workspace.settle");
    }

    private static TaskCompletionSource<long> CreateActivitySource()
    {
        return new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(XaeProjectGraphChangeScope));
        }
    }

    private sealed class ActivityState
    {
        public ActivityState(
            long version,
            DateTimeOffset lastActivityUtc,
            int eventCount,
            bool overflow,
            Task signal)
        {
            Version = version;
            LastActivityUtc = lastActivityUtc;
            EventCount = eventCount;
            Overflow = overflow;
            Signal = signal;
        }

        public long Version { get; }

        public DateTimeOffset LastActivityUtc { get; }

        public int EventCount { get; }

        public bool Overflow { get; }

        public Task Signal { get; }
    }
}

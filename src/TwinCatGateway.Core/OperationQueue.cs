using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public enum OperationCancellationResult
{
    Cancelled,
    NotFound,
    AlreadyStarted,
}

[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "OperationQueue is the architecture term for the serial operation scheduler.")]
public sealed class OperationQueue : IDisposable
{
    private readonly ConcurrentQueue<QueueItem> _queue = new();
    private readonly ConcurrentDictionary<string, QueueItem> _items =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleSync = new();
    private readonly OperationStore _store;
    private readonly IClock _clock;
    private readonly IOperationIdGenerator _idGenerator;
    private readonly IOperationExceptionSink _exceptionSink;
    private readonly IGatewayEventSink? _gatewayEventSink;
    private readonly Task _processor;
    private int _stopped;

    public OperationQueue(
        OperationStore store,
        IClock? clock = null,
        IOperationIdGenerator? idGenerator = null,
        IOperationExceptionSink? exceptionSink = null,
        IGatewayEventSink? gatewayEventSink = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? SystemClock.Instance;
        _idGenerator = idGenerator ?? GuidOperationIdGenerator.Instance;
        _exceptionSink = exceptionSink ?? TraceOperationExceptionSink.Instance;
        _gatewayEventSink = gatewayEventSink;
        _processor = Task.Run(ProcessQueueAsync);
    }

    public OperationAccepted Enqueue(
        OperationKind kind,
        Func<CancellationToken, Task<OperationExecutionResult>> executeAsync,
        TimeSpan? timeout = null)
    {
        if (executeAsync is null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        return Enqueue(
            kind,
            (_, cancellationToken) =>
                executeAsync(cancellationToken),
            timeout);
    }

    public OperationAccepted Enqueue(
        OperationKind kind,
        Func<
            string,
            CancellationToken,
            Task<OperationExecutionResult>> executeAsync,
        TimeSpan? timeout = null)
    {
        if (executeAsync is null)
        {
            throw new ArgumentNullException(nameof(executeAsync));
        }

        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_lifecycleSync)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                throw new InvalidOperationException("The operation queue is stopping.");
            }

            string operationId = _idGenerator.Create();
            QueueItem item = new(operationId, kind, executeAsync, timeout);
            if (!_items.TryAdd(operationId, item))
            {
                throw new InvalidOperationException(
                    $"Operation ID generator returned duplicate ID '{operationId}'.");
            }

            DateTimeOffset queuedAtUtc = _clock.UtcNow;
            _store.AddQueued(operationId, kind, queuedAtUtc);
            RecordOperationEvent(
                operationId,
                kind,
                OperationState.Queued,
                queuedAtUtc);
            _queue.Enqueue(item);
            _signal.Release();

            return new OperationAccepted
            {
                OperationId = operationId,
                State = OperationState.Queued,
            };
        }
    }

    public OperationCancellationResult CancelBeforeStart(string operationId)
    {
        if (!_items.TryGetValue(operationId, out QueueItem? item))
        {
            return _store.Get(operationId) is null
                ? OperationCancellationResult.NotFound
                : OperationCancellationResult.AlreadyStarted;
        }

        if (!item.TryCancelBeforeStart())
        {
            return OperationCancellationResult.AlreadyStarted;
        }

        DateTimeOffset completedAtUtc = _clock.UtcNow;
        bool completed = _store.TryComplete(
            operationId,
            OperationState.Cancelled,
            completedAtUtc);
        if (completed)
        {
            RecordOperationEvent(
                operationId,
                item.Kind,
                OperationState.Cancelled,
                completedAtUtc);
        }

        _items.TryRemove(operationId, out _);
        return OperationCancellationResult.Cancelled;
    }

    public async Task StopAsync()
    {
        bool shouldStop;
        lock (_lifecycleSync)
        {
            shouldStop = Interlocked.Exchange(ref _stopped, 1) == 0;
        }

        if (!shouldStop)
        {
            await _processor.ConfigureAwait(false);
            return;
        }

        _shutdown.Cancel();
        _signal.Release();
        await _processor.ConfigureAwait(false);
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);

                while (_queue.TryDequeue(out QueueItem? item))
                {
                    if (!item.TryStart())
                    {
                        continue;
                    }

                    await ExecuteItemAsync(item).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            CancelRemainingItems();
        }
    }

    private async Task ExecuteItemAsync(QueueItem item)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        if (_store.TryMarkRunning(
            item.OperationId,
            startedAtUtc))
        {
            RecordOperationEvent(
                item.OperationId,
                item.Kind,
                OperationState.Running,
                startedAtUtc);
        }

        using CancellationTokenSource? timeout = item.Timeout.HasValue
            ? new CancellationTokenSource(item.Timeout.Value)
            : null;
        using CancellationTokenSource execution = timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                _shutdown.Token,
                timeout.Token);

        try
        {
            OperationExecutionResult result =
                await item.ExecuteAsync(
                    item.OperationId,
                    execution.Token).ConfigureAwait(false);
            OperationState state = result.Succeeded
                ? OperationState.Succeeded
                : OperationState.Failed;
            GatewayError? error = WithOperationId(result.Error, item.OperationId);

            DateTimeOffset completedAtUtc = _clock.UtcNow;
            bool completed = _store.TryComplete(
                item.OperationId,
                state,
                completedAtUtc,
                result.Result,
                error,
                result.Resources);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    state,
                    completedAtUtc,
                    error,
                    result.Resources);
            }
        }
        catch (OperationCanceledException) when (
            timeout is not null
            && timeout.IsCancellationRequested
            && !_shutdown.IsCancellationRequested)
        {
            DateTimeOffset completedAtUtc = _clock.UtcNow;
            GatewayError error = CreateError(
                ErrorCodes.OperationTimeout,
                "The operation exceeded its deadline.",
                item.OperationId,
                retryable: true);
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.TimedOut,
                completedAtUtc,
                error: error);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    OperationState.TimedOut,
                    completedAtUtc,
                    error);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            DateTimeOffset completedAtUtc = _clock.UtcNow;
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Cancelled,
                completedAtUtc);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    OperationState.Cancelled,
                    completedAtUtc);
            }
        }
        catch (GatewayOperationException exception)
        {
            _exceptionSink.Record(item.OperationId, exception);
            DateTimeOffset completedAtUtc = _clock.UtcNow;
            GatewayError error = CreateError(
                exception.Code,
                exception.Message,
                item.OperationId,
                exception.Retryable,
                exception.Stage,
                exception.RawLogRef,
                exception.Details);
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Failed,
                completedAtUtc,
                error: error);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    OperationState.Failed,
                    completedAtUtc,
                    error);
            }
        }
        catch (Exception exception)
        {
            _exceptionSink.Record(item.OperationId, exception);
            DateTimeOffset completedAtUtc = _clock.UtcNow;
            GatewayError error = CreateError(
                ErrorCodes.OperationFailed,
                "The operation failed unexpectedly. See the local log for details.",
                item.OperationId,
                retryable: false,
                details:
                    $"{exception.GetType().FullName}: {exception.Message}");
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Failed,
                completedAtUtc,
                error: error);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    OperationState.Failed,
                    completedAtUtc,
                    error);
            }
        }
        finally
        {
            item.MarkCompleted();
            _items.TryRemove(item.OperationId, out _);
        }
    }

    private void CancelRemainingItems()
    {
        while (_queue.TryDequeue(out QueueItem? item))
        {
            if (!item.TryCancelBeforeStart())
            {
                continue;
            }

            DateTimeOffset completedAtUtc = _clock.UtcNow;
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Cancelled,
                completedAtUtc);
            if (completed)
            {
                RecordOperationEvent(
                    item.OperationId,
                    item.Kind,
                    OperationState.Cancelled,
                    completedAtUtc);
            }

            _items.TryRemove(item.OperationId, out _);
        }
    }

    private static GatewayError CreateError(
        string code,
        string message,
        string operationId,
        bool retryable,
        string? stage = null,
        string? rawLogRef = null,
        string? details = null)
    {
        return new GatewayError
        {
            Code = code,
            Message = message,
            Details = details,
            OperationId = operationId,
            Retryable = retryable,
            Stage = stage,
            RawLogRef = rawLogRef,
        };
    }

    private void RecordOperationEvent(
        string operationId,
        OperationKind kind,
        OperationState state,
        DateTimeOffset occurredAtUtc,
        GatewayError? error = null,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        _gatewayEventSink?.Record(
            new GatewayEvent
            {
                Type = GetOperationEventType(kind, state),
                Severity = GetOperationEventSeverity(state),
                OperationId = operationId,
                OperationKind = kind,
                Stage = error?.Stage
                    ?? GetOperationEventStage(state),
                Message = GetOperationEventMessage(kind, state),
                Error = error,
                Resources = resources?.ToList()
                    ?? new List<ResourceReference>(),
            },
            occurredAtUtc);
    }

    private static string GetOperationEventType(
        OperationKind kind,
        OperationState state)
    {
        switch (kind)
        {
            case OperationKind.OpenSolution:
                return GetSolutionOpenEventType(state);
            case OperationKind.Build:
                return GetBuildEventType(state);
            case OperationKind.Activate:
                return GetActivationEventType(state);
            case OperationKind.RecoverToConfig:
                return GetRecoveryEventType(state);
            case OperationKind.Test:
                return GetTcUnitEventType(state);
            default:
                return $"operation.{state.ToString().ToLowerInvariant()}";
        }
    }

    private static string GetSolutionOpenEventType(
        OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return GatewayEventTypes.SolutionOpenQueued;
            case OperationState.Running:
                return GatewayEventTypes.SolutionOpenStarted;
            case OperationState.Succeeded:
                return GatewayEventTypes.SolutionOpenSucceeded;
            case OperationState.Failed:
                return GatewayEventTypes.SolutionOpenFailed;
            case OperationState.TimedOut:
                return GatewayEventTypes.SolutionOpenTimedOut;
            case OperationState.Cancelled:
                return GatewayEventTypes.SolutionOpenCancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static string GetBuildEventType(OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return GatewayEventTypes.BuildQueued;
            case OperationState.Running:
                return GatewayEventTypes.BuildStarted;
            case OperationState.Succeeded:
                return GatewayEventTypes.BuildSucceeded;
            case OperationState.Failed:
                return GatewayEventTypes.BuildFailed;
            case OperationState.TimedOut:
                return GatewayEventTypes.BuildTimedOut;
            case OperationState.Cancelled:
                return GatewayEventTypes.BuildCancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static string GetActivationEventType(OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return GatewayEventTypes.ActivationQueued;
            case OperationState.Running:
                return GatewayEventTypes.ActivationStarted;
            case OperationState.Succeeded:
                return GatewayEventTypes.ActivationSucceeded;
            case OperationState.Failed:
                return GatewayEventTypes.ActivationFailed;
            case OperationState.TimedOut:
                return GatewayEventTypes.ActivationTimedOut;
            case OperationState.Cancelled:
                return GatewayEventTypes.ActivationCancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static string GetRecoveryEventType(OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return GatewayEventTypes.RecoveryQueued;
            case OperationState.Running:
                return GatewayEventTypes.RecoveryStarted;
            case OperationState.Succeeded:
                return GatewayEventTypes.RecoverySucceeded;
            case OperationState.Failed:
                return GatewayEventTypes.RecoveryFailed;
            case OperationState.TimedOut:
                return GatewayEventTypes.RecoveryTimedOut;
            case OperationState.Cancelled:
                return GatewayEventTypes.RecoveryCancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static string GetTcUnitEventType(OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return GatewayEventTypes.TcUnitQueued;
            case OperationState.Running:
                return GatewayEventTypes.TcUnitStarted;
            case OperationState.Succeeded:
                return GatewayEventTypes.TcUnitSucceeded;
            case OperationState.Failed:
                return GatewayEventTypes.TcUnitFailed;
            case OperationState.TimedOut:
                return GatewayEventTypes.TcUnitTimedOut;
            case OperationState.Cancelled:
                return GatewayEventTypes.TcUnitCancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static DiagnosticSeverity GetOperationEventSeverity(
        OperationState state)
    {
        switch (state)
        {
            case OperationState.Failed:
            case OperationState.TimedOut:
                return DiagnosticSeverity.Error;
            case OperationState.Cancelled:
                return DiagnosticSeverity.Warning;
            default:
                return DiagnosticSeverity.Info;
        }
    }

    private static string GetOperationEventStage(OperationState state)
    {
        switch (state)
        {
            case OperationState.Queued:
                return "operation.queue";
            case OperationState.Running:
                return "operation.execute";
            case OperationState.Succeeded:
            case OperationState.Failed:
                return "operation.complete";
            case OperationState.TimedOut:
                return "operation.timeout";
            case OperationState.Cancelled:
                return "operation.cancel";
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static string GetOperationEventMessage(
        OperationKind kind,
        OperationState state)
    {
        return $"{kind} operation {state.ToString().ToLowerInvariant()}.";
    }

    private static GatewayError? WithOperationId(
        GatewayError? source,
        string operationId)
    {
        return source is null
            ? null
            : new GatewayError
            {
                Code = source.Code,
                Message = source.Message,
                Details = source.Details,
                Retryable = source.Retryable,
                OperationId = string.IsNullOrEmpty(source.OperationId)
                    ? operationId
                    : source.OperationId,
                Stage = source.Stage,
                RawLogRef = source.RawLogRef,
            };
    }

    private sealed class QueueItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Cancelled = 2;
        private const int Completed = 3;
        private int _state;

        public QueueItem(
            string operationId,
            OperationKind kind,
            Func<
                string,
                CancellationToken,
                Task<OperationExecutionResult>> executeAsync,
            TimeSpan? timeout)
        {
            OperationId = operationId;
            Kind = kind;
            ExecuteAsync = executeAsync;
            Timeout = timeout;
        }

        public string OperationId { get; }

        public OperationKind Kind { get; }

        public Func<
            string,
            CancellationToken,
            Task<OperationExecutionResult>> ExecuteAsync { get; }

        public TimeSpan? Timeout { get; }

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;
        }

        public bool TryCancelBeforeStart()
        {
            return Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued;
        }

        public void MarkCompleted()
        {
            Interlocked.Exchange(ref _state, Completed);
        }
    }
}

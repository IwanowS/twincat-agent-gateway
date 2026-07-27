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

            _store.AddQueued(operationId, kind, _clock.UtcNow);
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

        _store.TryComplete(
            operationId,
            OperationState.Cancelled,
            _clock.UtcNow);
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
        _store.TryMarkRunning(item.OperationId, _clock.UtcNow);

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
            if (completed && error is not null)
            {
                _gatewayEventSink?.Record(
                    CreateErrorEvent(
                        item.Kind,
                        error,
                        result.Resources),
                    completedAtUtc);
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
                _gatewayEventSink?.Record(
                    CreateErrorEvent(
                        item.Kind,
                        error),
                    completedAtUtc);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _store.TryComplete(
                item.OperationId,
                OperationState.Cancelled,
                _clock.UtcNow);
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
                exception.RawLogRef);
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Failed,
                completedAtUtc,
                error: error);
            if (completed)
            {
                _gatewayEventSink?.Record(
                    CreateErrorEvent(
                        item.Kind,
                        error),
                    completedAtUtc);
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
                retryable: false);
            bool completed = _store.TryComplete(
                item.OperationId,
                OperationState.Failed,
                completedAtUtc,
                error: error);
            if (completed)
            {
                _gatewayEventSink?.Record(
                    CreateErrorEvent(
                        item.Kind,
                        error),
                    completedAtUtc);
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

            _store.TryComplete(
                item.OperationId,
                OperationState.Cancelled,
                _clock.UtcNow);
            _items.TryRemove(item.OperationId, out _);
        }
    }

    private static GatewayError CreateError(
        string code,
        string message,
        string operationId,
        bool retryable,
        string? stage = null,
        string? rawLogRef = null)
    {
        return new GatewayError
        {
            Code = code,
            Message = message,
            OperationId = operationId,
            Retryable = retryable,
            Stage = stage,
            RawLogRef = rawLogRef,
        };
    }

    private static GatewayEvent CreateErrorEvent(
        OperationKind kind,
        GatewayError error,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        return new GatewayEvent
        {
            Type = GatewayEventTypes.ErrorOccurred,
            Severity = DiagnosticSeverity.Error,
            OperationId = error.OperationId,
            OperationKind = kind,
            Stage = error.Stage,
            Message = error.Message,
            Error = error,
            Resources = resources?.ToList()
                ?? new List<ResourceReference>(),
        };
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

using System;
using System.Diagnostics;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal sealed class ComCallTelemetry
{
    private long _rejectedCallCount;
    private long _retryCount;
    private long _lastCallLatencyMs;
    private int _lastHResult;
    private int _hasLastHResult;

    public void RecordRejectedCall()
    {
        Interlocked.Increment(ref _rejectedCallCount);
    }

    public void RecordRetry()
    {
        Interlocked.Increment(ref _retryCount);
    }

    public void RecordCall(TimeSpan elapsed, Exception? exception)
    {
        Interlocked.Exchange(
            ref _lastCallLatencyMs,
            (long)elapsed.TotalMilliseconds);
        if (exception is null)
        {
            return;
        }

        Interlocked.Exchange(ref _lastHResult, exception.HResult);
        Volatile.Write(ref _hasLastHResult, 1);
    }

    public ComDiagnostics Snapshot()
    {
        return new ComDiagnostics
        {
            RejectedCallCount = Interlocked.Read(ref _rejectedCallCount),
            RetryCount = Interlocked.Read(ref _retryCount),
            LastCallLatencyMs = Interlocked.Read(ref _lastCallLatencyMs),
            LastHResult = Volatile.Read(ref _hasLastHResult) == 0
                ? null
                : (int?)Volatile.Read(ref _lastHResult),
        };
    }
}

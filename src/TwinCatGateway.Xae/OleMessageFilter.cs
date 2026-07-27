using System;
using System.Runtime.InteropServices;

namespace TwinCatGateway.Xae;

internal sealed class OleMessageFilter : IDisposable, IOleMessageFilter
{
    private const int ServerCallRetryLater = 2;
    private const int PendingMessageWaitDefaultProcess = 2;
    private const int RetryDelayMilliseconds = 100;
    private readonly ComCallTelemetry _telemetry;
    private IOleMessageFilter? _previousFilter;
    private DateTimeOffset? _deadlineUtc;
    private bool _registered;

    private OleMessageFilter(ComCallTelemetry telemetry)
    {
        _telemetry = telemetry;
    }

    public static OleMessageFilter Register(ComCallTelemetry telemetry)
    {
        OleMessageFilter filter = new(telemetry);
        int hResult = CoRegisterMessageFilter(filter, out filter._previousFilter);
        Marshal.ThrowExceptionForHR(hResult);
        filter._registered = true;
        return filter;
    }

    public IDisposable BeginCall(DateTimeOffset deadlineUtc)
    {
        _deadlineUtc = deadlineUtc;
        return new CallScope(this);
    }

    public int HandleInComingCall(
        int callType,
        IntPtr taskCaller,
        int tickCount,
        IntPtr interfaceInfo)
    {
        return 0;
    }

    public int RetryRejectedCall(
        IntPtr taskCallee,
        int tickCount,
        int rejectType)
    {
        _telemetry.RecordRejectedCall();
        if (rejectType != ServerCallRetryLater
            || !_deadlineUtc.HasValue
            || DateTimeOffset.UtcNow >= _deadlineUtc.Value)
        {
            return -1;
        }

        _telemetry.RecordRetry();
        return RetryDelayMilliseconds;
    }

    public int MessagePending(
        IntPtr taskCallee,
        int tickCount,
        int pendingType)
    {
        return PendingMessageWaitDefaultProcess;
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        int hResult = CoRegisterMessageFilter(_previousFilter, out _);
        _registered = false;
        Marshal.ThrowExceptionForHR(hResult);
    }

    private void EndCall()
    {
        _deadlineUtc = null;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(
        IOleMessageFilter? newFilter,
        out IOleMessageFilter? oldFilter);

    private sealed class CallScope : IDisposable
    {
        private OleMessageFilter? _owner;

        public CallScope(OleMessageFilter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            OleMessageFilter? owner = _owner;
            _owner = null;
            owner?.EndCall();
        }
    }
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000016-0000-0000-C000-000000000046")]
internal interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(
        int callType,
        IntPtr taskCaller,
        int tickCount,
        IntPtr interfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(
        IntPtr taskCallee,
        int tickCount,
        int rejectType);

    [PreserveSig]
    int MessagePending(
        IntPtr taskCallee,
        int tickCount,
        int pendingType);
}

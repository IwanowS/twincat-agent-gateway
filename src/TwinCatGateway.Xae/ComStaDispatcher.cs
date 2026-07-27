using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class ComStaDispatcher : IDisposable
{
    private const uint RunWorkMessage = 0x8001;
    private const uint QuitMessage = 0x0012;
    private readonly ConcurrentQueue<IWorkItem> _workItems = new();
    private readonly TaskCompletionSource<bool> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly ComCallTelemetry _telemetry = new();
    private uint _nativeThreadId;
    private int _disposed;

    public ComStaDispatcher(string threadName = "TwinCAT XAE COM STA")
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    public Task<T> InvokeAsync<T>(
        Func<T> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ComStaDispatcher));
        }

        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow.Add(timeout);
        WorkItem<T> workItem = new(
            action,
            deadlineUtc,
            _telemetry,
            cancellationToken);
        _workItems.Enqueue(workItem);
        if (!PostThreadMessage(
            _nativeThreadId,
            RunWorkMessage,
            UIntPtr.Zero,
            IntPtr.Zero))
        {
            workItem.Reject(
                new InvalidOperationException(
                    "Could not post work to the XAE STA thread."));
        }

        return WaitForCompletionAsync(
            workItem,
            timeout,
            cancellationToken);
    }

    public ComDiagnostics GetDiagnostics()
    {
        return _telemetry.Snapshot();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PostThreadMessage(
            _nativeThreadId,
            QuitMessage,
            UIntPtr.Zero,
            IntPtr.Zero);
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            Trace.TraceError(
                "TwinCAT XAE STA thread did not stop within 10 seconds.");
        }
    }

    private static async Task<T> WaitForCompletionAsync<T>(
        WorkItem<T> workItem,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task delay = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(
            workItem.Completion,
            delay).ConfigureAwait(false);
        if (ReferenceEquals(completed, workItem.Completion))
        {
            return await workItem.Completion.ConfigureAwait(false);
        }

        ObserveLateFault(workItem.Completion);
        cancellationToken.ThrowIfCancellationRequested();
        throw new GatewayOperationException(
            ErrorCodes.ComCallTimeout,
            workItem.HasStarted
                ? "The COM call exceeded its deadline after it started."
                : "The COM call exceeded its deadline before it started.",
            retryable: true,
            stage: "com.invoke");
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThreadMain()
    {
        try
        {
            _nativeThreadId = GetCurrentThreadId();
            PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                removeMessage: 0);
            using OleMessageFilter filter =
                OleMessageFilter.Register(_telemetry);
            _ready.TrySetResult(true);

            while (true)
            {
                int result = GetMessage(
                    out NativeMessage message,
                    IntPtr.Zero,
                    0,
                    0);
                if (result == 0 || message.Message == QuitMessage)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new InvalidOperationException(
                        "The XAE STA message pump failed.");
                }

                if (message.Message == RunWorkMessage)
                {
                    DrainWorkItems(filter);
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            Trace.TraceError("TwinCAT XAE STA thread failed: {0}", exception);
        }
        finally
        {
            ObjectDisposedException disposed =
                new(nameof(ComStaDispatcher));
            while (_workItems.TryDequeue(out IWorkItem? item))
            {
                item.Reject(disposed);
            }
        }
    }

    private void DrainWorkItems(OleMessageFilter filter)
    {
        while (_workItems.TryDequeue(out IWorkItem? item))
        {
            item.Execute(filter);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    private interface IWorkItem
    {
        void Execute(OleMessageFilter filter);

        void Reject(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _action;
        private readonly DateTimeOffset _deadlineUtc;
        private readonly CancellationToken _cancellationToken;
        private readonly ComCallTelemetry _telemetry;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public WorkItem(
            Func<T> action,
            DateTimeOffset deadlineUtc,
            ComCallTelemetry telemetry,
            CancellationToken cancellationToken)
        {
            _action = action;
            _deadlineUtc = deadlineUtc;
            _cancellationToken = cancellationToken;
            _telemetry = telemetry;
        }

        public Task<T> Completion => _completion.Task;

        public bool HasStarted => Volatile.Read(ref _started) != 0;

        public void Execute(OleMessageFilter filter)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled();
                return;
            }

            if (DateTimeOffset.UtcNow >= _deadlineUtc)
            {
                _completion.TrySetException(
                    new GatewayOperationException(
                        ErrorCodes.ComCallTimeout,
                        "The COM call exceeded its deadline before it started.",
                        retryable: true,
                        stage: "com.queue"));
                return;
            }

            Volatile.Write(ref _started, 1);
            Stopwatch stopwatch = Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                using (filter.BeginCall(_deadlineUtc))
                {
                    T result = _action();
                    _completion.TrySetResult(result);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                _completion.TrySetException(exception);
            }
            finally
            {
                stopwatch.Stop();
                _telemetry.RecordCall(stopwatch.Elapsed, failure);
            }
        }

        public void Reject(Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

internal enum XaeKnownDialogKind
{
    Unknown,
    PlatformMismatch,
    ActivationConfirmation,
    RunConfirmation,
    FatalError,
    ProjectCloseFailure,
}

public sealed class XaeDialogButtonObservation
{
    public string AutomationId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool InvokeAvailable { get; set; }
}

public sealed class XaeDialogObservation
{
    public DateTimeOffset ObservedAtUtc { get; set; }

    public string? OperationId { get; set; }

    public string? OperationName { get; set; }

    public string? Stage { get; set; }

    public int ProcessId { get; set; }

    public int NativeWindowHandle { get; set; }

    public string RuntimeId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public bool Known { get; set; }

    public bool Modal { get; set; }

    public string FrameworkId { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool ActionRequested { get; set; }

    public bool ActionCompleted { get; set; }

    public bool Failure { get; set; }

    public List<XaeDialogButtonObservation> Buttons { get; set; } =
        new();
}

public sealed class XaeDialogObservationEventArgs : EventArgs
{
    public XaeDialogObservationEventArgs(
        XaeDialogObservation observation)
    {
        Observation = observation
            ?? throw new ArgumentNullException(nameof(observation));
    }

    public XaeDialogObservation Observation { get; }
}

public sealed class XaeActivationCommandResult
{
    public bool ActivationConfirmed { get; set; }

    public bool RunDecisionHandled { get; set; }

    public bool RunRequested { get; set; }

    public AutostartBootProjectSelection AutostartSelection { get; set; }

    public IReadOnlyList<XaeDialogObservation> Dialogs { get; set; } =
        Array.Empty<XaeDialogObservation>();
}

public sealed class XaeDialogOperationScope : IDisposable
{
    private readonly XaeDialogSupervisor _owner;
    private readonly XaeDialogOperationContext _context;
    private int _disposed;

    internal XaeDialogOperationScope(
        XaeDialogSupervisor owner,
        XaeDialogOperationContext context)
    {
        _owner = owner;
        _context = context;
    }

    public void SetStage(string stage)
    {
        ThrowIfDisposed();
        _context.SetStage(stage);
    }

    public async Task ObserveAsync(Task operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        ThrowIfDisposed();
        Task completed = await Task.WhenAny(
            operation,
            _context.FailureDetected).ConfigureAwait(false);
        if (completed == _context.FailureDetected)
        {
            ObserveFault(operation);
            throw await _context.FailureDetected.ConfigureAwait(false);
        }

        await operation.ConfigureAwait(false);
        ThrowIfFailed();
    }

    public async Task<T> ObserveAsync<T>(Task<T> operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        ThrowIfDisposed();
        Task completed = await Task.WhenAny(
            operation,
            _context.FailureDetected).ConfigureAwait(false);
        if (completed == _context.FailureDetected)
        {
            ObserveFault(operation);
            throw await _context.FailureDetected.ConfigureAwait(false);
        }

        T result = await operation.ConfigureAwait(false);
        ThrowIfFailed();
        return result;
    }

    public void ThrowIfFailed()
    {
        ThrowIfDisposed();
        if (_context.TryGetFailure(
            out GatewayOperationException? failure))
        {
            throw failure!;
        }
    }

    public XaeActivationCommandResult GetActivationResult()
    {
        ThrowIfDisposed();
        return _context.CreateActivationResult();
    }

    public async Task WaitForActivationDialogOutcomeAsync(
        TimeSpan settleTimeout,
        CancellationToken cancellationToken)
    {
        if (settleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settleTimeout));
        }

        ThrowIfDisposed();
        Task timeout = Task.Delay(
            settleTimeout,
            cancellationToken);
        Task completed = await Task.WhenAny(
            _context.ActivationDialogSequenceCompleted,
            _context.FailureDetected,
            timeout).ConfigureAwait(false);
        if (completed == _context.FailureDetected)
        {
            throw await _context.FailureDetected
                .ConfigureAwait(false);
        }

        if (completed
            == _context.ActivationDialogSequenceCompleted)
        {
            ThrowIfFailed();
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.EndOperation(_context);
    }

    private static void ObserveFault(Task operation)
    {
        operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(XaeDialogOperationScope));
        }
    }
}

internal sealed class XaeDialogOperationContext
{
    private readonly TaskCompletionSource<GatewayOperationException> _failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool>
        _activationDialogSequenceCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<XaeDialogObservation> _observations = new();
    private readonly object _sync = new();
    private string _stage;
    private bool _activationConfirmed;
    private bool _runDecisionHandled;
    private AutostartBootProjectSelection _autostartSelection =
        AutostartBootProjectSelection.Unknown;
    private int? _blockingDialogHandle;

    public XaeDialogOperationContext(
        string operationId,
        string operationName,
        string stage,
        bool? runAfterActivation)
    {
        OperationId = operationId;
        OperationName = operationName;
        _stage = stage;
        RunAfterActivation = runAfterActivation;
    }

    public string OperationId { get; }

    public string OperationName { get; }

    public bool? RunAfterActivation { get; }

    public Task<GatewayOperationException> FailureDetected =>
        _failure.Task;

    public Task ActivationDialogSequenceCompleted =>
        _activationDialogSequenceCompleted.Task;

    public string ReadStage()
    {
        lock (_sync)
        {
            return _stage;
        }
    }

    public void SetStage(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException(
                "Dialog operation stage is required.",
                nameof(stage));
        }

        lock (_sync)
        {
            _stage = stage;
        }
    }

    public void Record(
        XaeDialogObservation observation,
        int? blockingDialogHandle = null)
    {
        lock (_sync)
        {
            _observations.Add(observation);
            if (blockingDialogHandle.HasValue)
            {
                _blockingDialogHandle = blockingDialogHandle;
            }
        }
    }

    public void ConfirmActivation(
        AutostartBootProjectSelection selection)
    {
        lock (_sync)
        {
            _activationConfirmed = true;
            _autostartSelection = selection;
        }
    }

    public bool IsActivationConfirmed()
    {
        lock (_sync)
        {
            return _activationConfirmed;
        }
    }

    public void ConfirmRunDecision()
    {
        lock (_sync)
        {
            _runDecisionHandled = true;
        }

        _activationDialogSequenceCompleted.TrySetResult(true);
    }

    public void Fail(GatewayOperationException exception)
    {
        _failure.TrySetResult(exception);
    }

    public bool TryGetFailure(
        out GatewayOperationException? failure)
    {
        if (!_failure.Task.IsCompleted)
        {
            failure = null;
            return false;
        }

        failure = _failure.Task.GetAwaiter().GetResult();
        return true;
    }

    public int? GetBlockingDialogHandle()
    {
        lock (_sync)
        {
            return _blockingDialogHandle;
        }
    }

    public XaeActivationCommandResult CreateActivationResult()
    {
        lock (_sync)
        {
            return new XaeActivationCommandResult
            {
                ActivationConfirmed = _activationConfirmed,
                RunDecisionHandled = _runDecisionHandled,
                RunRequested = RunAfterActivation == true,
                AutostartSelection = _autostartSelection,
                Dialogs = _observations
                    .Select(CloneObservation)
                    .ToArray(),
            };
        }
    }

    private static XaeDialogObservation CloneObservation(
        XaeDialogObservation source)
    {
        return new XaeDialogObservation
        {
            ObservedAtUtc = source.ObservedAtUtc,
            OperationId = source.OperationId,
            OperationName = source.OperationName,
            Stage = source.Stage,
            ProcessId = source.ProcessId,
            NativeWindowHandle = source.NativeWindowHandle,
            RuntimeId = source.RuntimeId,
            Title = source.Title,
            Content = source.Content,
            Kind = source.Kind,
            Known = source.Known,
            Modal = source.Modal,
            FrameworkId = source.FrameworkId,
            ClassName = source.ClassName,
            Action = source.Action,
            ActionRequested = source.ActionRequested,
            ActionCompleted = source.ActionCompleted,
            Failure = source.Failure,
            Buttons = source.Buttons
                .Select(button => new XaeDialogButtonObservation
                {
                    AutomationId = button.AutomationId,
                    Name = button.Name,
                    InvokeAvailable = button.InvokeAvailable,
                })
                .ToList(),
        };
    }
}

internal sealed class XaeDialogSupervisor : IDisposable
{
    private const string DialogClassName = "#32770";
    private const string ButtonAutomationIdCancel = "2";
    private const string ButtonAutomationIdOk = "1";
    private const string ButtonAutomationIdYes = "6";
    private static readonly TimeSpan ControlReadyTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DialogCloseTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ActiveReconciliationInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleReconciliationInterval =
        TimeSpan.FromSeconds(2);
    private readonly BlockingCollection<int> _opened =
        new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _processId;
    private readonly Thread _worker;
    private XaeDialogOperationContext? _activeOperation;
    private int? _blockingDialogHandle;
    private Exception? _startupFailure;
    private int _disposed;

    public XaeDialogSupervisor(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        _processId = processId;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = $"XAE UIA dialog supervisor ({processId})",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new GatewayOperationException(
                ErrorCodes.XaeDialogMonitorUnavailable,
                "The XAE UI Automation dialog supervisor did not start.",
                retryable: true,
                stage: "xae.dialog.start");
        }

        if (_startupFailure is not null)
        {
            Exception failure = _startupFailure;
            Dispose();
            throw new GatewayOperationException(
                ErrorCodes.XaeDialogMonitorUnavailable,
                "The XAE UI Automation dialog supervisor could not "
                    + "subscribe to window events.",
                retryable: true,
                stage: "xae.dialog.start",
                innerException: failure);
        }
    }

    public event EventHandler<XaeDialogObservationEventArgs>?
        DialogObserved;

    public int ProcessId => _processId;

    public XaeDialogOperationScope BeginOperation(
        string operationId,
        string operationName,
        string stage,
        bool? runAfterActivation)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "Operation ID is required.",
                nameof(operationId));
        }

        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new ArgumentException(
                "Operation name is required.",
                nameof(operationName));
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException(
                "Operation stage is required.",
                nameof(stage));
        }

        ThrowIfDisposed();
        XaeDialogOperationContext context;
        int? blockingDialogHandle;
        lock (_sync)
        {
            if (_startupFailure is not null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.XaeDialogMonitorUnavailable,
                    "The XAE UI Automation dialog supervisor is "
                        + "unavailable.",
                    retryable: true,
                    stage: "xae.dialog.preflight",
                    innerException: _startupFailure);
            }

            ClearClosedBlockingDialog();
            if (_activeOperation is not null)
            {
                throw new InvalidOperationException(
                    "An XAE dialog operation is already active.");
            }

            context = new XaeDialogOperationContext(
                operationId,
                operationName,
                stage,
                runAfterActivation);
            _activeOperation = context;
            blockingDialogHandle = _blockingDialogHandle;
            if (blockingDialogHandle.HasValue)
            {
                _seen.Clear();
            }
        }

        if (blockingDialogHandle.HasValue
            && !_opened.IsAddingCompleted)
        {
            _opened.Add(blockingDialogHandle.Value);
        }

        return new XaeDialogOperationScope(this, context);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _opened.CompleteAdding();
        if (Thread.CurrentThread != _worker)
        {
            _worker.Join(TimeSpan.FromSeconds(5));
        }

        _ready.Dispose();
        _opened.Dispose();
        _cancellation.Dispose();
    }

    internal void EndOperation(XaeDialogOperationContext context)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeOperation, context))
            {
                return;
            }

            int? blockingHandle = context.GetBlockingDialogHandle();
            if (blockingHandle.HasValue
                && IsWindow(new IntPtr(blockingHandle.Value)))
            {
                _blockingDialogHandle = blockingHandle;
            }

            _activeOperation = null;
        }
    }

    internal static XaeKnownDialogKind ClassifyDialog(
        string? title,
        string? content)
    {
        string combined = NormalizeText(
            (title ?? string.Empty)
                + " "
                + (content ?? string.Empty));
        if (combined.IndexOf(
                "differs from current target platform",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeKnownDialogKind.PlatformMismatch;
        }

        if (combined.IndexOf(
                "Autostart PLC Boot Project(s)",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeKnownDialogKind.ActivationConfirmation;
        }

        if (combined.IndexOf(
                "Restart TwinCAT System in Run Mode",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeKnownDialogKind.RunConfirmation;
        }

        if (combined.IndexOf(
                "Target system reports a fatal error",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeKnownDialogKind.FatalError;
        }

        if (combined.IndexOf(
                "Closing project failed",
                StringComparison.OrdinalIgnoreCase) >= 0
            && combined.IndexOf(
                "Visual Studio will restart now",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeKnownDialogKind.ProjectCloseFailure;
        }

        return XaeKnownDialogKind.Unknown;
    }

    private void Run()
    {
        AutomationEventHandler openedHandler = OnWindowOpened;
        try
        {
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowOpenedEvent,
                AutomationElement.RootElement,
                TreeScope.Subtree,
                openedHandler);
            EnqueueExistingWindows();
            _ready.Set();
            DateTimeOffset nextReconciliationUtc =
                DateTimeOffset.UtcNow;

            while (!_cancellation.IsCancellationRequested)
            {
                int nativeWindowHandle;
                try
                {
                    if (!_opened.TryTake(
                        out nativeWindowHandle,
                        millisecondsTimeout: 100,
                        _cancellation.Token))
                    {
                        nativeWindowHandle = 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (nativeWindowHandle != 0)
                {
                    ProcessWindow(nativeWindowHandle);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= nextReconciliationUtc)
                {
                    ReconcileWindows();
                    nextReconciliationUtc = now
                        + (HasActiveOperation()
                            ? ActiveReconciliationInterval
                            : IdleReconciliationInterval);
                }
            }
        }
        catch (Exception exception)
        {
            XaeDialogOperationContext? operation;
            lock (_sync)
            {
                _startupFailure = exception;
                operation = _activeOperation;
            }

            Trace.TraceError(
                "XAE UI Automation dialog supervisor failed: {0}",
                exception);
            operation?.Fail(
                new GatewayOperationException(
                    ErrorCodes.XaeDialogMonitorUnavailable,
                    "The XAE UI Automation dialog supervisor stopped "
                        + "while an operation was running.",
                    retryable: true,
                    stage: "xae.dialog.monitor",
                    innerException: exception));
            _ready.Set();
        }
        finally
        {
            try
            {
                Automation.RemoveAutomationEventHandler(
                    WindowPattern.WindowOpenedEvent,
                    AutomationElement.RootElement,
                    openedHandler);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "Could not remove the XAE UI Automation handler: {0}",
                    exception);
            }
        }
    }

    private void OnWindowOpened(
        object sender,
        AutomationEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not AutomationElement element
            || _opened.IsAddingCompleted)
        {
            return;
        }

        try
        {
            if (element.Current.ProcessId == _processId)
            {
                int nativeWindowHandle =
                    element.Current.NativeWindowHandle;
                if (nativeWindowHandle != 0)
                {
                    _opened.Add(nativeWindowHandle);
                }
            }
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
            || exception is InvalidOperationException)
        {
            Trace.TraceInformation(
                "Ignored an unavailable UI Automation window-open "
                    + "event: {0}",
                exception.Message);
        }
    }

    private void EnqueueExistingWindows()
    {
        foreach (int nativeWindowHandle
            in EnumerateDialogWindowHandles())
        {
            _opened.Add(nativeWindowHandle);
        }
    }

    private void ReconcileWindows()
    {
        foreach (int nativeWindowHandle
            in EnumerateDialogWindowHandles())
        {
            ProcessWindow(nativeWindowHandle);
        }
    }

    private List<int> EnumerateDialogWindowHandles()
    {
        List<int> handles = new();
        EnumWindows(
            (windowHandle, _) =>
            {
                if (GetWindowThreadProcessId(
                        windowHandle,
                        out uint processId) == 0)
                {
                    return true;
                }

                if (processId != (uint)_processId)
                {
                    return true;
                }

                char[] className = new char[256];
                int classNameLength = GetClassName(
                        windowHandle,
                        className,
                        className.Length);
                if (classNameLength > 0
                    && string.Equals(
                        new string(
                            className,
                            startIndex: 0,
                            length: classNameLength),
                        DialogClassName,
                        StringComparison.Ordinal))
                {
                    handles.Add(windowHandle.ToInt32());
                }

                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private bool HasActiveOperation()
    {
        lock (_sync)
        {
            return _activeOperation is not null;
        }
    }

    private void ProcessWindow(int nativeWindowHandle)
    {
        AutomationElement element;
        try
        {
            element = AutomationElement.FromHandle(
                new IntPtr(nativeWindowHandle));
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
            || exception is InvalidOperationException)
        {
            return;
        }

        CapturedDialog? dialog = TryCaptureDialog(element);
        if (dialog is null)
        {
            return;
        }

        string key = dialog.RuntimeId
            + "|"
            + dialog.Title
            + "|"
            + dialog.Content;
        lock (_sync)
        {
            if (!_seen.Add(key))
            {
                return;
            }
        }

        XaeDialogOperationContext? operation;
        lock (_sync)
        {
            operation = _activeOperation;
        }

        XaeKnownDialogKind kind = ClassifyDialog(
            dialog.Title,
            dialog.Content);
        XaeDialogObservation observation = CreateObservation(
            dialog,
            operation,
            kind);
        if (operation is null)
        {
            observation.Action = "observe";
            lock (_sync)
            {
                _blockingDialogHandle =
                    dialog.NativeWindowHandle;
            }
            Publish(observation);
            return;
        }

        HandleOperationDialog(
            operation,
            dialog,
            kind,
            observation);
    }

    private void HandleOperationDialog(
        XaeDialogOperationContext operation,
        CapturedDialog dialog,
        XaeKnownDialogKind kind,
        XaeDialogObservation observation)
    {
        if (kind == XaeKnownDialogKind.ProjectCloseFailure)
        {
            observation.Action = "none";
            observation.Failure = true;
            operation.Record(
                observation,
                dialog.NativeWindowHandle);
            Publish(observation);
            operation.Fail(CreateDialogFailure(
                ErrorCodes.XaeDialogReportedFailure,
                operation,
                observation,
                "XAE could not close the project. Automatic "
                    + "confirmation was withheld because the dialog "
                    + "would restart Visual Studio."));
            return;
        }

        if (kind == XaeKnownDialogKind.FatalError)
        {
            RequestFatalDismissal(
                dialog,
                "dismiss-fatal-error",
                observation);
            observation.Failure = true;
            operation.Record(
                observation,
                observation.ActionCompleted
                    ? null
                    : dialog.NativeWindowHandle);
            Publish(observation);
            operation.Fail(observation.ActionCompleted
                ? CreateDialogFailure(
                    ErrorCodes.XaeDialogReportedFailure,
                    operation,
                    observation,
                    "XAE reported a fatal target error.")
                : CreateActionFailure(operation, observation));
            return;
        }

        if (kind == XaeKnownDialogKind.Unknown)
        {
            FailUnknownDialog(
                operation,
                dialog,
                observation);
            return;
        }

        if (!string.Equals(
            operation.OperationName,
            "activate",
            StringComparison.OrdinalIgnoreCase))
        {
            FailUnexpectedDialog(
                operation,
                dialog,
                observation,
                kind);
            return;
        }

        if (!string.Equals(
            operation.ReadStage(),
            "activation.activateConfiguration",
            StringComparison.Ordinal))
        {
            FailUnexpectedDialog(
                operation,
                dialog,
                observation,
                kind);
            return;
        }

        switch (kind)
        {
            case XaeKnownDialogKind.PlatformMismatch:
                RequestAction(
                    dialog,
                    ButtonAutomationIdCancel,
                    "cancel-platform-mismatch",
                    observation);
                observation.Failure = true;
                operation.Record(
                    observation,
                    observation.ActionCompleted
                        ? null
                        : dialog.NativeWindowHandle);
                Publish(observation);
                operation.Fail(observation.ActionCompleted
                    ? CreateDialogFailure(
                        ErrorCodes.ActivationDialogDetected,
                        operation,
                        observation,
                        "The active solution platform differs from the "
                            + "selected target platform.")
                    : CreateActionFailure(operation, observation));
                return;

            case XaeKnownDialogKind.ActivationConfirmation:
                if (operation.IsActivationConfirmed())
                {
                    FailUnexpectedDialog(
                        operation,
                        dialog,
                        observation,
                        kind);
                    return;
                }

                AutostartBootProjectSelection selection =
                    ReadAutostartSelection(dialog);
                RequestAction(
                    dialog,
                    ButtonAutomationIdOk,
                    "ok-preserve-autostart",
                    observation);
                if (observation.ActionCompleted)
                {
                    operation.ConfirmActivation(selection);
                }
                else
                {
                    observation.Failure = true;
                }

                operation.Record(
                    observation,
                    observation.ActionCompleted
                        ? null
                        : dialog.NativeWindowHandle);
                Publish(observation);
                if (!observation.ActionCompleted)
                {
                    operation.Fail(CreateActionFailure(
                        operation,
                        observation));
                }

                return;

            case XaeKnownDialogKind.RunConfirmation:
                if (!operation.IsActivationConfirmed())
                {
                    FailUnexpectedDialog(
                        operation,
                        dialog,
                        observation,
                        kind);
                    return;
                }

                bool run = operation.RunAfterActivation == true;
                RequestAction(
                    dialog,
                    run
                        ? ButtonAutomationIdOk
                        : ButtonAutomationIdCancel,
                    run ? "ok-run" : "cancel-run",
                    observation);
                if (!observation.ActionCompleted)
                {
                    observation.Failure = true;
                }

                operation.Record(
                    observation,
                    observation.ActionCompleted
                        ? null
                        : dialog.NativeWindowHandle);
                Publish(observation);
                if (observation.ActionCompleted)
                {
                    operation.ConfirmRunDecision();
                }
                else
                {
                    operation.Fail(CreateActionFailure(
                        operation,
                        observation));
                }

                return;

            default:
                FailUnknownDialog(
                    operation,
                    dialog,
                    observation);
                return;
        }
    }

    private void FailUnexpectedDialog(
        XaeDialogOperationContext operation,
        CapturedDialog dialog,
        XaeDialogObservation observation,
        XaeKnownDialogKind kind)
    {
        RequestOptionalCancel(dialog, observation);
        observation.Failure = true;
        operation.Record(
            observation,
            observation.ActionCompleted
                ? null
                : dialog.NativeWindowHandle);
        Publish(observation);
        operation.Fail(CreateDialogFailure(
            ErrorCodes.XaeUnexpectedModalDialog,
            operation,
            observation,
            $"XAE displayed the known '{kind}' dialog in an "
                + "unexpected operation or stage."));
    }

    private void FailUnknownDialog(
        XaeDialogOperationContext operation,
        CapturedDialog dialog,
        XaeDialogObservation observation)
    {
        RequestOptionalCancel(dialog, observation);
        observation.Failure = true;
        operation.Record(
            observation,
            observation.ActionCompleted
                ? null
                : dialog.NativeWindowHandle);
        Publish(observation);
        operation.Fail(CreateDialogFailure(
            ErrorCodes.XaeUnknownModalDialog,
            operation,
            observation,
            "XAE displayed an unknown modal dialog."));
    }

    private static void RequestOptionalCancel(
        CapturedDialog dialog,
        XaeDialogObservation observation)
    {
        if (dialog.Buttons.ContainsKey(ButtonAutomationIdCancel))
        {
            RequestAction(
                dialog,
                ButtonAutomationIdCancel,
                "cancel-unknown-dialog",
                observation);
        }
        else
        {
            observation.Action = "none";
        }
    }

    private static void RequestFatalDismissal(
        CapturedDialog dialog,
        string action,
        XaeDialogObservation observation)
    {
        if (dialog.Buttons.ContainsKey(ButtonAutomationIdOk))
        {
            RequestAction(
                dialog,
                ButtonAutomationIdOk,
                action,
                observation);
            return;
        }

        string[] numericButtonIds = dialog.Buttons.Keys
            .Where(automationId =>
                int.TryParse(
                    automationId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
            .ToArray();
        if (numericButtonIds.Length == 1)
        {
            RequestAction(
                dialog,
                numericButtonIds[0],
                action,
                observation);
            return;
        }

        observation.Action = action;
        observation.ActionRequested = false;
        observation.ActionCompleted = false;
    }

    private static void RequestAction(
        CapturedDialog dialog,
        string automationId,
        string action,
        XaeDialogObservation observation)
    {
        observation.Action = action;
        if (!dialog.Buttons.TryGetValue(
                automationId,
                out CapturedButton? button))
        {
            observation.ActionRequested = false;
            observation.ActionCompleted = false;
            return;
        }

        observation.ActionRequested = true;
        if (!button.InvokeAvailable
            || !button.Element.TryGetCurrentPattern(
                InvokePattern.Pattern,
                out object? pattern)
            || pattern is not InvokePattern invoke)
        {
            observation.ActionCompleted = false;
            return;
        }

        try
        {
            invoke.Invoke();
            observation.ActionCompleted = WaitUntilClosed(
                dialog.NativeWindowHandle,
                DialogCloseTimeout);
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
            || exception is InvalidOperationException)
        {
            observation.ActionCompleted =
                !IsWindow(
                    new IntPtr(dialog.NativeWindowHandle));
        }
    }

    private static GatewayOperationException CreateActionFailure(
        XaeDialogOperationContext operation,
        XaeDialogObservation observation)
    {
        string code = observation.ActionRequested
            ? ErrorCodes.XaeDialogActionFailed
            : ErrorCodes.XaeDialogButtonNotFound;
        return CreateDialogFailure(
            code,
            operation,
            observation,
            observation.ActionRequested
                ? "The requested UI Automation dialog action did not "
                    + "close the dialog."
                : "The required dialog button was not found by "
                    + "AutomationId.");
    }

    private static GatewayOperationException CreateDialogFailure(
        string code,
        XaeDialogOperationContext operation,
        XaeDialogObservation observation,
        string summary)
    {
        string buttons = string.Join(
            ", ",
            observation.Buttons.Select(button =>
                $"{button.AutomationId}=\"{button.Name}\""));
        string message = LimitText(
            $"{summary} Operation={operation.OperationName}; "
                + $"Stage={operation.ReadStage()}; "
                + $"Title=\"{observation.Title}\"; "
                + $"Content=\"{observation.Content}\"; "
                + $"Buttons=[{buttons}]; "
                + $"Action={observation.Action}; "
                + $"ActionCompleted={observation.ActionCompleted}.",
            2048);
        return new GatewayOperationException(
            code,
            message,
            retryable: false,
            stage: operation.ReadStage());
    }

    private static AutostartBootProjectSelection
        ReadAutostartSelection(CapturedDialog dialog)
    {
        CapturedControl? checkbox =
            dialog.Controls.FirstOrDefault(control =>
                control.ControlType == ControlType.CheckBox
                && NormalizeText(control.Name).IndexOf(
                    "Autostart PLC Boot Project",
                    StringComparison.OrdinalIgnoreCase) >= 0);
        if (checkbox is null
            || !checkbox.Element.TryGetCurrentPattern(
                TogglePattern.Pattern,
                out object? pattern)
            || pattern is not TogglePattern toggle)
        {
            return AutostartBootProjectSelection.Unknown;
        }

        try
        {
            return toggle.Current.ToggleState switch
            {
                ToggleState.On =>
                    AutostartBootProjectSelection.Enabled,
                ToggleState.Indeterminate =>
                    AutostartBootProjectSelection.PartiallyEnabled,
                _ => AutostartBootProjectSelection.Disabled,
            };
        }
        catch (ElementNotAvailableException)
        {
            return AutostartBootProjectSelection.Unknown;
        }
    }

    private CapturedDialog? TryCaptureDialog(
        AutomationElement element)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                if (element.Current.ProcessId != _processId
                    || element.Current.ControlType
                        != ControlType.Window
                    || !string.Equals(
                        element.Current.ClassName,
                        DialogClassName,
                        StringComparison.Ordinal)
                    || !element.TryGetCurrentPattern(
                        WindowPattern.Pattern,
                        out object? windowObject)
                    || windowObject is not WindowPattern window
                    || !window.Current.IsModal)
                {
                    return null;
                }

                AutomationElementCollection descendants =
                    element.FindAll(
                        TreeScope.Descendants,
                        Condition.TrueCondition);
                if (descendants.Count == 0
                    && stopwatch.Elapsed < ControlReadyTimeout)
                {
                    Thread.Sleep(RetryInterval);
                    continue;
                }

                return CaptureDialog(element, descendants);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                if (stopwatch.Elapsed >= ControlReadyTimeout)
                {
                    return null;
                }

                Thread.Sleep(RetryInterval);
            }
        }
        while (stopwatch.Elapsed < ControlReadyTimeout);

        return null;
    }

    private static CapturedDialog CaptureDialog(
        AutomationElement element,
        AutomationElementCollection descendants)
    {
        List<CapturedControl> controls = new();
        Dictionary<string, CapturedButton> buttons =
            new(StringComparer.Ordinal);
        foreach (AutomationElement descendant in descendants)
        {
            try
            {
                ControlType controlType =
                    descendant.Current.ControlType;
                string name = descendant.Current.Name
                    ?? string.Empty;
                string automationId =
                    descendant.Current.AutomationId
                    ?? string.Empty;
                CapturedControl control = new(
                    descendant,
                    controlType,
                    name,
                    automationId);
                controls.Add(control);
                if (controlType == ControlType.Button
                    && !string.IsNullOrWhiteSpace(automationId))
                {
                    bool invokeAvailable =
                        descendant.TryGetCurrentPattern(
                            InvokePattern.Pattern,
                            out _);
                    buttons[automationId] = new CapturedButton(
                        descendant,
                        name,
                        invokeAvailable);
                }
            }
            catch (ElementNotAvailableException exception)
            {
                Trace.TraceInformation(
                    "Skipped a dialog control that disappeared during "
                        + "capture: {0}",
                    exception.Message);
            }
        }

        string content = LimitText(
            string.Join(
                " | ",
                controls
                    .Where(control =>
                        control.ControlType != ControlType.Button)
                    .Select(control => NormalizeText(control.Name))
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)),
            4096);
        int[] runtimeId = element.GetRuntimeId()
            ?? Array.Empty<int>();
        return new CapturedDialog(
            element,
            element.Current.Name ?? string.Empty,
            content,
            element.Current.FrameworkId ?? string.Empty,
            element.Current.ClassName ?? string.Empty,
            element.Current.NativeWindowHandle,
            string.Join(
                ".",
                runtimeId.Select(value =>
                    value.ToString("X", CultureInfo.InvariantCulture))),
            controls,
            buttons);
    }

    private XaeDialogObservation CreateObservation(
        CapturedDialog dialog,
        XaeDialogOperationContext? operation,
        XaeKnownDialogKind kind)
    {
        return new XaeDialogObservation
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
            OperationId = operation?.OperationId,
            OperationName = operation?.OperationName,
            Stage = operation?.ReadStage(),
            ProcessId = _processId,
            NativeWindowHandle = dialog.NativeWindowHandle,
            RuntimeId = dialog.RuntimeId,
            Title = dialog.Title,
            Content = dialog.Content,
            Kind = kind.ToString(),
            Known = kind != XaeKnownDialogKind.Unknown,
            Modal = true,
            FrameworkId = dialog.FrameworkId,
            ClassName = dialog.ClassName,
            Buttons = dialog.Buttons
                .Select(pair => new XaeDialogButtonObservation
                {
                    AutomationId = pair.Key,
                    Name = pair.Value.Name,
                    InvokeAvailable = pair.Value.InvokeAvailable,
                })
                .OrderBy(button => button.AutomationId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private void Publish(XaeDialogObservation observation)
    {
        try
        {
            DialogObserved?.Invoke(
                this,
                new XaeDialogObservationEventArgs(observation));
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "XAE dialog observation subscriber failed: {0}",
                exception);
        }
    }

    private void ClearClosedBlockingDialog()
    {
        if (_blockingDialogHandle.HasValue
            && !IsWindow(
                new IntPtr(_blockingDialogHandle.Value)))
        {
            _blockingDialogHandle = null;
        }
    }

    private static bool WaitUntilClosed(
        int nativeWindowHandle,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!IsWindow(new IntPtr(nativeWindowHandle)))
            {
                return true;
            }

            Thread.Sleep(RetryInterval);
        }

        return !IsWindow(new IntPtr(nativeWindowHandle));
    }

    private static string NormalizeText(string? value)
    {
        return Regex.Replace(
            value ?? string.Empty,
            "\\s+",
            " ").Trim();
    }

    private static string LimitText(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value.Substring(0, maximumLength);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(XaeDialogSupervisor));
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        [Out] char[] className,
        int maximumCount);

    private delegate bool EnumWindowsCallback(
        IntPtr windowHandle,
        IntPtr parameter);

    private sealed class CapturedDialog
    {
        public CapturedDialog(
            AutomationElement element,
            string title,
            string content,
            string frameworkId,
            string className,
            int nativeWindowHandle,
            string runtimeId,
            IReadOnlyList<CapturedControl> controls,
            IReadOnlyDictionary<string, CapturedButton> buttons)
        {
            Element = element;
            Title = title;
            Content = content;
            FrameworkId = frameworkId;
            ClassName = className;
            NativeWindowHandle = nativeWindowHandle;
            RuntimeId = runtimeId;
            Controls = controls;
            Buttons = buttons;
        }

        public AutomationElement Element { get; }

        public string Title { get; }

        public string Content { get; }

        public string FrameworkId { get; }

        public string ClassName { get; }

        public int NativeWindowHandle { get; }

        public string RuntimeId { get; }

        public IReadOnlyList<CapturedControl> Controls { get; }

        public IReadOnlyDictionary<string, CapturedButton> Buttons { get; }
    }

    private sealed class CapturedControl
    {
        public CapturedControl(
            AutomationElement element,
            ControlType controlType,
            string name,
            string automationId)
        {
            Element = element;
            ControlType = controlType;
            Name = name;
            AutomationId = automationId;
        }

        public AutomationElement Element { get; }

        public ControlType ControlType { get; }

        public string Name { get; }

        public string AutomationId { get; }
    }

    private sealed class CapturedButton
    {
        public CapturedButton(
            AutomationElement element,
            string name,
            bool invokeAvailable)
        {
            Element = element;
            Name = name;
            InvokeAvailable = invokeAvailable;
        }

        public AutomationElement Element { get; }

        public string Name { get; }

        public bool InvokeAvailable { get; }
    }
}

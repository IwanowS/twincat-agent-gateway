using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal enum XaeActivationDialogKind
{
    Unknown,
    PlatformMismatch,
    ActivationConfirmation,
    RunConfirmation,
    FatalError,
}

internal sealed class XaeActivationDialogInfo
{
    public XaeActivationDialogInfo(
        IntPtr windowHandle,
        string title,
        string text,
        XaeActivationDialogKind kind,
        string action,
        bool actionRequested)
    {
        WindowHandle = windowHandle;
        Title = title;
        Text = text;
        Kind = kind;
        Action = action;
        ActionRequested = actionRequested;
    }

    public IntPtr WindowHandle { get; }

    public string Title { get; }

    public string Text { get; }

    public XaeActivationDialogKind Kind { get; }

    public string Action { get; }

    public bool ActionRequested { get; }
}

public sealed class XaeActivationCommandResult
{
    public bool ActivationConfirmed { get; set; }

    public bool RunDecisionHandled { get; set; }

    public bool RunRequested { get; set; }

    public AutostartBootProjectSelection AutostartSelection { get; set; }

    public IReadOnlyList<XaeActivationDialogObservation> Dialogs { get; set; } =
        Array.Empty<XaeActivationDialogObservation>();
}

public sealed class XaeActivationDialogObservation
{
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool ActionRequested { get; set; }
}

internal sealed class XaeActivationDialogController : IDisposable
{
    private const int DialogButtonCancel = 2;
    private const int DialogButtonOk = 1;
    private const int MaximumTextLength = 4096;
    private const uint ButtonMessageClick = 0x00F5;
    private const uint ButtonMessageGetCheck = 0x00F0;
    private const uint ButtonStateChecked = 1;
    private const uint ButtonStateIndeterminate = 2;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint WindowMessageClose = 0x0010;
    private const uint WindowMessageGetText = 0x000D;
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly char[] WhitespaceCharacters =
    {
        ' ',
        '\t',
        '\r',
        '\n',
    };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<XaeActivationDialogInfo> _failure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<IntPtr, string> _handledWindows = new();
    private readonly List<XaeActivationDialogInfo> _observed = new();
    private readonly object _sync = new();
    private readonly int _processId;
    private readonly bool _runAfterActivation;
    private readonly Task _worker;
    private AutostartBootProjectSelection _autostartSelection =
        AutostartBootProjectSelection.Unknown;
    private bool _activationConfirmed;
    private bool _runDecisionHandled;
    private int _disposed;

    private XaeActivationDialogController(
        int processId,
        bool runAfterActivation)
    {
        _processId = processId;
        _runAfterActivation = runAfterActivation;
        _worker = Task.Run(MonitorLoop);
    }

    public Task<XaeActivationDialogInfo> FailureDetected =>
        _failure.Task;

    public static XaeActivationDialogController Start(
        int processId,
        bool runAfterActivation)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "The XAE process ID must be positive.");
        }

        return new XaeActivationDialogController(
            processId,
            runAfterActivation);
    }

    internal static XaeActivationDialogKind ClassifyDialog(
        string? title,
        string? text)
    {
        string normalizedTitle = NormalizeText(title);
        string normalizedText = NormalizeText(text);
        if (normalizedText.IndexOf(
                "Active solution platform",
                StringComparison.OrdinalIgnoreCase) >= 0
            && normalizedText.IndexOf(
                "differs from current target platform",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeActivationDialogKind.PlatformMismatch;
        }

        if (string.Equals(
                normalizedTitle,
                "Activate Configuration",
                StringComparison.OrdinalIgnoreCase)
            && normalizedText.IndexOf(
                "Autostart PLC Boot Project",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeActivationDialogKind.ActivationConfirmation;
        }

        if (normalizedText.IndexOf(
                "Restart TwinCAT System in Run Mode",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeActivationDialogKind.RunConfirmation;
        }

        if (normalizedTitle.IndexOf(
                "Target system reports a fatal error",
                StringComparison.OrdinalIgnoreCase) >= 0
            || normalizedText.IndexOf(
                "Target system reports a fatal error",
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return XaeActivationDialogKind.FatalError;
        }

        return XaeActivationDialogKind.Unknown;
    }

    public XaeActivationCommandResult StopAndGetResult()
    {
        StopWorker();
        lock (_sync)
        {
            return new XaeActivationCommandResult
            {
                ActivationConfirmed = _activationConfirmed,
                RunDecisionHandled = _runDecisionHandled,
                RunRequested = _runAfterActivation,
                AutostartSelection = _autostartSelection,
                Dialogs = _observed
                    .Select(item =>
                        new XaeActivationDialogObservation
                        {
                            Kind = item.Kind.ToString(),
                            Title = item.Title,
                            Text = item.Text,
                            Action = item.Action,
                            ActionRequested = item.ActionRequested,
                        })
                    .ToArray(),
            };
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopWorker();
        _cancellation.Dispose();
    }

    private void MonitorLoop()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            CapturedDialog? dialog = FindFirstUnhandledDialog();
            if (dialog is not null)
            {
                HandleDialog(dialog);
                if (_failure.Task.IsCompleted)
                {
                    return;
                }
            }

            cancellationToken.WaitHandle.WaitOne(PollInterval);
        }
    }

    private void HandleDialog(CapturedDialog dialog)
    {
        string action;
        bool actionRequested;
        switch (dialog.Kind)
        {
            case XaeActivationDialogKind.PlatformMismatch:
                action = "cancel";
                actionRequested = RequestButtonAction(
                    dialog.WindowHandle,
                    DialogButtonCancel);
                RecordFailure(dialog, action, actionRequested);
                return;

            case XaeActivationDialogKind.ActivationConfirmation:
                action = "ok-preserve-autostart";
                AutostartBootProjectSelection selection =
                    ReadAutostartSelection(dialog.Controls);
                actionRequested = RequestButtonAction(
                    dialog.WindowHandle,
                    DialogButtonOk);
                RecordDialog(dialog, action, actionRequested);
                if (!actionRequested)
                {
                    RecordFailure(dialog, action, actionRequested);
                    return;
                }

                lock (_sync)
                {
                    _activationConfirmed = true;
                    _autostartSelection = selection;
                }

                return;

            case XaeActivationDialogKind.RunConfirmation:
                action = _runAfterActivation
                    ? "ok-run"
                    : "cancel-run";
                actionRequested = RequestButtonAction(
                    dialog.WindowHandle,
                    _runAfterActivation
                        ? DialogButtonOk
                        : DialogButtonCancel);
                RecordDialog(dialog, action, actionRequested);
                if (!actionRequested)
                {
                    RecordFailure(dialog, action, actionRequested);
                    return;
                }

                lock (_sync)
                {
                    _runDecisionHandled = true;
                }

                return;

            case XaeActivationDialogKind.FatalError:
                action = "dismiss";
                actionRequested = RequestButtonAction(
                        dialog.WindowHandle,
                        DialogButtonOk)
                    || RequestButtonAction(
                        dialog.WindowHandle,
                        DialogButtonCancel)
                    || PostMessage(
                        dialog.WindowHandle,
                        WindowMessageClose,
                        IntPtr.Zero,
                        IntPtr.Zero);
                RecordFailure(dialog, action, actionRequested);
                return;

            default:
                action = "none";
                actionRequested = false;
                RecordFailure(dialog, action, actionRequested);
                return;
        }
    }

    private void RecordDialog(
        CapturedDialog dialog,
        string action,
        bool actionRequested)
    {
        XaeActivationDialogInfo info = new(
            dialog.WindowHandle,
            dialog.Title,
            dialog.Text,
            dialog.Kind,
            action,
            actionRequested);
        lock (_sync)
        {
            _observed.Add(info);
        }
    }

    private void RecordFailure(
        CapturedDialog dialog,
        string action,
        bool actionRequested)
    {
        XaeActivationDialogInfo info = new(
            dialog.WindowHandle,
            dialog.Title,
            dialog.Text,
            dialog.Kind,
            action,
            actionRequested);
        lock (_sync)
        {
            if (!_observed.Any(item =>
                item.WindowHandle == dialog.WindowHandle))
            {
                _observed.Add(info);
            }
        }

        _failure.TrySetResult(info);
    }

    private CapturedDialog? FindFirstUnhandledDialog()
    {
        CapturedDialog? result = null;
        EnumWindows(
            (window, _) =>
            {
                if (!IsWindowVisible(window)
                    || !string.Equals(
                        ReadClassName(window),
                        "#32770",
                        StringComparison.Ordinal))
                {
                    return true;
                }

                uint threadId = GetWindowThreadProcessId(
                    window,
                    out uint windowProcessId);
                if (threadId == 0
                    || windowProcessId != (uint)_processId)
                {
                    return true;
                }

                string title = ReadTopLevelText(window);
                List<DialogControl> controls =
                    ReadDialogControls(window);
                string text = LimitText(
                    string.Join(
                        " | ",
                        controls
                            .Select(control => control.Text)
                            .Where(value =>
                                !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.Ordinal)));
                string fingerprint = title + "\n" + text;
                lock (_sync)
                {
                    if (_handledWindows.TryGetValue(
                            window,
                            out string? previous)
                        && string.Equals(
                            previous,
                            fingerprint,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }

                    _handledWindows[window] = fingerprint;
                }

                result = new CapturedDialog(
                    window,
                    title,
                    text,
                    ClassifyDialog(title, text),
                    controls);
                return false;
            },
            IntPtr.Zero);
        return result;
    }

    private static List<DialogControl> ReadDialogControls(
        IntPtr dialog)
    {
        List<DialogControl> controls = new();
        EnumChildWindows(
            dialog,
            (window, _) =>
            {
                string className = ReadClassName(window);
                if (!string.Equals(
                        className,
                        "Static",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        className,
                        "Button",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        className,
                        "Edit",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                controls.Add(
                    new DialogControl(
                        window,
                        className,
                        ReadControlText(window)));
                return true;
            },
            IntPtr.Zero);
        return controls;
    }

    private static AutostartBootProjectSelection
        ReadAutostartSelection(
        IReadOnlyList<DialogControl> controls)
    {
        DialogControl? checkbox = controls.FirstOrDefault(control =>
            string.Equals(
                control.ClassName,
                "Button",
                StringComparison.OrdinalIgnoreCase)
            && control.Text.IndexOf(
                "Autostart PLC Boot Project",
                StringComparison.OrdinalIgnoreCase) >= 0);
        if (checkbox is null)
        {
            return AutostartBootProjectSelection.Unknown;
        }

        IntPtr sent = SendMessageTimeout(
            checkbox.WindowHandle,
            ButtonMessageGetCheck,
            UIntPtr.Zero,
            IntPtr.Zero,
            SendMessageAbortIfHung,
            250,
            out UIntPtr result);
        if (sent == IntPtr.Zero)
        {
            return AutostartBootProjectSelection.Unknown;
        }

        uint state = result.ToUInt32();
        if (state == ButtonStateChecked)
        {
            return AutostartBootProjectSelection.Enabled;
        }

        if (state == ButtonStateIndeterminate)
        {
            return AutostartBootProjectSelection.PartiallyEnabled;
        }

        return AutostartBootProjectSelection.Disabled;
    }

    private static bool RequestButtonAction(
        IntPtr dialog,
        int buttonId)
    {
        IntPtr button = GetDlgItem(dialog, buttonId);
        return button != IntPtr.Zero
            && PostMessage(
                button,
                ButtonMessageClick,
                IntPtr.Zero,
                IntPtr.Zero);
    }

    private void StopWorker()
    {
        _cancellation.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner =>
                inner is OperationCanceledException))
        {
        }
    }

    private static string ReadTopLevelText(IntPtr window)
    {
        char[] text = new char[MaximumTextLength];
        int length = GetWindowText(
            window,
            text,
            text.Length);
        return length == 0
            ? string.Empty
            : LimitText(
                NormalizeText(
                    new string(text, 0, length)));
    }

    private static string ReadControlText(IntPtr window)
    {
        char[] text = new char[MaximumTextLength];
        IntPtr sent = SendMessageTimeout(
            window,
            WindowMessageGetText,
            new UIntPtr((uint)text.Length),
            text,
            SendMessageAbortIfHung,
            250,
            out UIntPtr result);
        return sent == IntPtr.Zero
            ? string.Empty
            : LimitText(
                NormalizeText(
                    new string(
                        text,
                        0,
                        Math.Min(
                            checked((int)result.ToUInt32()),
                            text.Length))));
    }

    private static string ReadClassName(IntPtr window)
    {
        char[] className = new char[256];
        int length = GetClassName(
            window,
            className,
            className.Length);
        return length == 0
            ? string.Empty
            : new string(className, 0, length);
    }

    private static string NormalizeText(string? value)
    {
        return string.Join(
            " ",
            (value ?? string.Empty).Split(
                WhitespaceCharacters,
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static string LimitText(string value)
    {
        return value.Length <= MaximumTextLength
            ? value
            : value.Substring(0, MaximumTextLength);
    }

    private delegate bool EnumWindowsCallback(
        IntPtr window,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent,
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(
        IntPtr dialog,
        int controlId);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr window,
        [Out] char[] text,
        int maximumCount);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetClassName(
        IntPtr window,
        [Out] char[] className,
        int maximumCount);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wordParameter,
        [Out] char[] longParameter,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wordParameter,
        IntPtr longParameter,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);

    private sealed class CapturedDialog
    {
        public CapturedDialog(
            IntPtr windowHandle,
            string title,
            string text,
            XaeActivationDialogKind kind,
            IReadOnlyList<DialogControl> controls)
        {
            WindowHandle = windowHandle;
            Title = title;
            Text = text;
            Kind = kind;
            Controls = controls;
        }

        public IntPtr WindowHandle { get; }

        public string Title { get; }

        public string Text { get; }

        public XaeActivationDialogKind Kind { get; }

        public IReadOnlyList<DialogControl> Controls { get; }
    }

    private sealed class DialogControl
    {
        public DialogControl(
            IntPtr windowHandle,
            string className,
            string text)
        {
            WindowHandle = windowHandle;
            ClassName = className;
            Text = text;
        }

        public IntPtr WindowHandle { get; }

        public string ClassName { get; }

        public string Text { get; }
    }
}

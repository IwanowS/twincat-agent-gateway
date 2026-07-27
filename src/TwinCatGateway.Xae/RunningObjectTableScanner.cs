using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal static class RunningObjectTableScanner
{
    public static RotScanResult Scan()
    {
        int hResult = CreateBindCtx(0, out IBindCtx? bindContext);
        Marshal.ThrowExceptionForHR(hResult);
        hResult = GetRunningObjectTable(0, out IRunningObjectTable? table);
        Marshal.ThrowExceptionForHR(hResult);

        List<RunningXaeCandidate> candidates = new();
        IEnumMoniker? enumerator = null;
        try
        {
            table.EnumRunning(out enumerator);
            enumerator.Reset();
            IMoniker[] monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    string? displayName = TryGetDisplayName(
                        moniker,
                        bindContext);
                    if (displayName is null)
                    {
                        continue;
                    }

                    if (!IsXaeDteMoniker(displayName))
                    {
                        continue;
                    }

                    try
                    {
                        table.GetObject(moniker, out object dte);
                        candidates.Add(InspectCandidate(displayName, dte));
                    }
                    catch (Exception exception)
                    {
                        candidates.Add(CreateInspectionFailure(
                            displayName,
                            exception));
                    }
                }
                finally
                {
                    ComObject.Release(moniker);
                }
            }
        }
        finally
        {
            ComObject.Release(enumerator);
            ComObject.Release(table);
            ComObject.Release(bindContext);
        }

        return new RotScanResult(candidates);
    }

    private static RunningXaeCandidate InspectCandidate(
        string moniker,
        object dte)
    {
        try
        {
            return new RunningXaeCandidate(
                InspectDte(moniker, dte),
                dte);
        }
        catch (Exception exception)
        {
            ComObject.Release(dte);
            return CreateInspectionFailure(moniker, exception);
        }
    }

    private static RunningXaeCandidate CreateInspectionFailure(
        string moniker,
        Exception exception)
    {
        DteInstanceInfo info = new()
        {
            Moniker = moniker,
            ProgId = GetProgId(moniker),
            ProcessId = GetProcessId(moniker),
            InspectionError =
                "The DTE instance could not be inspected.",
            InspectionHResult = exception.HResult,
        };
        Trace.TraceWarning(
            "Could not access ROT entry '{0}': {1}",
            moniker,
            exception);
        return new RunningXaeCandidate(info, dte: null);
    }

    internal static DteInstanceInfo InspectDte(
        string moniker,
        object dte)
    {
        DteInstanceInfo info = new()
        {
            Moniker = moniker,
            ProgId = GetProgId(moniker),
            ProcessId = GetProcessId(moniker),
        };
        dynamic automation = dte;
        info.Version = Convert.ToString(
            automation.Version,
            CultureInfo.InvariantCulture);
        object? solution = null;
        object? mainWindow = null;
        try
        {
            solution = automation.Solution;
            string? solutionPath = Convert.ToString(
                ((dynamic)solution).FullName,
                CultureInfo.InvariantCulture);
            info.Solution = NormalizeOptionalPath(solutionPath);
            info.SolutionLoaded = !string.IsNullOrWhiteSpace(info.Solution);

            if (info.ProcessId is null)
            {
                mainWindow = automation.MainWindow;
                IntPtr windowHandle = new(
                    Convert.ToInt32(
                        ((dynamic)mainWindow).HWnd,
                        CultureInfo.InvariantCulture));
                uint windowThreadId = GetWindowThreadProcessId(
                    windowHandle,
                    out uint processId);
                if (windowThreadId == 0 || processId == 0)
                {
                    throw new InvalidOperationException(
                        "The DTE main window process could not be identified.");
                }

                info.ProcessId = checked((int)processId);
            }
        }
        finally
        {
            ComObject.Release(mainWindow);
            ComObject.Release(solution);
        }

        return info;
    }

    private static string? TryGetDisplayName(
        IMoniker moniker,
        IBindCtx bindContext)
    {
        try
        {
            moniker.GetDisplayName(
                bindContext,
                null,
                out string displayName);
            return displayName;
        }
        catch (UnauthorizedAccessException exception)
        {
            Trace.TraceWarning(
                "Could not inspect an access-restricted ROT moniker: {0}",
                exception.Message);
            return null;
        }
        catch (COMException exception)
        {
            Trace.TraceWarning(
                "Could not inspect a ROT moniker. HRESULT: 0x{0:X8}.",
                exception.HResult);
            return null;
        }
    }

    private static bool IsXaeDteMoniker(string displayName)
    {
        return displayName.IndexOf(
            "!VisualStudio.DTE.",
            StringComparison.OrdinalIgnoreCase) >= 0
            || displayName.IndexOf(
                "!TcXaeShell.DTE.",
                StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? GetProgId(string moniker)
    {
        int start = moniker.IndexOf('!');
        int end = moniker.IndexOf(':');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return moniker.Substring(start + 1, end - start - 1);
    }

    private static int? GetProcessId(string moniker)
    {
        int separator = moniker.LastIndexOf(':');
        if (separator < 0
            || !int.TryParse(
                moniker.Substring(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || processId <= 0)
        {
            return null;
        }

        return processId;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(
        int reserved,
        out IBindCtx bindContext);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(
        int reserved,
        out IRunningObjectTable runningObjectTable);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}

internal sealed class RotScanResult : IDisposable
{
    public RotScanResult(IReadOnlyList<RunningXaeCandidate> candidates)
    {
        Candidates = candidates;
    }

    public IReadOnlyList<RunningXaeCandidate> Candidates { get; }

    public void Dispose()
    {
        foreach (RunningXaeCandidate candidate in Candidates)
        {
            candidate.Dispose();
        }
    }
}

internal sealed class RunningXaeCandidate : IDisposable
{
    public RunningXaeCandidate(DteInstanceInfo info, object? dte)
    {
        Info = info;
        Dte = dte;
    }

    public DteInstanceInfo Info { get; }

    public object? Dte { get; private set; }

    public object TakeDte()
    {
        object result = Dte
            ?? throw new InvalidOperationException(
                "The ROT candidate has no usable DTE object.");
        Dte = null;
        return result;
    }

    public void Dispose()
    {
        ComObject.Release(Dte);
        Dte = null;
    }
}

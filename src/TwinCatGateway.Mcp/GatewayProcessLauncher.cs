using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace TwinCatGateway.Mcp;

public interface IGatewayProcessLauncher
{
    void Launch(
        string command,
        string configurationPath);
}

public sealed class InteractiveGatewayLaunchException
    : InvalidOperationException
{
    public InteractiveGatewayLaunchException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class GatewayProcessLauncher
    : IGatewayProcessLauncher
{
    private readonly IExplorerShellExecutor _executor;

    public GatewayProcessLauncher()
        : this(new ExplorerShellExecutor())
    {
    }

    internal GatewayProcessLauncher(
        IExplorerShellExecutor executor)
    {
        _executor = executor
            ?? throw new ArgumentNullException(
                nameof(executor));
    }

    public void Launch(
        string command,
        string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(
                "Gateway command is required.",
                nameof(command));
        }

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            throw new ArgumentException(
                "Gateway configuration path is required.",
                nameof(configurationPath));
        }

        string executable =
            GatewayCommandResolver.Resolve(command);
        string fullConfigurationPath =
            Path.GetFullPath(configurationPath);
        string workingDirectory =
            Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException(
                "Gateway command has no parent directory.");
        string arguments =
            "--config "
            + QuoteArgument(fullConfigurationPath)
            + " --launch-source agent";

        try
        {
            _executor.Execute(
                executable,
                arguments,
                workingDirectory);
        }
        catch (Exception exception)
            when (exception
                is not InteractiveGatewayLaunchException)
        {
            throw new InteractiveGatewayLaunchException(
                "The desktop gateway could not be launched "
                + "through the interactive Windows Explorer "
                + "session. Start twincat-gateway manually "
                + "for this project.",
                exception);
        }
    }

    private static string QuoteArgument(string value)
    {
        return "\""
            + value.Replace("\"", "\\\"")
            + "\"";
    }
}

internal interface IExplorerShellExecutor
{
    void Execute(
        string executable,
        string arguments,
        string workingDirectory);
}

internal sealed class ExplorerShellExecutor
    : IExplorerShellExecutor
{
    public void Execute(
        string executable,
        string arguments,
        string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Interactive Explorer launch requires Windows.");
        }

        ExecuteWindows(
            executable,
            arguments,
            workingDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static void ExecuteWindows(
        string executable,
        string arguments,
        string workingDirectory)
    {
        Exception? failure = null;
        Thread thread = new(
            () =>
            {
                try
                {
                    ExecuteOnSta(
                        executable,
                        arguments,
                        workingDirectory);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            })
        {
            IsBackground = true,
            Name = "TwinCAT gateway Explorer launcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InteractiveGatewayLaunchException(
                "Windows Explorer did not accept the desktop "
                + "gateway launch request.",
                failure);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ExecuteOnSta(
        string executable,
        string arguments,
        string workingDirectory)
    {
        object? shellWindows = null;
        object? desktopDispatch = null;
        object? browserObject = null;
        IShellView? shellView = null;
        object? backgroundDispatch = null;
        object? shellApplication = null;

        try
        {
            Type shellWindowsType =
                Type.GetTypeFromCLSID(
                    NativeIds.ShellWindowsClass,
                    throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Windows ShellWindows COM class "
                    + "is unavailable.");
            shellWindows =
                Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException(
                    "Windows ShellWindows COM class "
                    + "could not be created.");

            dynamic windows = shellWindows;
            object? location = null;
            object? root = null;
            int desktopWindow;
            desktopDispatch = windows.FindWindowSW(
                ref location,
                ref root,
                NativeIds.ShellWindowClassDesktop,
                out desktopWindow,
                NativeIds.ShellWindowNeedDispatch);
            if (desktopDispatch is null
                || desktopWindow == 0)
            {
                throw new InvalidOperationException(
                    "The interactive Windows Explorer desktop "
                    + "was not found.");
            }

            IComServiceProvider serviceProvider =
                (IComServiceProvider)desktopDispatch;
            Guid service = NativeIds.TopLevelBrowserService;
            Guid browserInterface =
                typeof(IShellBrowser).GUID;
            int result = serviceProvider.QueryService(
                ref service,
                ref browserInterface,
                out browserObject);
            Marshal.ThrowExceptionForHR(result);

            IShellBrowser browser =
                (IShellBrowser)browserObject;
            result = browser.QueryActiveShellView(
                out shellView);
            Marshal.ThrowExceptionForHR(result);

            Guid dispatchInterface =
                NativeIds.DispatchInterface;
            result = shellView.GetItemObject(
                NativeIds.ShellViewBackground,
                ref dispatchInterface,
                out backgroundDispatch);
            Marshal.ThrowExceptionForHR(result);

            dynamic folderView = backgroundDispatch;
            shellApplication = folderView.Application;
            dynamic shell = shellApplication;
            shell.ShellExecute(
                executable,
                arguments,
                workingDirectory,
                "open",
                NativeIds.ShowNormal);
        }
        finally
        {
            ReleaseComObject(shellApplication);
            ReleaseComObject(backgroundDispatch);
            ReleaseComObject(shellView);
            ReleaseComObject(browserObject);
            ReleaseComObject(desktopDispatch);
            ReleaseComObject(shellWindows);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value is not null
            && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private static class NativeIds
    {
        internal const int ShellWindowClassDesktop = 8;
        internal const int ShellWindowNeedDispatch = 1;
        internal const uint ShellViewBackground = 0;
        internal const int ShowNormal = 1;

        internal static readonly Guid ShellWindowsClass =
            new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

        internal static readonly Guid TopLevelBrowserService =
            new("4C96BE40-915C-11CF-99D3-00AA004AE837");

        internal static readonly Guid DispatchInterface =
            new("00020400-0000-0000-C000-000000000046");
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid service,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)]
            out object result);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr window);

        [PreserveSig]
        int ContextSensitiveHelp(
            [MarshalAs(UnmanagedType.Bool)]
            bool enterMode);

        [PreserveSig]
        int InsertMenusSB(
            IntPtr sharedMenu,
            IntPtr menuWidths);

        [PreserveSig]
        int SetMenuSB(
            IntPtr sharedMenu,
            IntPtr holeMenu,
            IntPtr activeObject);

        [PreserveSig]
        int RemoveMenusSB(IntPtr sharedMenu);

        [PreserveSig]
        int SetStatusTextSB(
            [MarshalAs(UnmanagedType.LPWStr)]
            string statusText);

        [PreserveSig]
        int EnableModelessSB(
            [MarshalAs(UnmanagedType.Bool)]
            bool enable);

        [PreserveSig]
        int TranslateAcceleratorSB(
            IntPtr message,
            ushort commandId);

        [PreserveSig]
        int BrowseObject(
            IntPtr itemIdList,
            uint flags);

        [PreserveSig]
        int GetViewStateStream(
            uint mode,
            out IntPtr stream);

        [PreserveSig]
        int GetControlWindow(
            uint controlId,
            out IntPtr window);

        [PreserveSig]
        int SendControlMsg(
            uint controlId,
            uint message,
            IntPtr wordParameter,
            IntPtr longParameter,
            out IntPtr result);

        [PreserveSig]
        int QueryActiveShellView(
            out IShellView shellView);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig]
        int GetWindow(out IntPtr window);

        [PreserveSig]
        int ContextSensitiveHelp(
            [MarshalAs(UnmanagedType.Bool)]
            bool enterMode);

        [PreserveSig]
        int TranslateAccelerator(IntPtr message);

        [PreserveSig]
        int EnableModeless(
            [MarshalAs(UnmanagedType.Bool)]
            bool enable);

        [PreserveSig]
        int UIActivate(uint state);

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int CreateViewWindow(
            IntPtr previousView,
            IntPtr folderSettings,
            IntPtr shellBrowser,
            IntPtr viewRectangle,
            out IntPtr window);

        [PreserveSig]
        int DestroyViewWindow();

        [PreserveSig]
        int GetCurrentInfo(IntPtr folderSettings);

        [PreserveSig]
        int AddPropertySheetPages(
            uint reserved,
            IntPtr addPage,
            IntPtr longParameter);

        [PreserveSig]
        int SaveViewState();

        [PreserveSig]
        int SelectItem(
            IntPtr itemIdList,
            uint flags);

        [PreserveSig]
        int GetItemObject(
            uint item,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)]
            out object result);
    }
}

internal static class GatewayCommandResolver
{
    internal static string Resolve(string command)
    {
        if (Path.IsPathRooted(command)
            || command.Contains(
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || command.Contains(
                Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return RequireExistingFile(
                Path.GetFullPath(command));
        }

        foreach (string directory in GetSearchDirectories())
        {
            foreach (string extension in GetExtensions(command))
            {
                string candidate = Path.Combine(
                    directory,
                    command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException(
            $"Gateway command '{command}' was not found.");
    }

    private static IEnumerable<string>
        GetSearchDirectories()
    {
        string? path =
            Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (string directory in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string>
        GetExtensions(string command)
    {
        if (Path.HasExtension(command))
        {
            yield return string.Empty;
            yield break;
        }

        string? pathExtensions =
            Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            yield return ".exe";
            yield return ".cmd";
            yield return ".bat";
            yield break;
        }

        foreach (string extension in pathExtensions.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
        {
            yield return extension.StartsWith('.')
                ? extension
                : "." + extension;
        }
    }

    private static string RequireExistingFile(
        string path)
    {
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Gateway command '{path}' was not found.",
                path);
    }
}

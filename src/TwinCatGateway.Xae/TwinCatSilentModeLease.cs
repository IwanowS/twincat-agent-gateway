using System;
using EnvDTE;
using TCatSysManagerLib;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

internal sealed class TwinCatSilentModeLease : IDisposable
{
    private readonly bool _previousValue;
    private readonly bool _restoreOnDispose;
    private ITwinCatSilentModeSettings? _settings;

    private TwinCatSilentModeLease(
        ITwinCatSilentModeSettings settings,
        bool previousValue,
        bool restoreOnDispose)
    {
        _settings = settings;
        _previousValue = previousValue;
        _restoreOnDispose = restoreOnDispose;
    }

    public static TwinCatSilentModeLease Enable(
        DTE dte,
        bool restoreOnDispose)
    {
        return Enable(GetSettings(dte), restoreOnDispose);
    }

    internal static TwinCatSilentModeLease Enable(
        ITwinCatSilentModeSettings settings,
        bool restoreOnDispose)
    {
        bool previousValue = false;
        bool previousValueRead = false;
        try
        {
            previousValue = settings.SilentMode;
            previousValueRead = true;
            settings.SilentMode = true;
            if (!settings.SilentMode)
            {
                throw new InvalidOperationException(
                    "TwinCAT did not retain the enabled Silent Mode value.");
            }

            return new TwinCatSilentModeLease(
                settings,
                previousValue,
                restoreOnDispose);
        }
        catch (Exception exception)
        {
            Exception failure = exception;
            if (previousValueRead)
            {
                try
                {
                    RestoreAndVerify(settings, previousValue);
                }
                catch (Exception restoreException)
                {
                    failure = new AggregateException(
                        exception,
                        restoreException);
                }
            }

            settings.Dispose();
            throw CreateFailure(
                "TwinCAT Silent Mode could not be enabled.",
                failure);
        }
    }

    public static bool Read(DTE dte)
    {
        using TwinCatAutomationSettings settings =
            GetSettings(dte);
        try
        {
            return settings.SilentMode;
        }
        catch (Exception exception)
        {
            throw CreateFailure(
                "TwinCAT Silent Mode could not be read.",
                exception);
        }
    }

    public void Dispose()
    {
        ITwinCatSilentModeSettings? settings = _settings;
        if (settings is null)
        {
            return;
        }

        _settings = null;
        try
        {
            if (_restoreOnDispose)
            {
                RestoreAndVerify(settings, _previousValue);
            }
        }
        catch (Exception exception)
        {
            throw CreateFailure(
                "The previous TwinCAT Silent Mode value could not be restored.",
                exception);
        }
        finally
        {
            settings.Dispose();
        }
    }

    private static TwinCatAutomationSettings GetSettings(DTE dte)
    {
        object? settingsObject = null;
        try
        {
            settingsObject = dte.GetObject("TcAutomationSettings");
            if (settingsObject is not ITcAutomationSettings settings)
            {
                throw new InvalidCastException(
                    "TcAutomationSettings does not implement ITcAutomationSettings.");
            }

            settingsObject = null;
            return new TwinCatAutomationSettings(settings);
        }
        catch (Exception exception)
        {
            ComObject.Release(settingsObject);
            throw CreateFailure(
                "Typed TwinCAT automation settings are unavailable.",
                exception);
        }
    }

    private static void RestoreAndVerify(
        ITwinCatSilentModeSettings settings,
        bool expectedValue)
    {
        settings.SilentMode = expectedValue;
        if (settings.SilentMode != expectedValue)
        {
            throw new InvalidOperationException(
                "TwinCAT did not retain the restored Silent Mode value.");
        }
    }

    private static GatewayOperationException CreateFailure(
        string message,
        Exception exception)
    {
        return exception as GatewayOperationException
            ?? new GatewayOperationException(
                ErrorCodes.XaeSilentModeFailed,
                message,
                retryable: false,
                stage: "xae.silentMode",
                innerException: exception);
    }
}

internal interface ITwinCatSilentModeSettings : IDisposable
{
    bool SilentMode { get; set; }
}

internal sealed class TwinCatAutomationSettings :
    ITwinCatSilentModeSettings
{
    private ITcAutomationSettings? _settings;

    public TwinCatAutomationSettings(
        ITcAutomationSettings settings)
    {
        _settings = settings;
    }

    public bool SilentMode
    {
        get => GetRequiredSettings().SilentMode;
        set => GetRequiredSettings().SilentMode = value;
    }

    public void Dispose()
    {
        ITcAutomationSettings? settings = _settings;
        _settings = null;
        ComObject.Release(settings);
    }

    private ITcAutomationSettings GetRequiredSettings()
    {
        return _settings
            ?? throw new ObjectDisposedException(
                nameof(TwinCatAutomationSettings));
    }
}

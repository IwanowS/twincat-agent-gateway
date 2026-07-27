using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class TwinCatSilentModeLeaseTests
{
    [Fact]
    public void RestoresPreviousDisabledValue()
    {
        FakeSilentModeSettings settings = new(initialValue: false);

        using (TwinCatSilentModeLease.Enable(
            settings,
            restoreOnDispose: true))
        {
            Assert.True(settings.SilentMode);
        }

        Assert.False(settings.Value);
        Assert.True(settings.Disposed);
    }

    [Fact]
    public void PreservesPreviousEnabledValue()
    {
        FakeSilentModeSettings settings = new(initialValue: true);

        using (TwinCatSilentModeLease.Enable(
            settings,
            restoreOnDispose: true))
        {
            Assert.True(settings.SilentMode);
        }

        Assert.True(settings.Value);
        Assert.True(settings.Disposed);
    }

    [Fact]
    public void FailedEnableUsesStableErrorAndDisposesSettings()
    {
        FakeSilentModeSettings settings = new(initialValue: false)
        {
            IgnoreEnableWrite = true,
        };

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => TwinCatSilentModeLease.Enable(
                    settings,
                    restoreOnDispose: true));

        Assert.Equal(
            ErrorCodes.XaeSilentModeFailed,
            exception.Code);
        Assert.False(settings.Value);
        Assert.True(settings.Disposed);
    }

    [Fact]
    public void FailedRestoreUsesStableErrorAndDisposesSettings()
    {
        FakeSilentModeSettings settings = new(initialValue: false);
        TwinCatSilentModeLease lease =
            TwinCatSilentModeLease.Enable(
                settings,
                restoreOnDispose: true);
        settings.FailDisableWrite = true;

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                lease.Dispose);

        Assert.Equal(
            ErrorCodes.XaeSilentModeFailed,
            exception.Code);
        Assert.True(settings.Disposed);
    }

    private sealed class FakeSilentModeSettings :
        ITwinCatSilentModeSettings
    {
        public FakeSilentModeSettings(bool initialValue)
        {
            Value = initialValue;
        }

        public bool Disposed { get; private set; }

        public bool FailDisableWrite { get; set; }

        public bool IgnoreEnableWrite { get; set; }

        public bool Value { get; private set; }

        public bool SilentMode
        {
            get => Value;
            set
            {
                if (value && IgnoreEnableWrite)
                {
                    return;
                }

                if (!value && FailDisableWrite)
                {
                    throw new InvalidOperationException(
                        "Synthetic restore failure.");
                }

                Value = value;
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}

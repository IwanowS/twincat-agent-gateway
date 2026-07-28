using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeDialogSupervisorTests
{
    [Theory]
    [InlineData(
        "TcXaeShell",
        "Active solution platform 'TwinCAT OS (ARMT2)' differs from "
            + "current target platform 'TwinCAT RT (x64)'!",
        "PlatformMismatch")]
    [InlineData(
        "Activate Configuration",
        "Project: Machine Target: Bench Autostart PLC Boot Project(s)",
        "ActivationConfirmation")]
    [InlineData(
        "TcXaeShell",
        "Restart TwinCAT System in Run Mode",
        "RunConfirmation")]
    [InlineData(
        "Target system reports a fatal error",
        "AdsError: 1804",
        "FatalError")]
    [InlineData(
        "Unexpected",
        "Unknown prompt",
        "Unknown")]
    public void ClassifiesActivationDialogs(
        string title,
        string text,
        string expected)
    {
        Assert.Equal(
            expected,
            XaeDialogSupervisor.ClassifyDialog(
                title,
                text).ToString());
    }

    [Fact]
    public async Task UnknownModalDialogCancelsAndFailsActiveOperation()
    {
        if (!Environment.UserInteractive)
        {
            return;
        }

        const string dialogScript =
            "Start-Sleep -Milliseconds 1000; "
            + "Add-Type -AssemblyName System.Windows.Forms; "
            + "$owner = New-Object System.Windows.Forms.Form; "
            + "$owner.ShowInTaskbar = $false; "
            + "$owner.Opacity = 0; "
            + "$owner.Show(); "
            + "[System.Windows.Forms.MessageBox]::Show("
            + "$owner, "
            + "'A deliberately unknown XAE-style prompt.', "
            + "'Unexpected gateway test dialog', "
            + "[System.Windows.Forms.MessageBoxButtons]::OKCancel, "
            + "[System.Windows.Forms.MessageBoxIcon]::Question) "
            + "| Out-Null; "
            + "$owner.Close()";
        string encodedScript = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(dialogScript));
        using Process dialogProcess = Process.Start(
            new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Sta -EncodedCommand "
                    + encodedScript,
                CreateNoWindow = true,
                UseShellExecute = false,
            })
            ?? throw new InvalidOperationException(
                "Could not start the modal dialog test process.");
        using XaeDialogSupervisor supervisor = new(
            dialogProcess.Id);
        List<XaeDialogObservation> observed = new();
        supervisor.DialogObserved += (_, eventArgs) =>
        {
            lock (observed)
            {
                observed.Add(eventArgs.Observation);
            }
        };
        using XaeDialogOperationScope operation =
            supervisor.BeginOperation(
                "dialog-test",
                "build",
                "xae.build",
                runAfterActivation: null);
        GatewayOperationException exception;
        try
        {
            exception =
                await Assert.ThrowsAsync<GatewayOperationException>(
                    () => operation.ObserveAsync(
                        Task.Delay(TimeSpan.FromSeconds(15))));
        }
        finally
        {
            if (!dialogProcess.HasExited)
            {
                dialogProcess.Kill();
            }
        }

        Assert.Equal(
            ErrorCodes.XaeUnknownModalDialog,
            exception.Code);
        Assert.True(
            dialogProcess.WaitForExit(
                milliseconds: (int)TimeSpan.FromSeconds(5)
                    .TotalMilliseconds));
        XaeDialogObservation observation;
        lock (observed)
        {
            observation = Assert.Single(observed);
        }

        Assert.Equal("Unknown", observation.Kind);
        Assert.Equal("cancel-unknown-dialog", observation.Action);
        Assert.True(observation.ActionRequested);
        Assert.True(observation.ActionCompleted);
        Assert.Contains(
            observation.Buttons,
            button => button.AutomationId == "2");
    }
}

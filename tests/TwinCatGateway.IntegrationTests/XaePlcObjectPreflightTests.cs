using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaePlcObjectPreflightTests
{
    [Fact]
    public void InvalidChangedPlcObjectFailsBeforeComSynchronization()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.TcPOU");
        try
        {
            File.WriteAllText(
                path,
                "<TcPlcObject Version=\"1.1.0.1\">"
                + "<Unexpected />"
                + "</TcPlcObject>");

            GatewayOperationException exception =
                Assert.Throws<GatewayOperationException>(
                    () => XaeSession.ValidateChangedPlcObjects(
                        new List<string> { path },
                        CancellationToken.None));

            Assert.Equal(
                ErrorCodes.PlcObjectInvalid,
                exception.Code);
            Assert.Equal(
                "xae.workspace.validate",
                exception.Stage);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void PlcObjectPreflightHonorsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => XaeSession.ValidateChangedPlcObjects(
                new List<string>
                {
                    @"C:\not-read.TcPOU",
                },
                cancellation.Token));
    }
}

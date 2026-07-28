using System;
using System.Threading;
using TwinCatGateway.Cli;
using TwinCatGateway.Client;

return await CliProgram.RunAsync(
    args,
    static pipeName => new TwinCatGatewayClient(pipeName),
    Console.Out,
    Console.Error,
    CancellationToken.None);

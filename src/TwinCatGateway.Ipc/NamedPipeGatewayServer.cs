#if NET48
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace TwinCatGateway.Ipc;

public sealed class NamedPipeGatewayServer : IDisposable
{
    private const int MaximumServerInstances = 10;
    private readonly string _pipeName;
    private readonly GatewayProtocolHandler _handler;
    private readonly Action<string, Exception>? _exceptionSink;
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly SemaphoreSlim _connectionSlots =
        new(MaximumServerInstances);
    private int _nextConnectionId;
    private int _disposed;

    public NamedPipeGatewayServer(
        string pipeName,
        GatewayProtocolHandler handler,
        Action<string, Exception>? exceptionSink = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        }

        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _exceptionSink = exceptionSink;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(NamedPipeGatewayServer));
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    pipe?.Dispose();
                    _connectionSlots.Release();
                    throw;
                }

                int connectionId = Interlocked.Increment(ref _nextConnectionId);
                Task connection = HandleConnectionAsync(
                    connectionId,
                    pipe,
                    cancellationToken);
                _connections.TryAdd(connectionId, connection);
                _ = connection.ContinueWith(
                    completedTask =>
                    {
                        _connections.TryRemove(connectionId, out _);
                        _connectionSlots.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_connections.Values.ToArray()).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connectionSlots.Dispose();
        }
    }

    private async Task HandleConnectionAsync(
        int connectionId,
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using (pipe)
        {
            try
            {
                string request = await IpcFrameProtocol.ReadAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                GatewayProtocolHandler.GatewayProtocolResponse
                    response =
                    await _handler.HandleForTransportAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                await IpcFrameProtocol.WriteAsync(
                    pipe,
                    response.Json,
                    cancellationToken).ConfigureAwait(false);
                response.NotifyResponseWritten();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                RecordException($"ipc-{connectionId}", exception);
            }
        }
    }

    private void RecordException(string connectionId, Exception exception)
    {
        try
        {
            _exceptionSink?.Invoke(connectionId, exception);
        }
        catch (Exception sinkException)
        {
            Trace.TraceError(
                "IPC exception sink failed while recording '{0}': {1}{2}Original: {3}",
                connectionId,
                sinkException,
                Environment.NewLine,
                exception);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? user = identity.User;
        if (user is null)
        {
            throw new InvalidOperationException(
                "The current Windows user has no security identifier.");
        }

        PipeSecurity security = new();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new PipeAccessRule(
                user,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }
}
#endif

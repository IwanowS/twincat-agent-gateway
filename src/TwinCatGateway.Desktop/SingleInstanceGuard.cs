using System;
using System.Security.Principal;
using System.Threading;

namespace TwinCatGateway.Desktop;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(
        string applicationName,
        out SingleInstanceGuard? guard)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new ArgumentException(
                "Application name is required.",
                nameof(applicationName));
        }

        SecurityIdentifier? user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            throw new InvalidOperationException(
                "The current Windows user has no security identifier.");
        }

        string mutexName = $@"Local\{applicationName}-{user.Value}";
        Mutex mutex = new(
            initiallyOwned: true,
            mutexName,
            out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

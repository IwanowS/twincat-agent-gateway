using System;
using System.Collections.Generic;
using TwinCAT.Ads;

namespace TwinCatGateway.Ads;

public interface IAdsStateProbe : IDisposable
{
    AdsStateReadResult Read(
        string amsNetId,
        int port,
        TimeSpan timeout);
}

public sealed class AdsStateProbe :
    IAdsStateProbe
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ClientEntry> _clients =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public AdsStateReadResult Read(
        string amsNetId,
        int port,
        TimeSpan timeout)
    {
        ClientEntry entry;
        string key = $"{amsNetId}|{port}";
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_clients.TryGetValue(key, out entry!))
            {
                entry = new ClientEntry();
                _clients.Add(key, entry);
            }
        }

        AdsStateReadResult result;
        lock (entry.Sync)
        {
            result = AdsStateReader.Read(
                entry.Client,
                amsNetId,
                port,
                timeout,
                connect: !entry.Connected);
            entry.Connected = result.Succeeded;
        }

        if (!result.Succeeded)
        {
            RemoveFailedClient(key, entry);
        }

        return result;
    }

    public void Dispose()
    {
        ClientEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = new ClientEntry[_clients.Count];
            _clients.Values.CopyTo(entries, 0);
            _clients.Clear();
        }

        foreach (ClientEntry entry in entries)
        {
            lock (entry.Sync)
            {
                entry.Client.Dispose();
            }
        }
    }

    private void RemoveFailedClient(
        string key,
        ClientEntry entry)
    {
        bool removed = false;
        lock (_sync)
        {
            if (_clients.TryGetValue(
                    key,
                    out ClientEntry? current)
                && ReferenceEquals(current, entry))
            {
                _clients.Remove(key);
                removed = true;
            }
        }

        if (removed)
        {
            lock (entry.Sync)
            {
                entry.Client.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(AdsStateProbe));
        }
    }

    private sealed class ClientEntry
    {
        public object Sync { get; } = new();

        public AdsClient Client { get; } = new();

        public bool Connected { get; set; }
    }
}

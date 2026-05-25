using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseCacheService
{
    private void EnforceResponseCapacity(int maxEntries)
    {
        if (_responses.Count <= maxEntries)
        {
            return;
        }

        foreach (var key in _responses
                     .OrderBy(static pair => pair.Value.LastAccessedAt)
                     .Take(Math.Max(1, _responses.Count - maxEntries))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            RemoveResponse(key);
        }
    }

    private void EnforceModelListCapacity(int maxEntries)
    {
        if (_modelLists.Count <= maxEntries)
        {
            return;
        }

        foreach (var key in _modelLists
                     .OrderBy(static pair => pair.Value.LastAccessedAt)
                     .Take(Math.Max(1, _modelLists.Count - maxEntries))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            RemoveModelList(key);
        }
    }

    private void RemoveResponse(string key)
    {
        if (_responses.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _evictions);
        }

        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            lock (_databaseSyncRoot)
            {
                try
                {
                    using var connection = OpenConnection();
                    EnsurePersistentSchema(connection);
                    DeletePersistentResponse(connection, key);
                }
                catch
                {
                }
            }
        }
    }

    private void RemoveModelList(string key)
    {
        if (_modelLists.TryRemove(key, out _))
        {
            Interlocked.Increment(ref _evictions);
        }

        if (!string.IsNullOrWhiteSpace(_databasePath) && File.Exists(_databasePath))
        {
            lock (_databaseSyncRoot)
            {
                try
                {
                    using var connection = OpenConnection();
                    EnsurePersistentSchema(connection);
                    DeletePersistentModelList(connection, key);
                }
                catch
                {
                }
            }
        }
    }
}

internal readonly struct TransparentProxyCacheLease : IDisposable
{
    private readonly string _cacheKey;
    private readonly SemaphoreSlim? _gate;
    private readonly ConcurrentDictionary<string, SemaphoreSlim>? _owner;

    internal static TransparentProxyCacheLease Empty => new();

    internal TransparentProxyCacheLease(
        string cacheKey,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, SemaphoreSlim> owner)
    {
        _cacheKey = cacheKey;
        _gate = gate;
        _owner = owner;
    }

    public void Dispose()
    {
        if (_gate is null)
        {
            return;
        }

        _gate.Release();
        if (_gate.CurrentCount > 0 &&
            _owner is not null &&
            _owner.TryGetValue(_cacheKey, out var current) &&
            ReferenceEquals(current, _gate))
        {
            ((ICollection<KeyValuePair<string, SemaphoreSlim>>)_owner)
                .Remove(new KeyValuePair<string, SemaphoreSlim>(_cacheKey, _gate));
        }
    }
}

using System;
using System.Collections.Generic;

namespace WindowManager.App.Runtime.Publishing;

internal sealed class HlsInMemoryArtifactStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ArtifactEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Put(string name, byte[] payload, TimeSpan ttl, bool removeAfterRead)
    {
        if (string.IsNullOrWhiteSpace(name) || payload is null || payload.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            PurgeExpiredUnsafe();
            _entries[name] = new ArtifactEntry((byte[])payload.Clone(), DateTime.UtcNow + ttl, removeAfterRead);
        }
    }

    public bool Has(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return _entries.ContainsKey(name);
        }
    }

    public bool TryRead(string name, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_gate)
        {
            PurgeExpiredUnsafe();
            if (!_entries.TryGetValue(name, out var entry))
            {
                return false;
            }

            payload = entry.Payload;
            if (entry.RemoveAfterRead)
            {
                _entries.Remove(name);
            }

            return true;
        }
    }

    public void Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(name);
        }
    }

    public void Retain(ISet<string> namesToKeep)
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            var keys = new List<string>(_entries.Keys);
            foreach (var key in keys)
            {
                if (!namesToKeep.Contains(key))
                {
                    _entries.Remove(key);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void PurgeExpiredUnsafe()
    {
        var nowUtc = DateTime.UtcNow;
        var keys = new List<string>(_entries.Keys);
        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= nowUtc)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class ArtifactEntry
    {
        public ArtifactEntry(byte[] payload, DateTime expiresAtUtc, bool removeAfterRead)
        {
            Payload = payload;
            ExpiresAtUtc = expiresAtUtc;
            RemoveAfterRead = removeAfterRead;
        }

        public byte[] Payload { get; }

        public DateTime ExpiresAtUtc { get; }

        public bool RemoveAfterRead { get; }
    }
}

using PrimSystem.Data;
using System.Collections.Concurrent;

namespace PrimSystem.Services
{
    public class SessionStore : IDisposable
    {
        private sealed class Entry(User user)
        {
            public User User = user;
            public DateTime LastSeen = DateTime.UtcNow;
        }

        // 8 saat hareketsiz kalan token'lar temizlenir
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

        private readonly ConcurrentDictionary<string, Entry> _sessions = new();
        private readonly Timer _cleanup;

        public SessionStore()
        {
            _cleanup = new Timer(_ => RemoveExpired(), null, CleanupInterval, CleanupInterval);
        }

        public User? Get(string token)
        {
            if (_sessions.TryGetValue(token, out var e))
            {
                e.LastSeen = DateTime.UtcNow;
                return e.User;
            }
            return null;
        }

        public void Set(string token, User user)
        {
            _sessions.AddOrUpdate(token,
                _ => new Entry(user),
                (_, e) => { e.User = user; e.LastSeen = DateTime.UtcNow; return e; });
        }

        public void Remove(string token) => _sessions.TryRemove(token, out _);

        private void RemoveExpired()
        {
            var cutoff = DateTime.UtcNow - IdleTimeout;
            foreach (var kv in _sessions)
            {
                if (kv.Value.LastSeen < cutoff)
                    _sessions.TryRemove(kv.Key, out _);
            }
        }

        public void Dispose() => _cleanup.Dispose();
    }
}

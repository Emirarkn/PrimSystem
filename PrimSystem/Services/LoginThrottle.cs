using System.Collections.Concurrent;

namespace PrimSystem.Services
{
    /// <summary>
    /// Kilitlenen hesaba giriş denendiğinde fırlatılır — UI bunu yakalayıp
    /// kullanıcıya kalan süreyi gösterir.
    /// </summary>
    public class LoginLockedException : Exception
    {
        public LoginLockedException(string message) : base(message) { }
    }

    /// <summary>
    /// Brute-force koruması: kullanıcı adı bazlı başarısız giriş sayacı + geçici kilit.
    /// Bellekte tutulur (Singleton) — SessionStore ile aynı desen; sunucu yeniden
    /// başlayınca sıfırlanır. Kalıcı kilit gerekiyorsa ileride User tablosuna taşınabilir.
    /// </summary>
    public class LoginThrottle
    {
        private const int MaxAttempts = 5;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

        private sealed record Entry(int Count, DateTime FirstAttemptUtc, DateTime? LockedUntilUtc);

        private readonly ConcurrentDictionary<string, Entry> _entries = new();

        private static string Key(string username) => (username ?? "").Trim().ToLowerInvariant();

        /// <summary>Hesap kilitliyse kalan süreyi döndürür; değilse null.</summary>
        public TimeSpan? GetLockRemaining(string username)
        {
            if (_entries.TryGetValue(Key(username), out var e) && e.LockedUntilUtc is { } until)
            {
                var remaining = until - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero) return remaining;
            }
            return null;
        }

        public void RegisterFailure(string username)
        {
            var key = Key(username);
            var now = DateTime.UtcNow;
            _entries.AddOrUpdate(key,
                _ => new Entry(1, now, null),
                (_, e) =>
                {
                    // Pencere dolmuş ve aktif kilit yoksa sayacı sıfırla
                    if (now - e.FirstAttemptUtc > Window &&
                        (e.LockedUntilUtc == null || now > e.LockedUntilUtc))
                        return new Entry(1, now, null);

                    var count = e.Count + 1;
                    DateTime? locked = count >= MaxAttempts ? now.Add(LockDuration) : e.LockedUntilUtc;
                    return e with { Count = count, LockedUntilUtc = locked };
                });
        }

        public void Reset(string username) => _entries.TryRemove(Key(username), out _);
    }
}

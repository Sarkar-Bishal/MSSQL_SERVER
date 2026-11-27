using System.Collections.Concurrent;

namespace MSSQL_Server.Security
{
    /// <summary>Locks an IP address for a minute after 5 failed logins.</summary>
    internal sealed class LoginThrottle
    {
        private const int MaxFailures = 5;
        private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<string, (int Fails, DateTime LockedUntil)> state = new();

        public bool IsLocked(string ip) =>
            state.TryGetValue(ip, out var s) && s.LockedUntil > DateTime.UtcNow;

        public void Failure(string ip) =>
            state.AddOrUpdate(
                ip,
                _ => (1, DateTime.MinValue),
                (_, s) =>
                {
                    int fails = s.LockedUntil != DateTime.MinValue && s.LockedUntil <= DateTime.UtcNow
                        ? 1 : s.Fails + 1;
                    return fails >= MaxFailures
                        ? (0, DateTime.UtcNow + LockDuration)
                        : (fails, DateTime.MinValue);
                });

        public void Success(string ip) => state.TryRemove(ip, out _);
    }
}

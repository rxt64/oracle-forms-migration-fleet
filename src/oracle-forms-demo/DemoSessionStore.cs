using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OracleFormsDemo;

public enum SessionRole
{
    Customer,
    Manager
}

public sealed record DemoSession(SessionRole Role, long? AccountId, string DisplayName, DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory bearer sessions for the synthetic demo. There is no user store and no persistence:
/// tokens are opaque random values that disappear when the process restarts.
/// </summary>
public sealed class DemoSessionStore
{
    private static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(2);
    private const int SweepInterval = 32;

    private readonly ConcurrentDictionary<string, DemoSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sessionLifetime;
    private int _issuedSinceSweep;

    public DemoSessionStore(TimeProvider? timeProvider = null, TimeSpan? sessionLifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionLifetime = sessionLifetime ?? DefaultSessionLifetime;

        if (_sessionLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime), "Session lifetime must be positive.");
        }
    }

    public (string Token, DemoSession Session) Create(SessionRole role, long? accountId, string displayName)
    {
        if (Interlocked.Increment(ref _issuedSinceSweep) >= SweepInterval)
        {
            Interlocked.Exchange(ref _issuedSinceSweep, 0);
            RemoveExpired();
        }

        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var session = new DemoSession(role, accountId, displayName, _timeProvider.GetUtcNow().Add(_sessionLifetime));
        _sessions[token] = session;
        return (token, session);
    }

    public DemoSession? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var session))
        {
            return null;
        }

        if (session.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return session;
        }

        _sessions.TryRemove(token, out _);
        return null;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

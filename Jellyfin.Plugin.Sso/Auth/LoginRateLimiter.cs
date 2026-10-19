using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// Counts how often one caller uses the anonymous parts of a login. Everything behind it
/// can be reached without a Jellyfin token, and every request costs the server something:
/// a pending state that lives for ten minutes, a token exchange with the identity
/// provider, an RSA signature check. The count is per caller address and per minute —
/// a number a household never reaches and a script reaches at once.
///
/// It is a fixed window rather than a sliding one: a caller can spend one window's worth
/// at the end of a window and another at the start of the next. That is a factor of two,
/// which matters to nobody at these numbers, and it costs two integers per caller instead
/// of a list of timestamps.
/// </summary>
public sealed class LoginRateLimiter
{
    /// <summary>
    /// How many addresses are counted at once. One entry is a key and three numbers; the
    /// cap is what stops a caller walking an IPv6 /64 from growing the table without end.
    /// </summary>
    internal const int MaxTrackedCallers = 10_000;

    /// <summary>
    /// How far the table is cut back when it reaches the cap. Trimming to the cap exactly
    /// would mean sweeping the whole table again on the very next request; leaving room
    /// makes the sweep rare enough to not be the cheapest thing to attack.
    /// </summary>
    private const int TrimTo = MaxTrackedCallers * 3 / 4;

    /// <summary>What a request the server could not place an address on is counted as.</summary>
    private const string UnknownCaller = "unknown";

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int> _limit;
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly Lock _purgeGate = new();
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;

    public LoginRateLimiter()
        : this(DefaultWindow, static () => DateTimeOffset.UtcNow, ConfiguredLimit)
    {
    }

    /// <summary>Lets a test fill a window without sixty seconds passing.</summary>
    internal LoginRateLimiter(TimeSpan window, Func<DateTimeOffset> clock, Func<int> limit)
    {
        _window = window;
        _clock = clock;
        _limit = limit;
    }

    /// <summary>
    /// The ruling on one request: whether it may go ahead, how long the caller has to wait
    /// if not, and whether this is the first refusal of this window — the one worth a log
    /// line, the rest being the same caller still knocking.
    /// </summary>
    public sealed record Decision(bool Allowed, TimeSpan RetryAfter, bool FirstRefusal);

    private sealed class Counter
    {
        public DateTimeOffset Start { get; set; }

        public int Used { get; set; }

        public int Refused { get; set; }
    }

    /// <summary>How many addresses are being counted right now.</summary>
    internal int TrackedCallers => _counters.Count;

    /// <summary>
    /// Forgets every count. Called when the configuration is saved: an administrator who
    /// raises the limit because somebody is locked out means now, not next minute.
    /// </summary>
    public void Reset() => _counters.Clear();

    /// <summary>Counts one request from <paramref name="caller"/> and rules on it.</summary>
    public Decision Check(string? caller)
    {
        var limit = _limit();
        if (limit <= 0)
        {
            // Turned off. Nothing is counted either, so switching it back on starts clean.
            return new Decision(Allowed: true, TimeSpan.Zero, FirstRefusal: false);
        }

        var now = _clock();
        var key = string.IsNullOrWhiteSpace(caller) ? UnknownCaller : caller;

        Purge(now);

        var counter = _counters.GetOrAdd(key, _ => new Counter { Start = now });

        lock (counter)
        {
            if (now - counter.Start >= _window)
            {
                counter.Start = now;
                counter.Used = 0;
                counter.Refused = 0;
            }

            if (counter.Used < limit)
            {
                counter.Used++;
                return new Decision(Allowed: true, TimeSpan.Zero, FirstRefusal: false);
            }

            return new Decision(
                Allowed: false,
                RetryAfter: counter.Start + _window - now,
                FirstRefusal: counter.Refused++ == 0);
        }
    }

    /// <summary>
    /// The limit the administrator set, read fresh so a change takes effect without a
    /// restart. Zero or less turns the limit off.
    /// </summary>
    private static int ConfiguredLimit()
        => Plugin.Instance?.Configuration.LoginRequestsPerMinute
            ?? Configuration.PluginConfiguration.DefaultLoginRequestsPerMinute;

    /// <summary>
    /// Drops the counters whose window has passed. Doing that on every request would make
    /// this the cheapest part of the server to attack — the sweep is the size of the
    /// table — so it happens once a window, or when the table hits its cap.
    /// </summary>
    private void Purge(DateTimeOffset now)
    {
        lock (_purgeGate)
        {
            if (now < _nextPurge && _counters.Count <= MaxTrackedCallers)
            {
                return;
            }

            _nextPurge = now + _window;
        }

        foreach (var (key, counter) in _counters)
        {
            if (now - counter.Start >= _window)
            {
                _counters.TryRemove(key, out _);
            }
        }

        if (_counters.Count <= MaxTrackedCallers)
        {
            return;
        }

        // Still over the cap, so every window in the table is live. The oldest go: they
        // are the ones closest to turning over anyway.
        foreach (var (key, _) in _counters.OrderBy(e => e.Value.Start).Take(_counters.Count - TrimTo))
        {
            _counters.TryRemove(key, out _);
        }
    }
}

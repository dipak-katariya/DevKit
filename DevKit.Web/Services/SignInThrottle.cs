using System.Collections.Concurrent;

namespace DevKit.Web.Services;

/// <summary>
/// Stops password guessing before Windows sees it: after <see cref="MaxFailures"/> wrong passwords for one account
/// within <see cref="Window"/>, that account is refused until the window has passed. The limit sits below common
/// domain lockout thresholds, so a guesser cannot lock the real account out of Windows through DevKit. Attempts Windows
/// is still checking count too (<see cref="TryBegin"/>), so guesses sent in parallel cannot pass the limit together. Past
/// <see cref="MaxFailuresForEveryone"/> wrong passwords a minute on this DevKit, every typed password waits, so one
/// password guessed for many accounts stays slow. Accounts are keyed by <see cref="IWindowsCredentialChecker.AccountKey"/>,
/// and those whose window has passed are forgotten, so names typed once never pile up.
/// </summary>
internal sealed class SignInThrottle
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wrong passwords this DevKit takes in <see cref="EveryoneWindow"/> across all accounts, past which every typed
    /// password waits: a guess at one password for many accounts stays slow, while single sign-on goes on working.
    /// </summary>
    public const int MaxFailuresForEveryone = 30;

    public static readonly TimeSpan EveryoneWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly ILogger<SignInThrottle>? _logger;

    // Attempts Windows is still checking, per account, and the times of the latest wrong passwords for anyone. Taken
    // together under one lock, so parallel attempts cannot all pass the check before any of them is counted.
    private readonly Lock _attempts = new();
    private readonly Dictionary<string, int> _checking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _everyoneFailures = new();
    private bool _everyoneStopped;

    public SignInThrottle(TimeProvider clock, ILogger<SignInThrottle>? logger = null)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The accounts still remembered, for the tests that check old ones are forgotten.</summary>
    internal int Remembered => _failures.Count;

    /// <summary>
    /// Starts a password attempt for <paramref name="account"/>, unless it must wait: it has had its wrong passwords, or
    /// attempts still being checked would bring it there, or this DevKit took <see cref="MaxFailuresForEveryone"/> wrong
    /// passwords in the last minute. Each attempt started is ended with <see cref="End"/>.
    /// </summary>
    public AttemptStart TryBegin(string account)
    {
        lock (_attempts)
        {
            var now = _clock.GetUtcNow();
            if (EveryoneMustWait(now, out var everyoneWait))
            {
                return AttemptStart.Refused(everyoneWait, forEveryone: true);
            }

            // IsBlocked also forgets the account once its window has passed.
            IsBlocked(account, out var wait);
            if (FailuresOf(account, now) + _checking.GetValueOrDefault(account) >= MaxFailures)
            {
                return AttemptStart.Refused(wait > TimeSpan.Zero ? wait : EveryoneWindow, forEveryone: false);
            }

            _checking[account] = _checking.GetValueOrDefault(account) + 1;
            return AttemptStart.Started;
        }
    }

    /// <summary>Ends an attempt <see cref="TryBegin"/> started, counting it as Windows answered.</summary>
    public void End(string account, bool accepted)
    {
        lock (_attempts)
        {
            var left = _checking.GetValueOrDefault(account) - 1;
            if (left > 0)
            {
                _checking[account] = left;
            }
            else
            {
                _checking.Remove(account);
            }

            if (accepted)
            {
                RecordSuccess(account);
                return;
            }

            RecordFailure(account);
            _everyoneFailures.Enqueue(_clock.GetUtcNow());
        }
    }

    private int FailuresOf(string account, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(account, out var failures))
        {
            return 0;
        }

        var (count, first) = failures.Read();
        return now - first < Window ? count : 0;
    }

    /// <summary>Whether every typed password waits now, and for how long; says so in the log when that starts.</summary>
    private bool EveryoneMustWait(DateTimeOffset now, out TimeSpan wait)
    {
        while (_everyoneFailures.TryPeek(out var oldest) && now - oldest >= EveryoneWindow)
        {
            _everyoneFailures.Dequeue();
        }

        wait = TimeSpan.Zero;
        if (_everyoneFailures.Count < MaxFailuresForEveryone)
        {
            _everyoneStopped = false;
            return false;
        }

        wait = _everyoneFailures.Peek() + EveryoneWindow - now;
        if (!_everyoneStopped)
        {
            _everyoneStopped = true;
            _logger?.LogWarning("{Failures} wrong passwords were typed on this DevKit within a minute, so typed passwords wait; single sign-on goes on working",
                MaxFailuresForEveryone);
        }

        return true;
    }

    /// <summary>Whether <paramref name="account"/> must wait, and for how long.</summary>
    public bool IsBlocked(string account, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (!_failures.TryGetValue(account, out var failures))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var (count, first) = failures.Read();
        if (now - first >= Window)
        {
            _failures.TryRemove(account, out _);
            return false;
        }

        wait = first + Window - now;
        return count >= MaxFailures;
    }

    public void RecordFailure(string account)
    {
        var now = _clock.GetUtcNow();
        foreach (var passed in _failures.Where(pair => now - pair.Value.Read().First >= Window).Select(pair => pair.Key).ToList())
        {
            _failures.TryRemove(passed, out _);
        }

        _failures.AddOrUpdate(account, _ => new Failures(now), (_, failures) => failures.Add(now, Window));
    }

    public void RecordSuccess(string account) => _failures.TryRemove(account, out _);

    /// <summary>What <see cref="TryBegin"/> decided: go ahead, or wait this long, for this account alone or for everyone.</summary>
    internal readonly record struct AttemptStart(bool IsStarted, TimeSpan Wait, bool ForEveryone)
    {
        public static AttemptStart Started => new(true, TimeSpan.Zero, false);

        public static AttemptStart Refused(TimeSpan wait, bool forEveryone) => new(false, wait, forEveryone);
    }

    private sealed class Failures
    {
        private readonly object _sync = new();
        private int _count = 1;
        private DateTimeOffset _first;

        public Failures(DateTimeOffset first)
        {
            _first = first;
        }

        public (int Count, DateTimeOffset First) Read()
        {
            lock (_sync)
            {
                return (_count, _first);
            }
        }

        /// <summary>Counts one more failure, starting a new window when the last one has passed.</summary>
        public Failures Add(DateTimeOffset now, TimeSpan window)
        {
            lock (_sync)
            {
                if (now - _first >= window)
                {
                    _first = now;
                    _count = 0;
                }

                _count++;
                return this;
            }
        }
    }
}

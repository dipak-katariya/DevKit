namespace DevKit.Tests;

/// <summary>
/// A clock whose timers fire only when the test moves time on, so a test can let seconds pass without waiting for
/// them. Task.Delay given this clock completes when <see cref="Advance"/> passes its due time.
/// </summary>
internal sealed class ManualTimers : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
            timer.Schedule(dueTime, period);
        }
        return timer;
    }

    /// <summary>Moves time on, then fires every timer that fell due on the way, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_timers)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).OrderBy(t => t.Due).ToList();
            due.ForEach(t => t.Reschedule());
        }
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>When the timer fires next; null while it is stopped.</summary>
        public DateTimeOffset? Due { get; private set; }

        // Called with the owner's lock held.
        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
        }

        // Called with the owner's lock held, as the timer fires: a one-shot timer stops, a periodic one moves on.
        public void Reschedule() =>
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers)
            {
                Schedule(dueTime, period);
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._timers)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

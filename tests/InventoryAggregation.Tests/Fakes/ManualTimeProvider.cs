namespace InventoryAggregation.Tests.Fakes;

/// <summary>
/// A clock that only moves when the test moves it. Timers created from it (a
/// CancellationTokenSource with a delay, for example) fire when <see cref="Now"/>
/// is moved to or past their due time, on the thread that moved it.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
        set
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _now = value;
                due = _timers.Where(timer => timer.DueAt <= value).ToList();
                _timers.RemoveAll(due.Contains);
            }

            // Outside the lock: a callback may create or dispose timers of its own.
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }
    }

    /// <summary>Timers that are waiting to fire: created, not yet due and not disposed.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>One-shot timers only; that is all the code under test asks for.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);

                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

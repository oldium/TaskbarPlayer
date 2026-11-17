namespace TaskbarPlayer;

/// <summary>What a measurement of the taskbar gave.</summary>
internal enum Measurement
{
    /// <summary>The taskbar could not be read, or what was read is of no use any more.</summary>
    Nothing,

    /// <summary>The layout that was known already.</summary>
    Same,

    Different,
}

/// <summary>
/// Says when the taskbar is to be measured. A measurement makes Explorer describe its whole taskbar, which costs
/// Explorer as much as it costs us. So it is done on every tick only until the widget has a measured place; after
/// that only when something signals a change, never at intervals.
/// <para>
/// What a sign asks for is owed until a measurement that started after it has run. One measurement runs at a
/// time; a sign that comes while one runs may be too late for it, so it is measured again afterwards. Signs come
/// from any thread.
/// </para>
/// </summary>
internal sealed class MeasureSchedule
{
    /// <summary>Measurements after a sign of change: icons take a few hundred milliseconds to settle.</summary>
    public const int FollowUps = 3;

    /// <summary>Tries on consecutive ticks while measurements give nothing, before the pace slows down.</summary>
    public const int MaxRetries = 10;

    /// <summary>Ticks between two tries after that: 30 s at two ticks a second.</summary>
    public const int RetryTicks = 60;

    private readonly object _gate = new();
    private int _owed;
    private bool _running;
    private int _failures;
    private int _wait;

    /// <summary>Something about the taskbar may have changed.</summary>
    public void Changed()
    {
        lock (_gate)
        {
            _owed = FollowUps;
            // The taskbar is changing: it may be readable in a moment.
            _failures = 0;
            _wait = 0;
        }
    }

    /// <summary>To be asked once per tick: is a look at the taskbar owed now?</summary>
    public bool Owed(bool measured)
    {
        lock (_gate)
        {
            if (!measured) return true;
            if (_owed == 0) return false;
            return _wait == 0 || --_wait == 0;
        }
    }

    /// <summary>
    /// Starts a measurement unless one is running. What was owed counts as taken before it starts: a sign that
    /// comes from now on is owed anew. Who gets true calls <see cref="Finished"/> when the measurement is over.
    /// </summary>
    public bool Begin()
    {
        lock (_gate)
        {
            if (_running) return false;
            _running = true;
            if (_owed > 0) _owed--;
            return true;
        }
    }

    /// <summary>The measurement that <see cref="Begin"/> started is over.</summary>
    public void Finished(Measurement result)
    {
        lock (_gate)
        {
            _running = false;
            switch (result)
            {
                case Measurement.Nothing:
                    // The taskbar may be in the middle of a change: owed until it can be read.
                    _owed = Math.Max(_owed, 1);
                    if (++_failures > MaxRetries) _wait = RetryTicks;
                    break;

                case Measurement.Different:
                    // Still changing (icons slide into place): looked at again.
                    _failures = 0;
                    _wait = 0;
                    _owed = FollowUps;
                    break;

                default:
                    _failures = 0;
                    _wait = 0;
                    break;
            }
        }
    }
}

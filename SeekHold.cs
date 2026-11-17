namespace TaskbarPlayer;

/// <summary>
/// Holds the place the timeline was moved to until the playing app reports it. After a seek the app goes on
/// reporting the old position for a moment; shown as it comes, the line would jump back and then forward again.
/// </summary>
internal sealed class SeekHold
{
    /// <summary>An app that never reports anew must not freeze the line.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(2);

    private bool _held;
    private TimeSpan _position;
    private DateTimeOffset _since;

    /// <summary>The user asked for this position.</summary>
    public void Request(TimeSpan position, DateTimeOffset now)
    {
        _held = true;
        _position = position;
        _since = now;
    }

    /// <summary>The app refused the seek.</summary>
    public void Cancel() => _held = false;

    /// <summary>
    /// Whether a position the app reports is to be shown: always, unless one is held and the report was made
    /// before the seek. A report made after it ends the hold.
    /// </summary>
    public bool Accepts(DateTimeOffset lastUpdated, DateTimeOffset now)
    {
        if (!_held) return true;
        if (now - _since < Longest && lastUpdated < _since) return false;
        _held = false;
        return true;
    }

    /// <summary>The held position, running on while the music plays; none when nothing is held.</summary>
    public TimeSpan? Position(DateTimeOffset now, bool playing)
    {
        if (!_held) return null;
        if (now - _since >= Longest)
        {
            _held = false;
            return null;
        }
        return playing ? _position + (now - _since) : _position;
    }
}

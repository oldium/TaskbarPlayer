namespace TaskbarPlayer;

internal enum Redraw
{
    /// <summary>The picture on screen is this one already.</summary>
    None,

    /// <summary>Only the progress line differs.</summary>
    BarOnly,

    Whole,
}

/// <summary>
/// Remembers what the picture on screen was made of, so that it is drawn again only when it would differ, and only
/// its progress line when nothing else would.
/// </summary>
internal sealed class FrameMemory
{
    private object? _still;
    private object? _bar;
    private bool _drawn;

    /// <summary>
    /// What has to be drawn for a picture made of these two: everything but the progress line, and the line (none
    /// when there is no line). They are compared by value; the picture then counts as being on screen.
    /// </summary>
    public Redraw Decide(object still, object? bar)
    {
        Redraw redraw = !_drawn || !still.Equals(_still) ? Redraw.Whole
            : !Equals(bar, _bar) ? Redraw.BarOnly
            : Redraw.None;
        _still = still;
        _bar = bar;
        _drawn = true;
        return redraw;
    }

    /// <summary>The window is new: whatever comes next is drawn whole.</summary>
    public void Forget() => _drawn = false;
}

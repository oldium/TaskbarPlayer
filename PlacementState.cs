namespace TaskbarPlayer;

/// <summary>What became of a measurement handed to <see cref="PlacementState.Apply"/>.</summary>
internal enum PlacementStep
{
    /// <summary>Of no use: the taskbar could not be read, or it is another taskbar by now.</summary>
    Dropped,

    /// <summary>The first look at a taskbar without a layout: kept back until a second look agrees.</summary>
    Held,

    Taken,
}

/// <summary>
/// What the widget knows about where it may sit, from measurements, remembered layouts and the taskbar's identity.
/// </summary>
internal sealed class PlacementState
{
    private readonly LayoutCache _cache;

    /// <param name="classic">A classic taskbar (Windows 10): its child windows are the layout, nothing is measured.</param>
    public PlacementState(LayoutCache cache, bool classic = false)
    {
        _cache = cache;
        Classic = classic;
    }

    public bool Classic { get; }

    /// <summary>The display and taskbar that <see cref="Layout"/> belongs to.</summary>
    public TaskbarIdentity? Identity { get; private set; }

    public TaskbarLayout? Layout { get; private set; }

    /// <summary>False while <see cref="Layout"/> is one remembered from an earlier run.</summary>
    public bool Measured { get; private set; }

    /// <summary>
    /// The last measurement agreed with what was there before it: the taskbar is at rest. A layout seen once
    /// may have been taken while icons slide.
    /// </summary>
    public bool Settled { get; private set; }

    public bool HasPlace => Classic || Layout is not null;

    /// <summary>
    /// The part of an auto-hiding taskbar that stays on the screen while it is hidden, relative to the taskbar
    /// window. The mouse must reach the taskbar there, or it cannot be brought back. Empty until seen.
    /// </summary>
    public Rectangle HiddenStrip { get; private set; }

    private Rectangle _lastTaskbar;
    private TaskbarLayout? _held;
    private bool _remembered; // since the start, or since the identity changed
    private bool _hadIdentity; // since the last reset

    /// <summary>
    /// What is remembered is not good enough to show (the place it gives hangs on the app icons): it is dropped,
    /// so that two looks decide, as for a taskbar never seen. A measured layout stays.
    /// </summary>
    public void Distrust()
    {
        if (!Measured) Layout = null;
    }

    /// <summary>A new taskbar window: nothing is known about it yet.</summary>
    public void Reset()
    {
        Identity = null;
        Layout = null;
        Measured = false;
        Settled = false;
        HiddenStrip = Rectangle.Empty;
        _lastTaskbar = Rectangle.Empty;
        _held = null;
        _remembered = false;
        _hadIdentity = false;
    }

    /// <summary>Takes the taskbar's and its screen's rectangles on every tick. Returns true when the strip changed.</summary>
    public bool ObserveTaskbar(Rectangle taskbar, Rectangle screen)
    {
        bool resting = taskbar == _lastTaskbar;
        _lastTaskbar = taskbar;
        // While it slides, any amount of it is on the screen.
        if (!resting) return false;

        var onScreen = Rectangle.Intersect(taskbar, screen);
        // Hidden means resting with most of it off the screen.
        bool hidden = 2L * onScreen.Width * onScreen.Height < (long)taskbar.Width * taskbar.Height;
        if (!hidden) return false;

        onScreen.Offset(-taskbar.Left, -taskbar.Top);
        if (onScreen == HiddenStrip) return false;
        HiddenStrip = onScreen;
        return true;
    }

    /// <summary>
    /// A layout is only good for the display and taskbar it was measured on. Returns true on a change.
    /// <para>
    /// What is remembered for a taskbar is taken at the start only. When the identity changes while running
    /// (icon alignment, the edge, the display, the scale), the taskbar is being rearranged right now: there is
    /// no layout until two measurements agree.
    /// </para>
    /// </summary>
    public bool SetIdentity(TaskbarIdentity? identity)
    {
        if (identity == Identity) return false;

        bool atStart = !_hadIdentity;
        if (identity is not null) _hadIdentity = true;
        Identity = identity;
        Layout = identity is null || Classic || !atStart ? null : _cache.Find(identity);
        Measured = false;
        Settled = false;
        HiddenStrip = Rectangle.Empty;
        _held = null;
        _remembered = false;
        return true;
    }

    /// <summary>
    /// Takes the result of a probe that was started while <paramref name="identity"/> was current. No layout means
    /// the taskbar could not be read: what we have stays.
    /// <para>
    /// A measurement may be taken while the taskbar's icons slide. With a layout to show, the widget follows what
    /// is measured at once. Without one, no place is better than one that jumps: the first look is held back
    /// until a second agrees.
    /// </para>
    /// </summary>
    public PlacementStep Apply(TaskbarLayout? layout, TaskbarIdentity? identity)
    {
        if (Classic || layout is null || identity != Identity) return PlacementStep.Dropped;

        if (Layout is null && layout != _held)
        {
            _held = layout;
            Settled = false;
            return PlacementStep.Held;
        }

        // Confirmed by what was held, remembered or measured before.
        bool settled = layout == Layout || layout == _held;
        _held = null;
        Layout = layout;
        Measured = true;
        Settled = settled;
        // Only the first settled measurement of a taskbar is remembered: the file is not rewritten while running.
        if (settled && !_remembered && identity is not null)
        {
            _cache.Remember(identity, layout);
            _remembered = true;
        }
        return PlacementStep.Taken;
    }
}

namespace TaskbarPlayer;

/// <summary>
/// The search for the chosen display's taskbar while the widget stands in on the main one. That taskbar gives no
/// sign when it comes; but it can only come after the displays or the settings changed, and Explorer may take a
/// while to make it. So it is looked for on every tick for a time after such a change, not for as long as the
/// display is away.
/// </summary>
internal sealed class StandInSearch
{
    /// <summary>How long to look after a change: 30 s at two ticks a second.</summary>
    public const int Ticks = 60;

    private int _left;

    /// <summary>The widget stands in, or something changed while it does.</summary>
    public void Begin() => _left = Ticks;

    /// <summary>The widget is where it is wanted.</summary>
    public void End() => _left = 0;

    /// <summary>To be asked once per tick: look now?</summary>
    public bool Due()
    {
        if (_left == 0) return false;
        _left--;
        return true;
    }
}

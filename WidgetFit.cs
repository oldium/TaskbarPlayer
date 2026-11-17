namespace TaskbarPlayer;

/// <summary>
/// What of the widget is shown. The play button always is. Short of room the widget gives up the rest in steps,
/// instead of lying over its neighbours in the taskbar: the text (a row has one, a column has not), the previous
/// and next buttons, the cover, the playlist button, the volume button. Where not even the play button fits,
/// nothing is shown.
/// </summary>
internal readonly record struct WidgetFit(bool Text, bool Cover, bool PreviousNext, bool Playlist, bool Volume)
{
    public int ButtonCount => 1 + (PreviousNext ? 2 : 0) + (Playlist ? 1 : 0) + (Volume ? 1 : 0);

    /// <summary>The buttons in the order they are drawn.</summary>
    public IReadOnlyList<WidgetPart> Buttons
    {
        get
        {
            var buttons = new List<WidgetPart>(5);
            if (PreviousNext) buttons.Add(WidgetPart.Previous);
            buttons.Add(WidgetPart.PlayPause);
            if (PreviousNext) buttons.Add(WidgetPart.Next);
            if (Playlist) buttons.Add(WidgetPart.AddToPlaylist);
            if (Volume) buttons.Add(WidgetPart.Volume);
            return buttons;
        }
    }

    /// <summary>
    /// Whether the playlist button is wanted: only where something is set up behind it. For a source that can be
    /// YouTube that is a connected YouTube account; MuSync, once installed, takes any source.
    /// </summary>
    public static bool WantsPlaylist(bool hasSession, bool youTubeSource, bool youTubeConnected, bool muSync) =>
        hasSession && ((youTubeSource && youTubeConnected) || muSync);

    /// <summary>
    /// The steps from everything that is wanted down to the play button alone; the first that fits is shown.
    /// </summary>
    public static IReadOnlyList<WidgetFit> Steps(bool text, bool cover, bool playlist)
    {
        var steps = new List<WidgetFit>(6);
        void Add(WidgetFit step)
        {
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
        }

        Add(new WidgetFit(text, cover, PreviousNext: true, playlist, Volume: true));
        Add(new WidgetFit(false, cover, PreviousNext: true, playlist, Volume: true));
        Add(new WidgetFit(false, cover, PreviousNext: false, playlist, Volume: true));
        Add(new WidgetFit(false, false, PreviousNext: false, playlist, Volume: true));
        Add(new WidgetFit(false, false, PreviousNext: false, Playlist: false, Volume: true));
        Add(new WidgetFit(false, false, PreviousNext: false, Playlist: false, Volume: false));
        return steps;
    }
}

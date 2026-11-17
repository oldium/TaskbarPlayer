namespace TaskbarPlayer;

/// <summary>
/// Keeps the widget on its side of the app icons while they rearrange. Icons on the move can make a gap look too
/// small for the length of one measurement; following that at once, the widget would jump across the taskbar and
/// back. A measurement that asks for the other side counts only when a second one has agreed with it.
/// </summary>
internal sealed class SideKeeper
{
    private bool? _beforeIcons;

    /// <summary>
    /// Whether the widget may be laid out on this side now. <paramref name="settled"/>: the layout that asks for
    /// it was measured twice in a row.
    /// </summary>
    public bool Accepts(bool beforeIcons, bool settled)
    {
        if (_beforeIcons is bool kept && kept != beforeIcons && !settled) return false;
        _beforeIcons = beforeIcons;
        return true;
    }

    /// <summary>Nothing on screen to keep in place, or the side was chosen anew.</summary>
    public void Forget() => _beforeIcons = null;
}

namespace TaskbarPlayer;

/// <summary>
/// The widget on a taskbar along the bottom or top of the screen: a row as high as the taskbar, the cover and the
/// text on the left, the buttons on the right. <see cref="Bounds"/> is relative to the taskbar window.
/// </summary>
internal sealed record WidgetRow(Rectangle Bounds, WidgetFit Fit, float TextWidth, float ButtonWidth)
{
    /// <summary>The widest the text gets, and the least it is shown with.</summary>
    private const float WidestText = 180, LeastText = 70;

    /// <summary>
    /// Lays the row into a free gap of the taskbar's band: at the gap's Start end after <paramref name="lead"/>
    /// pixels, or at its other end before <paramref name="margin"/> pixels. Of <paramref name="steps"/> the first
    /// that fits is taken; within it the text narrows first, then the buttons shrink. None when nothing fits.
    /// </summary>
    public static WidgetRow? Compute(Rectangle band, Rectangle gap, bool fromStart, int lead, int margin,
        IReadOnlyList<WidgetFit> steps, float scale)
    {
        float s = scale;
        int available = gap.Width - lead - margin;
        float coverSize = CoverSize(band.Height, s);

        foreach (var fit in steps)
        {
            // The cover and the space around it and before the buttons; the text comes on top of this.
            float info = InfoWidth(fit, coverSize, s);
            float leastText = fit.Text ? LeastText * s : 0;
            int count = fit.ButtonCount;
            if (available < info + leastText + count * WidgetColumn.SmallestButton * s) continue;

            float button = Math.Clamp((available - info - leastText) / count, WidgetColumn.SmallestButton * s, WidgetColumn.LargestButton * s);
            float text = fit.Text ? Math.Min(WidestText * s, available - info - count * button) : 0;
            int width = (int)Math.Ceiling(info + text + count * button);
            int x = fromStart ? gap.Left + lead : gap.Right - margin - width;
            return new WidgetRow(new Rectangle(x, band.Top, width, band.Height), fit, text, button);
        }
        return null;
    }

    /// <summary>
    /// On a classic taskbar (Windows 10) the task area ends where the tray begins: there is no free gap. The
    /// widget takes the room for its buttons at their smallest from the end of the task area, as it always did
    /// there. A gap that has that room is left as it is.
    /// </summary>
    public static Rectangle ClassicGap(Rectangle gap, int lead, int margin, int buttons, float scale, out bool borrowed)
    {
        int least = lead + margin + (int)Math.Ceiling(buttons * WidgetColumn.SmallestButton * scale);
        borrowed = gap.Width < least;
        return borrowed ? Rectangle.FromLTRB(gap.Right - least, gap.Top, gap.Right, gap.Bottom) : gap;
    }

    public static float CoverSize(int height, float scale) => Math.Min(height - 14 * scale, 32 * scale);

    /// <summary>The width of the part with cover and text, without the text and without the space after it.</summary>
    public static float InfoStart(WidgetFit fit, float coverSize, float scale) =>
        4 * scale + (fit.Cover ? coverSize + 8 * scale : 0);

    private static float InfoWidth(WidgetFit fit, float coverSize, float scale) =>
        fit.Text || fit.Cover ? InfoStart(fit, coverSize, scale) + 6 * scale : 0;
}

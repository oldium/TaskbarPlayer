namespace TaskbarPlayer;

/// <summary>
/// The widget on a taskbar along a side of the screen: a column as wide as the taskbar, the cover with the
/// progress line under it on top, the buttons below. There is no room for title and artist; the song card has
/// them. <see cref="Bounds"/> is relative to the taskbar window, the parts to <see cref="Bounds"/>.
/// </summary>
internal sealed record WidgetColumn(
    Rectangle Bounds, WidgetFit Fit, RectangleF Info, RectangleF Cover, RectangleF Progress, IReadOnlyList<RectangleF> Buttons)
{
    /// <summary>What a button measures along the taskbar, in a column and in a row.</summary>
    public const float SmallestButton = 26, LargestButton = 34;

    public bool ShowsInfo => !Info.IsEmpty;

    /// <summary>
    /// Lays the column into a free gap of the taskbar's band: at the gap's Start end after <paramref name="lead"/>
    /// pixels, or at its other end before <paramref name="margin"/> pixels. Of <paramref name="steps"/> the first
    /// that fits is taken; within it the buttons shrink. None when nothing fits.
    /// </summary>
    public static WidgetColumn? Compute(Rectangle band, Rectangle gap, bool fromStart, int lead, int margin,
        IReadOnlyList<WidgetFit> steps, float scale)
    {
        float s = scale;
        int width = band.Width;
        int available = gap.Height - lead - margin;

        float coverSize = Math.Min(width - 14 * s, 32 * s);
        // Under the cover: the progress line, and room for the knob it gets under the mouse.
        float infoHeight = 6 * s + coverSize + 12 * s;
        float infoAndSpace = infoHeight + 2 * s;

        foreach (var fit in steps)
        {
            float top = fit.Cover ? infoAndSpace : 0;
            int count = fit.ButtonCount;
            if (available < top + count * SmallestButton * s) continue;

            float button = Math.Clamp((available - top) / count, SmallestButton * s, LargestButton * s);
            int height = (int)Math.Ceiling(top + count * button);
            int y = fromStart ? gap.Top + lead : gap.Bottom - margin - height;

            var cover = fit.Cover ? new RectangleF((width - coverSize) / 2, 6 * s, coverSize, coverSize) : RectangleF.Empty;
            var progress = fit.Cover ? new RectangleF(cover.Left, cover.Bottom + 4 * s, coverSize, 2 * s) : RectangleF.Empty;
            var info = fit.Cover ? new RectangleF(0, 0, width, infoHeight) : RectangleF.Empty;
            var rects = new RectangleF[count];
            for (int i = 0; i < count; i++) rects[i] = new RectangleF(0, top + i * button, width, button);

            return new WidgetColumn(new Rectangle(band.Left, y, width, height), fit, info, cover, progress, rects);
        }
        return null;
    }
}

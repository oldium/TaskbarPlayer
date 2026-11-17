using System.Drawing.Imaging;

namespace TaskbarPlayer;

/// <summary>The picture of a small tooltip, in the measures of the taskbar's own; transparent around the text.</summary>
internal static class HoverTip
{
    public static Bitmap Render(string text, float scale, bool light)
    {
        int Px(float value) => (int)MathF.Round(value * scale);
        string face = GdiText.UiFace;
        int textSize = Px(12);
        var size = GdiText.Measure(text, face, textSize, semibold: false, Px(480));

        // One pixel of edge, then 8 px at the sides, 5 above and 7 below the text.
        var tip = new Bitmap(Px(9) + size.Width + Px(9), Px(6) + size.Height + Px(8), PixelFormat.Format32bppArgb);
        GdiText.DrawWrapped(tip, text, face, textSize, semibold: false, new Rectangle(Px(9), Px(6), size.Width, size.Height),
            FlyoutColors.Text(light));
        return tip;
    }
}

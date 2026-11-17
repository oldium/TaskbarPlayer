using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TaskbarPlayer;

/// <summary>What is known about the playing song. Numbers are 0 and texts empty where the music app says nothing.</summary>
internal sealed record SongDetails(string Title, string? Artist, string? Album, int TrackNumber, int TrackCount,
    TimeSpan Length, string? Source, Image? Cover);

/// <summary>What a line of the card is; its look follows from this, not from where it stands.</summary>
internal enum CardLineKind
{
    Title,
    Artist,
    Detail,
}

internal sealed record CardLine(string Text, CardLineKind Kind);

/// <summary>
/// The picture of a song's details in full: a large cover and whole lines, wrapped where long; transparent around
/// them. Only text beyond any sense is cut, so the card always fits a screen.
/// </summary>
internal static class SongCard
{
    // At 100 % display scale.
    private const int Padding = 12;
    private const int CoverSize = 96;
    private const int MaxTextWidth = 320;
    private const int TitleSize = 16;
    private const int TextSize = 12;
    private const int MaxTitleLines = 5;
    private const int MaxOtherLines = 2;

    /// <summary>The lines of text, title first; only what the music app reports.</summary>
    public static IReadOnlyList<CardLine> Lines(SongDetails song)
    {
        var lines = new List<CardLine> { new(song.Title, CardLineKind.Title) };
        if (!string.IsNullOrWhiteSpace(song.Artist)) lines.Add(new(song.Artist, CardLineKind.Artist));
        if (!string.IsNullOrWhiteSpace(song.Album)) lines.Add(new(song.Album, CardLineKind.Detail));

        var facts = new List<string>();
        if (song.TrackNumber > 0) facts.Add(Strings.TrackLabel(song.TrackNumber, song.TrackCount));
        if (song.Length > TimeSpan.Zero) facts.Add(Strings.Time(song.Length));
        if (facts.Count > 0) lines.Add(new(string.Join(" · ", facts), CardLineKind.Detail));

        if (!string.IsNullOrWhiteSpace(song.Source)) lines.Add(new(Strings.SourceLabel(song.Source), CardLineKind.Detail));
        return lines;
    }

    public static Bitmap Render(SongDetails song, float scale, bool light)
    {
        int Px(float value) => (int)MathF.Round(value * scale);
        int pad = Px(Padding), cover = Px(CoverSize);
        string face = GdiText.UiFace;
        var lines = Lines(song);
        Color text = FlyoutColors.Text(light);

        bool IsTitle(int i) => lines[i].Kind == CardLineKind.Title;
        int SizeOf(int i) => Px(IsTitle(i) ? TitleSize : TextSize);
        // A little more air under the title than between the other lines.
        int GapAbove(int i) => i == 0 ? 0 : Px(IsTitle(i - 1) ? 4 : 2);

        // The column is as wide as its longest line, up to the limit; beyond that lines wrap.
        var sizes = new Size[lines.Count];
        int column = 0, textHeight = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            sizes[i] = GdiText.Measure(lines[i].Text, face, SizeOf(i), semibold: IsTitle(i), Px(MaxTextWidth),
                IsTitle(i) ? MaxTitleLines : MaxOtherLines);
            column = Math.Max(column, sizes[i].Width);
            textHeight += GapAbove(i) + sizes[i].Height;
        }

        var card = new Bitmap(pad + cover + pad + column + pad, pad + Math.Max(cover, textHeight) + pad, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(card))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var frame = new RectangleF(pad, pad, cover, cover);
            using var path = Glyphs.RoundedRect(frame, 6 * scale);
            if (song.Cover is Image image)
            {
                g.SetClip(path);
                int side = Math.Min(image.Width, image.Height);
                var middle = new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side);
                g.DrawImage(image, frame, middle, GraphicsUnit.Pixel);
                g.ResetClip();
            }
            else
            {
                using var faint = new SolidBrush(Color.FromArgb(light ? 30 : 40, text));
                g.FillPath(faint, path);
            }
        }
        if (song.Cover is null)
        {
            GdiText.Draw(card, Glyphs.MusicNote, Glyphs.FontFamilyName, Px(32), semibold: false,
                new Rectangle(pad, pad, cover, cover), Color.FromArgb(150, text), centered: true);
        }

        int x = pad + cover + pad;
        // A few short lines sit level with the middle of the cover.
        int y = pad + Math.Max(0, (cover - textHeight) / 2);
        for (int i = 0; i < lines.Count; i++)
        {
            y += GapAbove(i);
            // Title and artist in full strength, the details quieter.
            Color color = lines[i].Kind == CardLineKind.Detail ? Color.FromArgb(light ? 150 : 165, text) : text;
            GdiText.DrawWrapped(card, lines[i].Text, face, SizeOf(i), semibold: IsTitle(i), new Rectangle(x, y, column, sizes[i].Height), color);
            y += sizes[i].Height;
        }
        return card;
    }
}

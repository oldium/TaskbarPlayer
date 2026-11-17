using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TaskbarPlayer;

/// <summary>
/// Text and icon-font glyphs drawn by the classic Windows text engine. It puts strokes on whole pixels, as the
/// taskbar's own text has them; GDI+ text spreads each stroke over two.
/// </summary>
internal static class GdiText
{
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20, DT_NOCLIP = 0x100, DT_CALCRECT = 0x400,
        DT_NOPREFIX = 0x800, DT_EDITCONTROL = 0x2000, DT_END_ELLIPSIS = 0x8000;
    private const uint OneLine = DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER;
    // New lines between words, inside a word wider than the box, and where the text has line breaks; dots at
    // the end of the last line the box has room for when there is more.
    private const uint Wrapped = DT_WORDBREAK | DT_EDITCONTROL | DT_NOPREFIX | DT_END_ELLIPSIS;
    private const uint DEFAULT_CHARSET = 1;
    private const uint ANTIALIASED_QUALITY = 4; // greyscale smoothing: coverage per pixel, usable as opacity
    private const int TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRIC
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
            tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits,
        IntPtr section, uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality,
        uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr hdc, int count, [Out] char[] name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

    private static readonly Dictionary<(string Face, int PixelSize, bool Semibold), IntPtr> Fonts = new();

    /// <summary>The taskbar's own text font where it is installed (Windows 11), else the classic one.</summary>
    public static string UiFace { get; } = FindUiFace();

    private static string FindUiFace()
    {
        const string variable = "Segoe UI Variable Text";
        // Both cuts or neither: a missing one would be drawn in some other face altogether.
        return Resolves(variable) && Resolves(variable + " Semibold") ? variable : "Segoe UI";
    }

    /// <summary>Whether the engine that draws knows a face by this name, rather than putting another in its place.</summary>
    internal static bool Resolves(string face)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, face);
        try
        {
            IntPtr old = SelectObject(dc, font);
            var name = new char[64];
            // The count includes the terminating zero.
            int length = GetTextFaceW(dc, name.Length, name);
            SelectObject(dc, old);
            return length > 1 && string.Equals(new string(name, 0, length - 1), face, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    // Kept for the life of the program: a handful of faces and sizes.
    private static IntPtr FontFor(string face, int pixelSize, bool semibold)
    {
        var key = (face, pixelSize, semibold);
        if (!Fonts.TryGetValue(key, out IntPtr font))
        {
            // The semibold cut is a face of its own; asking the plain face for weight 600 gives bold.
            font = CreateFontW(-pixelSize, 0, 0, 0, semibold ? 600 : 400, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
                ANTIALIASED_QUALITY, 0, semibold ? face + " Semibold" : face);
            Fonts[key] = font;
        }
        return font;
    }

    /// <summary>The height of one line of text, in whole pixels.</summary>
    public static int LineHeight(string face, int pixelSize, bool semibold)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            IntPtr old = SelectObject(dc, FontFor(face, pixelSize, semibold));
            bool ok = GetTextMetricsW(dc, out var metrics);
            SelectObject(dc, old);
            return ok ? metrics.tmHeight : pixelSize;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// The room text takes when broken into lines no wider than <paramref name="maxWidth"/>, and no more of them
    /// than <paramref name="maxLines"/> where that is given.
    /// </summary>
    public static Size Measure(string text, string face, int pixelSize, bool semibold, int maxWidth, int maxLines = 0)
    {
        if (text.Length == 0 || pixelSize <= 0 || maxWidth <= 0) return Size.Empty;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            IntPtr old = SelectObject(dc, FontFor(face, pixelSize, semibold));
            var rect = new RECT { Right = maxWidth };
            DrawTextW(dc, text, text.Length, ref rect, Wrapped | DT_CALCRECT);
            SelectObject(dc, old);
            int height = maxLines > 0 ? Math.Min(rect.Bottom, maxLines * LineHeight(face, pixelSize, semibold)) : rect.Bottom;
            return new Size(rect.Right, height);
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// Draws one line over what the picture already holds, inside <paramref name="box"/>: centred both ways,
    /// or from the left with an ellipsis when it does not fit.
    /// </summary>
    public static void Draw(Bitmap surface, string text, string face, int pixelSize, bool semibold, Rectangle box,
        Color color, bool centered)
    {
        if (pixelSize <= 0) return;
        Blend(surface, text, FontFor(face, pixelSize, semibold), box, color, OneLine | (centered ? DT_CENTER : DT_END_ELLIPSIS),
            Overhang(pixelSize));
    }

    /// <summary>Draws text from the top of <paramref name="box"/>, on as many lines as its width asks for.</summary>
    public static void DrawWrapped(Bitmap surface, string text, string face, int pixelSize, bool semibold, Rectangle box,
        Color color)
    {
        if (pixelSize <= 0) return;
        Blend(surface, text, FontFor(face, pixelSize, semibold), box, color, Wrapped, Overhang(pixelSize));
    }

    // The last letter of a line can lean out of its cell (f, V, W): that much room beside the box is its.
    private static int Overhang(int pixelSize) => Math.Max(2, pixelSize / 4);

    private static void Blend(Bitmap surface, string text, IntPtr font, Rectangle box, Color color, uint format, int overhang)
    {
        if (box.Width <= 0 || box.Height <= 0 || text.Length == 0 || color.A == 0) return;
        // What of the box and the room beside it lies on the picture; the text keeps its place in the box.
        var area = Rectangle.Intersect(new Rectangle(box.X, box.Y, box.Width + overhang, box.Height),
            new Rectangle(0, 0, surface.Width, surface.Height));
        if (area.Width <= 0 || area.Height <= 0) return;

        var inArea = new RECT { Left = box.X - area.X, Top = box.Y - area.Y, Right = box.Right - area.X, Bottom = box.Bottom - area.Y };
        byte[] coverage = Rasterize(text, font, area.Size, inArea, format);
        var data = surface.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[area.Width * 4];
            for (int y = 0; y < area.Height; y++)
            {
                IntPtr line = data.Scan0 + y * data.Stride;
                Marshal.Copy(line, row, 0, row.Length);
                bool touched = false;
                for (int i = 0, c = y * area.Width * 4; i < row.Length; i += 4, c += 4)
                {
                    int opacity = (coverage[c] + coverage[c + 1] + coverage[c + 2] + 1) / 3 * color.A / 255;
                    if (opacity == 0) continue;
                    int below = row[i + 3] * (255 - opacity) / 255;
                    int alpha = opacity + below;
                    row[i] = (byte)((color.B * opacity + row[i] * below) / alpha);
                    row[i + 1] = (byte)((color.G * opacity + row[i + 1] * below) / alpha);
                    row[i + 2] = (byte)((color.R * opacity + row[i + 2] * below) / alpha);
                    row[i + 3] = (byte)alpha;
                    touched = true;
                }
                if (touched) Marshal.Copy(row, 0, line, row.Length);
            }
        }
        finally
        {
            surface.UnlockBits(data);
        }
    }

    // White on black, four bytes per pixel: the brightness is how much of the pixel the text covers.
    private static byte[] Rasterize(string text, IntPtr font, Size size, RECT rect, uint format)
    {
        var pixels = new byte[size.Width * size.Height * 4];
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size.Width,
            biHeight = -size.Height, // top row first
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr dib = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero)
        {
            IntPtr oldBitmap = SelectObject(dc, dib);
            IntPtr oldFont = SelectObject(dc, font);
            SetTextColor(dc, 0x00FFFFFF);
            SetBkMode(dc, TRANSPARENT);
            // A text that fits its box is not cut at the box, so a last letter may lean out of it; one that does
            // not fit is cut by the box and ends in dots.
            var needed = rect;
            DrawTextW(dc, text, text.Length, ref needed, (format & ~(DT_CENTER | DT_VCENTER | DT_END_ELLIPSIS)) | DT_CALCRECT);
            bool fits = needed.Right <= rect.Right && needed.Bottom <= rect.Bottom;
            DrawTextW(dc, text, text.Length, ref rect, fits ? format | DT_NOCLIP : format);
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            SelectObject(dc, oldFont);
            SelectObject(dc, oldBitmap);
            DeleteObject(dib);
        }
        DeleteDC(dc);
        return pixels;
    }
}

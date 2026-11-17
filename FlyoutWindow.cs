using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>Colours of what pops up above the taskbar, following its light or dark mode.</summary>
internal static class FlyoutColors
{
    public static Color Text(bool light) => light ? Color.FromArgb(26, 26, 26) : Color.White;

    /// <summary>A thin edge darker than what is behind it, as the taskbar's own tooltips have.</summary>
    public static Color Edge(bool light) => light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(100, 0, 0, 0);

    /// <summary>The tint of the see-through material; its opacity keeps text readable over a bright window.</summary>
    public static Color Material(bool light) => light ? Color.FromArgb(217, 249, 249, 249) : Color.FromArgb(191, 44, 44, 44);

    /// <summary>The background of what is not see-through: menus and the volume popup.</summary>
    public static Color Solid(bool light) => Color.FromArgb(255, Material(light));

    /// <summary>The thin edge around a solid background, a little darker than it.</summary>
    public static Color SolidEdge(bool light) => light ? Color.FromArgb(222, 222, 222) : Color.FromArgb(28, 28, 28);

    /// <summary>Text of what cannot be chosen.</summary>
    public static Color Dim(bool light) => light ? Color.FromArgb(150, 150, 150) : Color.FromArgb(135, 135, 135);

    /// <summary>Behind the menu entry under the mouse or the keyboard.</summary>
    public static Color Highlight(bool light) => light ? Color.FromArgb(232, 232, 232) : Color.FromArgb(62, 62, 62);

    /// <summary>A dividing line.</summary>
    public static Color Line(bool light) => light ? Color.FromArgb(224, 224, 224) : Color.FromArgb(68, 68, 68);
}

/// <summary>
/// Shows a ready-made picture (a tooltip, the song card) above the taskbar, on blurred see-through material as
/// the taskbar's own tooltips are. It never takes the focus.
/// </summary>
internal sealed class FlyoutWindow : Form
{
    private const int WS_EX_TOPMOST = 0x8;
    private const int WM_DPICHANGED = 0x02E0;

    /// <summary>Windows 11 rounds a window's corners where asked; older versions leave them square.</summary>
    public static readonly bool RoundedByWindows = Environment.OSVersion.Version.Build >= 22000;

    private readonly System.Windows.Forms.Timer _hide = new();
    private Bitmap? _picture;

    public FlyoutWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        _hide.Tick += (_, _) => HideNow();
    }

    /// <summary>Told to show and not yet to go.</summary>
    public bool Showing { get; private set; }

    /// <summary>The scale of the display it shows on; the corners Windows gives it there follow it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float DisplayScale { get; set; } = 1f;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>
    /// Where a picture goes by a taskbar: a gap away from it on the screen's side, and starting level with
    /// <paramref name="anchor"/>, the top-left corner of what it belongs to: at its left edge above or below a
    /// lying taskbar, at its top edge beside an upright one. Kept on the screen both ways.
    /// </summary>
    public static Rectangle Place(Size picture, Point anchor, TaskbarEdge edge, Rectangle taskbar, Rectangle screen, int gap)
    {
        (int x, int y) = edge switch
        {
            TaskbarEdge.Top => (anchor.X, taskbar.Bottom + gap),
            TaskbarEdge.Left => (taskbar.Right + gap, anchor.Y),
            TaskbarEdge.Right => (taskbar.Left - gap - picture.Width, anchor.Y),
            _ => (anchor.X, taskbar.Top - gap - picture.Height),
        };
        // Where it is larger than the screen, its top-left is what shows.
        x = Math.Max(screen.Left, Math.Min(x, screen.Right - picture.Width));
        y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - picture.Height));
        return new Rectangle(x, y, picture.Width, picture.Height);
    }

    /// <summary>
    /// Shows the picture by the taskbar (see <see cref="Place"/>) for a time or (0) until hidden. The picture
    /// becomes the window's.
    /// </summary>
    public void ShowPicture(Bitmap picture, Point anchor, TaskbarEdge edge, Rectangle taskbar, int gap, bool light,
        bool smallCorners, int durationMs) =>
        ShowAt(picture, Place(picture.Size, anchor, edge, taskbar, Screen.FromRectangle(taskbar).Bounds, gap).Location, light,
            smallCorners, durationMs);

    /// <summary>
    /// Shows the picture with its top-left corner at a point on the screen, for a time or (0) until hidden. Where
    /// the picture is transparent the material shows. The picture becomes the window's.
    /// </summary>
    public void ShowAt(Bitmap picture, Point topLeft, bool light, bool smallCorners, int durationMs)
    {
        if (!NativeMethods.StyleMaterial(Handle, FlyoutColors.Material(light), smallCorners))
        {
            using var bare = picture;
            picture = OnPlainBackground(bare, light);
        }
        DrawEdge(picture, light, CornerRadius(smallCorners, DisplayScale, RoundedByWindows));
        _picture?.Dispose();
        _picture = picture;

        Bounds = new Rectangle(topLeft, picture.Size);
        if (!Visible) Show();
        Invalidate();
        Showing = true;

        _hide.Stop();
        if (durationMs > 0)
        {
            _hide.Interval = durationMs;
            _hide.Start();
        }
    }

    /// <summary>The picture on the material's tint made solid, for where Windows has no material to give.</summary>
    public static Bitmap OnPlainBackground(Bitmap picture, bool light)
    {
        var result = new Bitmap(picture.Width, picture.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.FromArgb(255, FlyoutColors.Material(light)));
        g.DrawImageUnscaled(picture, 0, 0);
        return result;
    }

    /// <summary>
    /// The radius Windows rounds the window's corners with: 4 px for small ones, else 8 px, at 100 %; none where
    /// Windows does not round windows.
    /// </summary>
    public static float CornerRadius(bool smallCorners, float scale, bool roundedByWindows) =>
        roundedByWindows ? (smallCorners ? 4 : 8) * scale : 0;

    /// <summary>Draws the thin edge along the picture's border, following corners of the given radius.</summary>
    public static void DrawEdge(Bitmap picture, bool light, float radius)
    {
        using var g = Graphics.FromImage(picture);
        using var pen = new Pen(FlyoutColors.Edge(light));
        if (radius <= 0)
        {
            g.DrawRectangle(pen, 0, 0, picture.Width - 1, picture.Height - 1);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Glyphs.RoundedRect(new RectangleF(0.5f, 0.5f, picture.Width - 1, picture.Height - 1), radius);
        g.DrawPath(pen, path);
    }

    protected override void WndProc(ref Message m)
    {
        // The window is as large as its picture on any display: Windows' offer to resize it for another scale,
        // made when it lands on a display that has one, is declined.
        if (m.Msg == WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A request to close from outside (taskkill) only hides it: the app runs on and needs it again.
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            HideNow();
        }
        base.OnFormClosing(e);
    }

    public void HideNow()
    {
        _hide.Stop();
        Showing = false;
        if (Visible) Hide();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The picture covers everything.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_picture is null) return;
        IntPtr hdc = e.Graphics.GetHdc();
        try
        {
            NativeMethods.CopyWithAlpha(hdc, _picture);
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hide.Dispose();
            _picture?.Dispose();
        }
        base.Dispose(disposing);
    }
}
